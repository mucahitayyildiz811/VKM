using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VKM.App
{
    // NTFS bölümünün MFT'sini ham olarak tarar, silinmiş dosyaları çıkarır.
    // DiscUtils silinmişleri vermediği için kendi ayrıştırıcımız.
    public class NtfsDeletedScanner
    {
        private int _bytesPerSector;
        private int _clusterSize;
        private int _recordSize;
        // index → (ad, ebeveyn): yol yeniden kurmak için tüm kayıtların adı (dizin/kullanımda olanlar dâhil)
        private readonly Dictionary<long, (string name, long parent)> _nodes = new();

        public ScanResult Scan(Stream part)
        {
            var result = new ScanResult();
            _nodes.Clear();

            // --- Boot sektörü (BPB) ---
            byte[] boot = ReadAt(part, 0, 512);
            if (boot.Length < 512) { result.Info = "Boot sektörü okunamadı"; return result; }

            _bytesPerSector = U16(boot, 0x0B);
            int sectorsPerCluster = boot[0x0D];
            if (_bytesPerSector <= 0) _bytesPerSector = 512;
            if (sectorsPerCluster <= 0) sectorsPerCluster = 8;
            _clusterSize = _bytesPerSector * sectorsPerCluster;

            long mftLcn = (long)U64(boot, 0x30);

            sbyte recByte = unchecked((sbyte)boot[0x40]);
            _recordSize = recByte >= 0 ? recByte * _clusterSize : 1 << (-recByte);
            if (_recordSize <= 0 || _recordSize > 65536) _recordSize = 1024;

            result.ClusterSize = _clusterSize;

            // --- MFT'nin kendi kaydı (record 0) ile tüm MFT aralıklarını bul ---
            long mftOffset = mftLcn * _clusterSize;
            byte[] rec0 = ReadAt(part, mftOffset, _recordSize);
            ApplyFixup(rec0);

            List<DataRun> mftRuns = ParseMftDataRuns(rec0);
            if (mftRuns.Count == 0)
            {
                // MFT aralıkları bulunamadı: bitişik varsay, sınırlı sayıda oku
                mftRuns.Add(new DataRun { Lcn = mftLcn, Clusters = 100000, Sparse = false });
            }

            // --- Tüm MFT kayıtlarını dolaş ---
            long index = 0;
            foreach (DataRun run in mftRuns)
            {
                if (run.Sparse) { index += (run.Clusters * _clusterSize) / _recordSize; continue; }

                long runBytes = run.Clusters * _clusterSize;
                long basePos = run.Lcn * _clusterSize;

                long done = 0;
                const int blockMax = 4 * 1024 * 1024;
                while (done < runBytes)
                {
                    int block = (int)Math.Min(blockMax, runBytes - done);
                    block -= block % _recordSize;
                    if (block <= 0) break;

                    byte[] buf = ReadAt(part, basePos + done, block);
                    if (buf.Length < _recordSize) break;

                    for (int off = 0; off + _recordSize <= buf.Length; off += _recordSize, index++)
                    {
                        try
                        {
                            byte[] rec = new byte[_recordSize];
                            Array.Copy(buf, off, rec, 0, _recordSize);
                            var mf = ParseRecord(rec, index);
                            if (mf != null) result.Files.Add(mf);
                        }
                        catch { /* bozuk kayıt, atla */ }

                        if (result.Files.Count > 2_000_000) break;
                    }
                    done += block;
                }
            }

            // Tüm kayıtların adı/ebeveyni toplandı; şimdi silinmişlerin tam klasör yolunu kur
            foreach (MftFile mf in result.Files)
            {
                (string path, bool orphan) = BuildPath(mf.ParentIndex);
                mf.FolderPath = path;
                mf.Orphan = orphan;
            }

            result.Info = result.Files.Count + " silinmiş dosya";
            return result;
        }

        // Ebeveyn zincirini yukarı yürüyerek klasör yolunu kur. Ebeveyn kayıpsa orphan=true.
        private (string path, bool orphan) BuildPath(long parentIndex)
        {
            var parts = new List<string>();
            long cur = parentIndex;
            int guard = 0;
            while (cur != 5 && cur != 0 && guard++ < 256)
            {
                if (!_nodes.TryGetValue(cur, out (string name, long parent) node))
                    return ("[Lost&Found]", true);   // ebeveyn kaydı yeniden kullanılmış/yok
                parts.Insert(0, node.name);
                cur = node.parent;
            }
            return (string.Join("\\", parts), false);
        }

        // Tek bir MFT kaydını ayrıştır; silinmiş DOSYA ise MftFile döner, değilse null
        private MftFile? ParseRecord(byte[] rec, long index)
        {
            if (rec.Length < 0x30) return null;
            if (rec[0] != (byte)'F' || rec[1] != (byte)'I' || rec[2] != (byte)'L' || rec[3] != (byte)'E')
                return null;

            ApplyFixup(rec);

            int flags = U16(rec, 0x16);
            bool inUse = (flags & 0x01) != 0;
            bool isDir = (flags & 0x02) != 0;

            int attrOff = U16(rec, 0x14);
            if (attrOff <= 0 || attrOff >= rec.Length) return null;

            string? bestName = null;
            int bestNs = -1;
            long parentIndex = 5;
            bool hasData = false;
            bool resident = false;
            byte[]? residentData = null;
            List<DataRun> runs = new();
            ulong realSize = 0;

            int pos = attrOff;
            while (pos + 8 <= rec.Length)
            {
                uint type = U32(rec, pos);
                if (type == 0xFFFFFFFF) break;
                int len = (int)U32(rec, pos + 4);
                if (len <= 0 || pos + len > rec.Length) break;

                byte nonResident = rec[pos + 8];

                if (type == 0x30) // $FILE_NAME
                {
                    int contentOff = U16(rec, pos + 0x14);
                    int cpos = pos + contentOff;
                    if (cpos + 0x42 <= rec.Length)
                    {
                        // ilk 8 bayt: ebeveyn dizin referansı (alt 6 bayt = kayıt indeksi)
                        long parentRef = (long)(U64(rec, cpos) & 0x0000FFFFFFFFFFFF);
                        int nameLen = rec[cpos + 0x40];
                        int ns = rec[cpos + 0x41];
                        int npos = cpos + 0x42;
                        if (npos + nameLen * 2 <= rec.Length)
                        {
                            string name = Encoding.Unicode.GetString(rec, npos, nameLen * 2);
                            // Win32 (1) ve Win32+DOS (3) adlarını DOS (2) üstüne tercih et
                            if (ns != 2 && (bestName == null || bestNs == 2))
                            { bestName = name; bestNs = ns; parentIndex = parentRef; }
                            else if (bestName == null)
                            { bestName = name; bestNs = ns; parentIndex = parentRef; }
                        }
                    }
                }
                else if (type == 0x80) // $DATA (adsız olan)
                {
                    int nameLen = rec[pos + 9];
                    if (nameLen == 0) // ana veri akışı
                    {
                        hasData = true;
                        if (nonResident == 0)
                        {
                            resident = true;
                            int cl = (int)U32(rec, pos + 0x10);
                            int co = U16(rec, pos + 0x14);
                            if (co + cl <= rec.Length && cl >= 0)
                            {
                                residentData = new byte[cl];
                                Array.Copy(rec, pos + co, residentData, 0, cl);
                                realSize = (ulong)cl;
                            }
                        }
                        else
                        {
                            resident = false;
                            realSize = U64(rec, pos + 0x30); // gerçek boyut
                            int runOff = U16(rec, pos + 0x20);
                            runs = ParseRunList(rec, pos + runOff, pos + len);
                        }
                    }
                }

                pos += len;
            }

            // Adı olan HER kaydı yol haritasına ekle (dizin/kullanımda olanlar dâhil) — yol kurmak için
            if (bestName != null) _nodes[index] = (bestName, parentIndex);

            if (inUse) return null;     // sadece silinmişler
            if (isDir) return null;     // dizinler dosya olarak kurtarılmaz (ama adı haritada)
            if (bestName == null || !hasData) return null;

            return new MftFile
            {
                Index = index,
                Name = bestName,
                Size = realSize,
                Resident = resident,
                ResidentData = residentData,
                Runs = runs,
                ParentIndex = parentIndex
            };
        }

        // $MFT kaydının $DATA data run'larını mutlak LCN aralıklarına çevir
        private List<DataRun> ParseMftDataRuns(byte[] rec)
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

        // Data run listesini çöz (mutlak LCN'lerle)
        private List<DataRun> ParseRunList(byte[] rec, int start, int end)
        {
            var runs = new List<DataRun>();
            int p = start;
            long prevLcn = 0;
            while (p < end && p < rec.Length)
            {
                byte header = rec[p++];
                if (header == 0) break;
                int lenBytes = header & 0x0F;
                int offBytes = (header >> 4) & 0x0F;
                if (lenBytes == 0 || p + lenBytes + offBytes > rec.Length) break;

                long length = 0;
                for (int i = 0; i < lenBytes; i++)
                    length |= (long)rec[p + i] << (8 * i);
                p += lenBytes;

                if (offBytes == 0)
                {
                    // Sparse (boşluk)
                    runs.Add(new DataRun { Lcn = 0, Clusters = length, Sparse = true });
                    continue;
                }

                long offset = 0;
                for (int i = 0; i < offBytes; i++)
                    offset |= (long)rec[p + i] << (8 * i);
                // işaretli sayıya çevir
                long signBit = 1L << (offBytes * 8 - 1);
                if ((offset & signBit) != 0)
                    offset -= (1L << (offBytes * 8));
                p += offBytes;

                prevLcn += offset;
                runs.Add(new DataRun { Lcn = prevLcn, Clusters = length, Sparse = false });
            }
            return runs;
        }

        // NTFS fixup (update sequence): her sektörün son 2 baytını düzelt
        private void ApplyFixup(byte[] rec)
        {
            int usaOff = U16(rec, 0x04);
            int usaCount = U16(rec, 0x06);
            if (usaCount <= 0 || usaOff <= 0) return;
            if (usaOff + usaCount * 2 > rec.Length) return;

            // İlk 2 bayt: update sequence number; kalanlar: her sektörün doğru son 2 baytı
            for (int i = 1; i < usaCount; i++)
            {
                int sectorEnd = i * _bytesPerSector - 2;
                if (sectorEnd + 2 > rec.Length) break;
                rec[sectorEnd] = rec[usaOff + i * 2];
                rec[sectorEnd + 1] = rec[usaOff + i * 2 + 1];
            }
        }

        // --- yardımcılar ---
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
            byte[] trimmed = new byte[got];
            Array.Copy(buf, trimmed, got);
            return trimmed;
        }

        private static int U16(byte[] b, int o) => b[o] | (b[o + 1] << 8);
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
