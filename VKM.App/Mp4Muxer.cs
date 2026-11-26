using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VKM.App
{
    // H.264 Annex-B elementer akışını MP4 kabına sarar (ffmpeg olmadan).
    // Kaynaktan [offset, length] akışını okur, NAL'leri ayrıştırır, mdat+moov yazar.
    // mdat önce yazılır (akış sırasında), boyutu sonra yamanır, moov en sona eklenir.
    public static class Mp4Muxer
    {
        private static int FPS = 25;                      // Mux çağrısında ayarlanır
        private const int MEDIA_TS = 90000;
        private static int SAMPLE_DUR = MEDIA_TS / FPS;   // FPS'e göre
        private const int MOVIE_TS = 1000;

        // Geçerli H.264 NAL tipleri (Hikvision özel başlıkları bunların dışında kalır → atılır)
        private static bool ValidNal(byte header)
        {
            if ((header & 0x80) != 0) return false;        // forbidden_zero_bit set → çöp
            int t = header & 0x1F;
            // 1 slice, 5 IDR, 6 SEI, 7 SPS, 8 PPS, 9 AUD (video kurtarma için anlamlı olanlar)
            return t == 1 || t == 5 || t == 6 || t == 7 || t == 8 || t == 9;
        }

        private static readonly byte[] StartCode = { 0, 0, 0, 1 };

        // true: sadece <dst>.mp4 yazıldı (yan elementer yedek yok — tek dosya).
        public static bool Mux(Func<int, Stream> openRaw, long offset, long length, string dstPath, CancellationToken tok, Action<long>? onBytes = null, int fps = 25)
        {
            if (fps < 1 || fps > 120) fps = 25;
            FPS = fps; SAMPLE_DUR = MEDIA_TS / fps;
            byte[]? sps = null, pps = null;
            var sizes = new List<int>();     // örnek boyutları (4+nal)
            var syncs = new List<int>();     // IDR örnek indeksleri (1-tabanlı)

            using Stream src = openRaw(512);
            src.Position = offset;

            // Temiz Annex-B elementer akış (doğrulanmış NAL'ler, standart start code)
            string tmp = dstPath + ".mdat.tmp";

            // Sadece .mp4 üret (yan .h264 yedeği yazma — tek dosya)
            using (var mdat = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                foreach (byte[] nal in Nals(src, length, tok, onBytes))
                {
                    if (nal.Length == 0) continue;
                    if (!ValidNal(nal[0])) continue;        // Hikvision çöpü / bozuk başlık → at
                    int t = nal[0] & 0x1F;

                    if (t == 7) { sps ??= nal; continue; }
                    if (t == 8) { pps ??= nal; continue; }
                    if (t == 1 || t == 5)   // VCL slice = bir kare = bir örnek
                    {
                        int len = nal.Length;
                        mdat.WriteByte((byte)(len >> 24)); mdat.WriteByte((byte)(len >> 16));
                        mdat.WriteByte((byte)(len >> 8)); mdat.WriteByte((byte)len);
                        mdat.Write(nal, 0, len);
                        sizes.Add(4 + len);
                        if (t == 5) syncs.Add(sizes.Count);
                    }
                    // AUD/SEI: elementer akışta kalır, MP4 örneği olmaz
                }
            }

            if (sps == null || pps == null || sizes.Count == 0)
            {
                // MP4 kurulamıyor → temiz .h264 elinde kaldı, geçici mdat'ı sil
                if (File.Exists(tmp)) File.Delete(tmp);
                return false;
            }

            (int w, int h) = ParseSpsDims(sps);

            // 2) Nihai mp4: ftyp + mdat + moov
            using (var outF = new FileStream(dstPath, FileMode.Create, FileAccess.Write))
            {
                WriteFtyp(outF);

                long mdatLen = new FileInfo(tmp).Length;
                uint dataOff;
                if (8 + mdatLen > 0xFFFFFFFFL)   // 4 GB üstü → 64-bit mdat boyutu
                {
                    WriteU32(outF, 1);
                    outF.Write(new byte[] { (byte)'m', (byte)'d', (byte)'a', (byte)'t' }, 0, 4);
                    WriteU64(outF, 16 + mdatLen);
                    dataOff = 40;
                }
                else
                {
                    WriteU32(outF, (uint)(8 + mdatLen));
                    outF.Write(new byte[] { (byte)'m', (byte)'d', (byte)'a', (byte)'t' }, 0, 4);
                    dataOff = 32;
                }
                using (var mdatIn = new FileStream(tmp, FileMode.Open, FileAccess.Read))
                    mdatIn.CopyTo(outF);

                WriteMoov(outF, sps, pps, sizes, syncs, w, h, dataOff);
            }
            File.Delete(tmp);
            return true;
        }

        // Annex-B akışından NAL'leri sırayla üretir (start code 00 00 01 / 00 00 00 01)
        private static IEnumerable<byte[]> Nals(Stream s, long length, CancellationToken tok, Action<long>? onBytes = null)
        {
            byte[] buf = new byte[1 << 20];
            var cur = new List<byte>(1 << 16);
            long remaining = length;
            int zeros = 0;
            bool inNal = false;

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
                        // start code bulundu — önceki NAL = cur eksi sondaki start-code sıfırları
                        if (inNal)
                        {
                            int realLen = cur.Count - zeros;
                            if (realLen > 0) yield return cur.GetRange(0, realLen).ToArray();
                        }
                        cur.Clear();
                        inNal = true;
                        zeros = 0;
                        continue;   // 0x01 eklenmez
                    }
                    if (inNal) cur.Add(b);        // HER baytı bir kez ekle
                    zeros = (b == 0x00) ? zeros + 1 : 0;
                }
            }
            if (inNal && cur.Count > 0) yield return cur.ToArray();
        }

        // ---- MP4 kutuları ----
        private static void WriteFtyp(Stream o)
        {
            WriteU32(o, 24);
            Ascii(o, "ftyp");
            Ascii(o, "isom");
            WriteU32(o, 0x200);
            Ascii(o, "isom"); Ascii(o, "mp41");
        }

        private static void WriteMoov(Stream o, byte[] sps, byte[] pps, List<int> sizes, List<int> syncs, int w, int h, uint dataOff)
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
                        Box("vmhd", Bytes(0x00,0x00,0x00,0x01, 0,0,0,0,0,0,0,0)),
                        Box("dinf", Box("dref", Concat(U32(0), U32(1), Box("url ", Bytes(0,0,0,1))))),
                        Box("stbl",
                            Stsd(sps, pps, w, h),
                            Stts(n),
                            Stss(syncs),
                            Stsc(n),
                            Stsz(sizes),
                            Stco(dataOff)
                        )
                    )
                )
            );

            byte[] moov = Box("moov", Mvhd(movieDur), trak);
            o.Write(moov, 0, moov.Length);
        }

        private static byte[] Mvhd(long dur) => Box("mvhd", Concat(
            U32(0), U32(0), U32(0), U32(MOVIE_TS), U32((uint)dur),
            U32(0x00010000), Bytes(0x01,0x00,0,0), U32(0), U32(0),
            Matrix(), new byte[24], U32(2)));

        private static byte[] TkhdMatrix() => Matrix();

        private static byte[] Tkhd(long dur, int w, int h) => Box("tkhd", Concat(
            Bytes(0,0,0,0x07), U32(0), U32(0), U32(1), U32(0), U32((uint)dur),
            new byte[8], U32(0), U32(0), Matrix(),
            U32((uint)(w << 16)), U32((uint)(h << 16))));

        private static byte[] Mdhd(long dur) => Box("mdhd", Concat(
            U32(0), U32(0), U32(0), U32(MEDIA_TS), U32((uint)dur), Bytes(0x55,0xC4,0,0)));

        private static byte[] Hdlr() => Box("hdlr", Concat(
            U32(0), U32(0), Ascii4("vide"), U32(0), U32(0), U32(0),
            System.Text.Encoding.ASCII.GetBytes("VideoHandler\0")));

        private static byte[] Stsd(byte[] sps, byte[] pps, int w, int h)
        {
            byte[] avcC = Box("avcC", Concat(
                Bytes(0x01, sps[1], sps[2], sps[3], 0xFF, 0xE1),
                Bytes((byte)(sps.Length >> 8), (byte)sps.Length), sps,
                Bytes(0x01, (byte)(pps.Length >> 8), (byte)pps.Length), pps));

            byte[] avc1 = Box("avc1", Concat(
                new byte[6], U16(1),
                new byte[16],
                U16((ushort)w), U16((ushort)h),
                U32(0x00480000), U32(0x00480000), U32(0), U16(1),
                new byte[32], U16(0x0018), Bytes(0xFF, 0xFF),
                avcC));

            return Box("stsd", Concat(U32(0), U32(1), avc1));
        }

        private static byte[] Stts(int n) => Box("stts", Concat(U32(0), U32(1), U32((uint)n), U32((uint)SAMPLE_DUR)));

        private static byte[] Stss(List<int> syncs)
        {
            var parts = new List<byte[]> { U32(0), U32((uint)syncs.Count) };
            foreach (int s in syncs) parts.Add(U32((uint)s));
            return Box("stss", Concat(parts.ToArray()));
        }

        // TÜM kareler TEK parçada (eski samples_per_chunk=1 → sadece 1 kare adresleniyordu)
        private static byte[] Stsc(int n) => Box("stsc", Concat(U32(0), U32(1), U32(1), U32((uint)n), U32(1)));

        private static byte[] Stsz(List<int> sizes)
        {
            var parts = new List<byte[]> { U32(0), U32(0), U32((uint)sizes.Count) };
            foreach (int s in sizes) parts.Add(U32((uint)s));
            return Box("stsz", Concat(parts.ToArray()));
        }

        // Tek chunk; offset mdat verisinin başına (ftyp 24 + mdat başlık 8 = 32)
        private static byte[] Stco(uint off) => Box("stco", Concat(U32(0), U32(1), U32(off)));

        private static byte[] Matrix() => Concat(
            U32(0x00010000), U32(0), U32(0), U32(0), U32(0x00010000), U32(0),
            U32(0), U32(0), U32(0x40000000));

        // ---- SPS boyut ayrıştırma (exp-Golomb) ----
        private static (int w, int h) ParseSpsDims(byte[] sps)
        {
            try
            {
                byte[] rbsp = Unescape(sps);
                var br = new BitR(rbsp, 1); // ilk bayt NAL header, atla
                int profile = br.U(8);
                br.U(8); // constraint+reserved
                br.U(8); // level
                br.UE(); // sps_id
                if (profile == 100 || profile == 110 || profile == 122 || profile == 244 ||
                    profile == 44 || profile == 83 || profile == 86 || profile == 118 ||
                    profile == 128 || profile == 138 || profile == 139 || profile == 134)
                {
                    int chroma = br.UE();
                    if (chroma == 3) br.U(1);
                    br.UE(); br.UE(); br.U(1);
                    if (br.U(1) == 1) // scaling matrix
                        for (int i = 0; i < (chroma != 3 ? 8 : 12); i++)
                            if (br.U(1) == 1) SkipScaling(br, i < 6 ? 16 : 64);
                }
                br.UE(); // log2_max_frame_num
                int poc = br.UE();
                if (poc == 0) br.UE();
                else if (poc == 1) { br.U(1); br.SE(); br.SE(); int num = br.UE(); for (int i = 0; i < num; i++) br.SE(); }
                br.UE(); // max_num_ref
                br.U(1); // gaps
                int wMbs = br.UE() + 1;
                int hMap = br.UE() + 1;
                int frameMbsOnly = br.U(1);
                if (frameMbsOnly == 0) br.U(1);
                br.U(1); // direct_8x8
                int cl = 0, cr = 0, ct = 0, cb = 0;
                if (br.U(1) == 1) { cl = br.UE(); cr = br.UE(); ct = br.UE(); cb = br.UE(); }
                int w = wMbs * 16 - (cl + cr) * 2;
                int h = (2 - frameMbsOnly) * hMap * 16 - (ct + cb) * 2;
                if (w < 16 || w > 8192 || h < 16 || h > 8192) return (1920, 1080);
                return (w, h);
            }
            catch { return (1920, 1080); }
        }

        private static void SkipScaling(BitR br, int size)
        {
            int last = 8, next = 8;
            for (int j = 0; j < size; j++)
            {
                if (next != 0) { int d = br.SE(); next = (last + d + 256) % 256; }
                last = next == 0 ? last : next;
            }
        }

        private static byte[] Unescape(byte[] b)
        {
            var o = new List<byte>(b.Length);
            int z = 0;
            foreach (byte x in b)
            {
                if (x == 3 && z >= 2) { z = 0; continue; }   // emulation prevention
                o.Add(x);
                z = x == 0 ? z + 1 : 0;
            }
            return o.ToArray();
        }

        private sealed class BitR
        {
            private readonly byte[] _b; private int _pos;
            public BitR(byte[] b, int startByte) { _b = b; _pos = startByte * 8; }
            public int U(int n) { int v = 0; for (int i = 0; i < n; i++) { v = (v << 1) | Bit(); } return v; }
            public int UE() { int z = 0; while (Bit() == 0 && z < 32) z++; int v = 0; for (int i = 0; i < z; i++) v = (v << 1) | Bit(); return (1 << z) - 1 + v; }
            public int SE() { int k = UE(); int v = (k + 1) / 2; return (k & 1) == 1 ? v : -v; }
            private int Bit() { int by = _pos >> 3; int bit = 7 - (_pos & 7); _pos++; if (by >= _b.Length) return 0; return (_b[by] >> bit) & 1; }
        }

        // ---- yardımcılar ----
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
