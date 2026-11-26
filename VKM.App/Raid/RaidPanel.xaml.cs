using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace VKM.App
{
    // Sütun / panel genişlikleri: sürükleyerek ayarlanır, kapatıp açınca hatırlanır
    public class RaidColumns : System.ComponentModel.INotifyPropertyChanged
    {
        static readonly string FilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKM", "raid_duzen.txt");
        private readonly Dictionary<string, double> _w = new() { ["Detail"] = 230, ["Start"] = 110, ["Size"] = 90, ["Status"] = 190, ["Right"] = 400, ["LogH"] = 170 };
        public static readonly Dictionary<string, double> Defaults = new() { ["Detail"] = 230, ["Start"] = 110, ["Size"] = 90, ["Status"] = 190, ["Right"] = 400, ["LogH"] = 170 };
        private System.Windows.Threading.DispatcherTimer? _save;
        public double LastRight = 400;

        public RaidColumns()
        {
            try
            {
                if (File.Exists(FilePath))
                    foreach (var l in File.ReadAllLines(FilePath))
                    {
                        var p = l.Split('=');
                        if (p.Length == 2 && _w.ContainsKey(p[0]) && double.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v) && v >= 0 && v < 5000) _w[p[0]] = v;
                    }
            }
            catch { }
            if (_w["Right"] > 0) LastRight = _w["Right"];
        }

        GridLength Get(string k) => new(_w[k]);
        void Set(string k, GridLength v)
        {
            if (!v.IsAbsolute) return;
            _w[k] = v.Value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(k));
            if (_save == null) { _save = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; _save.Tick += (_, _) => { _save!.Stop(); Save(); }; }
            _save.Stop(); _save.Start();
        }
        void Save()
        {
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
                File.WriteAllLines(FilePath, _w.Select(kv => kv.Key + "=" + kv.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
            catch { }
        }
        public void Reset() { foreach (var kv in Defaults) Set(kv.Key, new GridLength(kv.Value)); LastRight = Defaults["Right"]; }

        public GridLength Detail { get => Get("Detail"); set => Set("Detail", value); }
        public GridLength Start { get => Get("Start"); set => Set("Start", value); }
        public GridLength Size { get => Get("Size"); set => Set("Size", value); }
        public GridLength Status { get => Get("Status"); set => Set("Status", value); }
        public GridLength Right { get => Get("Right"); set { if (value.IsAbsolute && value.Value > 40) LastRight = value.Value; Set("Right", value); } }
        public GridLength LogH { get => Get("LogH"); set => Set("LogH", value); }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    // RAID modülü: diskleri takınca RAID'i kendisi bulur (UFS mantığı + üst verisiz çözücü)
    public partial class RaidPanel : UserControl
    {
        private RaidColumns Cols => (RaidColumns)Resources["Cols"];
        private System.Windows.Threading.DispatcherTimer? _plug;
        private ObservableCollection<DiskInfo>? _disks;
        private Action<Func<Stream>, long, string, int[]>? _openInRecovery;
        public Action? BackToDisks;
        private readonly ObservableCollection<RaidNode> _items = new();
        private readonly List<RaidMember> _images = new();
        private readonly List<RaidNode> _diskNodes = new();
        private RaidScanner? _scanner;
        private CancellationTokenSource? _cts;
        private bool _scannedOnce;

        public RaidPanel()
        {
            InitializeComponent();
            Tree.ItemsSource = _items;
        }

        private void Back_Click(object sender, RoutedEventArgs e) => BackToDisks?.Invoke();

        public void Init(ObservableCollection<DiskInfo> disks, Action<Func<Stream>, long, string, int[]> openInRecovery)
        {
            if (_disks != disks)
            {
                _disks = disks;
                // yeni disk takılınca / çıkınca: 3 sn bekle, iş yoksa kendiliğinden yeniden tara
                disks.CollectionChanged += (_, _) =>
                {
                    if (!_scannedOnce) return;
                    _plug ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                    _plug.Tick -= Plug_Tick; _plug.Tick += Plug_Tick;
                    _plug.Stop(); _plug.Start();
                };
            }
            _openInRecovery = openInRecovery;
            if (!_scannedOnce) { _scannedOnce = true; Scan_Click(this, new RoutedEventArgs()); }   // modüle girince kendiliğinden tara
        }

        private void Plug_Tick(object? sender, EventArgs e)
        {
            _plug?.Stop();
            if (StopBtn.IsEnabled) { _plug?.Start(); return; }       // iş sürüyor → sonra
            Log("Disk listesi değişti (takıldı/çıkarıldı) → yeniden taranıyor.");
            Scan_Click(this, new RoutedEventArgs());
        }

        // ---------------- düzen ----------------
        private void TogglePanel_Click(object sender, RoutedEventArgs e)
        {
            Cols.Right = Cols.Right.Value > 40 ? new GridLength(0) : new GridLength(Math.Max(300, Cols.LastRight));
        }
        private void Split_DoubleClick(object sender, MouseButtonEventArgs e) => TogglePanel_Click(sender, e);
        private void ResetLayout_Click(object sender, RoutedEventArgs e) { Cols.Reset(); Log("Düzen varsayılana döndü."); }

        private void ShowRight(string title, string text)
        {
            if (Cols.Right.Value < 40) Cols.Right = new GridLength(Math.Max(380, Cols.LastRight));
            DetTitle.Text = title;
            DetText.Text = text;
            DetText.ScrollToHome();
        }

        // ---------------- log ----------------
        private void Log(string line)
        {
            if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => Log(line)); return; }
            LogBox.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + line + Environment.NewLine);
            LogBox.ScrollToEnd();
        }
        private void Status(string s) { if (!Dispatcher.CheckAccess()) { Dispatcher.InvokeAsync(() => Status(s)); return; } StatusText.Text = s; }

        private bool Busy
        {
            set
            {
                ScanBtn.IsEnabled = !value;
                StopBtn.IsEnabled = value;
            }
        }

        // ---------------- üyeler ----------------
        private static int DriveNo(string id)
        {
            var m = Regex.Match(id ?? "", @"PHYSICALDRIVE(\d+)", RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        private List<(RaidMember m, DiskInfo? info, int idx)> BuildMembers()
        {
            var list = new List<(RaidMember, DiskInfo?, int)>();
            foreach (var d in _disks ?? new ObservableCollection<DiskInfo>())
            {
                if (string.IsNullOrEmpty(d.DeviceId) || d.SizeBytes == 0) continue;
                string id = d.DeviceId; long len = (long)d.SizeBytes; int sec = d.BytesPerSector <= 0 ? 512 : d.BytesPerSector;
                int no = DriveNo(id);
                var m = new RaidMember
                {
                    Name = "Disk " + no + " · " + d.Model + (d.Serial.Length > 0 ? " · " + d.Serial : ""),
                    Model = d.Model, Serial = d.Serial, DeviceId = id, Length = len, Sector = sec,
                    HealthPercent = d.HealthPercent, HealthText = d.Health, Risky = d.IsRisky,
                    Open = () => PhysicalDisk.OpenReadOnly(id, len, sec),
                    OpenStrict = () => PhysicalDisk.OpenReadOnly(id, len, sec, true)
                };
                list.Add((m, d, no));
            }
            foreach (var im in _images) list.Add((im, null, -1));
            return list;
        }

        private static RaidMember ImageMember(string path)
        {
            long len = new FileInfo(path).Length;
            Func<Stream> op = () => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return new RaidMember
            {
                Name = "İmaj · " + Path.GetFileName(path), Model = Path.GetFileName(path), ImagePath = path, Length = len,
                Open = op, OpenStrict = op
            };
        }

        // ---------------- tarama ----------------
        private void LogToggle_Click(object sender, RoutedEventArgs e)
        {
            bool show = LogRow.Height.Value < 1;
            LogSplitRow.Height = new GridLength(show ? 6 : 0);
            LogRow.Height = new GridLength(show ? 170 : 0);
        }

        public void AutoScan() { if (_disks != null && _disks.Count > 0 && ScanBtn.IsEnabled) Scan_Click(this, new RoutedEventArgs()); }

        private void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (_disks == null) return;
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var tok = _cts.Token;
            _items.Clear(); _diskNodes.Clear();
            EmptyText.Visibility = Visibility.Collapsed;
            Busy = true;
            var members = BuildMembers();
            Log($"Tarama başladı: {members.Count} disk/imaj (salt okunur).");
            Status("taranıyor…");
            Task.Run(() =>
            {
                var sc = new RaidScanner(Log, () => tok.IsCancellationRequested);
                int i = 0;
                foreach (var (m, info, idx) in members)
                {
                    if (tok.IsCancellationRequested) break;
                    Status($"disk okunuyor {++i}/{members.Count}: {m.Model}");
                    sc.AddDisk(m, info, idx);
                }
                Status("RAID / NAS / LVM yapıları kuruluyor…");
                sc.AssembleAll();
                foreach (var r in sc.Roots) r.SetDepth(0);
                foreach (var v in sc.Virtual) v.SetDepth(0);
                var roots = sc.Roots.ToList(); var virt = sc.Virtual.ToList();
                Dispatcher.Invoke(() =>
                {
                    _scanner = sc;
                    ShowTree(virt, roots);
                    Log($"Üst veriden kurulan: {virt.Count} sanal aygıt.");
                });

                // üst verisi olmayan eş boyutlu diskler → çözücü
                int before = sc.Virtual.Count;
                Status("üst verisiz diskler çözülüyor…");
                sc.SolveLeftovers(roots);
                sc.AssembleMd(); sc.AssembleLvm();
                var added = sc.Virtual.Skip(before).ToList();
                foreach (var v in added) v.SetDepth(0);
                Dispatcher.Invoke(() =>
                {
                    int at = 1;
                    foreach (var v in added) _items.Insert(at++, v);
                    Busy = false;
                    Status(tok.IsCancellationRequested ? "durduruldu" : $"bitti · {_scanner?.Virtual.Count ?? 0} sanal aygıt");
                    Log("Tarama bitti.");
                    if (_items.Count == 0) EmptyText.Visibility = Visibility.Visible;
                });
            });
        }

        private void ShowTree(List<RaidNode> virt, List<RaidNode> roots)
        {
            _items.Clear();
            _items.Add(new RaidNode { Kind = RaidNodeKind.Info, Icon = "", Name = "KURULAN RAID / BİRİMLER", NameBrush = Brs.Teal, Info = "Üst veriden ya da çözücüyle kurulan sanal diskler. Çift tıkla → Veri Kurtarma'da aç." });
            foreach (var v in virt) _items.Add(v);
            _items.Add(new RaidNode { Kind = RaidNodeKind.Info, Icon = "", Name = "FİZİKSEL DİSKLER", NameBrush = Brs.Teal, Info = "Takılı diskler ve imajlar. Kutucukla seçip '🧩 Seçili diskleri çöz' ya da '⚙ Elle RAID kur'." });
            foreach (var r in roots) { _items.Add(r); _diskNodes.Add(r); }
        }

        private void Stop_Click(object sender, RoutedEventArgs e) { _cts?.Cancel(); Log("Durdurma istendi."); }

        private void AddImage_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "RAID üyesi imaj(lar) seç", Multiselect = true,
                Filter = "Disk imajı|*.img;*.dd;*.bin;*.raw;*.001;*.ima;*.dsk|Tüm dosyalar|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            foreach (var f in dlg.FileNames.OrderBy(x => x))
            {
                if (_images.Any(i => i.ImagePath == f)) continue;
                try { _images.Add(ImageMember(f)); Log("İmaj eklendi: " + f); } catch (Exception ex) { Log("İmaj açılamadı: " + ex.Message); }
            }
            Scan_Click(sender, e);
        }

        // ---------------- seçim / ayrıntı ----------------
        private RaidNode? Sel => Tree.SelectedItem as RaidNode;

        private void Tree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var n = Sel;
            bool has = n != null && n.Open != null && n.Kind != RaidNodeKind.Info;
            OpenBtn.IsEnabled = has; HexBtn.IsEnabled = has; ImageBtn.IsEnabled = has;
            ParityBtn.IsEnabled = n?.Config != null && n.Config.Kind is RaidKind.Raid5 or RaidKind.Raid6;
            EditBtn.IsEnabled = n?.Config != null;
            SaveCfgBtn.IsEnabled = n?.Config != null;
            StaleBtn.IsEnabled = n?.Config != null && n.Config.Kind is RaidKind.Raid5 or RaidKind.Raid6 && n.Config.Missing == 0;
            OldBtn.IsEnabled = n?.Member != null && n.Kind is RaidNodeKind.Disk or RaidNodeKind.Part;
            PropBtn.IsEnabled = n != null && n.Kind is RaidNodeKind.Disk or RaidNodeKind.Part or RaidNodeKind.Volume or RaidNodeKind.Raid;
            if (n == null) { DetTitle.Text = "Seçili öğe"; DetText.Text = ""; return; }
            DetTitle.Text = n.Name;
            DetText.Text = n.Info;
        }

        private void Tree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<ToggleButton>(d) != null) return;
            if (e.OriginalSource is DependencyObject d2 && FindParent<CheckBox>(d2) != null) return;
            var n = Sel;
            if (n?.Open != null && n.Kind != RaidNodeKind.Info && n.Kind != RaidNodeKind.LvmVg) { e.Handled = true; OpenNode(n); }
        }

        private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            DependencyObject? cur = d;
            while (cur != null && cur is not T)
                cur = cur is System.Windows.Media.Visual || cur is System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(cur) ?? LogicalTreeHelper.GetParent(cur)
                    : LogicalTreeHelper.GetParent(cur);
            return cur as T;
        }

        private int[] DrivesOf(RaidNode n)
        {
            var ids = new List<string>();
            if (n.Config != null) ids.AddRange(n.Config.Members.Where(m => m != null).Select(m => m!.DeviceId));
            if (n.Member != null) ids.Add(n.Member.DeviceId);
            if (ids.Count == 0 && _scanner != null)   // RAID içindeki bölüm/birim: tüm üye diskler
                ids.AddRange(_scanner.Virtual.SelectMany(v => v.Config?.Members ?? Array.Empty<RaidMember?>()).Where(m => m != null).Select(m => m!.DeviceId));
            return ids.Select(DriveNo).Where(x => x >= 0).Distinct().ToArray();
        }

        private void OpenNode(RaidNode n)
        {
            if (n.Open == null || _openInRecovery == null) return;
            Log("Veri Kurtarma'da açılıyor: " + n.Name);
            _openInRecovery(n.Open, n.Length, n.Name, DrivesOf(n));
        }

        private void OpenNode_Click(object sender, RoutedEventArgs e) { if (Sel != null) OpenNode(Sel); }

        private void Hex_Click(object sender, RoutedEventArgs e)
        {
            var n = Sel; if (n?.Open == null) return;
            new HexViewWindow(n.Open, n.Length, n.Name, n.Locate) { Owner = Window.GetWindow(this) }.Show();
        }

        // ---------------- sağ tık menüsü ----------------
        private void Tree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d && FindParent<TreeViewItem>(d) is TreeViewItem tvi) { tvi.IsSelected = true; tvi.Focus(); }
        }

        private void Tree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            var n = Sel;
            TreeMenu.Items.Clear();
            if (n == null || n.Kind == RaidNodeKind.Info) { e.Handled = true; return; }
            void Add(string text, Action a, bool enabled = true)
            {
                var mi = new MenuItem { Header = text, IsEnabled = enabled };
                mi.Click += (_, _) => a();
                TreeMenu.Items.Add(mi);
            }
            if (n.Open != null && n.Kind != RaidNodeKind.LvmVg) Add("📂 Veri Kurtarma'da aç", () => OpenNode(n));
            if (n.Config != null)
            {
                Add("▦ RAID bilgileri", () => ShowRight("RAID bilgileri — " + n.Name, RaidScanner.RaidInfoText(n.Config!)));
                Add("🧪 Parite kontrolü", () => Parity_Click(this, new RoutedEventArgs()), n.Config.Kind is RaidKind.Raid5 or RaidKind.Raid6);
                Add("🔎 Eski (düşmüş) diski bul", () => Stale_Click(this, new RoutedEventArgs()), n.Config.Kind is RaidKind.Raid5 or RaidKind.Raid6 && n.Config.Missing == 0);
                Add("⚙ Ayarları değiştir (sıra, blok, yerleşim…)", () => Edit_Click(this, new RoutedEventArgs()));
                Add("💾 Yapılandırmayı kaydet", () => SaveCfg_Click(this, new RoutedEventArgs()));
            }
            if (n.Member != null && n.Kind is RaidNodeKind.Disk or RaidNodeKind.Part)
                Add("▦ Eski RAID yapısını göster (sıra, blok, sol/sağ, seviye)", () => Old_Click(this, new RoutedEventArgs()));
            if (n.Kind is RaidNodeKind.Disk or RaidNodeKind.Part or RaidNodeKind.Volume or RaidNodeKind.Raid)
                Add("ℹ Özellikler", () => Props_Click(this, new RoutedEventArgs()));
            if (n.Kind == RaidNodeKind.Disk) Add(n.IsChecked ? "☐ Çözücü seçiminden çıkar" : "☑ Çözücü için işaretle", () => n.IsChecked = !n.IsChecked);
            if (n.Open != null && n.Kind != RaidNodeKind.LvmVg)
            {
                TreeMenu.Items.Add(new Separator());
                Add("🔍 Kayıp bölüm / dosya sistemi / RAID-LVM parçası ara…", () => FsScan(n));
                Add("✚ Bölüm tanımla (başlangıç + boyut)…", () => DefinePart(n));
                Add("⇆ Bölüm tablosunu 4096 bayt sektörle oku", () => Reread4K(n));
            }
            if (n.Member != null && n.Kind == RaidNodeKind.Disk)
            {
                Add("🩹 Okunamayan sektör haritası yükle (ddrescue / LBA listesi)…", () => LoadBadMap(n));
                if (n.Member.BadRanges != null) Add("🩹 Sektör haritasını kaldır", () => { n.Member.SetBadMap(null); n.Status = "harita kaldırıldı"; Log("Harita kaldırıldı: " + n.Name); });
            }
            if (n.Open != null)
            {
                TreeMenu.Items.Add(new Separator());
                Add("🔎 Hex görüntüle", () => Hex_Click(this, new RoutedEventArgs()));
                Add("💾 İmaj al", () => Image_Click(this, new RoutedEventArgs()));
                Add("#️⃣ Hash hesapla (MD5 / SHA-1 / SHA-256)", () => Hash(n));
            }
            if (n.Kind is RaidNodeKind.Raid or RaidNodeKind.Volume or RaidNodeKind.LvmVg || (n.Kind == RaidNodeKind.Disk && n.Member?.ImagePath.Length > 0))
                Add("✕ Listeden çıkar", () => RemoveNode(n));
            Add("📋 Satırı kopyala", () => { try { Clipboard.SetText($"{n.Name}\t{n.Detail}\t{n.StartText}\t{n.SizeText}\t{n.Status}"); } catch { } });
        }

        private void Props_Click(object sender, RoutedEventArgs e)
        {
            var n = Sel; if (n == null) return;
            ShowRight("Özellikler — " + n.Name, "okunuyor…");
            Task.Run(() =>
            {
                string t = n.Kind switch
                {
                    RaidNodeKind.Disk when n.Member != null => RaidProps.Disk(n.Member, n.Disk),
                    RaidNodeKind.Raid when n.Config != null => RaidScanner.RaidInfoText(n.Config) + Environment.NewLine + Environment.NewLine + RaidProps.Volume(n),
                    _ => RaidProps.Volume(n)
                };
                Dispatcher.Invoke(() => ShowRight("Özellikler — " + n.Name, t));
            });
        }

        private void Old_Click(object sender, RoutedEventArgs e)
        {
            var n = Sel; if (n?.Member == null) return;
            ShowRight("Eski RAID yapısı — " + n.Name, "okunuyor…");
            var sc = _scanner; var m = n.Member; bool whole = n.Kind == RaidNodeKind.Disk;
            Task.Run(() =>
            {
                string t = RaidProps.OldStructure(m, whole, sc);
                if (whole && n.Children.Count > 0)
                    foreach (var ch in n.Children.Where(c => c.Member != null))
                    {
                        string sub = RaidProps.OldStructure(ch.Member!, false, sc);
                        if (!sub.Contains("üst verisi (md / DDF / Intel / LVM) YOK")) t += Environment.NewLine + Environment.NewLine + sub;
                    }
                Dispatcher.Invoke(() => ShowRight("Eski RAID yapısı — " + n.Name, t));
            });
        }

        private void Stale_Click(object sender, RoutedEventArgs e)
        {
            var n = Sel; var c = n?.Config; if (n == null || c == null) return;
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var tok = _cts.Token;
            Busy = true; Status("eski disk aranıyor…");
            ShowRight("Eski diski bul — " + n.Name, "Her disk sırayla 'eksik' sayılıp sanal disk pariteden kuruluyor, NTFS kayıtları sayılıyor…");
            Task.Run(() =>
            {
                string t = RaidProps.FindStale(c, Log, () => tok.IsCancellationRequested, out int slot);
                Log(t.TrimEnd());
                Dispatcher.Invoke(() =>
                {
                    Busy = false; Status("hazır");
                    ShowRight("Eski diski bul — " + n.Name, t);
                    if (slot >= 0 && MessageBox.Show($"{slot + 1}. disk eski görünüyor ({c.Members[slot]!.Name}).\nBu disk hariç (pariteden) yeni bir RAID kurulsun mu?", "Eski disk", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    {
                        var c2 = c.Clone(); c2.Members[slot] = null; c2.Notes = (c2.Notes + $"\n{slot + 1}. disk eski olduğu için hariç tutuldu.").Trim();
                        AddVirtualNode(c2, n.Name + " (eski disk hariç)");
                    }
                });
            });
        }

        // ---------------- kayıp bölüm / FS / md-LVM parçası arama ----------------
        private RaidNode? ParentOf(RaidNode child)
        {
            RaidNode? Walk(IEnumerable<RaidNode> list)
            {
                foreach (var x in list) { if (x.Children.Contains(child)) return x; var r = Walk(x.Children); if (r != null) return r; }
                return null;
            }
            return Walk(_items);
        }

        private RaidNode FoundNode(RaidNode parent, long start, long len, string label, string fam)
        {
            var po = parent.Open!; var loc = parent.Locate;
            Func<Stream> op = () => new SubStream(po(), start, len);
            RaidMember mem = parent.Member != null ? parent.Member.Sub(start, len, parent.Name + " › " + label)
                : new RaidMember { Name = parent.Name + " › " + label, Length = len, Open = op, OpenStrict = op, DeviceId = "virt:" + parent.Name + ":" + start };
            var node = new RaidNode
            {
                Kind = RaidNodeKind.Volume, Icon = "🔍", Name = label, Detail = "bulundu (" + fam + ")", StartText = (start / 512).ToString("N0", Fmt.Tr),
                SizeText = Fmt.Size(len), Length = len, Start = start, Open = mem.Open, Member = mem, Fs = fam, StatusBrush = Brs.Teal, Status = "kayıp alandan",
                Locate = loc == null ? null : v => loc(v + start),
                Info = $"Kayıp alan aramasında bulundu: {label}\nBaşlangıç: sektör {start / 512:N0} ({Fmt.Size(start)})\nBoyut: {Fmt.Size(len)}\n(çift tık: Veri Kurtarma'da aç)"
            };
            node.SetDepth(parent.Depth + 1);
            return node;
        }

        private void FsScan(RaidNode n)
        {
            if (n.Open == null) return;
            var dlg = new FsScanDialog(n.Length, n.Name) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true) return;
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var tok = _cts.Token;
            Busy = true; n.IsExpanded = true;
            Log($"Kayıp alan araması: {n.Name} · sektör {dlg.From / 512:N0} + {dlg.Count / 512:N0} · {string.Join(", ", dlg.Fams)}");
            _scanner ??= new RaidScanner(Log, () => false);
            var sc = _scanner; var open = n.Open;
            int foundCnt = 0, partsAdded = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            Task.Run(() =>
            {
                try
                {
                    RaidFsScan.Scan(open, dlg.From, dlg.Count, dlg.Fams, f =>
                    {
                        foundCnt++;
                        Log($"  bulundu: {f.Label} · sektör {f.Start / 512:N0} · {Fmt.Size(f.Length)}");
                        Dispatcher.Invoke(() =>
                        {
                            var ch = FoundNode(n, f.Start, f.Length, f.Label, f.Fam);
                            n.Children.Add(ch);
                            if ((f.Fam is "md" or "LVM") && ch.Member != null && sc.AddFound(ch.Member, ch)) partsAdded++;
                        });
                    }, p => Status($"aranıyor %{p * 100:0.0} · {foundCnt} bulundu · {sw.Elapsed:hh\\:mm\\:ss}"), () => tok.IsCancellationRequested);
                }
                catch (Exception ex) { Log("Arama hatası: " + ex.Message); }
                // bulunan md/LVM parçalarından dizi / birim kur
                List<RaidNode> added = new();
                if (partsAdded > 0)
                {
                    int before = sc.Virtual.Count;
                    sc.AssembleMd(); sc.AssembleLvm();
                    added = sc.Virtual.Skip(before).ToList();
                    foreach (var v in added) v.SetDepth(0);
                }
                Dispatcher.Invoke(() =>
                {
                    int at = 1; foreach (var v in added) _items.Insert(at++, v);
                    Busy = false;
                    Status(tok.IsCancellationRequested ? "arama durduruldu" : $"arama bitti · {foundCnt} bulundu");
                    Log($"Arama {(tok.IsCancellationRequested ? "durduruldu" : "bitti")}: {foundCnt} bulgu" + (added.Count > 0 ? $" · bulunan parçalardan {added.Count} RAID/LVM kuruldu" : "") + $" · süre {sw.Elapsed:hh\\:mm\\:ss}");
                });
            });
        }

        // elle bölüm tanımla
        private void DefinePart(RaidNode n)
        {
            if (n.Open == null) return;
            string? a = Prompt("Bölüm tanımla", "Başlangıç sektörü:", "2048");
            if (a == null || !long.TryParse(a.Replace(".", ""), out long st)) return;
            string? b = Prompt("Bölüm tanımla", "Sektör sayısı (boş = sonuna kadar):", ((n.Length / 512) - st).ToString());
            if (b == null) return;
            long cnt = long.TryParse(b.Replace(".", ""), out long c) && c > 0 ? c : (n.Length / 512) - st;
            long start = st * 512, len = Math.Min(cnt * 512, n.Length - start);
            if (start < 0 || len <= 0) { Log("Geçersiz aralık."); return; }
            string label = "Elle tanımlı bölüm";
            try { using var s = new SubStream(n.Open(), start, len); var p = RaidMeta.Identify(s, len, false); label += " · " + p.Label; } catch { }
            var ch = FoundNode(n, start, len, label, "elle");
            ch.Icon = "✚"; ch.Status = "elle"; ch.Detail = "";
            n.Children.Add(ch); n.IsExpanded = true;
            Log($"Bölüm tanımlandı: {n.Name} · sektör {st:N0} · {Fmt.Size(len)} → {label}");
        }

        // USB kutusu 4K sektör gösteriyorsa (ya da tersi) bölüm tablosunu 4096 ile oku
        private void Reread4K(RaidNode n)
        {
            if (n.Open == null) return;
            try
            {
                List<DiskPartition> parts;
                using (var s = n.Open()) parts = PartitionReader.Read(s, 4096);
                if (parts.Count == 0) { Log("4096 bayt sektörle bölüm tablosu bulunamadı."); return; }
                foreach (var p in parts)
                {
                    long len = Math.Min(p.Length, n.Length - p.Offset); if (len <= 0) continue;
                    string label;
                    try { using var ss = new SubStream(n.Open(), p.Offset, len); label = RaidMeta.Identify(ss, len, false).Label; } catch { label = "?"; }
                    var ch = FoundNode(n, p.Offset, len, label + " (4K sektör)", "4K");
                    ch.Icon = "⇆"; ch.Status = "4K sektörle"; n.Children.Add(ch);
                }
                n.IsExpanded = true;
                Log($"4096 bayt sektörle {parts.Count} bölüm okundu: {n.Name}");
            }
            catch (Exception ex) { Log("Okunamadı: " + ex.Message); }
        }

        // ddrescue mapfile / PC-3000 / LBA listesi
        private void LoadBadMap(RaidNode n)
        {
            var m = n.Member; if (m == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Okunamayan sektör haritası (" + m.Name + ")", Filter = "Harita|*.map;*.log;*.txt;*.csv|Tüm dosyalar|*.*" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var map = BadMap.Load(dlg.FileName, 512);
                m.SetBadMap(map);
                n.Status = $"harita: {BadMap.Total(map) / 512:N0} bozuk sektör"; n.StatusBrush = Brs.Warn;
                Log($"Sektör haritası yüklendi: {m.Name} · {map.Count} bölge · {BadMap.Total(map) / 512:N0} sektör → RAID 5/6'da pariteden gerçek veriyle tamamlanacak, diğerlerinde sıfır.");
                Log("Not: bundan sonra açılan sekmeler, imajlar ve hesaplar haritayı kullanır (açık sekmeyi kapatıp yeniden açın).");
            }
            catch (Exception ex) { Log("Harita okunamadı: " + ex.Message); }
        }

        // MD5 / SHA-1 / SHA-256 tek geçişte
        private void Hash(RaidNode n)
        {
            if (n.Open == null) return;
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var tok = _cts.Token;
            Busy = true; var open = n.Open; long total = n.Length;
            Log($"Hash hesaplanıyor: {n.Name} ({Fmt.Size(total)})");
            Task.Run(() =>
            {
                using var md5 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.MD5);
                using var sha1 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA1);
                using var sha256 = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                var sw = System.Diagnostics.Stopwatch.StartNew(); long done = 0;
                try
                {
                    using var s = open(); var buf = new byte[4 << 20];
                    while (done < total && !tok.IsCancellationRequested)
                    {
                        int want = (int)Math.Min(buf.Length, total - done);
                        s.Position = done; int got = 0;
                        while (got < want) { int r = s.Read(buf, got, want - got); if (r <= 0) break; got += r; }
                        if (got < want) Array.Clear(buf, got, want - got);
                        md5.AppendData(buf, 0, want); sha1.AppendData(buf, 0, want); sha256.AppendData(buf, 0, want);
                        done += want;
                        double mbs = done / Math.Max(0.5, sw.Elapsed.TotalSeconds) / 1048576;
                        Status($"hash %{done * 100.0 / total:0.0} · {mbs:0} MB/s");
                    }
                    if (tok.IsCancellationRequested) { Log("Hash durduruldu."); }
                    else
                    {
                        string t = $"MD5    : {Convert.ToHexString(md5.GetHashAndReset())}\nSHA-1  : {Convert.ToHexString(sha1.GetHashAndReset())}\nSHA-256: {Convert.ToHexString(sha256.GetHashAndReset())}\nBoyut  : {total:N0} bayt · süre {sw.Elapsed:hh\\:mm\\:ss}";
                        Log("Hash — " + n.Name + "\n" + t);
                        Dispatcher.Invoke(() => ShowRight("Hash — " + n.Name, t));
                    }
                }
                catch (Exception ex) { Log("Hash hatası: " + ex.Message); }
                Dispatcher.Invoke(() => { Busy = false; Status("hazır"); });
            });
        }

        private void RemoveNode(RaidNode n)
        {
            if (n.Kind == RaidNodeKind.Disk && n.Member != null && n.Member.ImagePath.Length > 0)
            {
                _images.RemoveAll(i => i.ImagePath == n.Member.ImagePath);
                Log("İmaj listeden çıkarıldı: " + n.Member.ImagePath);
                Scan_Click(this, new RoutedEventArgs());
                return;
            }
            if (_items.Contains(n)) _items.Remove(n);
            else ParentOf(n)?.Children.Remove(n);
            _scanner?.Virtual.Remove(n);
        }

        // basit tek satırlık giriş kutusu
        private string? Prompt(string title, string label, string def)
        {
            var w = new Window { Title = title, Width = 420, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(this), Background = Theme.Brush("Bg") };
            var sp = new StackPanel { Margin = new Thickness(14) };
            sp.Children.Add(new TextBlock { Text = label, Foreground = Theme.Brush("Text"), Margin = new Thickness(0, 0, 0, 6) });
            var tb = new TextBox { Text = def, Height = 28, VerticalContentAlignment = VerticalAlignment.Center, Background = Theme.Brush("Card"), Foreground = Theme.Brush("Text") };
            sp.Children.Add(tb);
            var ok = new Button { Content = "Tamam", Height = 30, Width = 90, Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
            ok.Click += (_, _) => w.DialogResult = true;
            sp.Children.Add(ok);
            w.Content = sp;
            tb.Focus(); tb.SelectAll();
            return w.ShowDialog() == true ? tb.Text.Trim() : null;
        }

        // ---------------- imaj al ----------------
        private void Image_Click(object sender, RoutedEventArgs e)
        {
            var n = Sel; if (n?.Open == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "İmaj olarak kaydet", FileName = Safe(n.Name) + ".img", Filter = "Ham imaj|*.img|Tüm dosyalar|*.*" };
            if (dlg.ShowDialog() != true) return;
            string path = dlg.FileName;
            var src = DrivesOf(n); var dst = DiskGuard.PhysicalDriveNumbers(path);
            if (src.Intersect(dst).Any()) { MessageBox.Show("Hedef, kaynak disklerden birinin üzerinde. Kaynağa yazılmaz — başka bir disk seçin.", "Kaynak korunuyor", MessageBoxButton.OK, MessageBoxImage.Warning); return; }
            long free = DiskGuard.FreeSpace(path);
            if (free >= 0 && free < n.Length) { MessageBox.Show($"Hedefte yer yok: gereken {Fmt.Size(n.Length)}, boş {Fmt.Size(free)}.", "Yer yok"); return; }
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var tok = _cts.Token;
            Busy = true;
            Log($"İmaj alınıyor: {n.Name} → {path} ({Fmt.Size(n.Length)})");
            var open = n.Open; long total = n.Length;
            Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long done = 0;
                RaidStream.TotalReconstructed = 0; RaidStream.TotalUnreadable = 0;
                try
                {
                    using var s = open();
                    using var o = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
                    var buf = new byte[4 << 20];
                    while (done < total && !tok.IsCancellationRequested)
                    {
                        int want = (int)Math.Min(buf.Length, total - done);
                        s.Position = done;
                        int got = 0;
                        while (got < want) { int r = s.Read(buf, got, want - got); if (r <= 0) break; got += r; }
                        if (got < want) Array.Clear(buf, got, want - got);
                        o.Write(buf, 0, want);
                        done += want;
                        double sec = Math.Max(0.5, sw.Elapsed.TotalSeconds);
                        double mbs = done / sec / 1048576;
                        double eta = mbs > 0 ? (total - done) / 1048576.0 / mbs : 0;
                        Status($"imaj %{done * 100.0 / total:0.0} · {mbs:0} MB/s · kalan {TimeSpan.FromSeconds(eta):hh\\:mm\\:ss}");
                    }
                    Log(tok.IsCancellationRequested ? $"İmaj durduruldu ({Fmt.Size(done)} yazıldı)." :
                        $"İmaj tamam: {path} · süre {sw.Elapsed:hh\\:mm\\:ss}" +
                        (RaidStream.TotalReconstructed > 0 ? $" · pariteden tamamlanan {Fmt.Size(RaidStream.TotalReconstructed)}" : "") +
                        (RaidStream.TotalUnreadable > 0 ? $" · OKUNAMAYAN {Fmt.Size(RaidStream.TotalUnreadable)} (sıfırla dolduruldu)" : ""));
                }
                catch (Exception ex) { Log("İmaj hatası: " + ex.Message); }
                Dispatcher.Invoke(() => { Busy = false; Status("hazır"); });
            });
        }

        static string Safe(string s) => string.Join("_", s.Split(Path.GetInvalidFileNameChars())).Replace(' ', '_');

        // ---------------- parite kontrolü ----------------
        private void Parity_Click(object sender, RoutedEventArgs e)
        {
            var c = Sel?.Config; if (c == null) return;
            if (c.Missing > 0) { Log("Parite kontrolü için tüm diskler gerekli (eksik disk var)."); return; }
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var tok = _cts.Token;
            Busy = true; Status("parite kontrol ediliyor…");
            Task.Run(() =>
            {
                var (ok, tot) = RaidStream.ParityCheck(c, 400, () => tok.IsCancellationRequested);
                Log(tot == 0 ? "Parite: ölçülecek dolu satır bulunamadı." :
                    $"Parite kontrolü: {ok}/{tot} satır tutarlı (%{ok * 100.0 / tot:0.0})" +
                    (ok == tot ? " → üyeler ve sıra doğru." : ok * 10 >= tot * 9 ? " → büyük ölçüde doğru (birkaç satır yazma anında kesilmiş olabilir)." : " → TUTARSIZ: eski (düşmüş) bir disk ya da yanlış yerleşim olabilir."));
                Dispatcher.Invoke(() => { Busy = false; Status("hazır"); });
            });
        }

        // ---------------- elle kur / düzenle ----------------
        private List<RaidMember> AvailableMembers()
        {
            var l = new List<RaidMember>();
            void Walk(RaidNode n) { if (n.Member != null && (n.Kind == RaidNodeKind.Disk || n.Kind == RaidNodeKind.Part)) l.Add(n.Member); foreach (var c in n.Children) Walk(c); }
            foreach (var d in _diskNodes) Walk(d);
            return l;
        }

        private void Manual_Click(object sender, RoutedEventArgs e)
        {
            var checkedDisks = _diskNodes.Where(d => d.IsChecked && d.Member != null).Select(d => d.Member!).ToList();
            var c = new RaidConfig { Kind = RaidKind.Raid5, Layout = RaidLayout.LeftSym, Chunk = 65536, Members = checkedDisks.Cast<RaidMember?>().ToArray(), DisksPerGroup = Math.Max(1, checkedDisks.Count), DataStart = new long[checkedDisks.Count], Source = "Elle kuruldu" };
            Edit(c);
        }

        private void Edit_Click(object sender, RoutedEventArgs e) { if (Sel?.Config != null) Edit(Sel.Config.Clone()); }

        private void Edit(RaidConfig c)
        {
            var w = new RaidBuilderWindow(c, AvailableMembers(), Log) { Owner = Window.GetWindow(this) };
            if (w.ShowDialog() != true || w.Result == null) return;
            AddVirtualNode(w.Result, "Elle: " + w.Result.LevelText());
        }

        private void AddVirtualNode(RaidConfig c, string title)
        {
            _scanner ??= new RaidScanner(Log, () => false);
            var sc = _scanner;
            Busy = true; Status("içerik okunuyor…");
            Task.Run(() =>
            {
                int before = sc.Virtual.Count;
                RaidNode node;
                try { node = sc.AddVirtual(c, title); sc.AssembleMd(); sc.AssembleLvm(); }
                catch (Exception ex) { Log("Kurulamadı: " + ex.Message); Dispatcher.Invoke(() => Busy = false); return; }
                var added = sc.Virtual.Skip(before).ToList();
                foreach (var v in added) v.SetDepth(0);
                Dispatcher.Invoke(() =>
                {
                    if (_items.Count == 0) ShowTree(new List<RaidNode>(), _diskNodes.ToList());
                    int at = 1;
                    foreach (var v in added) _items.Insert(at++, v);
                    Busy = false; Status("hazır");
                    Log($"Kuruldu: {c.Summary()} → {Fmt.Size(c.Capacity())}");
                    if (node.Children.Count > 0) Log("İçerik: " + string.Join(", ", node.Children.Select(x => x.Name + " " + x.SizeText)));
                });
            });
        }

        // ---------------- seçili diskleri çöz ----------------
        private void SolveSelected_Click(object sender, RoutedEventArgs e)
        {
            var members = _diskNodes.Where(d => d.IsChecked && d.Member != null).Select(d => d.Member!).ToList();
            if (members.Count < 2) { MessageBox.Show("En az 2 diskin kutucuğunu işaretleyin (FİZİKSEL DİSKLER altında).", "RAID çözücü"); return; }
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var tok = _cts.Token;
            Busy = true; Status("çözücü çalışıyor…");
            Log($"Çözücü: {members.Count} disk → {string.Join(", ", members.Select(m => m.Name))}");
            Task.Run(() =>
            {
                var r = RaidSolver.Solve(members, null, Log, () => tok.IsCancellationRequested);
                Log(r.Report.TrimEnd());
                Dispatcher.Invoke(() => Busy = false);
                if (r.Config == null) { Status("bulunamadı"); return; }
                if (r.Score < 0.6) Log("⚠ Güven düşük — sonucu kontrol edin (içerik tanınıyor mu, parite kontrolü).");
                Dispatcher.Invoke(() => AddVirtualNode(r.Config, "Çözücü: " + r.Config.LevelText()));
            });
        }

        // ---------------- yapılandırma dosyası ----------------
        private void SaveCfg_Click(object sender, RoutedEventArgs e)
        {
            var c = Sel?.Config; if (c == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog { Title = "RAID yapılandırmasını kaydet", FileName = Safe(c.Name) + ".vkmraid", Filter = "VKM RAID|*.vkmraid" };
            if (dlg.ShowDialog() != true) return;
            File.WriteAllText(dlg.FileName, c.ToText());
            Log("Yapılandırma kaydedildi: " + dlg.FileName);
        }

        private void LoadCfg_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "RAID yapılandırması aç", Filter = "VKM RAID|*.vkmraid|Tüm dosyalar|*.*" };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var c = RaidConfig.FromText(File.ReadAllText(dlg.FileName), AvailableMembers());
                if (c.Members.All(m => m == null)) { Log("Yapılandırmadaki diskler bulunamadı — önce diskleri tarayın / imajları ekleyin."); return; }
                AddVirtualNode(c, "Kayıtlı: " + c.LevelText());
            }
            catch (Exception ex) { Log("Yapılandırma okunamadı: " + ex.Message); }
        }
    }
}
