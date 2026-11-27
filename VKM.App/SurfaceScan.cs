using System;
using System.Diagnostics;
using System.IO;

namespace VKM.App
{
    // Salt-okunur yüzey taraması: diski bloklara böler, her bloğun okuma süresini ölçer,
    // iyi / yavaş / bad olarak sınıflar. Bad'in NEREDE kümelendiğini haritada gösterir.
    public class SurfaceScan
    {
        public readonly byte[] Status;        // 0 bekliyor, 1 iyi, 2 yavaş, 3 bad
        public readonly int Blocks;
        public readonly long Total, BlockBytes;
        public readonly int Sector;
        public long Cur = -1;
        public volatile bool Stopping;
        public int OkCount, SlowCount, BadCount, Done;

        readonly Func<Stream> _open;
        readonly int _slowMs, _badMs;

        public SurfaceScan(Func<Stream> open, long total, int sector, int slowMs = 60, int badMs = 800)
        {
            _open = open; Total = total; Sector = sector < 512 ? 512 : sector;

            // blok boyutu: ~20000 blok hedefle ama 1MB..64MB arası tut (ilerleme görünür olsun)
            long bb = Total / 20000;
            if (bb > 64L * 1024 * 1024) bb = 64L * 1024 * 1024;
            if (bb < 1L * 1024 * 1024) bb = 1L * 1024 * 1024;
            bb = (bb / Sector) * Sector;
            if (bb < Sector) bb = Sector;
            BlockBytes = bb;
            Blocks = (int)Math.Min(2_000_000, (Total + BlockBytes - 1) / BlockBytes);
            Status = new byte[Blocks];

            // eşikleri blok boyutuna göre ölçekle — yoksa büyük blokta her okuma "yavaş" görünür.
            // sağlıklı HDD ~50-150 MB/s → ~7-20 ms/MB; yavaş = belirgin üstü, bad = çok üstü/okunamaz.
            double mb = BlockBytes / 1048576.0;
            _slowMs = (int)Math.Max(slowMs, mb * 40);
            _badMs = (int)Math.Max(badMs, mb * 300);
        }

        public void Run(Action report)
        {
            Stream? s = null;
            try { s = _open(); Log.Ok("Yüzey: disk açıldı, okunuyor…"); }
            catch (Exception ex) { Log.Bad("Yüzey: disk AÇILAMADI — " + ex.Message); for (int i = 0; i < Blocks; i++) Status[i] = 3; BadCount = Blocks; Done = Blocks; Cur = -1; report(); return; }

            var buf = new byte[BlockBytes];
            var sw = new Stopwatch();
            try
            {
                for (long b = 0; b < Blocks && !Stopping; b++)
                {
                    Cur = b;
                    long off = b * BlockBytes;
                    int len = (int)Math.Min(BlockBytes, Total - off);
                    if (len <= 0) { Status[b] = 1; Done++; continue; }

                    byte st;
                    try
                    {
                        s.Seek(off, SeekOrigin.Begin);
                        sw.Restart();
                        int read = 0;
                        while (read < len)
                        {
                            int n = s.Read(buf, read, len - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        sw.Stop();
                        long ms = sw.ElapsedMilliseconds;
                        st = read < len ? (byte)3 : ms >= _badMs ? (byte)3 : ms >= _slowMs ? (byte)2 : (byte)1;
                    }
                    catch { st = 3; }

                    Status[b] = st;
                    if (st == 1) OkCount++; else if (st == 2) SlowCount++; else BadCount++;
                    Done++;
                    if (b == 0) Log.Ok("Yüzey: ilk blok okundu (çalışıyor).");
                    else if ((b & 0x3FFF) == 0) Log.Info("Yüzey: " + Done.ToString("N0") + "/" + Blocks.ToString("N0") + " blok");
                    report();
                }
            }
            finally { try { s?.Dispose(); } catch { } }
            Cur = -1;
            report();
        }

        // HIZLI ÖRNEKLEME: tam tarama yerine ~24 noktadan 1 MB okur (saniyeler). Bad NEREDE, kabaca söyler.
        public static (int bad, int slow, int total, string where) QuickSample(Func<Stream> open, long total, int sector)
        {
            const int points = 24;
            if (sector < 512) sector = 512;
            long chunk = 1L * 1024 * 1024;
            if (total < chunk * 2) chunk = Math.Max(sector, total / 8 / sector * sector);
            int bad = 0, slow = 0;
            var badFrac = new System.Collections.Generic.List<double>();
            Stream? s = null;
            try { s = open(); } catch { return (points, 0, points, "disk açılamadı"); }
            var buf = new byte[chunk];
            int sampled = 0;
            try
            {
                for (int i = 0; i < points; i++)
                {
                    sampled = i + 1;
                    double frac = points == 1 ? 0 : (double)i / (points - 1);
                    long off = (long)(frac * (total - chunk)); off = (off / sector) * sector; if (off < 0) off = 0;
                    try
                    {
                        s.Seek(off, SeekOrigin.Begin);
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        int read = 0; while (read < buf.Length) { int n = s.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
                        sw.Stop();
                        if (read < buf.Length || sw.ElapsedMilliseconds >= 1500) { bad++; badFrac.Add(frac); }
                        else if (sw.ElapsedMilliseconds >= 80) slow++;
                    }
                    catch { bad++; badFrac.Add(frac); }
                    if (bad >= 12 && i >= 12) break;   // yaygın bad → disk ölüyor, boşuna bekleme
                }
            }
            finally { try { s.Dispose(); } catch { } }

            string where = "yok";
            if (badFrac.Count > 0)
            {
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var f in badFrac) { if (f < lo) lo = f; if (f > hi) hi = f; }
                where = lo >= 0.65 ? "SONDA" : hi <= 0.35 ? "BAŞTA" : (hi - lo) <= 0.35 ? "ORTADA (kümeli)" : "DAĞINIK";
            }
            return (bad, slow, sampled > 0 ? sampled : points, where);
        }

        public long LbaOf(long block) => block * BlockBytes / Sector;
        public (long start, long end) RangeOf(long block)
        {
            long a = block * BlockBytes;
            long bEnd = Math.Min(Total, a + BlockBytes);
            return (a, bEnd);
        }
    }
}
