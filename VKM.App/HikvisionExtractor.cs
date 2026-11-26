using System;
using System.IO;
using System.Threading;

namespace VKM.App
{
    // Hikvision veri bloğundan H.264/H.265 elementer akışını çıkarır.
    // Blok başındaki Hikvision başlığını atlar, ilk NAL başlangıç kodundan (00 00 01)
    // itibaren yazar → VLC .h264/.h265 olarak oynatabilir.
    public static class HikvisionExtractor
    {
        public static void Extract(Func<int, Stream> openRaw, long offset, long length,
            string dstPath, CancellationToken tok)
        {
            using Stream s = openRaw(512);
            s.Position = offset;
            using var outF = new FileStream(dstPath, FileMode.Create, FileAccess.Write);

            byte[] buf = new byte[4 * 1024 * 1024];
            long remaining = length;

            // İlk parçayı oku, başlangıç kodunu bul
            int want = (int)Math.Min(buf.Length, remaining);
            int got = ReadFull(s, buf, want);
            if (got <= 0) return;
            remaining -= got;

            int sc = FindStartCode(buf, got);
            int start = 0;
            if (sc >= 0)
            {
                // 4 baytlık koda hizala (önünde 00 varsa onu da al)
                start = (sc > 0 && buf[sc - 1] == 0) ? sc - 1 : sc;
            }
            outF.Write(buf, start, got - start);

            // Kalanı olduğu gibi akıt
            while (remaining > 0 && !tok.IsCancellationRequested)
            {
                want = (int)Math.Min(buf.Length, remaining);
                got = ReadFull(s, buf, want);
                if (got <= 0) break;
                outF.Write(buf, 0, got);
                remaining -= got;
            }
        }

        // 00 00 01 (3 baytlık NAL başlangıç kodu) ilk konumu
        private static int FindStartCode(byte[] b, int len)
        {
            for (int i = 0; i + 2 < len; i++)
                if (b[i] == 0x00 && b[i + 1] == 0x00 && b[i + 2] == 0x01)
                    return i;
            return -1;
        }

        private static int ReadFull(Stream s, byte[] buf, int count)
        {
            int off = 0;
            while (off < count)
            {
                int r = s.Read(buf, off, count - off);
                if (r <= 0) break;
                off += r;
            }
            return off;
        }
    }
}
