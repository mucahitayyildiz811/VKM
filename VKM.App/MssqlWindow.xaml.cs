using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace VKM.App
{
    public partial class MssqlPanel : UserControl
    {
        private readonly List<string> _files = new();
        private bool _localDbOk;
        private string? _ref;                       // (geriye uyum) ilk referans
        private readonly List<string> _refs = new(); // çoklu referans: her veri dosyasına ADI en çok benzeyen referans
        private System.Text.StringBuilder? _cap;
        private string? _outDir;          // klasör modunda çıktı: <klasör>\VKM_KURTARILAN (kaynağa dokunulmaz)
        private volatile bool _stop;   // dosya başına rapor (_rapor.txt)

        public MssqlPanel()
        {
            InitializeComponent();
            Loaded += (_, __) => CheckLocalDb();
            LoadRefs();
        }

        private void Check_Click(object sender, RoutedEventArgs e) => CheckLocalDb();

        private void CheckLocalDb()
        {
            MssqlBak.DetectPreferred();
            if (MssqlBak.LocalDbReady(out string err))
            {
                LocalDbStatus.Text = "✓ LocalDB hazır (" + MssqlBak.Instance + ")" +
                    (MssqlBak.PreferredName.Length > 0 ? "  ·  .bak " + MssqlBak.PreferredName + " formatında üretilecek (" + MssqlBak.PreferredName + " ve üstü her SQL'de açılır)" : "  ·  ⚠ .bak en yeni sürümde üretilecek");
                Log(MssqlBak.DetectInfo);
                LocalDbStatus.Foreground = System.Windows.Media.Brushes.LightGreen;
                _localDbOk = true;
            }
            else
            {
                LocalDbStatus.Text = "✗ LocalDB bulunamadı — kur, sonra 'Tekrar kontrol'. (" + Short(err) + ")";
                LocalDbStatus.Foreground = System.Windows.Media.Brushes.Salmon;
                _localDbOk = false;
            }
            UpdateRecoverBtn();
        }

        private void UpdateRecoverBtn() => RecoverBtn.IsEnabled = _localDbOk && _files.Count > 0;

        private void Pick_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Kurtarılacak veritabanı dosyası/dosyaları (.mdf / ._MS …)",
                Filter = "Veritabanı dosyaları (şifreli uzantılar dahil)|*.mdf*;*.ndf*;*._MS*;*.MS*;*_mdf*|Tüm dosyalar (*.*)|*.*",
                Multiselect = true
            };
            if (d.ShowDialog() != true) return;
            _files.Clear(); _files.AddRange(d.FileNames); _outDir = null;
            FileBox.Text = _files.Count == 1 ? _files[0] : _files.Count + " dosya seçildi";
            UpdateRecoverBtn();
            if (_files.Count <= 10) _ = QuickAnalyze(new List<string>(_files));
            else { LogText.Text = ""; Log(_files.Count + " dosya seçildi — hızlı analiz çok dosyada atlanır, 'Otomatik Kurtar' ile başla."); }
        }

        private void PickRef_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Referans dosyalar (Opsiyonel) — programın SAĞLAM / YENİ KURULUM veritabanları (.bak veya .mdf), birden çok seçilebilir",
                Filter = "Sağlam veritabanı|*.bak;*.mdf|Tüm dosyalar (*.*)|*.*",
                Multiselect = true
            };
            if (d.ShowDialog() != true) return;
            _refs.Clear(); _refs.AddRange(d.FileNames); _ref = _refs[0];
            ShowRefs(); SaveRefs();
            Log("");
            Log($"Referans seçildi ({_refs.Count}): " + string.Join(", ", _refs.Select(Path.GetFileName)));
            Log("   Her veri dosyasına ADI eşleşen referans kullanılır (ORKARESIMLER → ORKARESIMLER, ORKA_0001_2024 → ORKA_xxxx_yyyy …).");
            Log("   Yapı (tüm kolonlar, ID/PK/index/prosedür) referanstan gelir → program veritabanını kendi DB'si gibi tanır.");
        }

        private void ClearRef_Click(object sender, RoutedEventArgs e)
        {
            _ref = null; _refs.Clear(); SaveRefs();
            RefBox.Text = "Referans dosya (Opsiyonel) — aynı programın sağlam .bak / .mdf'si. Seçilirse yapı (ID, PK, index, prosedür) oradan alınır.";
            RefBox.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9f, 0xb0, 0xc3));
        }

        // Referanslar hatırlanır (program her açılışta aynı sağlam dosyaları kendisi seçer)
        private static string RefsFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VKM", "referanslar.txt");
        private void SaveRefs()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(RefsFile)!); File.WriteAllLines(RefsFile, _refs); } catch { }
        }
        private void LoadRefs()
        {
            try
            {
                if (!File.Exists(RefsFile)) return;
                var l = File.ReadAllLines(RefsFile).Where(File.Exists).ToList();
                if (l.Count == 0) return;
                _refs.Clear(); _refs.AddRange(l); _ref = _refs[0]; ShowRefs();
            }
            catch { }
        }
        private void ShowRefs()
        {
            RefBox.Text = _refs.Count == 1 ? "Referans: " + _refs[0] : $"Referans: {_refs.Count} dosya ({string.Join(", ", _refs.Select(Path.GetFileNameWithoutExtension).Take(6))}{(_refs.Count > 6 ? " …" : "")})";
            RefBox.Foreground = System.Windows.Media.Brushes.LightGreen;
        }

        // Klasör: içindeki tüm veri dosyaları (log dosyaları hariç), alt klasörler dahil
        private void PickFolder_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Müşteri veri klasörü (tüm MDF'ler taranır, alt klasörler dahil)" };
            if (d.ShowDialog() != true) return;
            string root = d.FolderName;
            var list = new List<string>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (f.IndexOf("VKM_KURTARILAN", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    string n = Path.GetFileName(f).ToLowerInvariant();
                    if (n.Contains(".ldf") || n.Contains("_log.") || n.Contains("_log_")) continue;
                    if (n.Contains(".mdf") || n.Contains(".ndf") || n.Contains("_mdf") || n.Contains("._ms")) list.Add(f);
                }
            }
            catch (Exception ex) { MessageBox.Show("Klasör okunamadı: " + ex.Message); return; }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            _files.Clear(); _files.AddRange(list);
            _outDir = Path.Combine(root, "VKM_KURTARILAN");
            long total = 0; foreach (var f in list) try { total += new FileInfo(f).Length; } catch { }
            FileBox.Text = $"{root}  →  {list.Count} veri dosyası ({total / 1073741824.0:0.0} GB)";
            LogText.Text = "";
            Log($"Klasör: {root}");
            Log($"Bulunan veri dosyası: {list.Count}  ·  toplam {total / 1073741824.0:0.0} GB  (log/LDF dosyaları hariç)");
            Log($"Çıktılar: {_outDir}  (kaynak dosyalara dokunulmaz)");
            Log("Yarıda kalırsa tekrar başlat: .bak'ı hazır olan dosyalar atlanır, kaldığı yerden devam eder.");
            Log("Tüm dosyalar birlikte işlenir → kataloglar havuzlanır, her dosyanın eksik tablo/kolon bilgisi diğerlerinden tamamlanır.");
            UpdateRecoverBtn();
        }

        private void Target_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (TargetBox?.SelectedItem is ComboBoxItem it && int.TryParse(it.Tag?.ToString(), out int t)) MssqlBak.Target = t;
            if (!IsLoaded) return;
            MssqlBak.DetectPreferred();
            Log("Hedef SQL değişti → " + MssqlBak.DetectInfo + (MssqlBak.PreferredName.Length > 0 ? "  →  .bak'lar " + MssqlBak.PreferredName + " formatında" : "  →  ⚠ seçilen sürümün LocalDB'si kurulu değil"));
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _stop = true; StopBtn.IsEnabled = false;
            Log("■ Durdurma istendi — şu anki dosya bitince duracak.");
        }

        // Dosya eklenir eklenmez: Teşhis + Sayfa analizi + Tablolar → tek log
        private async Task QuickAnalyze(List<string> files)
        {
            SetBusy(true); LogText.Text = "";
            await Task.Run(() =>
            {
                int i = 0;
                foreach (var f in files)
                {
                    i++;
                    if (files.Count > 1) { Log(""); Log("──────── " + i + "/" + files.Count + ": " + Path.GetFileName(f) + " ────────"); }
                    try
                    {
                        Log("══════ TEŞHİS ══════");
                        var res = DbDiagnose.Diagnose(f, _ => { }, _ => { }, System.Threading.CancellationToken.None);
                        if (res != null) DbDiagnose.Report(res, Log);
                        Log("");
                        PageRebuild.QuickReport(f, Log);
                    }
                    catch (Exception ex) { Log("✗ Analiz hatası: " + FirstLine(ex.Message)); }
                }
                Log("");
                Log("→ Hazır. '🚀 Otomatik Kurtar → .bak' ile devam et.");
            });
            SetBusy(false);
        }

        private async void AutoRecover_Click(object sender, RoutedEventArgs e)
        {
            if (_files.Count == 0) return;
            // SQL sürümü: müşteride açılmayacak .bak'larla bir gece harcanmasın
            MssqlBak.DetectPreferred();
            if (MssqlBak.PreferredName.Length == 0)
            {
                var ans = MessageBox.Show("2016 / 2017 / 2019 LocalDB bulunamadı → .bak'lar EN YENİ SQL sürümünde üretilecek ve daha eski SQL'lerde AÇILMAZ.\n\n" +
                                          MssqlBak.DetectInfo + "\n\nYine de devam edilsin mi?", "SQL sürümü", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (ans != MessageBoxResult.Yes) { Log(MssqlBak.DetectInfo); return; }
            }
            // referanssız üretilen .bak'ta, katalog sayfası şifreli tabloların kolonları/ID özelliği eksik kalabilir → program (Orka) hata verir
            if (!_refs.Any(File.Exists) &&
                MessageBox.Show("Referans (sağlam örnek) seçilmedi.\n\nReferanssız üretilen veritabanlarında, kataloğu şifreli kalan tablolarda kolon eksik olabilir ve program " +
                                "(ör. Orka) 'Invalid column name' / 'Cannot insert NULL into ID' gibi hatalar verebilir.\n\nYine de referanssız devam edilsin mi?",
                                "Referans yok", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true);
            _stop = false; StopBtn.IsEnabled = true; StopBtn.Visibility = Visibility.Visible;
            LogText.Text = "";
            // firma listesi (GNL_FIRMATANIM) önce öğrenilsin → sonraki dosyalarda kurtarılan belgeler firmalarına bağlanır
            PageRebuild.ResetSession();
            // GENELTANIM önce; sonra firma firma ESKİ yıldan YENİYE → her yılın çıkan .bak'ı bir sonraki yıla yardımcı olur
            //  (personel kartları, hesap planı, cari, şablonlar yıldan yıla devreder; bir yılda şifreli kalan diğerinden tamamlanır)
            var files = _files.OrderBy(x => Path.GetFileName(x).ToUpperInvariant().Contains("GENELTANIM") ? 0 : 1)
                              .ThenBy(x => FirmYear(CleanBase(x)) is var fy && fy != null ? fy.Value.pre + fy.Value.firm : CleanBase(x).ToUpperInvariant())
                              .ThenBy(x => FirmYear(CleanBase(x))?.year ?? 0).ToList();
            Log(MssqlBak.DetectInfo + (MssqlBak.PreferredName.Length > 0 ? "  →  .bak'lar " + MssqlBak.PreferredName + " formatında" : "  →  ⚠ en yeni sürümde"));
            var refList = _refs.Where(File.Exists).ToList();
            bool ghosts = GhostChk.IsChecked == true;
            if (ghosts) Log("Silinmiş kayıt taraması: AÇIK");

            int done = 0, ok = 0;
            await Task.Run(() =>
            {
                // Birden çok dosya → kataloglar havuzlanır: her dosyanın sağlam parçası diğerinin eksiğini kapatır
                PageRebuild.Pool? pool = null;
                if (files.Count > 1)
                {
                    Log("Katalog havuzu kuruluyor (" + files.Count + " dosya) — dosyalar birbirinin eksik tablo/kolon bilgisini tamamlayacak…");
                    string? cache = _outDir != null ? Path.Combine(_outDir, $"VKM_havuz_{files.Count}_{(files.Count > 0 ? PageRebuild.FamilyOf(files[0]).Replace('#', 'N') : "x")}.bin") : null;
                    if (cache != null && File.Exists(cache)) pool = PageRebuild.LoadPool(cache, files.Count);
                    if (pool != null) Log("Havuz diskten yüklendi (önceki çalışmadan) → tekrar okumaya gerek yok.");
                    else
                    {
                        pool = PageRebuild.BuildPool(files, Log, () => _stop);
                        if (cache != null && !_stop)
                            try { Directory.CreateDirectory(_outDir!); PageRebuild.SavePool(pool, cache, files.Count); Log("Havuz kaydedildi: " + cache); } catch { }
                    }
                }
                else if (files.Count == 1)
                {
                    // tek dosya: aynı klasördeki aynı türden kardeş dosyalar (ör. diğer ORKA_xxxx_2026 firmaları) yapı havuzu olur
                    //  → kendi kataloğu şifreli tablolar (Hızlı Fiş, e-Defter …) ve yeni sürüm fonksiyonları onlardan tamamlanır
                    try
                    {
                        string me = files[0], fam = PageRebuild.FamilyOf(me);
                        string year = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(me), @"_(\d{4})(?=\.mdf|\.ndf|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Groups[1].Value;
                        var sib = Directory.GetFiles(Path.GetDirectoryName(me) ?? ".")
                            .Where(x => !x.Equals(me, StringComparison.OrdinalIgnoreCase)
                                        && Path.GetFileName(x).IndexOf(".mdf", StringComparison.OrdinalIgnoreCase) > 0
                                        && PageRebuild.FamilyOf(x) == fam)
                            .Select(x => new FileInfo(x)).Where(x => x.Length > 8192 * 64 && x.Length < 3L << 30)
                            .OrderByDescending(x => year.Length > 0 && x.Name.Contains("_" + year)).ThenBy(x => x.Length)
                            .Take(8).Select(x => x.FullName).ToList();
                        if (sib.Count > 0)
                        {
                            Log($"Tek dosya: aynı klasördeki {sib.Count} kardeş dosyadan yapı havuzu kuruluyor (şifreli kalan tablo/fonksiyon yapıları için)…");
                            pool = PageRebuild.BuildPool(new List<string> { me }.Concat(sib).ToList(), Log, () => _stop);
                        }
                    }
                    catch (Exception ex) { Log("Kardeş dosya havuzu atlandı: " + ex.Message); }
                }
                foreach (var f in files)
                {
                    if (_stop) { Log(""); Log("■ Durduruldu. Tekrar başlatınca biten dosyalar atlanır."); break; }
                    done++;
                    Dispatcher.Invoke(() => _cap = new System.Text.StringBuilder());
                    Log("");
                    Log("──────── Dosya " + done + "/" + files.Count + ": " + Path.GetFileName(f) + "  [" + DateTime.Now.ToString("HH:mm") + "] ────────");
                    var fileClock = System.Diagnostics.Stopwatch.StartNew();
                    if (!File.Exists(f)) { Log("✗ Dosya bulunamadı."); continue; }

                    string? ldf = GuessLdf(f);
                    if (ldf != null) Log("Log otomatik bulundu: " + Path.GetFileName(ldf));

                    string dir = _outDir ?? Path.GetDirectoryName(f) ?? ".";
                    if (_outDir != null) Directory.CreateDirectory(dir);
                    string baseName = CleanBase(f);
                    string bak = Path.Combine(dir, baseName + ".bak");
                    if (File.Exists(bak))
                    {
                        // kaldığı yerden devam (çıktı klasörü ayrı olsun olmasın): sağlam .bak varsa atla.
                        //  Paralel pencerelerde de aynı dosya iki kez işlenmez. Açılamayan/yarım .bak yeniden üretilir.
                        bool fresh = (DateTime.Now - File.GetLastWriteTime(bak)).TotalSeconds < 5;   // başka pencere şu an yazıyor olabilir
                        if (!fresh && MssqlBak.BakCompatible(bak)) { ok++; Log("✓ Zaten hazır, atlandı: " + Path.GetFileName(bak)); continue; }
                        if (fresh) { Log("… başka pencere bu dosyayı şu an bitiriyor, atlandı: " + Path.GetFileName(bak)); continue; }
                        Log("↻ " + Path.GetFileName(bak) + " açılamıyor (yarım / başka sürüm) → yeniden üretiliyor.");
                        try { File.Delete(bak); } catch { bak = Path.Combine(dir, baseName + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bak"); }
                    }

                    // paralel pencere koruması: bu dosyayı işleyen pencere ".isleniyor" işareti bırakır (2 saatten eskisi geçersiz)
                    string claim = bak + ".isleniyor";
                    try
                    {
                        if (File.Exists(claim) && (DateTime.Now - File.GetLastWriteTime(claim)).TotalHours < 2) { Log("… başka pencere bu dosyayı işliyor, atlandı."); continue; }
                        File.WriteAllText(claim, Environment.ProcessId.ToString());
                    }
                    catch { }
                    bool headerOk = PageRebuild.HeaderLooksOk(f);
                    if (!headerOk) Log("Dosya başlığı (sayfa 0/9) hasarlı → motor denemesi atlanıyor.");
                    string err = "";
                    if (headerOk) MssqlBak.PickInstanceFor(new FileInfo(f).Length / 1048576, Log);
                    bool made = headerOk && MssqlBak.Make(f, ldf, bak, Log, out err);
                    if (!made && headerOk && MssqlBak.UsingPreferredOld)
                    {
                        // dosya daha yeni bir SQL sürümünden olabilir → en yeni LocalDB ile tekrar dene
                        Log("Eski sürüm motor açamadı → en yeni LocalDB ile tekrar deneniyor…");
                        MssqlBak.UseDefault();
                        made = MssqlBak.Make(f, ldf, bak, Log, out err);
                    }
                    if (made)
                    {
                        ok++;
                        Log("Doğrulanıyor…");
                        bool v = MssqlBak.QuickVerify(bak, out string vinfo);
                        Log(v ? "✓✓ HAZIR — .bak üretildi ve doğrulandı. Müşteriye verilebilir." : "⚠ .bak üretildi ama doğrulama uyarısı: " + vinfo);
                        MssqlBak.LogCompat(Log);
                        Log("   → " + bak);
                    }
                    else
                    {
                        Log(headerOk ? "Motor dosyayı açamadı → İÇERİKTEN KURTARMA'ya geçiliyor…" : "İÇERİKTEN KURTARMA başlıyor…");
                        string? refFile = PickRefFor(f, refList);
                        // yardımcılar: referans listesindeki sağlam yıllar + bu klasörde daha önce üretilmiş aynı firmanın diğer yıl .bak'ları
                        var helperCands = new List<string>(refList);
                        try { helperCands.AddRange(Directory.GetFiles(dir, "*.bak").Where(x => !x.Equals(bak, StringComparison.OrdinalIgnoreCase))); } catch { }
                        string? helper = PickHelperFor(f, helperCands);
                        if (refFile != null) Log("Referans kullanılacak: " + Path.GetFileName(refFile));
                        else if (refList.Count > 0) Log("Bu dosyaya uyan referans yok (ad eşleşmedi) → referanssız.");
                        if (helper != null) Log("Aynı firmanın başka yıl verisi (hesap planı / cari / şablon / tanım tamamlama, yakın yıldan uzağa): " + string.Join(", ", helper.Split('|').Select(Path.GetFileName)));
                        bool rb = PageRebuild.Rebuild(f, bak, Log, out string rerr, refFile, pool, ghosts, helper);
                        if (rb)
                        {
                            ok++;
                            Log("Doğrulanıyor…");
                            bool v = MssqlBak.QuickVerify(bak, out string vinfo);
                            Log(v ? "✓✓ HAZIR — sağlam sayfalardan yeniden kurulan .bak üretildi ve doğrulandı."
                                  : "⚠ .bak üretildi ama doğrulama uyarısı: " + vinfo);
                            Log("   → " + bak + $"   (süre: {fileClock.Elapsed.TotalMinutes:0.#} dk)");
                            Log(refFile != null && PageRebuild.LastRefUsed
                                ? "   Yapı referanstan: ID/PK/index/prosedür/view tam → program doğrudan kullanabilir."
                                : refFile != null
                                    ? "   ⚠ REFERANS AÇILAMADI — bu .bak referanssız üretildi (yukarıdaki ⚠ satırı). Program (Orka) eksik kolon / ID hatası verebilir; sorunu giderip bu dosyayı yeniden üret."
                                    : "   Yapı dosyanın kendi kataloğundan + havuzdan kuruldu (yukarıdaki 'Yapı:' satırı). Sağlam bir yedek 'Referans' verilirse eksik kalanlar da tamamlanır.");
                        }
                        else
                        {
                            Log("✗ İçerikten kurtarma da başarısız: " + rerr);
                        }
                    }
                    // rapor: .bak'ın yanına aynı adla _rapor.txt (müşteriye teslimde eklenir)
                    try
                    {
                        string rapor = Path.Combine(dir, Path.GetFileNameWithoutExtension(bak) + "_rapor.txt");
                        string text = "";
                        Dispatcher.Invoke(() => { text = _cap?.ToString() ?? ""; _cap = null; });
                        File.WriteAllText(rapor, "VKM — SQL Kurtarma Raporu  ·  " + DateTime.Now.ToString("dd.MM.yyyy HH:mm") + Environment.NewLine +
                                                 "Kaynak: " + f + Environment.NewLine + text, new System.Text.UTF8Encoding(true));
                        Log("   Rapor: " + rapor);
                    }
                    catch { }
                    try { File.Delete(claim); } catch { }   // işaret kaldırılır
                }
            });

            SetBusy(false); StopBtn.Visibility = Visibility.Collapsed;
            string msg = ok + "/" + files.Count + " dosya için .bak üretildi.";
            Log(""); Log("══════ BİTTİ — " + msg + " ══════");
            MessageBox.Show(msg, "Bitti", MessageBoxButton.OK,
                ok > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void Hex_Click(object sender, RoutedEventArgs e)
        {
            string f = FirstFileOrPick("Hex/sayfa görüntülenecek dosya");
            if (f.Length == 0) return;
            new SqlHexWindow(f) { Owner = Window.GetWindow(this) }.Show();
        }

        private async void Teshis_Click(object sender, RoutedEventArgs e)
        {
            string f = FirstFileOrPick("Teşhis edilecek dosya");
            if (f.Length == 0) return;

            SetBusy(true); LogText.Text = ""; Log("Teşhis: " + f);
            var prog = new Progress<double>(x => { Busy.IsIndeterminate = false; Busy.Value = x * 100; });
            DbDiagnose.Result? res = null; string err = "";
            await Task.Run(() =>
            {
                try { res = DbDiagnose.Diagnose(f, Log, x => ((IProgress<double>)prog).Report(x), System.Threading.CancellationToken.None); }
                catch (Exception ex) { err = ex.Message; }
            });
            SetBusy(false);
            if (res != null) DbDiagnose.Report(res, Log);
            else Log("✗ Teşhis hatası: " + err);
        }

        // ── yardımcılar ──
        private string FirstFileOrPick(string title)
        {
            if (_files.Count > 0 && File.Exists(_files[0])) return _files[0];
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = title,
                Filter = "Veritabanı dosyaları|*.mdf;*.ndf;*.ldf;*._MS;*.MS;*.bak;*.fdb;*.gdb|Tüm dosyalar (*.*)|*.*"
            };
            if (d.ShowDialog() != true) return "";
            _files.Clear(); _files.Add(d.FileName);
            FileBox.Text = d.FileName; UpdateRecoverBtn();
            return d.FileName;
        }

        // ── MÜŞTERİDE GERİ YÜKLE: .bak'ları bu bilgisayardaki SQL Server'a kurar + kullanıcıları bağlar (SSMS gerekmez)
        private async void Install_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Müşteriye kurulacak .bak dosyaları (birden fazla seçilebilir)",
                Filter = "SQL yedek (*.bak)|*.bak", Multiselect = true
            };
            if (_outDir != null && Directory.Exists(_outDir)) d.InitialDirectory = _outDir;
            if (d.ShowDialog() != true || d.FileNames.Length == 0) return;
            var baks = d.FileNames.ToList();
            var dbs = string.Join(", ", baks.Select(MusteriKur.DbNameOf));
            if (MessageBox.Show("Bu bilgisayardaki SQL Server'a şu veritabanları kurulacak:\n\n" + dbs +
                                "\n\n• Aynı adla açılamayan (Recovery Pending) eski kayıtlar kaldırılır.\n• Aynı adla SAĞLAM bir veritabanı varsa önce onun yedeği alınır, sonra üzerine yazılır.\n• Orka (program) kapalı olmalı.\n\nDevam edilsin mi?",
                                "Müşteride Geri Yükle", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            SetBusy(true); InstallBtn.IsEnabled = false;
            LogText.Text = "";
            Log("══════ MÜŞTERİDE GERİ YÜKLE ══════");
            string? user = null, pass = null;
            try
            {
                string? server = await Task.Run(() => MusteriKur.PickServer(null, null, Log, out _));
                if (server == null)
                {
                    // Windows girişi yetmedi → SQL kullanıcısı (çoğunlukla sa) sorulur
                    var cred = AskSqlLogin();
                    if (cred == null) { Log("İptal edildi."); return; }
                    (user, pass) = cred.Value;
                    string? u = user, p = pass;
                    server = await Task.Run(() => MusteriKur.PickServer(u, p, Log, out _));
                }
                if (server == null) { Log("✗ Bu bilgisayarda bağlanılabilen SQL Server bulunamadı."); return; }
                string srv = server; string? uu = user, pp = pass;
                bool ok = await Task.Run(() => MusteriKur.Run(srv, uu, pp, baks, Log));
                MessageBox.Show(ok ? "Kurulum bitti. Orka'yı açıp giriş yapabilirsiniz." : "Bazı veritabanları kurulamadı — ayrıntı logda.",
                                "Müşteride Geri Yükle", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex) { Log("✗ " + ex.Message); }
            finally { SetBusy(false); InstallBtn.IsEnabled = true; }
        }

        // Zaten kurtarılmış bir .bak'ı baştan taramadan düzelt (Fiş Bütünleme + Fiş tipi + başlık tutarı)
        private async void FixBak_Click(object sender, RoutedEventArgs e)
        {
            var d = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Onarılacak .bak (zaten kurtarılmış/teslim edilmiş)",
                Filter = "SQL yedek (*.bak)|*.bak", Multiselect = true
            };
            if (_outDir != null && Directory.Exists(_outDir)) d.InitialDirectory = _outDir;
            if (d.ShowDialog() != true || d.FileNames.Length == 0) return;
            var baks = d.FileNames.ToList();
            SetBusy(true); FixBakBtn.IsEnabled = false;
            LogText.Text = "";
            Log("══════ MEVCUT .bak ONAR ══════");
            try
            {
                int ok = 0;
                foreach (var inBak in baks)
                {
                    string outBak = Path.Combine(Path.GetDirectoryName(inBak) ?? ".", Path.GetFileNameWithoutExtension(inBak) + "_onarildi.bak");
                    Log("── " + Path.GetFileName(inBak) + " ──");
                    bool r = await Task.Run(() => PageRebuild.FixBak(inBak, outBak, Log, out _));
                    if (r) ok++;
                }
                MessageBox.Show($"{ok}/{baks.Count} .bak onarıldı (yanına _onarildi.bak olarak kaydedildi).\nOrka'da geri yükleyip (🖥 Müşteride Geri Yükle) kontrol edebilirsin.",
                                "Mevcut .bak Onar", MessageBoxButton.OK, ok == baks.Count ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            catch (Exception ex) { Log("✗ " + ex.Message); }
            finally { SetBusy(false); FixBakBtn.IsEnabled = true; }
        }

        // küçük giriş penceresi: SQL kullanıcı adı + şifre
        private (string user, string pass)? AskSqlLogin()
        {
            var w = new Window
            {
                Title = "SQL Server girişi", Width = 380, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this)
            };
            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock { Text = "Windows girişiyle SQL Server'a bağlanılamadı.\nSQL kullanıcı adı ve şifresini girin (çoğunlukla 'sa'):", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            var ub = new TextBox { Text = "sa", Margin = new Thickness(0, 0, 0, 6), Height = 26 };
            var pb = new PasswordBox { Margin = new Thickness(0, 0, 0, 10), Height = 26 };
            var okb = new Button { Content = "Bağlan", Height = 28, IsDefault = true };
            okb.Click += (_, __) => w.DialogResult = true;
            sp.Children.Add(new TextBlock { Text = "Kullanıcı:" }); sp.Children.Add(ub);
            sp.Children.Add(new TextBlock { Text = "Şifre:" }); sp.Children.Add(pb);
            sp.Children.Add(okb);
            w.Content = sp;
            return w.ShowDialog() == true ? (ub.Text.Trim(), pb.Password) : null;
        }

        private void SetBusy(bool on)
        {
            Busy.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            Busy.IsIndeterminate = on;
            RecoverBtn.IsEnabled = !on && _localDbOk && _files.Count > 0;
            PickBtn.IsEnabled = !on; FolderBtn.IsEnabled = !on; CheckBtn.IsEnabled = !on; RefBtn.IsEnabled = !on; RefClearBtn.IsEnabled = !on; GhostChk.IsEnabled = !on; TargetBox.IsEnabled = !on; HexBtn.IsEnabled = !on; TeshisBtn.IsEnabled = !on;
        }

        private void Log(string line)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(line)); return; }
            // bozuk veriden gelen kontrol karakterleri (özellikle \0) kopyalanan logu yarıda keser → görünür işaretle değiştir
            if (line.Any(ch => ch < ' ' && ch != '\t' && ch != '\n' && ch != '\r')) line = new string(line.Select(ch => ch < ' ' && ch != '\t' && ch != '\n' && ch != '\r' ? '·' : ch).ToArray());
            _cap?.AppendLine(line);
            if (LogText.Text.Length > 1_500_000) LogText.Text = "… (eski satırlar kısaltıldı — her dosyanın tam logu _rapor.txt içinde)\n" + LogText.Text.Substring(LogText.Text.Length - 1_000_000);
            LogText.AppendText((LogText.Text.Length > 0 ? "\n" : "") + line);
            LogText.ScrollToEnd();
        }

        // Veri dosyası ↔ referans: adlardaki rakamlar atılıp karşılaştırılır (ORKA_0001_2026 ~ ORKA_9999_2150, ORKARESIMLER ~ ORKARESIMLER_2150)
        private static string NameKey(string baseName)
        {
            string k = System.Text.RegularExpressions.Regex.Replace(baseName.ToUpperInvariant(), @"[\d_\-\s]+", "_").Trim('_');
            return k;
        }
        // "ORKA_0015_2024" → ("ORKA_", "0015", "2024")  — firma + yıl kalıbı (şablon 2150 hariç)
        private static (string pre, string firm, int year)? FirmYear(string baseName)
        {
            var m = System.Text.RegularExpressions.Regex.Match(baseName, @"^(.*?_)(\d+)_(\d{4})$");
            if (!m.Success) return null;
            int y = int.Parse(m.Groups[3].Value);
            return y == 2150 ? null : (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value, y);
        }
        // Aynı firmanın başka yıla ait sağlam verisi (ORKA_0015_2024 ↔ ORKA_0015_2026) → yapı referansı DEĞİL, eksik tamamlama verisi.
        //  (Yapı referansı yapılsaydı 2024 fişleri 2026'ya karışırdı.) Hedef yıla en yakın yıl seçilir.
        private static string? PickHelperFor(string dataFile, List<string> refs)
        {
            var me = FirmYear(CleanBase(dataFile));
            if (me == null) return null;
            return refs.Where(r => !r.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase))
                       .Select(r => (r, fy: FirmYear(CleanBase(r))))
                       .Where(x => x.fy != null && x.fy.Value.pre == me.Value.pre && x.fy.Value.firm == me.Value.firm && x.fy.Value.year != me.Value.year)
                       .OrderBy(x => Math.Abs(x.fy!.Value.year - me.Value.year)).ThenByDescending(x => x.fy!.Value.year)
                       .Select(x => x.r).Take(3).ToList() is var l && l.Count > 0 ? string.Join("|", l) : null;
        }
        private static string? PickRefFor(string dataFile, List<string> refs)
        {
            if (refs.Count == 0) return null;
            string bn0 = CleanBase(dataFile);
            // log dosyaları referans değil; firma+yıl verisi (gerçek kayıtlı) ancak birebir aynı adsa yapı referansı olur
            refs = refs.Where(r => !r.EndsWith(".ldf", StringComparison.OrdinalIgnoreCase))
                       .Where(r => FirmYear(CleanBase(r)) == null || CleanBase(r).Equals(bn0, StringComparison.OrdinalIgnoreCase)).ToList();
            if (refs.Count == 0) return null;
            if (refs.Count == 1) return refs[0];
            string bn = CleanBase(dataFile);
            // 1) birebir aynı ad (ORKABURO_2150 ↔ ORKABURO_2150.mdf)
            var exact = refs.FirstOrDefault(r => Path.GetFileNameWithoutExtension(CleanBase(r)).Equals(bn, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            // 2) rakamlar atılınca aynı ad (ORKA_0001_2024 ↔ ORKA_9999_2150); şablon (_2150) olmayan önce, sonra en büyük dosya
            string k = NameKey(bn);
            bool isTpl = bn.Contains("2150");
            return refs.Where(r => NameKey(Path.GetFileNameWithoutExtension(CleanBase(r))) == k)
                       .OrderBy(r => Path.GetFileName(r).Contains("2150") == isTpl ? 0 : 1)
                       // aynı türden birden çok referans: en yeni tarihli (güncel program sürümünün şablonu) önce
                       .ThenByDescending(r => { try { return new FileInfo(r).LastWriteTimeUtc.Date; } catch { return DateTime.MinValue; } })
                       .ThenByDescending(r => { try { return new FileInfo(r).Length; } catch { return 0L; } })
                       .FirstOrDefault();
        }

        // "ORKA_0001_2026.mdf.pegasus_onionmail…" → "ORKA_0001_2026"  ·  "X_mdf.locked" → "X"
        private static string CleanBase(string path)
        {
            string n = Path.GetFileName(path);
            foreach (var ext in new[] { ".mdf", ".ndf", "._ms", ".ms" })
            {
                int i = n.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
                if (i > 0) return n.Substring(0, i);
            }
            int dot = n.IndexOf('.');
            if (dot > 0) n = n.Substring(0, dot);
            foreach (var suf in new[] { "_mdf", "-mdf", "_ndf" })
                if (n.EndsWith(suf, StringComparison.OrdinalIgnoreCase) && n.Length > suf.Length) return n.Substring(0, n.Length - suf.Length);
            return n;
        }

        // MDF'nin yanındaki _log.ldf'yi otomatik bul (._MS / - Copy varyantları dahil)
        private static string? GuessLdf(string mdf)
        {
            try
            {
                string dir = Path.GetDirectoryName(mdf) ?? "";
                string baseName = Path.GetFileNameWithoutExtension(mdf);
                var ldfs = Directory.GetFiles(dir, "*.ldf");
                if (ldfs.Length == 0) return null;
                bool wantCopy = baseName.IndexOf("copy", StringComparison.OrdinalIgnoreCase) >= 0;
                string? best = null; int bestScore = -1;
                foreach (var f in ldfs)
                {
                    string n = Path.GetFileNameWithoutExtension(f);
                    int pref = CommonPrefix(baseName, n);
                    bool hasCopy = n.IndexOf("copy", StringComparison.OrdinalIgnoreCase) >= 0;
                    int score = pref + (hasCopy == wantCopy ? 100 : 0);
                    if (score > bestScore) { bestScore = score; best = f; }
                }
                return bestScore >= 6 ? best : null;
            }
            catch { return null; }
        }

        private static int CommonPrefix(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i])) i++;
            return i;
        }

        private static string FirstLine(string s)
        { s = (s ?? "").Replace("\r", " "); int i = s.IndexOf('\n'); return (i > 0 ? s.Substring(0, i) : s).Trim(); }

        private static string Short(string s)
        { s = (s ?? "").Replace("\r", " ").Replace("\n", " "); return s.Length > 120 ? s.Substring(0, 120) + "…" : s; }
    }
}
