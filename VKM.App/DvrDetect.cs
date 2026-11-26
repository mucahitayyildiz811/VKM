using System;
using System.IO;
using System.Text;

namespace VKM.App
{
    public class DvrBrand
    {
        public string Name = "";        // "Hikvision", "Dahua", ...
        public string Kind = "";        // "hikvision" | "dahua" | "generic"
        public long SigOffset = -1;      // imzanın bulunduğu ofset
        public string Note = "";
    }

    // Diskin ilk MB'larını KISA tarar, bilinen DVR imzalarından markayı bulur.
    // Tablo genişletilebilir: yeni marka = yeni satır.
    public static class DvrDetect
    {
        // (marka, tür, ASCII imza)
        private static readonly (string Name, string Kind, string Sig)[] Table =
        {
            ("Hikvision",  "hikvision", "HIKVISION@HANGZHOU"),
            ("Dahua",      "dahua",     "DHFS"),   // Dahua dosya sistemi
            ("Dahua",      "dahua",     "DHAV"),   // Dahua video çerçeve sihri
            ("Dahua",      "dahua",     "Dahua"),
        };

        // İlk 'scan' baytını (varsayılan 64 MB) okuyup imza arar. Bulamazsa null.
        public static DvrBrand? Identify(Func<int, Stream> openRaw, long length, long scan = 64L * 1024 * 1024)
        {
            long n = Math.Min(scan, length);
            const int CHUNK = 1 << 20;     // 1 MB
            const int OVER = 64;           // sınır kaçağı için örtüşme
            byte[] hay = new byte[CHUNK + OVER];
            int carry = 0;                 // örtüşmede taşınan bayt sayısı
            long basePos = 0;              // hay[0]'ın diskteki ofseti

            try
            {
                using Stream s = openRaw(512);
                long readPos = 0;
                while (readPos < n)
                {
                    int want = (int)Math.Min(CHUNK, n - readPos);
                    s.Position = readPos;
                    int got = ReadFull(s, hay, carry, want);
                    if (got <= 0) break;
                    int total = carry + got;

                    foreach (var e in Table)
                    {
                        int idx = IndexOf(hay, total, Encoding.ASCII.GetBytes(e.Sig));
                        if (idx >= 0)
                            return new DvrBrand { Name = e.Name, Kind = e.Kind, SigOffset = basePos + idx, Note = e.Sig };
                    }

                    // Son OVER baytı başa taşı (sonraki blokla birleşik arama)
                    int keep = Math.Min(OVER, total);
                    Array.Copy(hay, total - keep, hay, 0, keep);
                    carry = keep;
                    basePos += total - keep;
                    readPos += got;
                }
            }
            catch { }
            return null;
        }

        private static int IndexOf(byte[] hay, int hayLen, byte[] needle)
        {
            int end = hayLen - needle.Length;
            for (int i = 0; i <= end; i++)
            {
                int j = 0;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        private static int ReadFull(Stream s, byte[] buf, int offset, int count)
        {
            int off = 0;
            while (off < count)
            {
                int r = s.Read(buf, offset + off, count - off);
                if (r <= 0) break;
                off += r;
            }
            return off;
        }
    }
}
