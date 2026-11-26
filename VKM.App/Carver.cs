using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VKM.App
{
    public class CarvedFile
    {
        public long Offset;
        public long Length;
        public string Ext = "bin";
        public string Codec = "";   // video için: "h264" | "h265" | "mjpeg" (boş = imza/diğer)
    }

    // İmza (signature) tabanlı kurtarma — PhotoRec benzeri.
    // Dosya sistemi olmasa da (formatlı disk) çalışır.
    // Reliable (sonlu/boyutlu): JPEG, PNG, PDF, ZIP/Office, BMP, RIFF(AVI/WAV), SQLite
    // Best-effort (başlık→sonraki başlık): MP4/MOV, GIF, TIFF, MP3, GZ, 7Z, RAR, OLE(doc/xls/ppt)
    // Metin: uzun okunabilir ASCII blokları (.txt) — yüksek eşik.
    public static class Carver
    {
        private const int BUF = 8 * 1024 * 1024;
        private const int OVER = 64;
        private const long MAXFILE = 500L * 1024 * 1024;
        private const long MINFILE = 512;
        private const int TXTMIN = 4096;   // metin bloğu için yüksek eşik (gürültüyü azalt)

        public static List<CarvedFile> Scan(Func<int, Stream> openRaw, long length,
            CancellationToken tok, Action<double, int> progress)
        {
            var res = new List<CarvedFile>();
            byte[] buf = new byte[BUF];

            // sonlu (footer) açık kayıtlar
            long jpeg = -1, png = -1, pdf = -1, zip = -1;
            // best-effort tek bekleyen kayıt
            long beStart = -1; string beExt = "";
            // metin bloğu
            long txtStart = -1;

            using Stream s = openRaw(512);
            long pos = 0;
            int lastReport = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (pos < length)
            {
                if (tok.IsCancellationRequested) break;
                s.Position = pos;
                int toRead = (int)Math.Min(BUF, length - pos);
                int got = ReadFull(s, buf, toRead);
                if (got <= 0) break;

                bool atEnd = pos + got >= length;
                int scanEnd = atEnd ? got : got - OVER;
                if (scanEnd <= 0) scanEnd = got;

                for (int i = 0; i < scanEnd; i++)
                {
                    long abs = pos + i;
                    byte b = buf[i];

                    // ---- footer: açık sonlu kayıtların sonu ----
                    if (jpeg >= 0 && b == 0xFF && i + 1 < got && buf[i + 1] == 0xD9) { Emit(res, jpeg, abs + 2 - jpeg, "jpg"); jpeg = -1; }
                    else if (png >= 0 && b == 0x49 && Match(buf, i, got, 0x49, 0x45, 0x4E, 0x44)) { Emit(res, png, abs + 8 - png, "png"); png = -1; }
                    else if (pdf >= 0 && b == 0x25 && Match(buf, i, got, 0x25, 0x25, 0x45, 0x4F, 0x46)) { Emit(res, pdf, abs + 7 - pdf, "pdf"); pdf = -1; }
                    else if (zip >= 0 && b == 0x50 && Match(buf, i, got, 0x50, 0x4B, 0x05, 0x06) && i + 21 < got)
                    { int cl = buf[i + 20] | (buf[i + 21] << 8); Emit(res, zip, abs + 22 + cl - zip, "zip"); zip = -1; }

                    // ---- başlıklar ----
                    bool newHeader = false; string beNew = "";

                    if (b == 0xFF && Match(buf, i, got, 0xFF, 0xD8, 0xFF)) { if (jpeg < 0) jpeg = abs; newHeader = true; }
                    else if (b == 0x89 && Match(buf, i, got, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) { if (png < 0) png = abs; newHeader = true; }
                    else if (b == 0x25 && Match(buf, i, got, 0x25, 0x50, 0x44, 0x46)) { if (pdf < 0) pdf = abs; newHeader = true; }
                    else if (b == 0x50 && Match(buf, i, got, 0x50, 0x4B, 0x03, 0x04)) { if (zip < 0) zip = abs; newHeader = true; }
                    else if (b == 0x42 && i + 5 < got && buf[i + 1] == 0x4D)
                    {
                        long sz = (uint)(buf[i + 2] | (buf[i + 3] << 8) | (buf[i + 4] << 16) | (buf[i + 5] << 24));
                        if (sz >= MINFILE && sz <= MAXFILE) { Emit(res, abs, sz, "bmp"); newHeader = true; }
                    }
                    else if (b == 0x52 && Match(buf, i, got, 0x52, 0x49, 0x46, 0x46) && i + 11 < got)   // RIFF
                    {
                        long sz = (uint)(buf[i + 4] | (buf[i + 5] << 8) | (buf[i + 6] << 16) | (buf[i + 7] << 24));
                        string ext = Match(buf, i + 8, got, 0x41, 0x56, 0x49, 0x20) ? "avi"
                                   : Match(buf, i + 8, got, 0x57, 0x41, 0x56, 0x45) ? "wav" : "";
                        if (ext != "" && sz >= MINFILE && sz <= MAXFILE) { Emit(res, abs, sz + 8, ext); newHeader = true; }
                    }
                    else if (b == 0x53 && Match(buf, i, got, 0x53, 0x51, 0x4C, 0x69, 0x74, 0x65) && i + 31 < got)  // "SQLite"
                    {
                        int ps = (buf[i + 16] << 8) | buf[i + 17];
                        long pageSize = ps == 1 ? 65536 : ps;
                        long pageCount = (uint)((buf[i + 28] << 24) | (buf[i + 29] << 16) | (buf[i + 30] << 8) | buf[i + 31]);
                        long sz = pageSize * pageCount;
                        if (pageSize >= 512 && sz >= MINFILE && sz <= MAXFILE) { Emit(res, abs, sz, "db"); newHeader = true; }
                    }
                    // best-effort türler
                    else if (b == 0x66 && Match(buf, i, got, 0x66, 0x74, 0x79, 0x70) && i >= 4) { beNew = "mp4"; }   // 'ftyp' (box başı -4)
                    else if (b == 0x47 && Match(buf, i, got, 0x47, 0x49, 0x46, 0x38)) { beNew = "gif"; }
                    else if (b == 0x49 && Match(buf, i, got, 0x49, 0x49, 0x2A, 0x00)) { beNew = "tif"; }
                    else if (b == 0x4D && Match(buf, i, got, 0x4D, 0x4D, 0x00, 0x2A)) { beNew = "tif"; }
                    else if (b == 0x49 && Match(buf, i, got, 0x49, 0x44, 0x33)) { beNew = "mp3"; }
                    else if (b == 0x1F && Match(buf, i, got, 0x1F, 0x8B, 0x08)) { beNew = "gz"; }
                    else if (b == 0x37 && Match(buf, i, got, 0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) { beNew = "7z"; }
                    else if (b == 0x52 && Match(buf, i, got, 0x52, 0x61, 0x72, 0x21)) { beNew = "rar"; }
                    else if (b == 0xD0 && Match(buf, i, got, 0xD0, 0xCF, 0x11, 0xE0)) { beNew = "ole"; }  // doc/xls/ppt

                    if (beNew != "")
                    {
                        long start = (beNew == "mp4") ? abs - 4 : abs;
                        FlushBe(res, ref beStart, ref beExt, start);   // önceki best-effort'u kapat
                        beStart = start; beExt = beNew; newHeader = true;
                    }
                    else if (newHeader)
                    {
                        FlushBe(res, ref beStart, ref beExt, abs);     // sonlu başlık da best-effort'u kapatır
                    }

                    // ---- metin bloğu (.txt) — sadece sınıflandırılmamış bölgede ----
                    bool clean = jpeg < 0 && png < 0 && pdf < 0 && zip < 0 && beStart < 0;
                    bool printable = b == 0x09 || b == 0x0A || b == 0x0D || (b >= 0x20 && b <= 0x7E);
                    if (clean && printable)
                    {
                        if (txtStart < 0) txtStart = abs;
                    }
                    else
                    {
                        if (txtStart >= 0)
                        {
                            long tlen = abs - txtStart;
                            if (tlen >= TXTMIN) Emit(res, txtStart, tlen, "txt");
                            txtStart = -1;
                        }
                    }

                    // ---- üst sınır ----
                    if (jpeg >= 0 && abs - jpeg > MAXFILE) jpeg = -1;
                    if (png >= 0 && abs - png > MAXFILE) png = -1;
                    if (pdf >= 0 && abs - pdf > MAXFILE) pdf = -1;
                    if (zip >= 0 && abs - zip > MAXFILE) zip = -1;
                    if (beStart >= 0 && abs - beStart > MAXFILE) FlushBe(res, ref beStart, ref beExt, abs);
                }

                pos += atEnd ? got : scanEnd;

                if (sw.ElapsedMilliseconds - lastReport > 300)
                {
                    lastReport = (int)sw.ElapsedMilliseconds;
                    progress((double)pos / length, res.Count);
                }
            }

            progress(1.0, res.Count);
            return res;
        }

        private static void FlushBe(List<CarvedFile> res, ref long beStart, ref string beExt, long end)
        {
            if (beStart >= 0)
            {
                long len = end - beStart;
                if (len >= MINFILE && len <= MAXFILE) Emit(res, beStart, len, beExt);
                beStart = -1; beExt = "";
            }
        }

        private static void Emit(List<CarvedFile> res, long off, long len, string ext)
        {
            if (len >= MINFILE && len <= MAXFILE && res.Count < 500000)
                res.Add(new CarvedFile { Offset = off, Length = len, Ext = ext });
        }

        private static bool Match(byte[] b, int i, int got, params byte[] sig)
        {
            if (i < 0 || i + sig.Length > got) return false;
            for (int k = 0; k < sig.Length; k++) if (b[i + k] != sig[k]) return false;
            return true;
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
