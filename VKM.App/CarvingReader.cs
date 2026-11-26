using System;
using System.Collections.Generic;
using System.IO;

namespace VKM.App
{
    public class CarvedRec
    {
        public int Channel;
        public string Camera = "";
        public DateTime Start, End;
        public bool HasTime;
        public long FirstOffset, LastOffset;
        public int FrameCount;
        public int Width, Height;
    }

    public class CarveInfo
    {
        public List<CarvedRec> Recordings = new();
        public bool Sampled;            // tüm disk değil, örnek tarandı
        public string Diagnostic = "";
    }

    // Bilinmeyen/MJPEG DVR için GENEL carving: formatı çözmeden kareleri çıkarır,
    // tarih/kamera'yı iki yoldan dener — (1) kare-içi unix etiketi, (2) OSD OCR.
    public static class CarvingReader
    {
        private const int CHUNK = 16 << 20;
        private static readonly DateTime Unix = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static bool DetectJpeg(Func<int, Stream> openRaw, long totalSize)
        {
            try
            {
                using Stream s = openRaw(4 << 20);
                byte[] b = new byte[4 << 20];
                foreach (long m in new long[] { 4L << 30, 16L << 30, totalSize / 3 })
                {
                    if (m <= 0 || m + b.Length > totalSize) continue;
                    s.Position = m;
                    int got = 0; while (got < b.Length) { int k = s.Read(b, got, b.Length - got); if (k <= 0) break; got += k; }
                    int c = 0;
                    for (int i = 0; i < got - 3; i++) if (b[i] == 0xFF && b[i + 1] == 0xD8 && b[i + 2] == 0xFF) c++;
                    if (c >= 15) return true;
                }
            }
            catch { }
            return false;
        }

        // Diski tara, JPEG kareleri bul, çözünürlüğe göre grupla. Liste önizlemesi için tarama sınırlıdır.
        public static CarveInfo ParseJpeg(Func<int, Stream> openRaw, long totalSize, Action<long>? onProgress = null)
        {
            var info = new CarveInfo();
            long scanCap = Math.Min(totalSize, 32L << 30);   // önizleme: ilk ~32 GB
            info.Sampled = scanCap < totalSize;

            // çözünürlük(kanal) -> toplanan kayıt
            var groups = new Dictionary<(int, int), CarvedRec>();
            long minT = ToUnix(new DateTime(2010, 1, 1)), maxT = ToUnix(new DateTime(2031, 1, 1));

            try
            {
                const int MAXJ = 2 << 20;   // en büyük makul JPEG
                using Stream s = openRaw(CHUNK);
                s.Position = 0;
                byte[] buf = new byte[CHUNK + MAXJ];
                int carry = 0; long bufStart = 0;

                while (bufStart + carry < scanCap)
                {
                    int got = 0, want = (int)Math.Min(CHUNK, scanCap - (bufStart + carry));
                    while (got < want) { int k = s.Read(buf, carry + got, want - got); if (k <= 0) break; got += k; }
                    int avail = carry + got;
                    if (avail < 4) break;

                    int i = 0;
                    while (i < avail - 4)
                    {
                        if (buf[i] == 0xFF && buf[i + 1] == 0xD8 && buf[i + 2] == 0xFF)
                        {
                            int j = IndexOf(buf, i + 3, avail, 0xFF, 0xD9);
                            if (j < 0)
                            {
                                if (avail - i > MAXJ) { i++; continue; }  // sahte SOI (2 MB'da EOI yok)
                                break;                                      // gerçek kare sınırı aşıyor → carry
                            }
                            int end = j + 2;
                            if (end - i >= 800)
                            {
                                var wh = JpegDims(buf, i, end);
                                if (wh.w >= 160 && wh.h >= 120 && wh.w <= 4096 && wh.h <= 4096)
                                {
                                    long t = NearUnix(buf, i, avail, minT, maxT);
                                    AddFrame(groups, wh.w, wh.h, bufStart + i, t);
                                }
                            }
                            i = end;
                        }
                        else i++;
                    }
                    carry = avail - i;
                    if (carry > 0) Array.Copy(buf, i, buf, 0, carry);
                    bufStart += i;
                    onProgress?.Invoke(bufStart);
                    if (got < want) break;
                }
            }
            catch (Exception ex) { info.Diagnostic = "carving hatası: " + ex.Message; }

            int ch = 1;
            foreach (var kv in groups)
            {
                var r = kv.Value;
                r.Channel = ch++;
                r.Camera = "Kanal " + r.Channel + " (" + r.Width + "x" + r.Height + ")";
                info.Recordings.Add(r);
            }
            info.Recordings.Sort((a, b) => b.FrameCount.CompareTo(a.FrameCount));
            info.Diagnostic = info.Recordings.Count + " kanal · " +
                              SumFrames(info.Recordings) + " kare" + (info.Sampled ? " (ilk 32 GB örnek)" : "");
            return info;
        }

        private static void AddFrame(Dictionary<(int, int), CarvedRec> g, int w, int h, long off, long unixT)
        {
            var key = (w, h);
            if (!g.TryGetValue(key, out var r))
            {
                r = new CarvedRec { Width = w, Height = h, FirstOffset = off, LastOffset = off };
                g[key] = r;
            }
            r.FrameCount++;
            r.LastOffset = off;
            if (unixT > 0)
            {
                var dt = FromUnix(unixT).ToLocalTime();
                if (!r.HasTime) { r.Start = dt; r.End = dt; r.HasTime = true; }
                else { if (dt < r.Start) r.Start = dt; if (dt > r.End) r.End = dt; }
            }
        }

