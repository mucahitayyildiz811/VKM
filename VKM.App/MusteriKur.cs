using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace VKM.App
{
    // MÜŞTERİDE GERİ YÜKLE — üretilen .bak'ları müşterinin kendi SQL Server'ına tek düğmeyle kurar.
    //  • Bilgisayardaki SQL örneklerini bulur; Orka (ORKA…) veritabanları hangisindeyse onu seçer.
    //  • Açılamayan eski kayıtları (Recovery Pending: ransomware dosya adını değiştirdiği için) ve
    //    yarım kalmış VKM_Rebuild/VKM_Ref geçicilerini temizler.
    //  • Sağlam (ONLINE) bir veritabanının üzerine yazmadan önce onun yedeğini alır (VKM_eski_yedek klasörü).
    //  • Geri yükleme sonrası: SQL girişlerini (login) her veritabanına kullanıcı + db_owner olarak bağlar,
    //    eski sunucudan gelen kimliği uyuşmayan (orphan) kullanıcıları düzeltir → program "Cannot open database … login failed" demez.
    public static class MusteriKur
    {
        public static List<string> LocalInstances()
        {
            var res = new List<string>();
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                try
                {
                    using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var k = hk.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                    if (k == null) continue;
                    foreach (var n in k.GetValueNames())
                    {
                        string s = n.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? "." : @".\" + n;
                        if (!res.Contains(s, StringComparer.OrdinalIgnoreCase)) res.Add(s);
                    }
                }
                catch { }
            return res;
        }

        public static string Cs(string server, string? user, string? pass, string db = "master") =>
            new SqlConnectionStringBuilder
            {
                DataSource = server, InitialCatalog = db, TrustServerCertificate = true, Encrypt = false, ConnectTimeout = 8,
                IntegratedSecurity = string.IsNullOrEmpty(user), UserID = user ?? "", Password = pass ?? "",
                Pooling = false
            }.ConnectionString;

        static void Exec(string cs, string sql, int timeout = 3600)
        {
            using var cn = new SqlConnection(cs); cn.Open();
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = timeout };
            cmd.ExecuteNonQuery();
        }

        static List<Dictionary<string, object>> Rows(string cs, string sql, int timeout = 600)
        {
            var res = new List<Dictionary<string, object>>();
            using var cn = new SqlConnection(cs); cn.Open();
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = timeout };
            using var rd = cmd.ExecuteReader();
            while (rd.Read())
            {
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < rd.FieldCount; i++) d[rd.GetName(i)] = rd.GetValue(i);
                res.Add(d);
            }
            return res;
        }

        static string Q(string n) => "[" + n.Replace("]", "]]") + "]";
        static string L(string s) => s.Replace("'", "''");
        static string First(string s) { s = (s ?? "").Replace("\r", " "); int i = s.IndexOf('\n'); return (i > 0 ? s[..i] : s).Trim(); }

        // Bağlanılabilen ve içinde en çok ORKA… veritabanı olan örnek
        public static string? PickServer(string? user, string? pass, Action<string> log, out bool authFailed)
        {
            authFailed = false;
            var list = LocalInstances();
            if (list.Count == 0) list.Add(".");
            string? best = null; int bestN = -1;
            foreach (var s in list)
            {
                try
                {
                    var r = Rows(Cs(s, user, pass), "SELECT COUNT(*) AS n FROM sys.databases WHERE name LIKE 'ORKA%';", 30);
                    int n = Convert.ToInt32(r[0]["n"]);
                    log($"  SQL örneği {s}: bağlanıldı · {n} ORKA veritabanı kayıtlı");
                    if (n > bestN) { bestN = n; best = s; }
                }
                catch (SqlException ex) when (ex.Number == 18456) { authFailed = true; log($"  SQL örneği {s}: giriş reddedildi"); }
                catch (Exception ex) { log($"  SQL örneği {s}: bağlanılamadı ({First(ex.Message)})"); }
            }
            return best;
        }

        public static bool Run(string server, string? user, string? pass, List<string> baks, Action<string> log)
        {
            string m = Cs(server, user, pass);
            var info = Rows(m, @"SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int) AS v,
                                        CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(400)) AS dp,
                                        CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(400)) AS lp;", 30)[0];
            int ver = Convert.ToInt32(info["v"]);
            string dataDir = Convert.ToString(info["dp"]) ?? "", logDir = Convert.ToString(info["lp"]) ?? "";
            if (dataDir.Length == 0)
                dataDir = Convert.ToString(Rows(m, "SELECT TOP 1 LEFT(physical_name, LEN(physical_name) - CHARINDEX('\\', REVERSE(physical_name)) + 1) AS d FROM sys.master_files WHERE database_id = 1 AND file_id = 1;", 30)[0]["d"]) ?? "";
            if (logDir.Length == 0) logDir = dataDir;
            log($"Sunucu: {server} · SQL sürüm {ver} · veri klasörü: {dataDir}");

            // 1) açılamayan eski kayıtlar + yarım kalmış VKM geçicileri
            var names = baks.Select(DbNameOf).ToList();
            foreach (var r in Rows(m, "SELECT name, state_desc FROM sys.databases WHERE database_id > 4;"))
            {
                string n = Convert.ToString(r["name"]) ?? "", st = Convert.ToString(r["state_desc"]) ?? "";
                bool vkmTmp = n.StartsWith("VKM_Rebuild_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("VKM_Ref_", StringComparison.OrdinalIgnoreCase);
                if (st == "ONLINE" && !vkmTmp) continue;
                if (st == "ONLINE" && vkmTmp) { TryDrop(m, n, log, "VKM geçici veritabanı"); continue; }
                if (vkmTmp || names.Contains(n, StringComparer.OrdinalIgnoreCase)) TryDrop(m, n, log, $"açılamayan eski kayıt ({st})");
                else log($"  ⚠ {n}: durumu {st} — bunun için .bak seçilmedi, dokunulmadı.");
            }

            int ok = 0;
            string safeDir = Path.Combine(dataDir, "VKM_eski_yedek");
            string stage = Path.Combine(dataDir, "VKM_geri_yukle");
            foreach (var bak in baks)
            {
                string db = DbNameOf(bak);
                log("");
                log($"── {db}  ←  {Path.GetFileName(bak)}");
                string? staged = null;
                try
                {
                    // SQL servisi masaüstündeki dosyayı okuyamayabilir → kendi veri klasörünün altına kopyala
                    string src = bak;
                    try { Directory.CreateDirectory(stage); staged = Path.Combine(stage, Path.GetFileName(bak)); File.Copy(bak, staged, true); src = staged; }
                    catch { staged = null; }

                    var head = Rows(m, $"RESTORE HEADERONLY FROM DISK = N'{L(src)}';")[0];
                    int bv = Convert.ToInt32(head["SoftwareVersionMajor"]);
                    if (bv > ver) { log($"  ✗ Bu .bak SQL {bv} sürümünden, sunucu {ver} → açılamaz (Microsoft geri sürüme izin vermez)."); continue; }

                    // aynı adla SAĞLAM veritabanı varsa: önce onun yedeği
                    var ex = Rows(m, $"SELECT state_desc FROM sys.databases WHERE name = N'{L(db)}';");
                    if (ex.Count > 0 && Convert.ToString(ex[0]["state_desc"]) == "ONLINE")
                    {
                        Directory.CreateDirectory(safeDir);
                        string sb = Path.Combine(safeDir, $"{db}_{DateTime.Now:yyyyMMdd_HHmmss}.bak");
                        try { Exec(m, $"BACKUP DATABASE {Q(db)} TO DISK = N'{L(sb)}' WITH COPY_ONLY, INIT;", 7200); log($"  Mevcut {db} önce yedeklendi → {sb}"); }
                        catch (Exception e2) { log($"  ⚠ mevcut {db} yedeklenemedi ({First(e2.Message)}) — yine de üzerine yazılıyor."); }
                        try { Exec(m, $"ALTER DATABASE {Q(db)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", 120); } catch { }
                    }

                    var files = Rows(m, $"RESTORE FILELISTONLY FROM DISK = N'{L(src)}';");
                    var moves = new List<string>(); int dn = 0, ln = 0;
                    foreach (var f in files)
                    {
                        string logical = Convert.ToString(f["LogicalName"]) ?? "", type = (Convert.ToString(f["Type"]) ?? "D").Trim();
                        string target = type == "L"
                            ? Path.Combine(logDir, db + (ln++ == 0 ? "_log.ldf" : $"_log{ln}.ldf"))
                            : Path.Combine(dataDir, db + (dn++ == 0 ? ".mdf" : $"_{dn}.ndf"));
                        moves.Add($"MOVE N'{L(logical)}' TO N'{L(target)}'");
                    }
                    Exec(m, $"RESTORE DATABASE {Q(db)} FROM DISK = N'{L(src)}' WITH {string.Join(", ", moves)}, REPLACE, RECOVERY;", 14400);
                    try { Exec(m, $"ALTER DATABASE {Q(db)} SET MULTI_USER;", 120); } catch { }
                    log("  ✓ geri yüklendi");
                    FixUsers(m, server, user, pass, db, log);
                    ok++;
                }
                catch (Exception e)
                {
                    log($"  ✗ geri yüklenemedi: {First(e.Message)}");
                    try { Exec(m, $"IF DB_ID(N'{L(db)}') IS NOT NULL ALTER DATABASE {Q(db)} SET MULTI_USER;", 60); } catch { }
                }
                finally { if (staged != null) try { File.Delete(staged); } catch { } }
            }
            try { if (Directory.Exists(stage) && !Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage); } catch { }
            log("");
            log($"══════ {ok}/{baks.Count} veritabanı müşterinin SQL Server'ına kuruldu. Program kapalıysa açıp giriş yapılabilir. ══════");
            return ok == baks.Count;
        }

        static void TryDrop(string m, string n, Action<string> log, string why)
        {
            try
            {
                try { Exec(m, $"ALTER DATABASE {Q(n)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;", 60); } catch { }
                Exec(m, $"DROP DATABASE {Q(n)};", 300);
                log($"  {n}: {why} → kaldırıldı");
            }
            catch (Exception e) { log($"  ⚠ {n} kaldırılamadı ({First(e.Message)})"); }
        }

        // .bak'ın içindeki ad (VKM_… ise dosya adından; sondaki _yyyymmdd_hhmmss atılır)
        public static string DbNameOf(string bak)
        {
            string n = "";
            n = Path.GetFileNameWithoutExtension(bak);
            n = System.Text.RegularExpressions.Regex.Replace(n, @"_\d{8}_\d{6}$", "");
            return n;
        }

        // SQL girişlerini veritabanına bağla: kimliği uyuşmayan kullanıcıyı aynı adlı girişe eşle, eksikleri yarat, db_owner yap
        static void FixUsers(string m, string server, string? user, string? pass, string db, Action<string> log)
        {
            var logins = Rows(m, @"SELECT name, sid FROM sys.server_principals
                                  WHERE type IN ('S','U') AND is_disabled = 0 AND name NOT LIKE '##%' AND name NOT LIKE 'NT %' AND name <> 'sa';");
            string d = Cs(server, user, pass, db);
            var users = Rows(d, @"SELECT dp.name, dp.sid, sp.name AS login FROM sys.database_principals dp
                                 LEFT JOIN sys.server_principals sp ON sp.sid = dp.sid
                                 WHERE dp.type IN ('S','U') AND dp.principal_id > 4;");
            int fixedN = 0, added = 0, dropped = 0;
            foreach (var u in users)
            {
                string un = Convert.ToString(u["name"]) ?? "";
                if (u["login"] != DBNull.Value) continue;                       // zaten bir girişe bağlı
                if (logins.Any(l => (Convert.ToString(l["name"]) ?? "").Equals(un, StringComparison.OrdinalIgnoreCase)))
                {
                    try { Exec(d, $"ALTER USER {Q(un)} WITH LOGIN = {Q(un)};", 60); fixedN++; } catch { }
                }
                else
                {
                    // eski sunucudan kalan, bu sunucuda karşılığı olmayan kullanıcı → sahip olduğu şemalar dbo'ya, kullanıcı silinir
                    try
                    {
                        foreach (var s in Rows(d, $"SELECT name FROM sys.schemas WHERE principal_id = USER_ID(N'{L(un)}');"))
                            Exec(d, $"ALTER AUTHORIZATION ON SCHEMA::{Q(Convert.ToString(s["name"]) ?? "")} TO dbo;", 60);
                        Exec(d, $"DROP USER {Q(un)};", 60); dropped++;
                    }
                    catch { }
                }
            }
            foreach (var l in logins)
            {
                string ln = Convert.ToString(l["name"]) ?? "";
                try
                {
                    var has = Rows(d, $"SELECT name FROM sys.database_principals WHERE sid = SUSER_SID(N'{L(ln)}');");
                    string un = has.Count > 0 ? Convert.ToString(has[0]["name"]) ?? ln : ln;
                    if (has.Count == 0) { Exec(d, $"CREATE USER {Q(ln)} FOR LOGIN {Q(ln)};", 60); added++; }
                    if (!un.Equals("dbo", StringComparison.OrdinalIgnoreCase))
                        Exec(d, $"EXEC sp_addrolemember N'db_owner', N'{L(un)}';", 60);
                }
                catch { }
            }
            try { Exec(m, $"ALTER AUTHORIZATION ON DATABASE::{Q(db)} TO [sa];", 60); } catch { }
            log($"  Kullanıcılar: {logins.Count} SQL girişi bağlandı (yeni {added}, düzeltilen {fixedN}, eski sunucudan kalan {dropped} silindi) → program giriş yapabilir");
        }
    }
}
