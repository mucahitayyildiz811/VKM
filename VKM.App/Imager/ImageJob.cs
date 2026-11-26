using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace VKM.App
{
    // İş (CASE): iş no + ad soyad + klasör + imaj yolu
    public class CaseInfo
    {
        public string Number = "";
        public string Name = "";
        public string Folder = "";
        public string ImagePath = "";
    }

    public struct ImageProgress
    {
        public long Done, Ok, Bad, CurBlock, CurLba;
        public double Mbps, Pct, EtaSec;
        public bool Finished;
    }

    // ELLE TAM İMAJ (Başlat): diski baştan sona okuyup paylaşımlı depoya yazar.
    // Talep üzerine okuma (ImagingStream) ile aynı depoyu/haritayı paylaşır.
    public sealed class ImageJob
    {
        private readonly Func<Stream> _open;
        private readonly ImageStore _store;
        private readonly Action<ImageProgress> _report;
        public int SpeedDelayMs;
        public bool Reverse;
        public volatile bool Paused, Stopping;
        public Action<string>? OnError;

        public ImageJob(Func<Stream> open, ImageStore store, Action<ImageProgress> report)
        { _open = open; _store = store; _report = report; }

        int Sector => _store.Sector;
        long BlockBytes => _store.BlockBytes;

        public void Run()
        {
            var sw = Stopwatch.StartNew(); var spd = Stopwatch.StartNew();
            long lastBytes = 0; double mbps = 0;
            Stream src;
            try { src = _open(); } catch (Exception ex) { OnError?.Invoke(ex.Message); Finish(); return; }
            var buf = new byte[BlockBytes];
            try
            {
                for (int idx = 0; idx < _store.Blocks && !Stopping; idx++)
                {
                    long b = Reverse ? _store.Blocks - 1 - idx : idx;
                    while (Paused && !Stopping) Thread.Sleep(80);
                    if (Stopping) break;
                    if (!_store.HeadOn[_store.HeadOf(b)]) { _store.Mark(b * BlockBytes, 1, 4); continue; }
                    if (_store.Status[b] == 1) continue;   // zaten tam okundu

                    _store.CurBlock = b;
                    long off = b * BlockBytes;
                    int len = (int)Math.Min(BlockBytes, _store.Total - off);
                    if (len <= 0) continue;
                    ReadBlock(src, off, len, buf);

                    if (SpeedDelayMs > 0) Thread.Sleep(SpeedDelayMs);
                    if (spd.ElapsedMilliseconds >= 250)
                    {
                        long now = (idx + 1L) * BlockBytes;
                        mbps = (now - lastBytes) / Math.Max(0.001, spd.Elapsed.TotalSeconds) / 1048576;
                        lastBytes = now; spd.Restart();
                        Report(idx + 1, mbps, sw);
                    }
                }
            }
            catch { }
            finally { try { src.Dispose(); } catch { } }
            Finish();
        }

        public void RetryBad()
        {
            Stream src; try { src = _open(); } catch { return; }
            var buf = new byte[BlockBytes]; var sw = Stopwatch.StartNew();
            try
            {
                for (int b = 0; b < _store.Blocks && !Stopping; b++)
                {
                    while (Paused && !Stopping) Thread.Sleep(80);
                    if (_store.Status[b] != 2 || !_store.HeadOn[_store.HeadOf(b)]) continue;
                    _store.CurBlock = b;
                    long off = (long)b * BlockBytes; int len = (int)Math.Min(BlockBytes, _store.Total - off);
                    ReadBlock(src, off, len, buf);
                    if (sw.ElapsedMilliseconds >= 250) { Report(b, 0, sw); sw.Restart(); }
                }
            }
            catch { } finally { try { src.Dispose(); } catch { } }
            Finish();
        }

        // bloğu oku → tam ise 1, kısmi ise 3, hiç ise 2; okunanı imaja yazar
        private void ReadBlock(Stream src, long off, int len, byte[] buf)
        {
            try
            {
                src.Position = off;
                int got = 0; while (got < len) { int r = src.Read(buf, got, len - got); if (r <= 0) break; got += r; }
                if (got == len) { _store.WriteRegion(off, buf, 0, len, 1); return; }
            }
            catch { }
            // sektör sektör kurtarma
            bool anyBad = false, anyGood = false;
            Array.Clear(buf, 0, len);
            for (int p = 0; p < len; p += Sector)
            {
                int sl = Math.Min(Sector, len - p);
                try
                {
                    src.Position = off + p;
                    int got = 0; while (got < sl) { int r = src.Read(buf, p + got, sl - got); if (r <= 0) break; got += r; }
                    if (got == sl) anyGood = true; else { anyBad = true; Array.Clear(buf, p + got, sl - got); }
                }
                catch { anyBad = true; Array.Clear(buf, p, sl); }
            }
            _store.WriteRegion(off, buf, 0, len, anyBad ? (byte)(anyGood ? 3 : 2) : (byte)1);
        }

        private void Report(long done, double mbps, Stopwatch sw)
        {
            var (ok, bad, _) = _store.Counts();
            double pct = done / (double)_store.Blocks * 100;
            double rate = done / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            _report(new ImageProgress { Done = done, Ok = ok, Bad = bad, CurBlock = _store.CurBlock, CurLba = _store.CurBlock >= 0 ? _store.LbaOf(_store.CurBlock) : 0, Mbps = mbps, Pct = pct, EtaSec = rate > 0 ? (_store.Blocks - done) / rate : 0 });
        }
        private void Finish()
        {
            _store.CurBlock = -1;
            var (ok, bad, _) = _store.Counts();
            _report(new ImageProgress { Done = _store.Blocks, Ok = ok, Bad = bad, Pct = 100, Finished = true });
        }
    }
}
