using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VKM.App
{
    // Oto-Teşhis sonucu.
    public class DvrDiagnosis
    {
        public string Brand = "—";
        public string Format = "—";
        public string ReaderKey = "none";   // uware | hik | dhfs | mjpeg | carve | none
        public bool Recoverable = false;
        public int Cameras = -1;             // bilinmiyorsa -1
        public string DateRange = "";        // "2026-09-07 → 2026-09-13"
        public string Summary = "";
    }

    // Takılan DVR disklerini otomatik tanıyan teşhis motoru.
    // Mevcut okuyucuları (DHFS, Hikvision, uware) ÇAĞIRIR — onlara dokunmaz.
    // Hiçbiri tutmazsa örnekleme ile MJPEG / ham H.26x carving kararı verir.
    public static class DvrAutoDiagnose
    {
        public static DvrDiagnosis Diagnose(Func<int, Stream> openRaw, long totalSize, Action<string>? step = null)
        {
            // 1) uware / PP (yeni çözdüğümüz NVR)
            step?.Invoke("uware/PP imzası kontrol ediliyor…");
            try
            {
                if (UwareDvrReader.Detect(openRaw))
                    return DiagUware(openRaw, totalSize);
            }
            catch { }

            // 2) Hikvision
            step?.Invoke("Hikvision imzası kontrol ediliyor…");
            try
            {
                if (HikvisionReader.Detect(openRaw))
                    return new DvrDiagnosis
                    {
                        Brand = "Hikvision",
                        Format = "Hikvision (ham H.265)",
                        ReaderKey = "hik",
                        Recoverable = true,
                        Summary = "Hikvision kayıt diski bulundu. Diski aç → kameralar/kayıtlar listelenecek."
                    };
            }
            catch { }

            // 3) Dahua DHFS (yeni nesil Dahua)
            step?.Invoke("Dahua DHFS imzası kontrol ediliyor…");
            try
            {
                if (DhfsReader.Detect(openRaw))
                    return new DvrDiagnosis
                    {
                        Brand = "Dahua",
                        Format = "DHFS (DHAV H.264/H.265)",
                        ReaderKey = "dhfs",
                        Recoverable = true,
                        Summary = "Dahua DHFS kayıt diski bulundu. Diski aç → kanallar/kayıtlar listelenecek."
                    };
            }
            catch { }

            // 4) Bilinen imza yok → örnekleme: MJPEG mi, ham H.26x mi, yoksa video yok mu?
            step?.Invoke("Video içeriği taranıyor (örnekleme)…");
            return DiagSample(openRaw, totalSize);
        }

        // --- uware için hızlı özet (kamera sayısı + tarih aralığı) ---
        private static DvrDiagnosis DiagUware(Func<int, Stream> openRaw, long totalSize)
        {
            var r = new DvrDiagnosis
            {
                Brand = "uware / PP (NVR)",
                Format = "PP slot + ham H.265 (chunk'lı)",
                ReaderKey = "uware",
                Recoverable = true
            };
            try
            {
                UwareInfo uw = UwareDvrReader.Parse(openRaw, totalSize, null);
                var cams = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                DateTime min = DateTime.MaxValue, max = DateTime.MinValue;
                foreach (UwareRecording rec in uw.Recordings)
                {
                    cams.Add(rec.Camera);
                    if (rec.Start < min) min = rec.Start;
                    if (rec.End > max) max = rec.End;
                }
                r.Cameras = cams.Count;
                if (uw.Recordings.Count > 0)
                {
                    r.DateRange = min.ToString("yyyy-MM-dd HH:mm") + " → " + max.ToString("yyyy-MM-dd HH:mm");
                    r.Summary = uw.Recordings.Count + " kayıt · " + cams.Count +
                                " kamera · " + r.DateRange + ". Kaydederken metadata otomatik sökülür (VLC'de tam oynar).";
                }
                else
                {
                    r.Recoverable = false;
                    r.Summary = "uware/PP imzası var ama okunabilir slot bulunamadı (disk boş ya da üzerine yazılmış olabilir).";
                }
            }
            catch (Exception ex) { r.Summary = "uware diski — hızlı tarama hatası: " + ex.Message; }
            return r;
        }

        // --- örnekleme: birkaç bölgeyi okuyup JPEG / H.26x yoğunluğuna bak ---
        private static DvrDiagnosis DiagSample(Func<int, Stream> openRaw, long totalSize)
        {
            long[] marks = { 4L << 30, 16L << 30, 64L << 30, 256L << 30, totalSize / 2 };
            const int WIN = 4 << 20;   // her bölgeden 4 MB
            int jpeg = 0, sc = 0, validNal = 0, samples = 0;
            byte[] buf = new byte[WIN];

            using (Stream s = openRaw(WIN))
            {
                foreach (long m in marks)
                {
                    if (m <= 0 || m + WIN > totalSize) continue;
                    s.Position = m;
                    int got = 0;
                    while (got < WIN) { int k = s.Read(buf, got, WIN - got); if (k <= 0) break; got += k; }
                    if (got < 1024) continue;
                    samples++;
                    for (int i = 0; i < got - 5; i++)
                    {
                        if (buf[i] == 0xFF && buf[i + 1] == 0xD8 && buf[i + 2] == 0xFF) jpeg++;
                        if (buf[i] == 0 && buf[i + 1] == 0 && buf[i + 2] == 0 && buf[i + 3] == 1)
                        {
                            sc++;
                            int t = (buf[i + 4] >> 1) & 0x3f;          // H265
                            int t4 = buf[i + 4] & 0x1f;                 // H264
                            if (t == 32 || t == 33 || t == 34 || t == 19 || t == 1 || t == 0 ||
                                t4 == 7 || t4 == 8 || t4 == 5 || t4 == 1) validNal++;
                        }
                    }
                }
            }

            var r = new DvrDiagnosis();
            if (samples == 0)
            {
                r.Summary = "Disk okunamadı ya da örneklenecek alan yok.";
                return r;
            }

            if (jpeg >= 15)
            {
                r.Brand = "MJPEG DVR / resim";
                r.Format = "JPEG kareler (carving)";
                r.ReaderKey = "mjpeg";
                r.Recoverable = true;
                r.Summary = "Çok sayıda JPEG kare bulundu (~" + jpeg + "/" + samples + " bölge). " +
                            "Eski MJPEG DVR ya da dağınık resim dosyaları olabilir — bir kareye bakıp doğrula. " +
                            "Carving ile çıkarılabilir.";
            }
            else if (sc >= 40 && validNal >= sc / 3)
            {
                r.Brand = "Bilinmeyen DVR";
                r.Format = "Ham H.264/H.265 (carving)";
                r.ReaderKey = "carve";
                r.Recoverable = true;
                r.Summary = "Ham H.264/H.265 video akışı bulundu (~" + sc + " start-code). " +
                            "Tanınan bir index yok; carving ile kareler çıkarılabilir.";
            }
            else
            {
                r.Brand = "—";
                r.Format = "—";
                r.Recoverable = false;
                r.Summary = "Tanınan DVR formatı veya video içeriği bulunamadı " +
                            "(JPEG=" + jpeg + ", start-code=" + sc + "). Disk boş, şifreli ya da desteklenmeyen bir format olabilir.";
            }
            return r;
        }
    }
}
