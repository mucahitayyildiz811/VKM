using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VKM.App
{
    // H.265 ve MJPEG kurtarma çıkışları.
    //   H.265 → temiz Annex-B elementer .h265 (VLC doğrudan oynatır; HEVC MP4 kabı sonra)
    //   MJPEG → standart AVI (MJPG) kabı
    public static class VideoSave
    {
        private static readonly byte[] StartCode = { 0, 0, 0, 1 };

        // Geçerli HEVC NAL tipi mi? (Hikvision/DVR özel başlık çöpünü eler)
        private static bool ValidHevc(byte header)
        {
            if ((header & 0x80) != 0) return false;          // forbidden_zero_bit
            int t = (header >> 1) & 0x3F;
            return t <= 40;                                   // 0..40 geçerli HEVC NAL tipleri
        }

        // [offset,length] bölgesini tarar, doğrulanmış HEVC NAL'lerini start code ile .h265 yazar.
        public static bool SaveH265(Func<int, Stream> openRaw, long offset, long length, string dstPath, CancellationToken tok, Action<long>? onBytes = null)
        {
            string h265 = Path.ChangeExtension(dstPath, ".h265");
            bool any = false, sawParam = false;
            using (Stream src = openRaw(512))
            using (var outF = new FileStream(h265, FileMode.Create, FileAccess.Write))
            {
                src.Position = offset;
                foreach (byte[] nal in Nals(src, length, tok, onBytes))
                {
                    if (nal.Length < 2 || !ValidHevc(nal[0])) continue;
                    int t = (nal[0] >> 1) & 0x3F;
                    if (t == 32 || t == 33 || t == 34) sawParam = true;   // VPS/SPS/PPS
                    outF.Write(StartCode, 0, 4);
                    outF.Write(nal, 0, nal.Length);
                    any = true;
                }
            }
            if (!any || !sawParam) { try { if (File.Exists(h265)) File.Delete(h265); } catch { } return false; }
            return true;
        }

        // H.264 geçerli NAL tipi mi? (1 slice..23; Hikvision/metadata çöpü elenir)
        private static bool ValidH264(byte b0)
        {
            if ((b0 & 0x80) != 0) return false;
            int t = b0 & 0x1F;
            return t >= 1 && t <= 23;
        }

        // [offset,length] bölgesini tarar, doğrulanmış H.264 NAL'lerini start code ile .h264 yazar.
        public static bool SaveH264(Func<int, Stream> openRaw, long offset, long length, string dstPath, CancellationToken tok, Action<long>? onBytes = null)
        {
            string h264 = Path.ChangeExtension(dstPath, ".h264");
            bool any = false, sawSps = false;
            using (Stream src = openRaw(512))
            using (var outF = new FileStream(h264, FileMode.Create, FileAccess.Write))
            {
                src.Position = offset;
                foreach (byte[] nal in Nals(src, length, tok, onBytes))
                {
                    if (nal.Length < 1 || !ValidH264(nal[0])) continue;
                    if ((nal[0] & 0x1F) == 7) sawSps = true;
                    outF.Write(StartCode, 0, 4);
                    outF.Write(nal, 0, nal.Length);
                    any = true;
                }
            }
            if (!any || !sawSps) { try { if (File.Exists(h264)) File.Delete(h264); } catch { } return false; }
            return true;
        }

        // Annex-B akışından NAL'leri üretir (00 00 01 / 00 00 00 01). H.264 ve H.265 için ortak.
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

        // ---- MJPEG → AVI (MJPG) ----
        // [offset,length] bölgesindeki ardışık JPEG karelerini (FFD8..FFD9) AVI kabına sarar.
        public static bool SaveMjpegAvi(Func<int, Stream> openRaw, long offset, long length, string dstPath, CancellationToken tok, Action<long>? onBytes = null, int fps = 15)
        {
            string tmp = dstPath + ".movi.tmp";
            var idx = new List<(uint off, uint len)>();   // movi-içi ofset + uzunluk (idx1 için)
            int w = 0, h = 0;
            long moviData = 0;

            using (Stream src = openRaw(512))
            using (var movi = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                src.Position = offset;
                byte[] rbuf = new byte[1 << 20];
                var frame = new List<byte>(1 << 18);
                long remaining = length;
                bool inFrame = false;
                byte prev = 0;

                while (remaining > 0 && !tok.IsCancellationRequested)
                {
                    int want = (int)Math.Min(rbuf.Length, remaining);
                    int got = src.Read(rbuf, 0, want);
                    if (got <= 0) break;
                    remaining -= got;
                    onBytes?.Invoke(got);

                    for (int i = 0; i < got; i++)
                    {
                        byte b = rbuf[i];
                        if (!inFrame)
                        {
                            if (prev == 0xFF && b == 0xD8) { inFrame = true; frame.Clear(); frame.Add(0xFF); frame.Add(0xD8); }
                        }
                        else
                        {
                            frame.Add(b);
                            if (prev == 0xFF && b == 0xD9)
                            {
                                byte[] jpg = frame.ToArray();
                                if (w == 0) ReadJpegDims(jpg, ref w, ref h);
                                WriteChunk(movi, jpg, idx, ref moviData);
                                inFrame = false;
                            }
                        }
                        prev = b;
                    }
                }
            }

            if (idx.Count == 0) { try { File.Delete(tmp); } catch { } return false; }
            if (w <= 0 || h <= 0) { w = 704; h = 576; }   // tipik DVR çözünürlüğü

            WriteAvi(dstPath, tmp, idx, w, h, moviData, fps);
            try { File.Delete(tmp); } catch { }
            return true;
        }

        private static void WriteChunk(FileStream movi, byte[] jpg, List<(uint, uint)> idx, ref long moviData)
        {
            uint chunkOff = (uint)(moviData + 4);          // '00dc' ckid'in movi başına göre ofseti (ilk kare = 4)
            Ascii(movi, "00dc");
            WriteU32(movi, (uint)jpg.Length);
            movi.Write(jpg, 0, jpg.Length);
            long written = 8 + jpg.Length;
            if ((jpg.Length & 1) == 1) { movi.WriteByte(0); written++; }   // word hizası
            idx.Add((chunkOff, (uint)jpg.Length));
            moviData += written;
        }

        private static void WriteAvi(string dstPath, string moviTmp, List<(uint off, uint len)> idx, int w, int h, long moviData, int fps = 15)
        {
            if (fps < 1 || fps > 120) fps = 15;
            uint maxFrame = 0;
            foreach (var e in idx) if (e.len > maxFrame) maxFrame = e.len;

            using var o = new FileStream(dstPath, FileMode.Create, FileAccess.Write);

            long idx1Size = idx.Count * 16;
            long moviListSize = 4 + moviData;                  // 'movi' + veriler
            long hdrlSize = 4 + (8 + 56) + (8 + (4 + (8 + 56) + (8 + 40)));
            long riffSize = 4 + (8 + hdrlSize) + (8 + moviListSize) + (8 + idx1Size);

            Ascii(o, "RIFF"); WriteU32(o, (uint)riffSize); Ascii(o, "AVI ");

            // LIST hdrl
            Ascii(o, "LIST"); WriteU32(o, (uint)hdrlSize); Ascii(o, "hdrl");
            // avih
            Ascii(o, "avih"); WriteU32(o, 56);
            WriteU32(o, (uint)(1000000 / fps));   // dwMicroSecPerFrame
            WriteU32(o, 0); WriteU32(o, 0);
            WriteU32(o, 0x10);                     // AVIF_HASINDEX
            WriteU32(o, (uint)idx.Count);          // dwTotalFrames
            WriteU32(o, 0); WriteU32(o, 1);        // dwStreams = 1
            WriteU32(o, maxFrame);
            WriteU32(o, (uint)w); WriteU32(o, (uint)h);
            WriteU32(o, 0); WriteU32(o, 0); WriteU32(o, 0); WriteU32(o, 0);

            // LIST strl
            long strlSize = 4 + (8 + 56) + (8 + 40);
            Ascii(o, "LIST"); WriteU32(o, (uint)strlSize); Ascii(o, "strl");
            // strh
            Ascii(o, "strh"); WriteU32(o, 56);
            Ascii(o, "vids"); Ascii(o, "MJPG");
            WriteU32(o, 0); WriteU16(o, 0); WriteU16(o, 0);
            WriteU32(o, 0);
            WriteU32(o, 1);                        // dwScale
            WriteU32(o, (uint)fps);                // dwRate → fps
            WriteU32(o, 0);
            WriteU32(o, (uint)idx.Count);          // dwLength
            WriteU32(o, maxFrame);
            WriteU32(o, 0xFFFFFFFF);               // dwQuality
            WriteU32(o, 0);
            WriteU16(o, 0); WriteU16(o, 0); WriteU16(o, (ushort)w); WriteU16(o, (ushort)h);
            // strf (BITMAPINFOHEADER)
            Ascii(o, "strf"); WriteU32(o, 40);
            WriteU32(o, 40);
            WriteU32(o, (uint)w); WriteU32(o, (uint)h);
            WriteU16(o, 1); WriteU16(o, 24);
            Ascii(o, "MJPG");
            WriteU32(o, (uint)(w * h * 3));
            WriteU32(o, 0); WriteU32(o, 0); WriteU32(o, 0); WriteU32(o, 0);

            // LIST movi
            Ascii(o, "LIST"); WriteU32(o, (uint)moviListSize); Ascii(o, "movi");
            using (var mi = new FileStream(moviTmp, FileMode.Open, FileAccess.Read)) mi.CopyTo(o);

            // idx1
            Ascii(o, "idx1"); WriteU32(o, (uint)idx1Size);
            foreach (var e in idx)
            {
                Ascii(o, "00dc");
                WriteU32(o, 0x10);        // AVIIF_KEYFRAME
                WriteU32(o, e.off);
                WriteU32(o, e.len);
            }
        }

        // JPEG SOF0/SOF2'den genişlik/yükseklik
        private static void ReadJpegDims(byte[] j, ref int w, ref int h)
        {
            try
            {
                for (int i = 2; i + 9 < j.Length;)
                {
                    if (j[i] != 0xFF) { i++; continue; }
                    byte m = j[i + 1];
                    if (m == 0xD8 || m == 0xD9 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                    int seg = (j[i + 2] << 8) | j[i + 3];
                    if (m == 0xC0 || m == 0xC1 || m == 0xC2 || m == 0xC3)
                    {
                        h = (j[i + 5] << 8) | j[i + 6];
                        w = (j[i + 7] << 8) | j[i + 8];
                        return;
                    }
                    i += 2 + seg;
                }
            }
            catch { }
        }

        private static void Ascii(Stream o, string s) { foreach (char c in s) o.WriteByte((byte)c); }
        private static void WriteU32(Stream o, uint v) { o.WriteByte((byte)v); o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)(v >> 16)); o.WriteByte((byte)(v >> 24)); }
        private static void WriteU16(Stream o, ushort v) { o.WriteByte((byte)v); o.WriteByte((byte)(v >> 8)); }
    }
}
