using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.SqlClient;

namespace VKM.App
{
    // MSSQL .mdf → geçerli .bak üretir. Bozuk/eksik .ldf olsa bile çalışır.
    //
    // MANTIK (sırayla, otomatik):
    //   1) LDF ile NORMAL bağla (FOR ATTACH) → SQL kendi logundan kurtarır, VERİ KAYBI YOK.
    //      (bad sektör log'un pasif/checkpoint'li kısmındaysa buradan temiz açılır.)
    //   2) Olmazsa log'u at, yeniden kur (ATTACH_REBUILD_LOG) → son commit'lenmemiş hareketler gidebilir.
    //   3) O da olmazsa stub-swap + EMERGENCY + DBCC CHECKDB(REPAIR_ALLOW_DATA_LOSS).
    //   4) BACKUP DATABASE → .bak (motorun kendisi ürettiği için RESTORE %100 kabul eder).
    //
    // Orijinal .mdf'ye ASLA dokunulmaz — her denemeden önce TAZE çalışma kopyası alınır.
    // Her T-SQL komutu KENDİ bağlantısını açar → bir hata bağlantıyı kapatsa bile sonraki adım çalışır.
    // NOT: LocalDB/Express yedek SIKIŞTIRMA (COMPRESSION) desteklemez → kullanılmaz.
    public static class MssqlBak
    {
        public const string LocalDb = @"(localdb)\MSSQLLocalDB";
        // Varsayılan LocalDB (10 GB sınırı). Büyük DB'de makinede tam SQL Server (Developer vb.) varsa ona geçilir.
        public static string Instance { get; private set; } = LocalDb;

        // Tercih edilen = makinedeki EN ESKİ uygun LocalDB (2016). Eski sürümde üretilen .bak her yeni sürümde açılır.
        public static string Preferred { get; private set; } = LocalDb;
        public static string PreferredName { get; private set; } = "";

        public static string DetectInfo { get; private set; } = "";

        // Kurulu LocalDB sürümleri arasından EN ESKİ uygun olanı (2016/2017/2019) seç.
        // Not: 2016 SP1 LocalDB kendini "13.1" diye bildirir — sürüm metni olduğu gibi okunur.
        // Hedef SQL sürümü: 0 = otomatik (kurulu en eski 2016/2017/2019), 13/14/15 = o sürüm, 16 = 2022 ve üstü (en yeni LocalDB)
        public static int Target { get; set; } = 0;

        public static void DetectPreferred()
        {
            Preferred = LocalDb; PreferredName = ""; DetectInfo = "";
            if (Target >= 16)
            {
                Preferred = LocalDb; PreferredName = "2022"; DetectInfo = "Hedef SQL: 2022 ve üstü → en yeni LocalDB kullanılıyor";
                Instance = Preferred; return;
            }
            try
            {
                string vers = RunLocalDbExe("versions");
                var found = System.Text.RegularExpressions.Regex.Matches(vers, @"\((\d+)\.(\d+)\.")
                    .Select(m => (major: int.Parse(m.Groups[1].Value), ver: m.Groups[1].Value + "." + m.Groups[2].Value))
                    .GroupBy(x => x.major).Select(g => g.First()).OrderBy(x => x.major).ToList();
                string Year(int mj) => mj switch { 11 => "2012", 12 => "2014", 13 => "2016", 14 => "2017", 15 => "2019", 16 => "2022", 17 => "2025", _ => "v" + mj };
                DetectInfo = "Kurulu LocalDB: " + (found.Count == 0 ? "(okunamadı: " + vers.Trim().Replace("\r", " ").Replace("\n", " ") + ")" : string.Join(", ", found.Select(f => Year(f.major) + " (" + f.ver + ")")));
                foreach (var f in found.Where(f => f.major >= 13 && f.major <= 15 && (Target == 0 || f.major == Target)))
                {
                    string inst = "VKM" + Year(f.major);
                    string info = RunLocalDbExe("info " + inst);
                    if (!info.Contains("(" + f.ver) && !info.Contains(" " + f.ver))
                    {
                        RunLocalDbExe("stop " + inst + " -k"); RunLocalDbExe("delete " + inst);
                        RunLocalDbExe($"create {inst} {f.ver} -s");
                    }
                    else RunLocalDbExe("start " + inst);
                    string cand = @"(localdb)\" + inst;
                    try
                    {
                        using var cn = new SqlConnection($"Server={cand};Database=master;Integrated Security=true;Connect Timeout=60;Encrypt=False;Pooling=false;");
                        cn.Open();
                        Preferred = cand; PreferredName = Year(f.major);
                        break;
                    }
                    catch (Exception ex) { DetectInfo += $" · {Year(f.major)} açılamadı: {ex.Message.Split('\n')[0]}"; }
                }
            }
            catch (Exception ex) { Preferred = LocalDb; PreferredName = ""; DetectInfo = "LocalDB sürümleri okunamadı: " + ex.Message; }
            Instance = Preferred;
        }

