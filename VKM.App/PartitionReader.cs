using System;
using System.Collections.Generic;
using System.IO;

namespace VKM.App
{
    // Diskte bir bölüm (partition): başlangıç baytı ve uzunluğu
    public class DiskPartition
    {
        public string Name = "";
        public long Offset;
        public long Length;
    }

    // GPT ve MBR bölüm tablolarını KENDİMİZ okur (DiscUtils'in okuyucusunu kullanmaz).
    public static class PartitionReader
    {
        public static List<DiskPartition> Read(Stream disk, int ss)
        {
            var list = new List<DiskPartition>();

            byte[] mbr = ReadAt(disk, 0, 512);
            if (mbr.Length < 512) return list;

            // GPT mi? Ya koruyucu MBR (0xEE) ya da LBA1'de "EFI PART" imzası
            bool gpt = false;
            for (int i = 0; i < 4; i++)
                if (mbr[0x1BE + i * 16 + 4] == 0xEE) { gpt = true; break; }

            byte[] hdr = ReadAt(disk, (long)ss, Math.Max(512, ss));
            if (hdr.Length >= 8 &&
                hdr[0] == (byte)'E' && hdr[1] == (byte)'F' && hdr[2] == (byte)'I' && hdr[3] == (byte)' ' &&
                hdr[4] == (byte)'P' && hdr[5] == (byte)'A' && hdr[6] == (byte)'R' && hdr[7] == (byte)'T')
                gpt = true;

            if (gpt && hdr.Length >= 0x58)
            {
                long entLba = (long)U64(hdr, 0x48);
                int numEnt = (int)U32(hdr, 0x50);
                int entSize = (int)U32(hdr, 0x54);
                if (numEnt <= 0 || numEnt > 8192) numEnt = 128;
                if (entSize < 128 || entSize > 4096) entSize = 128;

                long entOff = entLba * ss;
                long total = (long)numEnt * entSize;
                if (total > 2_000_000) total = 2_000_000;

                byte[] ents = ReadAt(disk, entOff, (int)total);
                int n = 1;
                for (int i = 0; i + entSize <= ents.Length; i += entSize)
                {
                    bool empty = true;
                    for (int k = 0; k < 16; k++) if (ents[i + k] != 0) { empty = false; break; }
                    if (empty) continue;

                    long first = (long)U64(ents, i + 0x20);
                    long last = (long)U64(ents, i + 0x28);
                    if (last < first) continue;

                    list.Add(new DiskPartition
                    {
                        Name = "Bölüm " + n + " (GPT)",
                        Offset = first * ss,
                        Length = (last - first + 1) * ss
                    });
                    n++;
                }
            }
            else
            {
                // MBR
                int n = 1;
                for (int i = 0; i < 4; i++)
                {
                    int e = 0x1BE + i * 16;
                    byte type = mbr[e + 4];
                    if (type == 0 || type == 0xEE) continue;
                    long start = U32(mbr, e + 0x08);
                    long cnt = U32(mbr, e + 0x0C);
                    if (cnt == 0) continue;

                    list.Add(new DiskPartition
                    {
                        Name = "Bölüm " + n + " (MBR)",
                        Offset = start * (long)ss,
                        Length = cnt * (long)ss
                    });
                    n++;
                }
            }

            return list;
        }

        private static byte[] ReadAt(Stream s, long pos, int count)
        {
            s.Position = pos;
            byte[] buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int r = s.Read(buf, got, count - got);
                if (r <= 0) break;
                got += r;
            }
            if (got == count) return buf;
            byte[] t = new byte[got];
            Array.Copy(buf, t, got);
            return t;
        }

        private static uint U32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static ulong U64(byte[] b, int o)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)b[o + i] << (8 * i);
            return v;
        }
    }
}
