using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VKM.App
{
    // Evrensel video kurtarma: ham diski/imajı tek geçişte tarar, video akışlarını
    // KENDİ İÇERİĞİNDEN bulur. Dosya sistemi / uzantı / DVR markası fark etmez.
    //   - H.264  : Annex-B start code (00 00 01) + SPS (NAL tip 7)
    //   - H.265  : Annex-B start code + VPS(32)/SPS(33) (2 baytlık NAL başlığı)
    //   - MJPEG  : ardışık JPEG kareleri (FF D8 ... FF D9)
    // Her segment Codec etiketiyle döner; kaydederken ona göre .mp4/.h265/.avi olur.
    public static class VideoCarver
    {
        private const int BUF = 8 * 1024 * 1024;
        private const int OVER = 8;
        private const long GAP = 8L * 1024 * 1024;      // bu kadar veri boyunca işaret yoksa segment bitti
        private const long MINSEG = 64 * 1024;           // çok küçük segmentleri atla
        private const long MAXSEG = 4L * 1024 * 1024 * 1024;
        private const int MJ_MINFRAMES = 12;             // MJPEG saymak için en az bu kadar kare

        public static List<CarvedFile> Scan(Func<int, Stream> openRaw, long length,
            CancellationToken tok, Action<double, int> progress)
        {
            var res = new List<CarvedFile>();
            byte[] buf = new byte[BUF];

            // H.264/H.265 akış durumu. Gerçek video, geçerli SPS'in HEMEN ARDINDAN PPS gelmesiyle
            // doğrulanır (gerçek akışta VPS→SPS→PPS ardışık; metadata sayacında araya NAL girer → elenir).
            long vStart = -1, vLast = -1;
            string vCodec = "";
            bool vConfirm = false, vHasVps = false;
            int prevType = -1;         // bir önceki NAL'in codec'e göre tipi
            bool prevSpsValid = false; // bir önceki NAL geçerli-profilli SPS miydi?

            // MJPEG akış durumu
            long mjStart = -1, mjLastEoi = -1;
            int mjFrames = 0;
            bool mjInFrame = false;

            using Stream s = openRaw(512);
            long pos = 0;
            int lastReport = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (pos < length)
            {
                if (tok.IsCancellationRequested) break;
                s.Position = pos;
                int toRead = (int)Math.Min(BUF, length - pos);
                int got = ReadFull(s, buf, toRead);
                if (got <= 0) break;

                bool atEnd = pos + got >= length;
                int scanEnd = atEnd ? got - 3 : got - OVER;
                if (scanEnd < 0) scanEnd = 0;

                for (int i = 0; i < scanEnd; i++)
                {
                    byte b0 = buf[i], b1 = buf[i + 1], b2 = buf[i + 2];

                    // --- H.264 / H.265 start code: 00 00 01 ---
                    if (b0 == 0x00 && b1 == 0x00 && b2 == 0x01)
                    {
                        long abs = pos + i;
                        byte h = buf[i + 3];
                        if ((h & 0x80) == 0)   // forbidden_zero_bit = 0
                        {
                            int t264 = h & 0x1F;
                            int t265 = (h >> 1) & 0x3F;

                            bool okH264Sps = (t264 == 7) && IsH264Profile(buf[i + 4]);
                            bool okH265Sps = (t265 == 33) && IsHevcProfile(buf[i + 6] & 0x1F);

                            if (vStart < 0)
                            {
                                // Akış ancak gerçek bir başlangıçla açılır: h264=geçerli SPS, h265=VPS
                                if (t265 == 32)     { vStart = abs; vLast = abs; vCodec = "h265"; vConfirm = false; vHasVps = true; prevType = 32; prevSpsValid = false; }
                                else if (okH264Sps) { vStart = abs; vLast = abs; vCodec = "h264"; vConfirm = false; prevType = 7; prevSpsValid = true; }
                            }
                            else
                            {
                                vLast = abs;
                                if (vCodec == "h264")
                                {
                                    if (t264 == 8 && prevType == 7 && prevSpsValid) vConfirm = true;   // geçerli SPS → PPS ardışık
                                    prevType = t264; prevSpsValid = okH264Sps;
                                }
                                else
                                {
                                    if (t265 == 32) vHasVps = true;
                                    if (t265 == 34 && prevType == 33 && prevSpsValid) vConfirm = true; // geçerli SPS → PPS ardışık
                                    prevType = t265; prevSpsValid = okH265Sps;
                                }
                            }
                        }
                        i += 2;
                        continue;
                    }

                    // --- MJPEG: SOI (FF D8 FF <geçerli JPEG işaretçisi>) / EOI (FF D9) ---
                    if (b0 == 0xFF && b1 == 0xD8 && b2 == 0xFF)
                    {
                        byte m = buf[i + 3];   // gerçek JPEG: APPn (E0..EF), DQT (DB), SOF (C0), DHT (C4)
                        bool realJpeg = (m >= 0xE0 && m <= 0xEF) || m == 0xDB || m == 0xC0 || m == 0xC4;
                        if (realJpeg)
                        {
                            long abs = pos + i;
                            if (mjStart < 0) { mjStart = abs; mjFrames = 0; }
                            mjInFrame = true;
                            i += 1;
                            continue;
                        }
                    }
                    if (b0 == 0xFF && b1 == 0xD9)
                    {
                        if (mjInFrame) { mjFrames++; mjLastEoi = pos + i + 2; mjInFrame = false; }
                        i += 1;
                        continue;
                    }
                }

                long windowEnd = pos + got;

                // H26x segmentini kapat (uzun işaretsizlik ya da aşırı büyüme)
                if (vStart >= 0 && vLast >= 0 && (windowEnd - vLast > GAP || vLast - vStart > MAXSEG))
                {
                    long len = vLast - vStart;
                    // Gerçek video: geçerli SPS→PPS ardışık (h265'te ayrıca VPS görülmüş olmalı)
                    bool ok = vConfirm && (vCodec != "h265" || vHasVps);
                    if (len >= MINSEG && ok) res.Add(new CarvedFile { Offset = vStart, Length = len, Ext = vCodec == "h265" ? "h265" : "mp4", Codec = vCodec });
                    vStart = -1; vLast = -1; vCodec = ""; vConfirm = false; vHasVps = false; prevType = -1; prevSpsValid = false;
                }

                // MJPEG segmentini kapat
                if (mjStart >= 0 && mjLastEoi >= 0 && (windowEnd - mjLastEoi > GAP || mjLastEoi - mjStart > MAXSEG))
                {
                    if (mjFrames >= MJ_MINFRAMES && mjLastEoi - mjStart >= MINSEG)
                        res.Add(new CarvedFile { Offset = mjStart, Length = mjLastEoi - mjStart, Ext = "avi", Codec = "mjpeg" });
                    mjStart = -1; mjLastEoi = -1; mjFrames = 0; mjInFrame = false;
                }

                pos += atEnd ? got : (got - OVER);

                if (sw.ElapsedMilliseconds - lastReport > 300)
                {
                    lastReport = (int)sw.ElapsedMilliseconds;
                    progress((double)pos / length, res.Count);
                }
            }

            // Kalan açık segmenti kapat
            if (vStart >= 0 && vLast > vStart && vConfirm && (vCodec != "h265" || vHasVps))
            {
                long len = vLast - vStart;
                if (len >= MINSEG) res.Add(new CarvedFile { Offset = vStart, Length = len, Ext = vCodec == "h265" ? "h265" : "mp4", Codec = vCodec });
            }
            if (mjStart >= 0 && mjLastEoi > mjStart && mjFrames >= MJ_MINFRAMES)
                res.Add(new CarvedFile { Offset = mjStart, Length = mjLastEoi - mjStart, Ext = "avi", Codec = "mjpeg" });

            res.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            progress(1.0, res.Count);
            return res;
        }

        // Geçerli H.264 profile_idc (SPS'in ikinci baytı). Metadata'daki sahte SPS'leri eler.
        private static bool IsH264Profile(byte p) =>
            p == 66 || p == 77 || p == 88 || p == 100 || p == 110 || p == 122 || p == 244 ||
            p == 44 || p == 83 || p == 86 || p == 118 || p == 128 || p == 138 || p == 139 || p == 134 || p == 135;

        // Geçerli HEVC general_profile_idc (1=Main, 2=Main10, 3=MainSP, 4=RExt)
        private static bool IsHevcProfile(int p) => p >= 1 && p <= 4;

        // --- SPS'i çöz ve MANTIKLI çözünürlük veriyor mu? Rastlantısal metadata saçma değer verir → elenir ---
        private static bool SaneH264(byte[] buf, int start, int avail)
        {
            if (avail < 12) return false;
            try
            {
                byte[] p = Unescape(buf, start, Math.Min(avail, 64));
                var br = new BitR(p);
                int profile = br.U(8); br.U(8); br.U(8); br.UE();
                if (profile == 100 || profile == 110 || profile == 122 || profile == 244 || profile == 44 ||
                    profile == 83 || profile == 86 || profile == 118 || profile == 128 || profile == 138 || profile == 139 || profile == 134)
                {
                    int chroma = br.UE();
                    if (chroma == 3) br.U(1);
                    br.UE(); br.UE(); br.U(1);
                    if (br.U(1) == 1) return false;   // scaling matrix — nadir; güvenli taraf
                }
                br.UE(); int poc = br.UE();
                if (poc == 0) br.UE();
                else if (poc == 1) { br.U(1); br.SE(); br.SE(); int n = br.UE(); for (int i = 0; i < n; i++) br.SE(); }
                br.UE(); br.U(1);
                int wMbs = br.UE() + 1; int hMap = br.UE() + 1; int fmo = br.U(1);
                if (fmo == 0) br.U(1);
                br.U(1);
                int cl = 0, cr = 0, ct = 0, cb = 0;
                if (br.U(1) == 1) { cl = br.UE(); cr = br.UE(); ct = br.UE(); cb = br.UE(); }
                int w = wMbs * 16 - (cl + cr) * 2;
                int h = (2 - fmo) * hMap * 16 - (ct + cb) * 2;
                return w >= 160 && w <= 8192 && h >= 90 && h <= 8192;
            }
            catch { return false; }
        }

        private static bool SaneH265(byte[] buf, int start, int avail)
        {
            if (avail < 16) return false;
            try
            {
                byte[] p = Unescape(buf, start, Math.Min(avail, 80));
                var br = new BitR(p);
                br.U(4); int maxSub = br.U(3); br.U(1);
                // profile_tier_level (general)
                br.U(8); br.U(32); br.U(32); br.U(12); br.U(8);
                if (maxSub > 0)
                {
                    var pf = new bool[maxSub]; var lf = new bool[maxSub];
                    for (int i = 0; i < maxSub; i++) { pf[i] = br.U(1) == 1; lf[i] = br.U(1) == 1; }
                    for (int i = maxSub; i < 8; i++) br.U(2);
                    for (int i = 0; i < maxSub; i++) { if (pf[i]) { br.U(8); br.U(32); br.U(32); br.U(12); } if (lf[i]) br.U(8); }
                }
                br.UE(); int chroma = br.UE(); if (chroma == 3) br.U(1);
                int wl = br.UE(); int hl = br.UE();
                int cl = 0, cr = 0, ct = 0, cb = 0;
                if (br.U(1) == 1) { cl = br.UE(); cr = br.UE(); ct = br.UE(); cb = br.UE(); }
                int sub = chroma == 1 ? 2 : 1;
                int w = wl - (cl + cr) * sub; int h = hl - (ct + cb) * sub;
                return w >= 160 && w <= 8192 && h >= 90 && h <= 8192;
            }
            catch { return false; }
        }

        private static byte[] Unescape(byte[] src, int off, int len)
        {
            var o = new List<byte>(len);
            int z = 0;
            for (int k = 0; k < len && (off + k) < src.Length; k++)
            {
                byte x = src[off + k];
                if (x == 3 && z >= 2) { z = 0; continue; }
                o.Add(x);
                z = x == 0 ? z + 1 : 0;
            }
            return o.ToArray();
        }

        private sealed class BitR
        {
            private readonly byte[] _b; private int _pos;
            public BitR(byte[] b) { _b = b; _pos = 0; }
            public int U(int n) { int v = 0; for (int i = 0; i < n; i++) v = (v << 1) | Bit(); return v; }
            public int UE() { int z = 0; while (Bit() == 0 && z < 32) z++; int v = 0; for (int i = 0; i < z; i++) v = (v << 1) | Bit(); return (1 << z) - 1 + v; }
            public int SE() { int k = UE(); int v = (k + 1) / 2; return (k & 1) == 1 ? v : -v; }
            private int Bit() { int by = _pos >> 3; int bit = 7 - (_pos & 7); _pos++; if (by >= _b.Length) return 0; return (_b[by] >> bit) & 1; }
        }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int r = s.Read(buf, off, count - off);
                if (r <= 0) break;
                off += r;
            }
            return off;
        }
    }
}