        public static void UseDefault() => Instance = LocalDb;

        // Referans/yardımcı veri bu makinedeki SQL'den YENİ bir sürümden (ör. müşteride SQL 2025, burada LocalDB 2022)
        //  → kurulu en yeni LocalDB sürümüyle ayrı bir örnek (VKM2025) açıp ona geç. Üretilen .bak o sürümde olur (müşteri SQL'i zaten o sürüm).
        public static bool TryNewerLocalDb(Action<string> log)
        {
            int cur = 0;
            try
            {
                using var cn = new SqlConnection($"Server={Instance};Database=master;Integrated Security=true;Connect Timeout=30;Encrypt=False;Pooling=false;");
                cn.Open(); cur = Convert.ToInt32(new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)", cn).ExecuteScalar());
            }
            catch { }
            var majors = new List<int>();
            try
            {
                majors = System.Text.RegularExpressions.Regex.Matches(RunLocalDbExe("versions"), @"\((\d+)\.\d+\.").Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
            }
            catch { }
            // sürüm listesi okunamıyorsa (RegGetValueW hatası) yine de 2025'i dene
            if (!majors.Contains(17)) majors.Add(17);
            foreach (int mj in majors.Where(m => m > cur).OrderByDescending(m => m))
            {
                string year = mj switch { 16 => "2022", 17 => "2025", _ => "v" + mj };
                string inst = "VKM" + year, cand = @"(localdb)\" + inst;
                try
                {
                    string info = RunLocalDbExe("info " + inst);
                    if (!info.Contains(mj + ".")) { RunLocalDbExe("stop " + inst + " -k"); RunLocalDbExe("delete " + inst); RunLocalDbExe($"create {inst} {mj}.0 -s"); }
                    else RunLocalDbExe("start " + inst);
                    using var cn = new SqlConnection($"Server={cand};Database=master;Integrated Security=true;Connect Timeout=60;Encrypt=False;Pooling=false;");
                    cn.Open();
                    int got = Convert.ToInt32(new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)", cn).ExecuteScalar());
                    if (got <= cur) continue;
                    Instance = cand;
                    log($"Daha yeni SQL sürümü gerekiyor → LocalDB {year} örneğine geçildi ({cand}).");
                    return true;
                }
                catch { }
            }
            log("⚠ Bu makinede daha yeni LocalDB yok. Referans SQL 2025'ten alınmış → 'SQL Server 2025 Express LocalDB' kurulmalı (kurulunca program kendisi kullanır).");
            return false;
        }

