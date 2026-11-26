using System;
using System.Collections.Generic;
using System.IO;

namespace VKM.App
{
    public class SectorMap
    {
        public int W;
        public int H;
        public byte[] Usage = Array.Empty<byte>();  // hücre başına doluluk 0-255
        public int ClusterSize = 4096;
        public long TotalClusters;
        public long UsedClusters;
    }

    // NTFS $Bitmap'i (küme kullanım haritası) okuyup W×H hücreye indirger.
    public static class NtfsBitmapReader
    {
        private static readonly byte[] POP = new byte[256];
        static NtfsBitmapReader()
        {
            for (int i = 0; i < 256; i++)
            {
                int c = 0, v = i;
                while (v != 0) { c += v & 1; v >>= 1; }
                POP[i] = (byte)c;
            }
        }

        // Sadece geometri: küme boyutu ve toplam küme (dosya haritası için)
        public static (int clusterSize, long totalClusters) ReadGeometry(Stream vol)
        {
            byte[] boot = ReadAt(vol, 0, 512);
            if (boot.Length < 512) return (4096, 0);
            int bps = U16(boot, 0x0B);
            int spc = boot[0x0D];
            if (bps <= 0) bps = 512;
            if (spc <= 0) spc = 8;
            int cluster = bps * spc;
            long totalSectors = (long)U64(boot, 0x28);
            long tc = spc > 0 ? totalSectors / spc : 0;
            return (cluster, tc);
        }

        public static SectorMap Build(Stream vol, int w, int h, Action<double>? progress)
        {
            var map = new SectorMap { W = w, H = h, Usage = new byte[w * h] };
            int cells = w * h;

            byte[] boot = ReadAt(vol, 0, 512);
            if (boot.Length < 512) return map;

            int bps = U16(boot, 0x0B);
            int spc = boot[0x0D];
            if (bps <= 0) bps = 512;
            if (spc <= 0) spc = 8;
            int cluster = bps * spc;
            long totalSectors = (long)U64(boot, 0x28);
            long totalClusters = spc > 0 ? totalSectors / spc : 0;
            long mftLcn = (long)U64(boot, 0x30);
            sbyte rb = unchecked((sbyte)boot[0x40]);
            int recSize = rb >= 0 ? rb * cluster : 1 << (-rb);
            if (recSize <= 0 || recSize > 65536) recSize = 1024;

            map.ClusterSize = cluster;
            map.TotalClusters = totalClusters;
            if (totalClusters <= 0) return map;

            // $Bitmap = MFT kaydı 6 (ilk MFT aralığı bitişik olduğundan konum güvenli)
            byte[] rec = ReadAt(vol, mftLcn * cluster + 6 * recSize, recSize);
            ApplyFixup(rec, bps);
            List<DataRun> runs = ParseDataRuns(rec);
            if (runs.Count == 0) return map;

            long[] used = new long[cells];
            long[] tot = new long[cells];
            long cl = 0;                 // şu anki küme indeksi
            long totalUsed = 0;
            byte[] buf = new byte[1 << 20];

            foreach (DataRun run in runs)
            {
                if (cl >= totalClusters) break;
                long runBytes = run.Clusters * cluster;

                if (run.Sparse)
                {
                    long bits = runBytes * 8;   // sparse = boş kümeler
                    DistributeFree(tot, cells, totalClusters, ref cl, bits);
                    continue;
                }

                vol.Position = run.Lcn * cluster;
                long r = 0;
                while (r < runBytes && cl < totalClusters)
                {
                    int chunk = (int)Math.Min(buf.Length, runBytes - r);
                    int got = ReadFull(vol, buf, chunk);
                    if (got <= 0) break;

                    for (int bi = 0; bi < got && cl < totalClusters; bi++)
                    {
                        byte b = buf[bi];
                        int cell = (int)(cl * (long)cells / totalClusters);
                        if (cell >= cells) cell = cells - 1;
                        int u = POP[b];
                        used[cell] += u;
                        tot[cell] += 8;
                        totalUsed += u;
                        cl += 8;   // bir bayt = 8 küme
                    }
                    r += got;
                    progress?.Invoke((double)cl / totalClusters);
                }
            }

            for (int i = 0; i < cells; i++)
                map.Usage[i] = tot[i] > 0 ? (byte)(255L * used[i] / tot[i]) : (byte)0;

            map.UsedClusters = totalUsed;
            return map;
        }