        // Kaydın karelerini carve edip AVI'ye yaz. OSD'den kamera adı/tarih'i OCR ile dener.
        public static bool ExtractJpeg(Func<int, Stream> openRaw, CarvedRec rec, string dstPath,
                                       System.Threading.CancellationToken tok, out string diag, Action<long>? onBytes)
        {
            string outPath = Path.ChangeExtension(dstPath, ".avi");
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }
            int written = 0; bool ocrTried = false;
            try
            {
                const int MAXJ = 2 << 20;
                using Stream s = openRaw(CHUNK);
                s.Position = rec.FirstOffset;
                long end = rec.LastOffset + (2 << 20);
                byte[] buf = new byte[CHUNK + MAXJ];
                int carry = 0; long bufStart = rec.FirstOffset;
                MjpegAviWriter? avi = null;

                while (bufStart + carry < end)
                {
                    tok.ThrowIfCancellationRequested();
                    int got = 0, want = (int)Math.Min(CHUNK, end - (bufStart + carry));
                    if (want <= 0) break;
                    while (got < want) { int k = s.Read(buf, carry + got, want - got); if (k <= 0) break; got += k; }
                    int avail = carry + got;
                    int i = 0;
                    while (i < avail - 4)
                    {
                        if (buf[i] == 0xFF && buf[i + 1] == 0xD8 && buf[i + 2] == 0xFF)
                        {
                            int j = IndexOf(buf, i + 3, avail, 0xFF, 0xD9);
                            if (j < 0)
                            {
                                if (avail - i > MAXJ) { i++; continue; }
                                break;
                            }
                            int fend = j + 2;
                            if (fend - i >= 800)
                            {
                                var wh = JpegDims(buf, i, fend);
                                if (wh.w == rec.Width && wh.h == rec.Height)
                                {
                                    byte[] frame = new byte[fend - i];
                                    Array.Copy(buf, i, frame, 0, frame.Length);
                                    if (!ocrTried) { ocrTried = true; TryOsd(frame, rec); }
                                    avi ??= new MjpegAviWriter(outPath, rec.Width, rec.Height, 12);
                                    avi.AddFrame(frame);
                                    written++;
                                }
                            }
                            i = fend;
                        }
                        else i++;
                    }
                    carry = avail - i;
                    if (carry > 0) Array.Copy(buf, i, buf, 0, carry);
                    bufStart += i;
                    onBytes?.Invoke(got);
                    if (got < want) break;
                }
                avi?.Dispose();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { diag = "carving çıkarma hatası: " + ex.Message; return false; }

            if (written == 0) { try { File.Delete(outPath); } catch { } diag = "kare bulunamadı"; return false; }
            diag = written + " kare · MJPEG AVI";
            return true;
        }

        private static void TryOsd(byte[] frame, CarvedRec rec)
        {
            try
            {
                if (!OcrHelper.Available) return;
                string text = OcrHelper.TryOcr(frame);
                var dt = OcrHelper.ExtractDate(text);
                if (dt.HasValue && !rec.HasTime) { rec.Start = dt.Value; rec.End = dt.Value; rec.HasTime = true; }
                string nm = OcrHelper.ExtractCameraName(text);
                if (!string.IsNullOrEmpty(nm)) rec.Camera = nm;
            }
            catch { }
        }

        // --- yardımcılar ---
        private static int IndexOf(byte[] b, int from, int to, byte a, byte c)
        {
            for (int i = from; i < to - 1; i++) if (b[i] == a && b[i + 1] == c) return i;
            return -1;
        }

        private static (int w, int h) JpegDims(byte[] b, int start, int end)
        {
            int i = start + 2;
            while (i < end - 9)
            {
                if (b[i] != 0xFF) { i++; continue; }
                int m = b[i + 1];
                if (m == 0xC0 || m == 0xC1 || m == 0xC2 || m == 0xC3)
                {
                    int h = (b[i + 5] << 8) | b[i + 6];
                    int w = (b[i + 7] << 8) | b[i + 8];
                    return (w, h);
                }
                if (m == 0xD8 || m == 0xD9 || (m >= 0xD0 && m <= 0xD7)) { i += 2; continue; }
                if (i + 4 > end) break;
                int ln = (b[i + 2] << 8) | b[i + 3];
                i += 2 + ln;
            }
            return (0, 0);
        }

        // Karenin ±2 KB çevresinde geçerli unix zaman damgası (küçük endian) ara.
        private static long NearUnix(byte[] b, int at, int avail, long minT, long maxT)
        {
            int lo = Math.Max(0, at - 2048), hi = Math.Min(avail - 4, at + 2048);
            for (int i = lo; i < hi; i++)
            {
                long v = (uint)(b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24));
                if (v > minT && v < maxT) return v;
            }
            return 0;
        }

        private static long ToUnix(DateTime dt) => (long)(dt.ToUniversalTime() - Unix).TotalSeconds;
        private static DateTime FromUnix(long t) => Unix.AddSeconds(t);
        private static long SumFrames(List<CarvedRec> r) { long s = 0; foreach (var x in r) s += x.FrameCount; return s; }
    }
}
