using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VKM.App
{
    // SQL & Veritabanı — TEŞHİS motoru (B2).
    // Dosyayı okur, SINIFINI belirler: fiziksel sağlam / eksik-sektör / ransomware / bilinmiyor.
    // Hiçbir şey yazmaz (salt okunur). Entropy + sıfır-run haritası + tip/sürüm → önerilen kurtarma yolu.
    public static class DbDiagnose
    {
        public sealed class Region { public long Off; public long Len; public string Kind = ""; }  // "enc" | "zero"

        public sealed class Result
        {
            public string Engine = "Bilinmiyor";   // MSSQL | Firebird | Bilinmiyor
            public string FileKind = "";
            public long Size;
            public int PageSize;
            public long PageCount;
            public string VersionHint = "";
            public double EncRatio, ZeroRatio, DataRatio;
            public bool EncAtStart;
            public List<Region> Regions = new();
            public string Class = "";
            public List<string> Recommend = new();
        }

        public static Result Diagnose(string path, Action<string> log, Action<double>? prog, CancellationToken tok)
        {
            var r = new Result();
            r.Size = new FileInfo(path).Length;
            r.FileKind = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            using var fs = File.OpenRead(path);
            byte[] head = new byte[65536];
            int hn = fs.Read(head, 0, head.Length);

            if (r.FileKind == "fdb" || r.FileKind == "gdb")
            {
                r.Engine = "Firebird";
                r.PageSize = FbPageSize(head, hn);
                r.PageCount = r.PageSize > 0 ? r.Size / r.PageSize : 0;
                log("Motor: Firebird  ·  sayfa boyutu ~" + r.PageSize + "  ·  dosya " + Mb(r.Size) + " MB");
            }
            else
            {
                r.Engine = "MSSQL";
                r.PageSize = 8192;
                r.PageCount = r.Size / 8192;
                r.VersionHint = MssqlVersion(fs);
                log("Motor: MSSQL  ·  8KB sayfa  ·  sayfa sayısı " + r.PageCount.ToString("N0") +
                    "  ·  dosya " + Mb(r.Size) + " MB");
                log("Sürüm: " + (string.IsNullOrEmpty(r.VersionHint) ? "okunamadı" : r.VersionHint));
            }

            // --- entropy + sıfır haritası (örnekleme) ---
            log("Entropy + bad-sektör haritası taranıyor…");
            const int WIN = 256 * 1024;
            long stride = Math.Max(2L << 20, r.Size / 4000);   // ~4000 örnek
            byte[] buf = new byte[WIN];
            long encB = 0, zeroB = 0, dataB = 0, sampled = 0;
            Region cur = null!;
            for (long p = 0; p < r.Size; p += stride)
            {
                if (tok.IsCancellationRequested) break;
                fs.Position = p;
                int got = fs.Read(buf, 0, WIN);
                if (got < 1024) continue;
                double e = Entropy(buf, got, out double zf);
                sampled += got;
                string kind = zf >= 0.97 ? "zero" : (e >= 7.90 ? "enc" : "data");
                if (kind == "zero") zeroB += got; else if (kind == "enc") { encB += got; if (p < r.Size / 10) r.EncAtStart = true; } else dataB += got;

                if (kind == "data") cur = null!;
                else if (cur != null && cur.Kind == kind && p - (cur.Off + cur.Len) <= stride)
                    cur.Len = (p + got) - cur.Off;
                else { cur = new Region { Off = p, Len = got, Kind = kind }; r.Regions.Add(cur); }

                prog?.Invoke((double)p / r.Size);
            }
            double tot = Math.Max(1, sampled);
            r.EncRatio = encB / tot; r.ZeroRatio = zeroB / tot; r.DataRatio = dataB / tot;

            Classify(r);
            return r;
        }

        private static void Classify(Result r)
        {
            r.Recommend.Clear();
            if (r.EncRatio > 0.50)
            {
                if (r.DataRatio > 0.15 || r.EncAtStart)
                {
                    r.Class = "RANSOMWARE — kısmi/baş şifreli (sağlam veri var)";
                    r.Recommend.Add("Sayfa-seviyesi çıkarma (B3): şifreli sayfaları atla, SAĞLAM sayfalardan tablo/satır kurtar");
                    r.Recommend.Add("Baş şifreliyse header'ı sağlam şablonla onar, gerisini carve et");
                }
                else
                {
                    r.Class = "RANSOMWARE — büyük ölçüde şifreli";
                    r.Recommend.Add("Anahtar yoksa tam kurtarma mümkün değil (dürüst sınır)");
                    r.Recommend.Add("Yine de sağlam kalan sayfalar varsa B3 ile kısmi kurtarılır");
                }
            }
            else if (r.EncRatio > 0.08 || (r.EncRatio > 0.01 && r.EncAtStart))
            {
                r.Class = "RANSOMWARE şüphesi — aralıklı şifreli bloklar";
                r.Recommend.Add("Sayfa-seviyesi çıkarma (B3): şifreli blokları atla, sağlamları topla");
            }
            else if (r.ZeroRatio > 0.03)
            {
                r.Class = "FİZİKSEL DELİK — eksik/bad sektör (kafa), torn page";
                r.Recommend.Add("Sağlam sayfalardan çıkar (B3/B5): hasarlı sayfayı atla — REPAIR'den az kayıp");
                r.Recommend.Add("CHECKDB REPAIR'i ilk değil SON çare yap (önce/sonra satır say)");
            }
            else
            {
                r.Class = "FİZİKSEL SAĞLAM — dosya bütün, muhtemelen sadece mount sorunu";
                if (r.Engine == "MSSQL")
                    r.Recommend.Add("LocalDB yolu (B1): .mdf(+.ldf) → attach / rebuild-log → .bak  [bu modülde hazır]");
                else
                    r.Recommend.Add("Firebird: gfix -mend / gbak restore (B8)");
            }
        }

        public static void Report(Result r, Action<string> log)
        {
            log("");
            log("══════ TEŞHİS SONUCU ══════");
            log("Motor      : " + r.Engine + (string.IsNullOrEmpty(r.VersionHint) ? "" : "  (" + r.VersionHint + ")"));
            log("Dosya      : ." + r.FileKind + "  ·  " + Mb(r.Size) + " MB  ·  " + r.PageCount.ToString("N0") + " sayfa");
            log(string.Format("Dağılım    : sağlam %{0:0.#}  ·  şifreli %{1:0.#}  ·  boş/bad %{2:0.#}",
                r.DataRatio * 100, r.EncRatio * 100, r.ZeroRatio * 100));
            if (r.Regions.Count > 0)
            {
                log("Sorunlu bölgeler (ilk 8):");
                int n = 0;
                foreach (var rg in r.Regions)
                {
                    if (n++ >= 8) { log("   … +" + (r.Regions.Count - 8) + " bölge daha"); break; }
                    log(string.Format("   {0}  @ {1} MB  ·  {2} MB",
                        rg.Kind == "enc" ? "şifreli" : "boş/bad", rg.Off / 1048576, Math.Max(1, rg.Len / 1048576)));
                }
            }
            log("");
            log("SINIF      : " + r.Class);
            log("ÖNERİLEN YOL:");
            foreach (var s in r.Recommend) log("   → " + s);
            log("═══════════════════════════");
        }

        // --- yardımcılar ---
        private static double Entropy(byte[] b, int len, out double zeroFrac)
        {
            int[] h = new int[256];
            for (int i = 0; i < len; i++) h[b[i]]++;
            double e = 0;
            for (int i = 0; i < 256; i++) { if (h[i] == 0) continue; double p = (double)h[i] / len; e -= p * Math.Log2(p); }
            zeroFrac = (double)h[0] / len;
            return e;
        }

        // Boot page (page 9 = 0x12000) içinde bilinen sürüm sabitini ara → tahmini SQL sürümü.
        private static readonly (int ver, string name)[] Vers =
        {
            (515,"SQL 2005"),(539,"SQL 2005"),(611,"SQL 2008"),(612,"SQL 2008"),(655,"SQL 2008 R2"),
            (661,"SQL 2008 R2"),(706,"SQL 2012"),(782,"SQL 2014"),(852,"SQL 2016"),(869,"SQL 2017"),
            (895,"SQL 2019"),(904,"SQL 2022")
        };
        private static string MssqlVersion(FileStream fs)
        {
            try
            {
                long bootOff = 9L * 8192;
                if (fs.Length < bootOff + 8192) return "";
                byte[] bp = new byte[8192];
                fs.Position = bootOff; fs.Read(bp, 0, 8192);
                // boot page'de dbi_version küçük bir LE 16-bit; bilinen sabitlerden birini ara
                for (int i = 0; i < 8190; i++)
                {
                    int v = bp[i] | (bp[i + 1] << 8);
                    foreach (var vv in Vers) if (vv.ver == v) return vv.name + " (tahmini, dbi_version=" + v + ")";
                }
                return "";
            }
            catch { return ""; }
        }

        private static int FbPageSize(byte[] head, int n)
        {
            try { if (n >= 0x12) { int ps = head[0x10] | (head[0x11] << 8); if (ps == 4096 || ps == 8192 || ps == 16384 || ps == 32768) return ps; } } catch { }
            return 8192;
        }

        private static long Mb(long b) => b / 1048576;
    }
}
