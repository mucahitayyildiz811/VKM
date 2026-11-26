using System;
using System.IO;

namespace VKM.App
{
    // Diske YAZAN onarım işlemleri (onaylı çağrılır).
    public static class DiskRepair
    {
        // Refresh: her bloğu oku → AYNI veriyi geri yaz. Zayıf/bekleyen sektörleri
        // diske yeniden değerlendirtir → Pending (197) temizlenir, gerekirse remap olur.
        // Veri KAYBI YOK (aynı bayt geri yazılır). Bağlı/kilitli bölge varsa o blok atlanır.
        public static void Refresh(string deviceId, long total, int sector,
                                   Action<long, long, long, long> report,
                                   Func<bool> cancel)
        {
            if (sector < 512) sector = 512;
            long bb = 1L * 1024 * 1024;                 // 1 MB blok
            bb = (bb / sector) * sector; if (bb < sector) bb = sector;
            long blocks = (total + bb - 1) / bb;

            using var fs = PhysicalDisk.OpenReadWrite(deviceId);
            var buf = new byte[bb];
            long written = 0, failed = 0, done = 0;

            for (long b = 0; b < blocks; b++)
            {
                if (cancel()) break;
                long off = b * bb;
                int len = (int)Math.Min(bb, total - off);
                len = (len / sector) * sector;
                if (len <= 0) { done++; continue; }

                try
                {
                    fs.Seek(off, SeekOrigin.Begin);
                    int read = 0;
                    while (read < len)
                    {
                        int n = fs.Read(buf, read, len - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read == len)
                    {
                        fs.Seek(off, SeekOrigin.Begin);
                        fs.Write(buf, 0, len);
                        written++;
                    }
                    else failed++;
                }
                catch { failed++; }

                done++;
                if ((done & 0x1F) == 0) report(written, failed, done, blocks);   // ~32 blokta bir
            }
            try { fs.Flush(); } catch { }
            report(written, failed, done, blocks);
        }
    }
}
