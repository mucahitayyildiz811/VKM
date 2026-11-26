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
            _slowMs = slowMs; _badMs = badMs;

            // blok boyutu: ~20000 blok hedefle ama 1MB..64MB arası tut (ilerleme görünür olsun)
            long bb = Total / 20000;
            if (bb > 64L * 1024 * 1024) bb = 64L * 1024 * 1024;
            if (bb < 1L * 1024 * 1024) bb = 1L * 1024 * 1024;
            bb = (bb / Sector) * Sector;
            if (bb < Sector) bb = Sector;
            BlockBytes = bb;
            Blocks = (int)Math.Min(2_000_000, (Total + BlockBytes - 1) / BlockBytes);
            Status = new byte[Blocks];
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

        public long LbaOf(long block) => block * BlockBytes / Sector;
        public (long start, long end) RangeOf(long block)
        {
            long a = block * BlockBytes;
            long bEnd = Math.Min(Total, a + BlockBytes);
            return (a, bEnd);
        }
    }
}
