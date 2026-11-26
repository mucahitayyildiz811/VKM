using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VKM.App
{
    // "PP / uware" tipi DVR (CCTV) disk/imaj okuyucu.
    // Disk düzeni (gerçek 4TB diskten tersine çevrildi):
    //   - offset 0: süper blok (02 05 17 20 ... "uware"), ardından boş bir FAT32 (mkdosfs) — kayıt tutmaz.
    //   - Kayıt alanı: SABİT 128 MB'lık slotlar (disk başından hizalı).
    //   - Her slot başında "PP" (0x5050) başlığı:
    //        +0x20 başlangıç unix (UTC), +0x24 bitiş unix (UTC), +0x40 kamera adı (UTF-8, 64 bayt)
    //   - Video: slot içinde 0x200000 (2 MB) offsetinden itibaren HAM H.265 Annex-B (~126 MB).
    //   - Zaman UTC saklanır; ekranda yerel saatle (OSD ile birebir) gösterilir.
    public class UwareRecording
    {
        public string Camera = "";
        public DateTime Start;
        public DateTime End;
        public long SlotOffset;     // slot başı (PP başlığı) — diskte mutlak
        public long VideoOffset;    // slot başı + 0x200000 (ham H.265)
        public long VideoLength;    // kopyalanacak ham video uzunluğu
    }

    public class UwareInfo
    {
        public bool IsUware;
        public long Capacity;
        public string Diagnostic = "";
        public List<UwareRecording> Recordings = new();
    }

    public static class UwareDvrReader
    {
        public const long SLOT = 128L * 1024 * 1024;   // 0x8000000
        public const long VID_OFF = 0x200000;          // 2 MB başlık/indeks sonrası
        private const uint PP_MAGIC = 0x00005050;      // "PP\0\0"

        public static bool Detect(Func<int, Stream> openRaw)
        {
            try
            {
                using Stream s = openRaw(512);
                byte[] buf = new byte[0x40];
                ReadAt(s, 0, buf);
                // offset 0'daki süper blok imzası + "uware" damgası
                bool magic = buf[0] == 0x02 && buf[1] == 0x05 && buf[2] == 0x17 && buf[3] == 0x20;
                bool uware = IndexOfAscii(buf, "uware") >= 0;
                return uware || magic;
            }
            catch { return false; }
        }

        public static UwareInfo Parse(Func<int, Stream> openRaw, long fileLen, Action<long>? onProgress = null)
        {
            var info = new UwareInfo { Capacity = fileLen };
            var diag = new StringBuilder();
            long minT = new DateTimeOffset(new DateTime(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            long maxT = new DateTimeOffset(new DateTime(2035, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();

            try
            {
                using Stream s = openRaw(1 << 20);
                byte[] h = new byte[0x80];
                long scanned = 0;

                for (long off = 0; off + 0x80 <= fileLen; off += SLOT, scanned++)
                {
                    ReadAt(s, off, h);

                    if ((scanned & 0xFF) == 0) onProgress?.Invoke(off);   // ~her 256 slotta ilerleme

                    if (U32(h, 0) != PP_MAGIC) continue;                  // slot değil (FAT/boş alan)
                    long t1 = U32(h, 0x20), t2 = U32(h, 0x24);
                    if (t1 < minT || t1 > maxT) continue;
                    if (t2 < t1 || t2 > maxT) t2 = t1;                    // bozuk bitişi başlangıca çek

                    string cam = ReadUtf8(h, 0x40, 0x40);
                    if (cam.Length == 0) cam = "Kamera";

                    long vlen = Math.Min(SLOT - VID_OFF, fileLen - (off + VID_OFF));
                    if (vlen <= 0) continue;

                    info.Recordings.Add(new UwareRecording
                    {
                        Camera = cam,
                        Start = DateTimeOffset.FromUnixTimeSeconds(t1).LocalDateTime,
                        End = DateTimeOffset.FromUnixTimeSeconds(t2).LocalDateTime,
                        SlotOffset = off,
                        VideoOffset = off + VID_OFF,
                        VideoLength = vlen
                    });
                }

                onProgress?.Invoke(fileLen);
                info.IsUware = info.Recordings.Count > 0;
                // kamera + zaman sırası
                info.Recordings.Sort((a, b) =>
                {
                    int c = string.Compare(a.Camera, b.Camera, StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : a.Start.CompareTo(b.Start);
                });

                var cams = new HashSet<string>();
                foreach (var r in info.Recordings) cams.Add(r.Camera);
                diag.Append(info.Recordings.Count).Append(" kayıt · ").Append(cams.Count).Append(" kamera");
            }
            catch (Exception ex) { diag.Append("hata: ").Append(ex.Message); }

            info.Diagnostic = diag.ToString();
            return info;
        }

        // Slotun video bölgesini (0x200000..slot sonu) oku, NVR'ın araya serpiştirdiği
        // 28 baytlık "chunk" metadata bloklarını SÖK ve kalan H.265'i birleştir → oynatılabilir .h265.
        // Bu DVR videoyu ~5 KB'lık parçalara bölüp her parçanın başına 28 baytlık bir başlık koyuyor;
        // bu başlıklar bazen sahte start-code içerdiğinden ham kopya VLC/ffmpeg'de bozuk açılır.
        // Blokları çıkarınca akış tam ve temiz olur. (Hikvision/Dahua yollarını ETKİLEMEZ.)
        public static bool Extract(Func<int, Stream> openRaw, long videoOffset, long videoLength,
                                   string dstMp4, System.Threading.CancellationToken tok,
                                   out string diag, Action<long>? onBytes)
        {
            string outPath = Path.ChangeExtension(dstMp4, ".h265");
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

            // 1) Video bölgesini belleğe oku (slot başına ~126 MB)
            byte[] vid = new byte[videoLength];
            int total = 0;
            using (Stream s = openRaw(16 << 20))
            {
                s.Position = videoOffset;
                while (total < vid.Length)
                {
                    tok.ThrowIfCancellationRequested();
                    int r = s.Read(vid, total, vid.Length - total);
                    if (r <= 0) break;
                    total += r;
                    onBytes?.Invoke(r);
                }
            }
            int n = total;
            // kullanılmayan sıfır kuyruğu at
            while (n > 0 && vid[n - 1] == 0) n--;

            // 2) İlk parametre setinden (VPS/SPS) başla
            int start = FindParamStart(vid, 0, n);
            if (start < 0) start = 0;

            // 3) De-chunk: 28 baytlık metadata bloklarını atla, gerisini yaz
            long outBytes = 0;
            using (var es = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            {
                int seg = start, i = start;
                while (i <= n - 28)
                {
                    if (IsChunkHeader(vid, i))
                    {
                        if (i > seg) { es.Write(vid, seg, i - seg); outBytes += i - seg; }
                        i += 28; seg = i;
                    }
                    else i++;
                }
                if (n > seg) { es.Write(vid, seg, n - seg); outBytes += n - seg; }
            }

            if (outBytes < 1024) { try { File.Delete(outPath); } catch { } diag = "H265 akışı bulunamadı"; return false; }
            diag = "H265 · " + (outBytes / 1048576) + "MB · de-chunk OK";
            return true;
        }

        // NVR chunk başlığı mı? Düzen: [size16 00 00][size16 00 00][8 bayt] 00 24 ... (toplam 28 bayt).
        // İki boyut alanı ~eşit ve makul (0x200..0x9C40), +20'de 0x00 0x24 işareti.
        private static bool IsChunkHeader(byte[] b, int i)
        {
            if (b[i + 2] != 0 || b[i + 3] != 0 || b[i + 6] != 0 || b[i + 7] != 0) return false;
            if (b[i + 20] != 0x00 || b[i + 21] != 0x24) return false;
            int s1 = b[i] | (b[i + 1] << 8);
            int s2 = b[i + 4] | (b[i + 5] << 8);
            return s2 > 0x200 && s2 < 0x9C40 && Math.Abs(s1 - s2) <= 4;
        }

        // 00 00 00 01 [40|42] (H265 VPS/SPS) — H264 (67/27) da kabul
        private static int FindParamStart(byte[] b, int from, int end)
        {
            for (int i = from; i + 5 < end; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 0 && b[i + 3] == 1)
                {
                    byte nb = b[i + 4];
                    if (nb == 0x40 || nb == 0x42 || nb == 0x67 || nb == 0x27) return i;
                }
            return -1;
        }

        private static long AllZeroTail(byte[] b, int len)
        {
            long z = 0;
            for (int i = len - 1; i >= 0 && b[i] == 0; i--) z++;
            return z;
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

        private static uint U32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static string ReadUtf8(byte[] b, int o, int max)
        {
            int n = 0;
            while (n < max && o + n < b.Length && b[o + n] != 0) n++;
            try { return Encoding.UTF8.GetString(b, o, n).Trim(); }
            catch { return ""; }
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
    }
}
