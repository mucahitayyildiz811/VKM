using System;

namespace VKM.App
{
    // OSD'den tarih/saat ve kamera adı ayıklama (saf .NET regex).
    // NOT: Windows yerleşik OCR'ı (Windows.Media.Ocr) yeni .NET SDK'larında
    // win10-* RID çakışması yaptığı için şimdilik devre dışı (Available=false).
    // Satır-içi zaman damgası yöntemi carving'de çalışmaya devam eder.
    public static class OcrHelper
    {
        public static bool Available => false;

        // OCR kapalı → "" döner (çağıran taraf Available=false'ta zaten erken çıkar).
        public static string TryOcr(byte[] imageBytes) => "";

        // OCR metninden tarih/saat ayıkla (ör. "2026-09-07 14:03:46" / "07/09/2026 14:03").
        // Bulursa DateTime döner, yoksa null.
        public static DateTime? ExtractDate(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var m = System.Text.RegularExpressions.Regex.Match(text,
                @"(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})[^\d]{1,3}(\d{1,2}):(\d{2})(?::(\d{2}))?");
            if (m.Success)
            {
                try
                {
                    return new DateTime(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
                        int.Parse(m.Groups[3].Value), int.Parse(m.Groups[4].Value), int.Parse(m.Groups[5].Value),
                        m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 0);
                }
                catch { }
            }
            // gün/ay/yıl düzeni (Avrupa DVR'ları)
            var m2 = System.Text.RegularExpressions.Regex.Match(text,
                @"(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})[^\d]{1,3}(\d{1,2}):(\d{2})(?::(\d{2}))?");
            if (m2.Success)
            {
                try
                {
                    return new DateTime(int.Parse(m2.Groups[3].Value), int.Parse(m2.Groups[2].Value),
                        int.Parse(m2.Groups[1].Value), int.Parse(m2.Groups[4].Value), int.Parse(m2.Groups[5].Value),
                        m2.Groups[6].Success ? int.Parse(m2.Groups[6].Value) : 0);
                }
                catch { }
            }
            return null;
        }

        // OSD'den kamera adı tahmini: "CAM01", "CH1", "Kapi", "1.kat uretim" gibi kısa satır.
        public static string ExtractCameraName(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length < 2 || line.Length > 28) continue;
                // tarih/saat satırlarını atla
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\d{1,2}:\d{2}")) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\d{4}[-/.]\d")) continue;
                // en az bir harf içersin
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"[A-Za-zÇĞİÖŞÜçğıöşü]")) return line;
            }
            return "";
        }
    }
}
