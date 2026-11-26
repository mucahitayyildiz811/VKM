using System;

namespace VKM.App
{
    // Dosyanın imza/başlık-sonuna bakarak sağlam mı bozuk mu tahmini.
    // sound: true=sağlam, false=bozuk/eksik, null=belirsiz (tür bilinmiyor)
    public static class SoundCheck
    {
        public static (bool? sound, string message) Check(string name, byte[] head, byte[] tail, bool haveFullTail)
        {
            string ext = Ext(name);

            bool H(params byte[] sig) => StartsWith(head, sig);
            bool At(int off, params byte[] sig) => HasAt(head, off, sig);

            switch (ext)
            {
                case "jpg":
                case "jpeg":
                    if (!H(0xFF, 0xD8)) return (false, "JPEG başlığı bozuk (FFD8 yok).");
                    if (haveFullTail && !EndsWith(tail, 0xFF, 0xD9)) return (false, "JPEG sonu eksik — dosya yarım.");
                    return (true, "JPEG sağlam görünüyor.");

                case "png":
                    if (!H(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)) return (false, "PNG başlığı bozuk.");
                    if (haveFullTail && !Contains(tail, 0x49, 0x45, 0x4E, 0x44)) return (false, "PNG sonu (IEND) yok — yarım.");
                    return (true, "PNG sağlam görünüyor.");

                case "gif":
                    if (!H(0x47, 0x49, 0x46, 0x38)) return (false, "GIF başlığı bozuk.");
                    return (true, "GIF başlığı sağlam.");

                case "bmp":
                    if (!H(0x42, 0x4D)) return (false, "BMP başlığı bozuk.");
                    return (true, "BMP başlığı sağlam.");

                case "pdf":
                    if (!H(0x25, 0x50, 0x44, 0x46)) return (false, "PDF başlığı bozuk (%PDF yok).");
                    if (haveFullTail && !Contains(tail, 0x25, 0x25, 0x45, 0x4F, 0x46)) return (false, "PDF sonu (%%EOF) yok — yarım.");
                    return (true, "PDF sağlam görünüyor.");

                case "zip":
                case "docx":
                case "xlsx":
                case "pptx":
                    if (!H(0x50, 0x4B, 0x03, 0x04) && !H(0x50, 0x4B, 0x05, 0x06)) return (false, "ZIP/Office başlığı bozuk (PK yok).");
                    return (true, "ZIP/Office başlığı sağlam.");

                case "rar":
                    if (!H(0x52, 0x61, 0x72, 0x21)) return (false, "RAR başlığı bozuk.");
                    return (true, "RAR başlığı sağlam.");

                case "7z":
                    if (!H(0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C)) return (false, "7z başlığı bozuk.");
                    return (true, "7z başlığı sağlam.");

                case "mp4":
                case "mov":
                case "m4v":
                case "m4a":
                    if (!At(4, 0x66, 0x74, 0x79, 0x70)) return (false, "MP4/MOV başlığı bozuk (ftyp yok).");
                    return (true, "MP4/MOV başlığı sağlam.");

                case "mp3":
                    if (!H(0x49, 0x44, 0x33) && !(head.Length > 1 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0))
                        return (false, "MP3 başlığı bozuk.");
                    return (true, "MP3 başlığı sağlam.");

                case "avi":
                case "wav":
                    if (!H(0x52, 0x49, 0x46, 0x46)) return (false, "RIFF (AVI/WAV) başlığı bozuk.");
                    return (true, "RIFF başlığı sağlam.");

                case "doc":
                case "xls":
                case "ppt":
                    if (!H(0xD0, 0xCF, 0x11, 0xE0)) return (false, "Office (eski) başlığı bozuk.");
                    return (true, "Office başlığı sağlam.");

                default:
                    return (null, "Tür bilinmiyor — sağlamlık belirsiz.");
            }
        }

        private static string Ext(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot >= 0 && dot < name.Length - 1 ? name.Substring(dot + 1).ToLowerInvariant() : "";
        }

        private static bool StartsWith(byte[] d, byte[] sig)
        {
            if (d.Length < sig.Length) return false;
            for (int i = 0; i < sig.Length; i++) if (d[i] != sig[i]) return false;
            return true;
        }

        private static bool HasAt(byte[] d, int off, byte[] sig)
        {
            if (d.Length < off + sig.Length) return false;
            for (int i = 0; i < sig.Length; i++) if (d[off + i] != sig[i]) return false;
            return true;
        }

        private static bool EndsWith(byte[] d, params byte[] sig)
        {
            if (d.Length < sig.Length) return false;
            for (int i = 0; i < sig.Length; i++) if (d[d.Length - sig.Length + i] != sig[i]) return false;
            return true;
        }

        private static bool Contains(byte[] d, params byte[] sig)
        {
            if (d.Length < sig.Length) return false;
            for (int i = 0; i <= d.Length - sig.Length; i++)
            {
                bool ok = true;
                for (int j = 0; j < sig.Length; j++) if (d[i + j] != sig[j]) { ok = false; break; }
                if (ok) return true;
            }
            return false;
        }
    }
}
