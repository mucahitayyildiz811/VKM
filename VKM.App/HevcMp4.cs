using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VKM.App
{
    // H.265/HEVC Annex-B akışını MP4 kabına (hvc1 + hvcC) sarar (ffmpeg olmadan).
    // Sadece .mp4 üretir (yan elementer yedek yok — tek dosya).
    public static class HevcMp4
    {
        private static int FPS = 25;                      // Mux çağrısında ayarlanır
        private const int MEDIA_TS = 90000;
        private static int SAMPLE_DUR = MEDIA_TS / FPS;
        private const int MOVIE_TS = 1000;

        private static readonly byte[] StartCode = { 0, 0, 0, 1 };

        private static bool ValidHevc(byte b0)
        {
            if ((b0 & 0x80) != 0) return false;
            int t = (b0 >> 1) & 0x3F;
            return t <= 40;
        }

        public static bool Mux(Func<int, Stream> openRaw, long offset, long length, string dstPath, CancellationToken tok, Action<long>? onBytes = null, int fps = 25)
        {
            if (fps < 1 || fps > 120) fps = 25;
            FPS = fps; SAMPLE_DUR = MEDIA_TS / fps;
            byte[]? vps = null, sps = null, pps = null;
            var sizes = new List<int>();
            var syncs = new List<int>();

            using Stream src = openRaw(512);
            src.Position = offset;

            string tmp = dstPath + ".mdat.tmp";

            // Sadece .mp4 üret (yan .h265 yedeği yazma — tek dosya)
            using (var mdat = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                foreach (byte[] nal in Nals(src, length, tok, onBytes))
                {
                    if (nal.Length < 2 || !ValidHevc(nal[0])) continue;
                    int t = (nal[0] >> 1) & 0x3F;

                    if (t == 32) { vps ??= nal; continue; }
                    if (t == 33) { sps ??= nal; continue; }
                    if (t == 34) { pps ??= nal; continue; }
                    if (t <= 31)   // VCL slice = örnek
                    {
                        int len = nal.Length;
                        mdat.WriteByte((byte)(len >> 24)); mdat.WriteByte((byte)(len >> 16));
                        mdat.WriteByte((byte)(len >> 8)); mdat.WriteByte((byte)len);
                        mdat.Write(nal, 0, len);
                        sizes.Add(4 + len);
                        if (t >= 16 && t <= 21) syncs.Add(sizes.Count);   // IRAP (IDR/CRA/BLA) = anahtar kare
                    }
                }
            }

            if (vps == null || sps == null || pps == null || sizes.Count == 0)
            {
                if (File.Exists(tmp)) File.Delete(tmp);
                return false;   // .h265 elinde kaldı
            }

            (int w, int h) = ParseHevcDims(sps);

            using (var outF = new FileStream(dstPath, FileMode.Create, FileAccess.Write))
            {
                WriteFtyp(outF);
                long mdatLen = new FileInfo(tmp).Length;
                uint dataOff;
                if (8 + mdatLen > 0xFFFFFFFFL)   // 4 GB üstü → 64-bit mdat boyutu
                {
                    WriteU32(outF, 1); Ascii(outF, "mdat"); WriteU64(outF, 16 + mdatLen);
                    dataOff = 40;   // ftyp(24) + mdat başlığı(16)
                }
                else
                {
                    WriteU32(outF, (uint)(8 + mdatLen)); Ascii(outF, "mdat");
                    dataOff = 32;   // ftyp(24) + mdat başlığı(8)
                }
                using (var mdatIn = new FileStream(tmp, FileMode.Open, FileAccess.Read)) mdatIn.CopyTo(outF);
                WriteMoov(outF, vps, sps, pps, sizes, syncs, w, h, dataOff);
            }
            File.Delete(tmp);
            return true;
        }

        private static IEnumerable<byte[]> Nals(Stream s, long length, CancellationToken tok, Action<long>? onBytes = null)
        {
            byte[] buf = new byte[1 << 20];
            var cur = new List<byte>(1 << 16);
            long remaining = length;
            int zeros = 0; bool inNal = false;
            while (remaining > 0 && !tok.IsCancellationRequested)
            {
                int want = (int)Math.Min(buf.Length, remaining);
                int got = s.Read(buf, 0, want);
                if (got <= 0) break;
                remaining -= got;
                onBytes?.Invoke(got);
                for (int i = 0; i < got; i++)
                {
                    byte b = buf[i];
                    if (b == 0x01 && zeros >= 2)
                    {
                        if (inNal)
                        {
                            int realLen = cur.Count - zeros;
                            if (realLen > 0) yield return cur.GetRange(0, realLen).ToArray();
                        }
                        cur.Clear(); inNal = true; zeros = 0; continue;
                    }
                    if (inNal) cur.Add(b);
                    zeros = (b == 0x00) ? zeros + 1 : 0;
                }
            }
            if (inNal && cur.Count > 0) yield return cur.ToArray();
        }

        // ---- MP4 kutuları ----
        private static void WriteFtyp(Stream o)
        {
            WriteU32(o, 24); Ascii(o, "ftyp"); Ascii(o, "isom");
            WriteU32(o, 0x200); Ascii(o, "isom"); Ascii(o, "mp41");
        }

        private static void WriteMoov(Stream o, byte[] vps, byte[] sps, byte[] pps, List<int> sizes, List<int> syncs, int w, int h, uint dataOff)
        {
            int n = sizes.Count;
            long movieDur = (long)n * MOVIE_TS / FPS;
            long mediaDur = (long)n * SAMPLE_DUR;

            var trak = Box("trak",
                Tkhd(movieDur, w, h),
                Box("mdia",
                    Mdhd(mediaDur),
                    Hdlr(),
                    Box("minf",
                        Box("vmhd", Bytes(0x00, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0, 0)),
                        Box("dinf", Box("dref", Concat(U32(0), U32(1), Box("url ", Bytes(0, 0, 0, 1))))),
                        Box("stbl",
                            Stsd(vps, sps, pps, w, h),
                            Stts(n), Stss(syncs), Stsc(n), Stsz(sizes), Stco(dataOff)
                        )
                    )
                )
            );
            byte[] moov = Box("moov", Mvhd(movieDur), trak);
            o.Write(moov, 0, moov.Length);
        }

        private static byte[] Mvhd(long dur) => Box("mvhd", Concat(
            U32(0), U32(0), U32(0), U32(MOVIE_TS), U32((uint)dur),
            U32(0x00010000), Bytes(0x01, 0x00, 0, 0), U32(0), U32(0),
            Matrix(), new byte[24], U32(2)));

        private static byte[] Tkhd(long dur, int w, int h) => Box("tkhd", Concat(
            Bytes(0, 0, 0, 0x07), U32(0), U32(0), U32(1), U32(0), U32((uint)dur),
            new byte[8], U32(0), U32(0), Matrix(),
            U32((uint)(w << 16)), U32((uint)(h << 16))));

        private static byte[] Mdhd(long dur) => Box("mdhd", Concat(
            U32(0), U32(0), U32(0), U32(MEDIA_TS), U32((uint)dur), Bytes(0x55, 0xC4, 0, 0)));

        private static byte[] Hdlr() => Box("hdlr", Concat(
            U32(0), U32(0), Ascii4("vide"), U32(0), U32(0), U32(0),
            System.Text.Encoding.ASCII.GetBytes("VideoHandler\0")));

        private static byte[] Stsd(byte[] vps, byte[] sps, byte[] pps, int w, int h)
        {
            byte[] hvcC = BuildHvcC(vps, sps, pps);
            byte[] hvc1 = Box("hvc1", Concat(
                new byte[6], U16(1),
                new byte[16],
                U16((ushort)w), U16((ushort)h),
                U32(0x00480000), U32(0x00480000), U32(0), U16(1),
                new byte[32], U16(0x0018), Bytes(0xFF, 0xFF),
                hvcC));
            return Box("stsd", Concat(U32(0), U32(1), hvc1));
        }

        // hvcC — SPS'in profile_tier_level baytlarını kullanır
        private static byte[] BuildHvcC(byte[] vps, byte[] sps, byte[] pps)
        {
            byte[] p = Unescape(sps);   // [0,1]=NAL başlık, [2]=vps_id+sublayers+nesting, [3..14]=PTL(12)
            byte profileByte = p.Length > 3 ? p[3] : (byte)0x01;
            byte[] compat = Slice(p, 4, 4);
            byte[] constraint = Slice(p, 8, 6);
            byte level = p.Length > 14 ? p[14] : (byte)0;
            byte nested = (byte)((p.Length > 2 ? p[2] : 0) & 0x01);

            var head = new List<byte>
            {
                0x01,                 // configurationVersion
                profileByte
            };
            head.AddRange(compat);        // 4
            head.AddRange(constraint);    // 6
            head.Add(level);
            head.Add(0xF0); head.Add(0x00);   // min_spatial_segmentation_idc = 0
            head.Add(0xFC);                   // parallelismType = 0
            head.Add(0xFD);                   // chromaFormat = 1 (4:2:0)
            head.Add(0xF8);                   // bitDepthLumaMinus8 = 0
            head.Add(0xF8);                   // bitDepthChromaMinus8 = 0
            head.Add(0x00); head.Add(0x00);   // avgFrameRate = 0
            head.Add((byte)(0x08 | (nested << 2) | 0x03));  // CFR=0,numTempLayers=1,nested,lengthSize=3
            head.Add(0x03);                   // numOfArrays = 3

            AddArray(head, 32, vps);
            AddArray(head, 33, sps);
            AddArray(head, 34, pps);

            return Box("hvcC", head.ToArray());
        }

        private static void AddArray(List<byte> o, int nalType, byte[] nal)
        {
            o.Add((byte)(0x80 | nalType));        // array_completeness=1 + NAL_type
            o.Add(0x00); o.Add(0x01);             // numNalus = 1
            o.Add((byte)(nal.Length >> 8)); o.Add((byte)nal.Length);
            o.AddRange(nal);
        }

        private static byte[] Stts(int n) => Box("stts", Concat(U32(0), U32(1), U32((uint)n), U32((uint)SAMPLE_DUR)));
        private static byte[] Stss(List<int> syncs)
        {
            var parts = new List<byte[]> { U32(0), U32((uint)syncs.Count) };
            foreach (int s in syncs) parts.Add(U32((uint)s));
            return Box("stss", Concat(parts.ToArray()));
        }
        // TÜM kareler TEK parçada: first_chunk=1, samples_per_chunk=n, desc=1
        // (eski hali samples_per_chunk=1 idi → stco tek parça olunca sadece 1 kare adreslenip video 1ms'de bitiyordu)
        private static byte[] Stsc(int n) => Box("stsc", Concat(U32(0), U32(1), U32(1), U32((uint)n), U32(1)));
        private static byte[] Stsz(List<int> sizes)
        {
            var parts = new List<byte[]> { U32(0), U32(0), U32((uint)sizes.Count) };
            foreach (int s in sizes) parts.Add(U32((uint)s));
            return Box("stsz", Concat(parts.ToArray()));
        }
        private static byte[] Stco(uint off) => Box("stco", Concat(U32(0), U32(1), U32(off)));
        private static byte[] Matrix() => Concat(
            U32(0x00010000), U32(0), U32(0), U32(0), U32(0x00010000), U32(0),
            U32(0), U32(0), U32(0x40000000));

        // ---- HEVC SPS boyut ----
        private static (int w, int h) ParseHevcDims(byte[] sps)
        {
            try
            {
                byte[] r = Unescape(sps);
                var br = new BitR(r, 2);          // 2 bayt NAL başlığını atla
                br.U(4);                           // sps_video_parameter_set_id
                int maxSub = br.U(3);              // sps_max_sub_layers_minus1
                br.U(1);                           // nesting
                SkipPtl(br, maxSub);               // profile_tier_level
                br.UE();                           // sps_seq_parameter_set_id
                int chroma = br.UE();
                if (chroma == 3) br.U(1);
                int wl = br.UE();
                int hl = br.UE();
                int cl = 0, cr = 0, ct = 0, cb = 0;
                if (br.U(1) == 1) { cl = br.UE(); cr = br.UE(); ct = br.UE(); cb = br.UE(); }
                int sub = chroma == 1 ? 2 : 1;
                int w = wl - (cl + cr) * sub;
                int h = hl - (ct + cb) * sub;
                if (w < 16 || w > 8192 || h < 16 || h > 8192) return (1920, 1080);
                return (w, h);
            }
            catch { return (1920, 1080); }
        }

        private static void SkipPtl(BitR br, int maxSub)
        {
            // general: 2+1+5 + 32 + 4 + 44 + 8 = 96 bit
            br.U(8);               // profile_space+tier+profile_idc (8)
            br.U(32);              // compatibility
            br.U(32); br.U(12);    // 44 bit (progressive/interlaced/... + reserved)
            br.U(8);               // general_level_idc
            if (maxSub > 0)
            {
                var pf = new bool[maxSub]; var lf = new bool[maxSub];
                for (int i = 0; i < maxSub; i++) { pf[i] = br.U(1) == 1; lf[i] = br.U(1) == 1; }
                if (maxSub > 0) for (int i = maxSub; i < 8; i++) br.U(2);
                for (int i = 0; i < maxSub; i++)
                {
                    if (pf[i]) { br.U(8); br.U(32); br.U(32); br.U(12); }
                    if (lf[i]) br.U(8);
                }
            }
        }

        private static byte[] Unescape(byte[] b)
        {
            var o = new List<byte>(b.Length);
            int z = 0;
            foreach (byte x in b)
            {
                if (x == 3 && z >= 2) { z = 0; continue; }
                o.Add(x);
                z = x == 0 ? z + 1 : 0;
            }
            return o.ToArray();
        }

        private static byte[] Slice(byte[] a, int off, int len)
        {
            byte[] r = new byte[len];
            for (int i = 0; i < len; i++) r[i] = (off + i) < a.Length ? a[off + i] : (byte)0;
            return r;
        }

        private sealed class BitR
        {
            private readonly byte[] _b; private int _pos;
            public BitR(byte[] b, int startByte) { _b = b; _pos = startByte * 8; }
            public int U(int n) { int v = 0; for (int i = 0; i < n; i++) v = (v << 1) | Bit(); return v; }
            public int UE() { int z = 0; while (Bit() == 0 && z < 32) z++; int v = 0; for (int i = 0; i < z; i++) v = (v << 1) | Bit(); return (1 << z) - 1 + v; }
            private int Bit() { int by = _pos >> 3; int bit = 7 - (_pos & 7); _pos++; if (by >= _b.Length) return 0; return (_b[by] >> bit) & 1; }
        }

        private static byte[] Box(string type, params byte[][] payload)
        {
            byte[] body = Concat(payload);
            byte[] box = new byte[8 + body.Length];
            uint sz = (uint)box.Length;
            box[0] = (byte)(sz >> 24); box[1] = (byte)(sz >> 16); box[2] = (byte)(sz >> 8); box[3] = (byte)sz;
            box[4] = (byte)type[0]; box[5] = (byte)type[1]; box[6] = (byte)type[2]; box[7] = (byte)type[3];
            Array.Copy(body, 0, box, 8, body.Length);
            return box;
        }
        private static byte[] Concat(params byte[][] parts)
        {
            int len = 0; foreach (var p in parts) len += p.Length;
            byte[] r = new byte[len]; int o = 0;
            foreach (var p in parts) { Array.Copy(p, 0, r, o, p.Length); o += p.Length; }
            return r;
        }
        private static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
        private static byte[] U16(ushort v) => new[] { (byte)(v >> 8), (byte)v };
        private static byte[] Bytes(params byte[] b) => b;
        private static byte[] Ascii4(string s) => System.Text.Encoding.ASCII.GetBytes(s);
        private static void WriteU32(Stream o, uint v) { o.WriteByte((byte)(v >> 24)); o.WriteByte((byte)(v >> 16)); o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)v); }
        private static void WriteU64(Stream o, long v) { for (int s = 56; s >= 0; s -= 8) o.WriteByte((byte)(v >> s)); }
        private static void Ascii(Stream o, string s) { foreach (char c in s) o.WriteByte((byte)c); }
    }
}
