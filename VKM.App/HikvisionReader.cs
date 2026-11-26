using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VKM.App
{
    // Hikvision DVR (CCTV) disk/imaj okuyucu.
    // Master blok (offset 0x200) "HIKVISION@HANGZHOU" imzası taşır.
    // Amaç: kayıtları (kanal, zaman, konum, boyut) listelemek ve ham video olarak çıkarmak.
    public class HikRecording
    {
        public int Channel;
        public DateTime Start;
        public DateTime End;
        public long Offset;     // diskte mutlak offset
        public long Length;     // bayt
    }

    public class HikInfo
    {
        public bool IsHik;
        public long Capacity;
        public long DataBlockSize;
        public long DataAreaOffset;
        public long HikBtreeOffset;
        public string Version = "";
        public string Diagnostic = "";
        public List<HikRecording> Recordings = new();
    }

    public static class HikvisionReader
    {
        public static bool Detect(Func<int, Stream> openRaw)
        {
            try
            {
                using Stream s = openRaw(512);
                byte[] buf = new byte[0x400];
                ReadAt(s, 0, buf);
                return IndexOfAscii(buf, "HIKVISION@HANGZHOU") >= 0;
            }
            catch { return false; }
        }

        public static HikInfo Parse(Func<int, Stream> openRaw, long fileLen, Action<long>? onProgress = null)
        {
            var info = new HikInfo();
            var diag = new StringBuilder();
            try
            {
                using Stream s = openRaw(512);

                byte[] master = new byte[0x600];
                ReadAt(s, 0, master);
                if (IndexOfAscii(master, "HIKVISION@HANGZHOU") < 0) { info.IsHik = false; return info; }
                info.IsHik = true;

                // Veri bloğu boyutu (master @0x288, genelde 1 GB)
                long blockSize = U64(master, 0x288);
                if (blockSize < (1L << 20) || blockSize > (4L << 30)) blockSize = 1L << 30;
                info.DataBlockSize = blockSize;
                info.Capacity = fileLen;
                diag.AppendLine("Hikvision algılandı. Blok boyutu = " + blockSize + " (" + (blockSize >> 20) + " MB).");

                // HER 1 GB BLOK SINIRINDA başlığı oku: geçerli veri bloğu mu?
                //  başlık: [0x0d]=kanal, [0x20]=başlangıç unix, [0x24]=bitiş unix (yerel saat=UTC olarak saklı)
                long minT = new DateTimeOffset(new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
                long maxT = new DateTimeOffset(new DateTime(2032, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

                byte[] hdr = new byte[0x30];
                using Stream s2 = openRaw(512);
                long scanned = 0;
                for (long off = 0; off + 0x30 <= fileLen; off += blockSize)
                {
                    try { ReadAt(s2, off, hdr); } catch { continue; }
                    scanned++;
                    if ((scanned & 0x3F) == 0) onProgress?.Invoke(off);   // ~her 64 blokta ilerleme
                    long t1 = U32(hdr, 0x20);
                    long t2 = U32(hdr, 0x24);
                    if (t1 < minT || t1 > maxT || t2 < t1 || t2 > maxT) continue;
                    if (t2 - t1 > 2L * 24 * 3600) continue;    // tek blok en çok ~2 gün

                    int chan = hdr[0x0d];
                    if (chan < 0 || chan > 63) chan = 0;

                    info.Recordings.Add(new HikRecording
                    {
                        Channel = chan,
                        // DVR yerel saati unix olarak saklıyor → UTC gibi çöz = etiket saati (ekrandaki OSD ile aynı)
                        Start = DateTimeOffset.FromUnixTimeSeconds(t1).UtcDateTime,
                        End = DateTimeOffset.FromUnixTimeSeconds(t2).UtcDateTime,
                        Offset = off,
                        Length = blockSize
                    });
                }
                info.Recordings.Sort((a, b) => a.Start.CompareTo(b.Start));
                diag.AppendLine("Taranan blok sınırı: " + scanned + " · bulunan kayıt: " + info.Recordings.Count);
            }
            catch (Exception ex) { diag.AppendLine("Hikvision okuma hatası: " + ex.Message); }

            info.Diagnostic = diag.ToString();
            return info;
        }

        // Hikvision kaydını ÇIKAR: blok offsetinden ham H265/H264 Annex-B akışını kopyala.
        // Video zaten ham Annex-B → ilk VPS/SPS'ten başla, sonuna kadar kopyala (DHAV sarmalı yok).
        // Döner: true + out codec/boyut. Çıktı dstPath'e yazılır (.h265/.h264).
        public static bool Extract(Func<int, Stream> openRaw, long blockOffset, long blockSize,
                                   string dstMp4, System.Threading.CancellationToken tok,
                                   out string diag, Action<long>? onBytes)
        {
            const int CH = 16 << 20;
            string outPath = Path.ChangeExtension(dstMp4, ".h265");   // ham H265 (gerekirse .h264'e döner)
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

            bool started = false, isH265 = true, any = false;
            long outBytes = 0, zeros = 0;
            using (Stream s = openRaw(CH))
            using (var es = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[CH];
                long pos = blockOffset, end = blockOffset + blockSize;
                s.Position = blockOffset;
                while (pos < end)
                {
                    tok.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buf.Length, end - pos);
                    int got = s.Read(buf, 0, want);
                    if (got <= 0) break;

                    int startIdx = 0;
                    if (!started)
                    {
                        // ilk Annex-B parametre NAL'ını bul: 00 00 00 01 (40|42=H265 VPS/SPS, 67=H264 SPS)
                        int p = FindParamStart(buf, 0, got, out isH265);
                        if (p < 0) { pos += got; onBytes?.Invoke(got); continue; }
                        startIdx = p; started = true;
                    }

                    // Kopyalanacak kısımda uzun sıfır dizisi = kaydın sonu → dur
                    int writeLen = got - startIdx;
                    es.Write(buf, startIdx, writeLen);
                    outBytes += writeLen; any = true;
                    onBytes?.Invoke(got);

                    // kaydın sonunu sez: son 4 KB tamamen sıfırsa muhtemelen bitti
                    zeros = AllZeroTail(buf, got);
                    if (started && zeros >= (1 << 20)) break;   // 1 MB sıfır → kayıt bitti

                    pos += got;
                    if (outBytes > 4L * 1024 * 1024 * 1024) break;   // güvenlik üst sınırı
                }
            }

            if (!any) { try { File.Delete(outPath); } catch { } diag = "H265 akışı bulunamadı"; return false; }
            if (!isH265)
            {
                string h264 = Path.ChangeExtension(dstMp4, ".h264");
                try { if (File.Exists(h264)) File.Delete(h264); File.Move(outPath, h264); } catch { }
            }
            diag = "HAM " + (isH265 ? "H265" : "H264") + " · " + (outBytes / 1048576) + "MB · OK";
            return true;
        }

        // 00 00 00 01 [param NAL] bul; isH265: VPS/SPS(40/42)=H265, SPS(67)=H264
        private static int FindParamStart(byte[] b, int from, int end, out bool isH265)
        {
            isH265 = true;
            for (int i = from; i + 5 < end; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1)
                {
                    byte nb = b[i + 4];
                    if (nb == 0x40 || nb == 0x42) { isH265 = true; return i; }
                    if (nb == 0x67 || nb == 0x27) { isH265 = false; return i; }
                }
            return -1;
        }

        private static long AllZeroTail(byte[] b, int len)
        {
            long z = 0;
            for (int i = len - 1; i >= 0 && b[i] == 0; i--) z++;
            return z;
        }

        // HIKBTREE içinde kayıt adaylarını sezgisel olarak bul:
        // bir u64 geçerli disk-offset'i + yanında iki makul zaman damgası (unix, 2015..2031) ara.
        private static List<HikRecording> ExtractRecords(byte[] tree, long capacity)
        {
            var list = new List<HikRecording>();
            long minT = new DateTimeOffset(new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            long maxT = new DateTimeOffset(new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

            for (int i = 0; i + 32 <= tree.Length; i += 4)
            {
                // İki ardışık u32 zaman damgası?
                long t1 = U32(tree, i);
                long t2 = U32(tree, i + 4);
                if (t1 < minT || t1 > maxT || t2 < t1 || t2 > maxT) continue;
                if (t2 - t1 > 24 * 3600) continue; // tek kayıt en çok ~1 gün

                // Yakında geçerli bir disk-offset u64 ara (sonraki 24 bayt içinde)
                long off = 0;
                for (int j = i + 8; j <= i + 24 && j + 8 <= tree.Length; j += 4)
                {
                    long cand = U64(tree, j);
                    if (cand > 0x100000 && cand < capacity) { off = cand; break; }
                }
                if (off == 0) continue;

                list.Add(new HikRecording
                {
                    Channel = 0,
                    Start = DateTimeOffset.FromUnixTimeSeconds(t1).LocalDateTime,
                    End = DateTimeOffset.FromUnixTimeSeconds(t2).LocalDateTime,
                    Offset = off,
                    Length = 0   // sonraki adımda blok boyutundan hesaplanacak
                });
                i += 28;
            }
            return list;
        }

        // Her kaydın uzunluğunu bir sonraki kaydın offsetinden (yoksa blok boyutundan) hesapla
        private static void AssignLengths(List<HikRecording> recs, long blockSize)
        {
            recs.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            for (int i = 0; i < recs.Count; i++)
            {
                long len = blockSize;
                if (i + 1 < recs.Count)
                {
                    long d = recs[i + 1].Offset - recs[i].Offset;
                    if (d > 0 && d <= blockSize * 2) len = d;
                }
                recs[i].Length = len;
            }
        }

        // ---- yardımcılar ----
        private static void ReadAt(Stream s, long pos, byte[] buf)
        {
            s.Position = pos;
            int off = 0;
            while (off < buf.Length)
            {
                int r = s.Read(buf, off, buf.Length - off);
                if (r <= 0) break;
                off += r;
            }
        }

        private static long U32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static long U64(byte[] b, int o)
        {
            long v = 0;
            for (int i = 7; i >= 0; i--) v = (v << 8) | b[o + i];
            return v;
        }

        private static int IndexOfAscii(byte[] b, string s)
        {
            byte[] p = Encoding.ASCII.GetBytes(s);
            for (int i = 0; i <= b.Length - p.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < p.Length; j++) if (b[i + j] != p[j]) { ok = false; break; }
                if (ok) return i;
            }
            return -1;
        }

        // Belirli offsetten sonra ilk sıfır olmayan 16-baytlık hizalı bloğun başını bul
        private static int FirstNonZero(byte[] b, int start)
        {
            for (int i = Math.Max(0, start); i + 16 <= b.Length; i += 16)
            {
                bool allZero = true;
                for (int j = 0; j < 16; j++) if (b[i + j] != 0) { allZero = false; break; }
                if (!allZero) return i;
            }
            return Math.Min(start, Math.Max(0, b.Length - 16));
        }

        private static string HexBlock(byte[] b, int start, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count && start + i < b.Length; i += 16)
            {
                sb.Append((start + i).ToString("X8")).Append("  ");
                var asc = new StringBuilder();
                for (int j = 0; j < 16; j++)
                {
                    if (start + i + j < b.Length && i + j < count)
                    {
                        byte c = b[start + i + j];
                        sb.Append(c.ToString("X2")).Append(' ');
                        asc.Append(c >= 32 && c < 127 ? (char)c : '.');
                    }
                }
                sb.Append(' ').Append(asc).Append('\n');
            }
            return sb.ToString();
        }

        private static string ReadAscii(byte[] b, int o, int max)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < max && o + i < b.Length; i++)
            {
                byte c = b[o + i];
                if (c == 0) break;
                sb.Append(c >= 32 && c < 127 ? (char)c : '.');
            }
            return sb.ToString();
        }
    }
}