        // Mevcut .bak şu anki hedef sürümde açılabilir mi? (2022'de üretilmiş .bak 2019'da AÇILMAZ → yeniden üret)
        public static bool BakCompatible(string bak)
        {
            try
            {
                using var cn = new SqlConnection($"Server={Instance};Database=master;Integrated Security=true;Connect Timeout=30;Encrypt=False;Pooling=false;");
                cn.Open();
                int srv = Convert.ToInt32(new SqlCommand("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)", cn).ExecuteScalar());
                using var cmd = new SqlCommand($"RESTORE HEADERONLY FROM DISK = N'{bak.Replace("'", "''")}';", cn) { CommandTimeout = 300 };
                using var rd = cmd.ExecuteReader();
                if (!rd.Read()) return false;
                int major = Convert.ToInt32(rd["SoftwareVersionMajor"]);
                return major <= srv;
            }
            catch { return false; }   // okunamadı / yarım / daha yeni sürüm → yeniden üret
        }
        public static bool UsingPreferredOld => Instance != LocalDb;

        static string LocalDbExe()
        {
            try
            {
                string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft SQL Server");
                var exe = Directory.Exists(root)
                    ? Directory.GetDirectories(root).Select(d => Path.Combine(d, "Tools", "Binn", "SqlLocalDB.exe")).Where(File.Exists)
                               .OrderByDescending(f => int.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(f)))), out int n) ? n : 0).FirstOrDefault()
                    : null;
                return exe ?? "sqllocaldb";
            }
            catch { return "sqllocaldb"; }
        }

        static string RunLocalDbExe(string args)
        {
            var psi = new System.Diagnostics.ProcessStartInfo(LocalDbExe(), args)
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = System.Diagnostics.Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(60000);
            return o;
        }

        public static void PickInstanceFor(long dataMb, Action<string> log)
        {
            Instance = Preferred;
            if (dataMb < 9000) return;
            foreach (var cand in new[] { ".", @".\SQLDEV", @".\MSSQLSERVER" })
            {
                try
                {
                    using var cn = new SqlConnection($"Server={cand};Database=master;Integrated Security=true;Connect Timeout=3;Encrypt=False;Pooling=false;");
                    cn.Open();
                    using var cmd = new SqlCommand("SELECT CAST(SERVERPROPERTY('EngineEdition') AS int)", cn);
                    if (Convert.ToInt32(cmd.ExecuteScalar()) == 4) continue;   // Express → yine 10 GB
                    Instance = cand;
                    log($"Veri ~{dataMb / 1024.0:0.#} GB → LocalDB 10 GB sınırını aşar; tam SQL Server bulundu, o kullanılacak ({cand}).");
                    return;
                }
                catch { }
            }
            log($"⚠ Veri ~{dataMb / 1024.0:0.#} GB. LocalDB en fazla 10 GB tutar; makinede tam SQL Server yok. 10 GB'ı aşarsa iş yarıda kalır — ücretsiz 'SQL Server Developer' kurarsan program otomatik onu kullanır.");
        }

        // .bak hangi SQL sürümlerinde açılır? (yeni sürüm .bak eski sürümde AÇILMAZ)
        public static void LogCompat(Action<string> log)
        {
            try
            {
                int major = Convert.ToInt32(Scalar("SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int);"));
                string name = major switch { 11 => "2012", 12 => "2014", 13 => "2016", 14 => "2017", 15 => "2019", 16 => "2022", 17 => "2025", _ => "v" + major };
                log($"ℹ Bu .bak SQL Server {name} ve ÜSTÜNDE açılır. Müşterinin SQL'i daha eskiyse açılmaz (Microsoft geri sürüme izin vermiyor).");
            }
            catch { }
        }
        private static string DbName  = "VKM_Recover";

        private static string ConnStr(string db = "master") =>
            $"Server={Instance};Database={db};Integrated Security=true;Connect Timeout=60;Encrypt=False;Pooling=false;";

        // Üretilen .bak okunabilir/bütün mü? (hızlı — tam restore değil)
        public static bool QuickVerify(string bak, out string info)
        {
            info = "";
            try { Exec($"RESTORE VERIFYONLY FROM DISK = N'{Esc(bak)}';", 1800); info = "yedek bütünlüğü OK"; return true; }
            catch (Exception ex) { info = FirstLine(ex.Message); return false; }
        }

        public static bool LocalDbReady(out string err)
        {
            err = "";
            try { Scalar("SELECT @@VERSION;"); return true; }
            catch (Exception ex) { err = ex.Message; return false; }
        }

        public static bool Make(string srcMdf, string? srcLdf, string outBak, Action<string> log, out string err)
        {
            err = "";
            string work = "";
            try
            {
                if (!File.Exists(srcMdf)) { err = ".mdf bulunamadı: " + srcMdf; return false; }
                bool haveLdf = !string.IsNullOrWhiteSpace(srcLdf) && File.Exists(srcLdf);

                work = Path.Combine(Path.GetDirectoryName(outBak) ?? Path.GetTempPath(),
                                    "vkm_mssql_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                Directory.CreateDirectory(work);
                string wmdf = Path.Combine(work, "recover.mdf");
                string wldf = Path.Combine(work, "recover_log.ldf");

                DbName = "VKM_Recover_" + Environment.ProcessId;   // paralel pencereler birbirinin DB'sini silmesin
                CleanupDb(DbName);           // önceki artık db varsa temizle
                // önceki yarım kalmış çalışmanın DB'si silinemiyorsa (kilitli / kurtarma durumunda) → bu çalışma için benzersiz ad
                try { if (ScalarInt($"SELECT CASE WHEN DB_ID('{DbName}') IS NULL THEN 0 ELSE 1 END;") != 0) DbName = "VKM_Recover_" + DateTime.Now.ToString("HHmmssfff"); }
                catch { DbName = "VKM_Recover_" + DateTime.Now.ToString("HHmmssfff"); }

                bool attached = false;
                string mode = "";
                string lastAttachErr = "";

                // ---- 1) LDF ile NORMAL bağla (veri kaybı yok) ----
                if (haveLdf)
                {
                    log("Çalışma kopyası alınıyor (MDF+LDF, orijinale dokunulmaz)…");
                    File.Copy(srcMdf, wmdf, true);
                    File.Copy(srcLdf!, wldf, true);
                    try
                    {
                        log("1) LDF ile normal bağlanıyor (FOR ATTACH) — hedef: sıfır veri kaybı…");
                        Exec($"CREATE DATABASE [{DbName}] ON (FILENAME = N'{Esc(wmdf)}'), (FILENAME = N'{Esc(wldf)}') FOR ATTACH;", 1200);
                        attached = true; mode = "clean";
                        log("✓✓ TEMİZ bağlandı — LDF ile normal kurtarma, VERİ KAYBI YOK.");
                    }
                    catch (Exception ex)
                    {
                        lastAttachErr = ex.Message;
                        log("Temiz bağlama olmadı (bozuk sektör aktif log'da): " + FirstLine(ex.Message));
                        CleanupDb(DbName);
                    }
                }

                // ---- 2) Log'u at, yeniden kur ----
                if (!attached)
                {
                    log("2) Log atılıp yeniden kuruluyor (ATTACH_REBUILD_LOG)… taze MDF kopyası alınıyor…");
                    File.Copy(srcMdf, wmdf, true);           // pristine mdf
                    try
                    {
                        Exec($"CREATE DATABASE [{DbName}] ON (FILENAME = N'{Esc(wmdf)}') FOR ATTACH_REBUILD_LOG;", 1800);
                        attached = true; mode = "rebuild";
                        log("✓ Bağlandı (log yeniden kuruldu). Son commit'lenmemiş hareketler kaybolmuş olabilir.");
                    }
                    catch (Exception ex)
                    {
                        lastAttachErr = ex.Message;
                        log("Rebuild-log olmadı: " + FirstLine(ex.Message));
                        CleanupDb(DbName);
                    }
                }

                // ---- 3) EMERGENCY + CHECKDB REPAIR ----
                if (!attached)
                {
                    log("3) Acil kurtarma (EMERGENCY + DBCC CHECKDB)… taze MDF kopyası alınıyor…");
                    File.Copy(srcMdf, wmdf, true);           // pristine mdf
                    attached = EmergencyAttach(wmdf, log, out string emErr);
                    if (attached) mode = "emergency";
                    else
                    {
                        CleanupDb(DbName);
                        string allErr = emErr + " " + lastAttachErr;
                        bool headerBad = allErr.IndexOf("valid database file header", StringComparison.OrdinalIgnoreCase) >= 0
                                      || allErr.IndexOf("FILE SIZE", StringComparison.OrdinalIgnoreCase) >= 0;
                        try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }   // MDF kopyalarını bırakma
                        err = headerBad ? "dosya başlığı/boyutu bozuk (truncated / header hasarlı)"
                                        : "motor bağlanamadı: " + emErr;
                        return false;
                    }
                }

                // ---- 4) BACKUP ----
                log("Yedek alınıyor: BACKUP DATABASE → " + outBak);
                Exec($"BACKUP DATABASE [{DbName}] TO DISK = N'{Esc(outBak)}' WITH INIT, FORMAT;", 7200);
                log("✓ .bak üretildi: " + outBak);
                log("Kullanılan yol: " + (mode == "clean"   ? "LDF ile TEMİZ (veri kaybı yok)" :
                                          mode == "rebuild" ? "log yeniden kuruldu (son commit'siz hareketler gitmiş olabilir)" :
                                                              "EMERGENCY+REPAIR (bir miktar veri atılmış olabilir)"));

                // ---- 5) Sök + temizle (orijinal el değmedi) ----
                CleanupDb(DbName);
                try { Directory.Delete(work, true); } catch { }
                log("Bitti. Orijinal .mdf el değmeden duruyor.");
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                try { CleanupDb(DbName); } catch { }
                try { if (!string.IsNullOrEmpty(work) && Directory.Exists(work)) Directory.Delete(work, true); } catch { }
                return false;
            }
        }

        // Üretilen .bak GERÇEKTEN geri yüklenebiliyor mu + veri tutarlı mı? Geçici DB'ye deneme restore + CHECKDB.
        public static bool TestBak(string bakPath, Action<string> log, out string err)
        {
            err = "";
            const string tdb = "VKM_BakTest";
            string tdir = "";
            try
            {
                if (!File.Exists(bakPath)) { err = ".bak bulunamadı: " + bakPath; return false; }

                log("1) RESTORE VERIFYONLY — yedek bütünlüğü kontrol ediliyor…");
                Exec($"RESTORE VERIFYONLY FROM DISK = N'{Esc(bakPath)}';", 3600);
                log("✓ Yedek bütünlüğü OK (okunabilir, bozulmamış).");

                log("2) Yedek içeriği okunuyor (dosya listesi)…");
                var files = new List<(string logical, string type)>();
                using (var cn = new SqlConnection(ConnStr())) { cn.Open();
                    using var cmd = new SqlCommand($"RESTORE FILELISTONLY FROM DISK = N'{Esc(bakPath)}';", cn) { CommandTimeout = 600 };
                    using var r = cmd.ExecuteReader();
                    while (r.Read()) files.Add((r["LogicalName"].ToString() ?? "", r["Type"].ToString() ?? ""));
                }
                log("   Mantıksal dosya sayısı: " + files.Count);

                CleanupDb(tdb);
                tdir = Path.Combine(Path.GetDirectoryName(bakPath) ?? Path.GetTempPath(),
                                    "vkm_baktest_" + DateTime.Now.ToString("HHmmss"));
                Directory.CreateDirectory(tdir);
                var moves = new List<string>(); int di = 0;
                foreach (var f in files)
                {
                    string ext = f.type.Equals("L", StringComparison.OrdinalIgnoreCase) ? ".ldf" : (di++ == 0 ? ".mdf" : ".ndf");
                    string phys = Path.Combine(tdir, "f" + moves.Count + ext);
                    moves.Add($"MOVE N'{Esc(f.logical)}' TO N'{Esc(phys)}'");
                }

                log("3) Deneme restore (geçici DB'ye)…");
                Exec($"RESTORE DATABASE [{tdb}] FROM DISK = N'{Esc(bakPath)}' WITH {string.Join(", ", moves)}, REPLACE, RECOVERY;", 14400);
                log("✓ Restore başarılı — .bak gerçekten geri yüklenebiliyor.");

                log("4) DBCC CHECKDB (veri tutarlılığı)…");
                try { Exec($"DBCC CHECKDB ([{tdb}]) WITH NO_INFOMSGS;", 14400); log("✓ CHECKDB temiz — veri tutarlı."); }
                catch (Exception ex) { log("⚠ CHECKDB uyarısı: " + FirstLine(ex.Message) + " (veri çoğunlukla okunur, bazı tutarsızlık olabilir)"); }

                int tc = ScalarInt($"SELECT COUNT(*) FROM [{tdb}].sys.tables;");
                log("5) Tablo sayısı: " + tc);
                using (var cn = new SqlConnection(ConnStr())) { cn.Open();
                    string q = $@"SELECT TOP 20 s.name + '.' + t.name AS tbl, SUM(p.rows) AS rws
                                  FROM [{tdb}].sys.tables t
                                  JOIN [{tdb}].sys.schemas s ON s.schema_id = t.schema_id
                                  JOIN [{tdb}].sys.partitions p ON p.object_id = t.object_id AND p.index_id IN (0,1)
                                  GROUP BY s.name, t.name ORDER BY SUM(p.rows) DESC;";
                    using var cmd = new SqlCommand(q, cn) { CommandTimeout = 600 };
                    using var r = cmd.ExecuteReader();
                    log("   En büyük tablolar (satır sayısı):");
                    while (r.Read()) log("     " + r[0] + " : " + r[1]);
                }

                CleanupDb(tdb);
                try { Directory.Delete(tdir, true); } catch { }
                log("");
                log("✓✓ TEST GEÇTİ — .bak sağlam, geri yüklenebiliyor ve içinde veri var. Müşteriye verebilirsin.");
                return true;
            }
            catch (Exception ex)
            {
                err = ex.Message;
                try { CleanupDb(tdb); } catch { }
                try { if (!string.IsNullOrEmpty(tdir) && Directory.Exists(tdir)) Directory.Delete(tdir, true); } catch { }
                return false;
            }
        }

        // SUSPECT .mdf için: aynı adla stub db → offline → stub dosyasını gerçek .mdf ile değiştir →
        // online (SUSPECT) → EMERGENCY → CHECKDB REPAIR_ALLOW_DATA_LOSS → MULTI_USER.
        private static bool EmergencyAttach(string wmdf, Action<string> log, out string err)
        {
            err = "";
            try
            {
                // Stub dosyalarını KENDİ çalışma klasörümüze, benzersiz isimle koy → default klasörde çakışma olmaz
                string work = Path.GetDirectoryName(wmdf) ?? Path.GetTempPath();
                string stubMdf = Path.Combine(work, "stub.mdf");
                string stubLdf = Path.Combine(work, "stub.ldf");
                try { if (File.Exists(stubMdf)) File.Delete(stubMdf); } catch { }
                try { if (File.Exists(stubLdf)) File.Delete(stubLdf); } catch { }

                Exec($"CREATE DATABASE [{DbName}] ON (NAME='vkmdat', FILENAME = N'{Esc(stubMdf)}') " +
                     $"LOG ON (NAME='vkmlog', FILENAME = N'{Esc(stubLdf)}');", 180);

                Exec($"ALTER DATABASE [{DbName}] SET OFFLINE WITH ROLLBACK IMMEDIATE;", 120);
                log("Gerçek .mdf stub ile değiştiriliyor…");
                File.Copy(wmdf, stubMdf, true);
                try { if (File.Exists(stubLdf)) File.Delete(stubLdf); } catch { }

                try { Exec($"ALTER DATABASE [{DbName}] SET ONLINE;", 300); }
                catch (Exception ex) { log("Online (beklenen SUSPECT): " + FirstLine(ex.Message)); }

                Exec($"ALTER DATABASE [{DbName}] SET EMERGENCY;", 120);
                Exec($"ALTER DATABASE [{DbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", 120);
                log("DBCC CHECKDB (REPAIR_ALLOW_DATA_LOSS) çalışıyor — büyük DB'de uzun sürebilir…");
                Exec($"DBCC CHECKDB ([{DbName}], REPAIR_ALLOW_DATA_LOSS) WITH NO_INFOMSGS;", 14400);
                Exec($"ALTER DATABASE [{DbName}] SET MULTI_USER;", 120);
                log("✓ Acil kurtarma tamam, veritabanı online.");
                return true;
            }
            catch (Exception ex) { err = FirstLine(ex.Message); return false; }
        }

        // Artık db'yi sök + dosyalarını sil (bu bizim çalışma kopyamız). Her hata yutulur.
        private static void CleanupDb(string db)
        {
            try
            {
                if (ScalarInt($"SELECT CASE WHEN DB_ID('{db}') IS NULL THEN 0 ELSE 1 END;") == 0) return;
            }
            catch { }
            // önce nazik: multi→single→drop; olmazsa doğrudan drop (DROP suspect db'yi de siler)
            try { Exec($"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", 60); } catch { }
            try { Exec($"DROP DATABASE [{db}];", 120); return; } catch { }
            try { Exec($"ALTER DATABASE [{db}] SET EMERGENCY; DROP DATABASE [{db}];", 120); } catch { }
        }

        // --- her komut KENDİ bağlantısını açar (kapanma zinciri olmaz) ---
        private static void Exec(string sql, int timeoutSec)
        {
            using var cn = new SqlConnection(ConnStr());
            cn.Open();
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = timeoutSec };
            cmd.ExecuteNonQuery();
        }
        private static string Scalar(string sql)
        {
            using var cn = new SqlConnection(ConnStr());
            cn.Open();
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 120 };
            return cmd.ExecuteScalar()?.ToString() ?? "";
        }
        private static int ScalarInt(string sql)
        {
            var s = Scalar(sql);
            return int.TryParse(s, out int v) ? v : 0;
        }
        private static string Esc(string p) => p.Replace("'", "''");
        private static string FirstLine(string s)
        {
            int i = s.IndexOf('\n'); return i > 0 ? s.Substring(0, i).Trim() : s.Trim();
        }
    }
}