        private static void DistributeFree(long[] tot, int cells, long totalClusters, ref long cl, long bits)
        {
            while (bits > 0 && cl < totalClusters)
            {
                int cell = (int)(cl * (long)cells / totalClusters);
                if (cell >= cells) cell = cells - 1;
                long cellEnd = (long)(cell + 1) * totalClusters / cells;
                long step = Math.Min(bits, cellEnd - cl);
                if (step <= 0) step = 1;
                tot[cell] += step;
                cl += step;
                bits -= step;
            }
        }

        private static List<DataRun> ParseDataRuns(byte[] rec)
        {
            var empty = new List<DataRun>();
            if (rec.Length < 0x30) return empty;
            int attrOff = U16(rec, 0x14);
            int pos = attrOff;
            while (pos + 8 <= rec.Length)
            {
                uint type = U32(rec, pos);
                if (type == 0xFFFFFFFF) break;
                int len = (int)U32(rec, pos + 4);
                if (len <= 0 || pos + len > rec.Length) break;
                if (type == 0x80 && rec[pos + 9] == 0 && rec[pos + 8] != 0)
                {
                    int runOff = U16(rec, pos + 0x20);
                    return ParseRunList(rec, pos + runOff, pos + len);
                }
                pos += len;
            }
            return empty;
        }

        private static List<DataRun> ParseRunList(byte[] rec, int start, int end)
        {
            var runs = new List<DataRun>();
            int p = start;
            long prev = 0;
            while (p < end && p < rec.Length)
            {
                byte header = rec[p++];
                if (header == 0) break;
                int lenB = header & 0x0F;
                int offB = (header >> 4) & 0x0F;
                if (lenB == 0 || p + lenB + offB > rec.Length) break;

                long length = 0;
                for (int i = 0; i < lenB; i++) length |= (long)rec[p + i] << (8 * i);
                p += lenB;

                if (offB == 0) { runs.Add(new DataRun { Sparse = true, Clusters = length }); continue; }

                long off = 0;
                for (int i = 0; i < offB; i++) off |= (long)rec[p + i] << (8 * i);
                long signBit = 1L << (offB * 8 - 1);
                if ((off & signBit) != 0) off -= (1L << (offB * 8));
                p += offB;

                prev += off;
                runs.Add(new DataRun { Lcn = prev, Clusters = length });
            }
            return runs;
        }

        private static void ApplyFixup(byte[] rec, int bps)
        {
            int usaOff = U16(rec, 0x04);
            int usaCount = U16(rec, 0x06);
            if (usaCount <= 0 || usaOff <= 0 || usaOff + usaCount * 2 > rec.Length) return;
            for (int i = 1; i < usaCount; i++)
            {
                int end = i * bps - 2;
                if (end + 2 > rec.Length) break;
                rec[end] = rec[usaOff + i * 2];
                rec[end + 1] = rec[usaOff + i * 2 + 1];
            }
        }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int r = s.Read(buf, got, count - got);
                if (r <= 0) break;
                got += r;
            }
            return got;
        }

        private static byte[] ReadAt(Stream s, long pos, int count)
        {
            s.Position = pos;
            byte[] buf = new byte[count];
            int got = ReadFull(s, buf, count);
            if (got == count) return buf;
            byte[] t = new byte[got];
            Array.Copy(buf, t, got);
            return t;
        }

        private static int U16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
        private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        private static ulong U64(byte[] b, int o)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)b[o + i] << (8 * i);
            return v;
        }
    }
}
