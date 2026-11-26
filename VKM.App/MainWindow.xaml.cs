using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VKM.App
{
    public partial class MainWindow : Window
    {
        public ObservableCollection<DiskInfo> Disks { get; } = new();
        private static readonly CultureInfo Tr = new("tr-TR");

        public MainWindow()
        {
            InitializeComponent();
            CctvDiskList.ItemsSource = Disks;   // CCTV modülü aynı disk listesini gösterir
            BuildModules();
            LoadDisks();
        }

        // Pencere kapanınca arka plan iş parçacıkları kalmasın, süreç tam kapansın
        // (yoksa .exe bellekte takılır ve bir sonraki derlemede dosya kilidi hatası verir)
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            Environment.Exit(0);
        }

        private bool _autoScanned;
        private void BuildModules()
        {
            ModuleBar.AddModule("veri", "🔍", "Veri Kurtarma");
            ModuleBar.AddModule("cctv", "📹", "CCTV");
            ModuleBar.AddModule("dosya", "🔧", "Dosya Onarım");
            ModuleBar.AddModule("imajal", "💾", "İmaj Al");
            ModuleBar.AddModule("journal", "🕑", "Adli Bilişim");
            ModuleBar.AddModule("boot", "🩹", "Boot Onarım");
            ModuleBar.AddModule("sql", "🗄", "SQL");
            ModuleBar.AddModule("imajboot", "▣", "İmaj Boot");
            ModuleBar.AddModule("usb", "🔌", "USB Dongle");
            ModuleBar.ModuleChanged += OnModule;
            ModuleBar.Select("veri");

            LogList.ItemsSource = Log.Items;
            Log.Added += () => { LogCount.Text = Log.Items.Count > 0 ? "(" + Log.Items.Count + ")" : ""; LogScroll.ScrollToEnd(); };
            Log.Info("Veri Kurtarma Merkezi hazır.");

            // Sol UFS ağacı = RAID paneli (disk → bölüm → üyelerin altında dizi); açılan düğüm sağda sekmede
            DevTree.Init(Disks, (open, len, title, drives) => AddRecoveryTab(new RecoveryWindow(open, len, title, drives), title, null));
            UpdateDiskPanel();
        }

        private double _logH = 170;
        private void LogClear_Click(object sender, RoutedEventArgs e) => Log.Clear();
        private void LogToggle_Click(object sender, RoutedEventArgs e)
        {
            bool open = LogScroll.Visibility == Visibility.Visible;
            if (open)
            {
                if (LogRow.ActualHeight > 40) _logH = LogRow.ActualHeight;
                LogScroll.Visibility = Visibility.Collapsed; LogSplit.Visibility = Visibility.Collapsed;
                LogRow.Height = GridLength.Auto; LogToggle.Content = "▴";
            }
            else
            {
                LogScroll.Visibility = Visibility.Visible; LogSplit.Visibility = Visibility.Visible;
                LogRow.Height = new GridLength(_logH); LogToggle.Content = "▾";
            }
        }

        // iş açıkken solda sadece o diski göster; iş kapanınca (null) hepsini geri getir
        public void FocusDisk(DiskInfo? d) { }   // (UFS ağacı sabit kalır)

        // iş/sekme açıkken sol disk panelini tamamen gizle (temiz ekran); hepsi kapanınca geri getir
        private void UpdateDiskPanel()
        {
            if (DevCol == null || TabCol == null || MidCol == null) return;
            bool busy = MainTabs != null && MainTabs.Items.Count > 0;
            DevCol.Width = busy ? new GridLength(480) : new GridLength(1, GridUnitType.Star);
            MidCol.Width = new GridLength(busy ? 6 : 0);
            TabCol.Width = busy ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }

        // UFS gibi temiz disk adı: baştaki "ATA", sondaki "SCSI Disk Device / USB Device" vb. gürültüyü at
        static string CleanModel(string m)
        {
            if (string.IsNullOrWhiteSpace(m)) return "Bilinmeyen disk";
            m = m.Trim();
            string[] tails = { " SCSI Disk Device", " SATA Disk Device", " USB Device", " ATA Device", " SCSI Disk", " Disk Device" };
            bool changed = true;
            while (changed) { changed = false; foreach (var t in tails) if (m.EndsWith(t, StringComparison.OrdinalIgnoreCase)) { m = m[..^t.Length].Trim(); changed = true; } }
            if (m.StartsWith("ATA ", StringComparison.OrdinalIgnoreCase)) m = m[4..].Trim();
            while (m.Contains("  ")) m = m.Replace("  ", " ");
            return m.Length == 0 ? "Bilinmeyen disk" : m;
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadDisks();

        // Kurtarma panelini yeni bir sekmede aç (kapatılabilir)
        // İş root'u: modül değiştirmeden üstte küçük bir pencerede aç (İmaj Al haritası + Başlat altta kalır)
        private Window? _browseWin;
        private void OpenBrowseWindow(System.Func<System.IO.Stream> open, long len, string title, int[] drives)
        {
            var view = new RecoveryWindow(open, len, title, drives);
            if (_browseWin != null) { try { _browseWin.Close(); } catch { } _browseWin = null; }
            var win = new Window
            {
                Title = title,
                Content = view,
                Width = 1060, Height = 700,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = Theme.Brush("Bg")
            };
            win.Closed += (_, __) => { try { view.Cleanup(); } catch { } if (ReferenceEquals(_browseWin, win)) _browseWin = null; };
            _browseWin = win;
            win.Show();
            Log.Ok("İş açıldı: " + title);
        }

        // PC-3000/UFS case panelini ÜST SEKME şeridinde aç (ayrı pencere değil)
        public void AddCaseTab(CasePanel panel, string title, DiskInfo? src)
        {
            if (ModuleBar != null && ModuleBar.SelectedTag != "veri") ModuleBar.Select("veri");

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock { Text = title.Length > 30 ? title.Substring(0, 29) + "…" : title, VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("Text"), FontSize = 12.5, FontWeight = FontWeights.SemiBold });
            var close = new Button { Content = "✕", Width = 18, Height = 18, Padding = new Thickness(0), Margin = new Thickness(10, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Theme.Brush("Dim"), Cursor = Cursors.Hand, FontSize = 11 };
            header.Children.Add(close);

            var tab = new TabItem { Header = header, Content = panel };
            close.Click += (_, __) =>
            {
                MainTabs.Items.Remove(tab);
                try { panel.Cleanup(); } catch { }
                FocusDisk(null);
                if (MainTabs.Items.Count == 0) EmptyHint.Visibility = Visibility.Visible;
                UpdateDiskPanel();
            };
            MainTabs.Items.Add(tab);
            MainTabs.SelectedItem = tab;
            EmptyHint.Visibility = Visibility.Collapsed;
            FocusDisk(src);
            UpdateDiskPanel();
        }

        private void AddRecoveryTab(RecoveryWindow view, string title, DiskInfo? owner)
        {
            // Sekmeler Veri Kurtarma modülünde; başka moduldeysek oraya geç ki görünsün
            if (ModuleBar != null && ModuleBar.SelectedTag != "veri") ModuleBar.Select("veri");

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = title.Length > 26 ? title.Substring(0, 25) + "…" : title,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Theme.Brush("Text"),
                FontSize = 12.5
            });
            var close = new Button
            {
                Content = "✕",
                Width = 18,
                Height = 18,
                Padding = new Thickness(0),
                Margin = new Thickness(10, 0, 0, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Theme.Brush("Dim"),
                Cursor = Cursors.Hand,
                FontSize = 11
            };
            header.Children.Add(close);

            var tab = new TabItem { Header = header, Content = view };
            close.Click += (_, __) =>
            {
                MainTabs.Items.Remove(tab);
                try { view.Cleanup(); } catch { }
                if (owner != null) owner.IsBusy = false;   // disk kilidi serbest
                if (MainTabs.Items.Count == 0) EmptyHint.Visibility = Visibility.Visible;
                UpdateDiskPanel();
            };

            MainTabs.Items.Add(tab);
            MainTabs.SelectedItem = tab;
            EmptyHint.Visibility = Visibility.Collapsed;
            UpdateDiskPanel();
        }

        // "＋ İmaj Aç" — disk imajı dosyası seç ve Veri Kurtarma sekmesinde aç
        private void OpenImage_Click(object sender, RoutedEventArgs e) => OpenImageCore(false);

        // CCTV'den "＋ DVR İmajı Aç" — aynı akış ama video taraması otomatik başlar
        private void Cctv_OpenImage_Click(object sender, RoutedEventArgs e) => OpenImageCore(false);

        private void OpenImageCore(bool autoVideo)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = autoVideo ? "DVR disk imajı seç" : "Disk imajı seç",
                Filter =
                    "İmaj dosyaları|*.img;*.dd;*.001;*.002;*.bin;*.raw;*.dsk;*.ima;*.flat;*.vhd;*.vhdx;*.vmdk;*.vdi;*.e01;*.ewf|" +
                    "Ham imaj (*.img;*.dd;*.001;*.bin;*.raw;*.dsk)|*.img;*.dd;*.001;*.bin;*.raw;*.dsk;*.ima;*.flat|" +
                    "Sanal disk (*.vhd;*.vhdx;*.vmdk;*.vdi)|*.vhd;*.vhdx;*.vmdk;*.vdi|" +
                    "Tüm dosyalar|*.*"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var view = new RecoveryWindow(dlg.FileName) { AutoVideoScan = autoVideo };
                AddRecoveryTab(view, System.IO.Path.GetFileName(dlg.FileName), null);
            }
            catch (Exception ex)
            {
                MessageBox.Show("İmaj açılamadı: " + ex.Message +
                    "\n\nBu imaj türü desteklenmiyor olabilir (ör. sıkıştırılmış/E01). Ham (dd) veya VHD/VHDX/VMDK/VDI deneyin.",
                    "İmaj", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // "Aç ▸" düğmesi — seçilen disk için Veri Kurtarma penceresini açar
        private void OpenRecovery_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is DiskInfo info)
                OpenForDisk(info);
        }

        // Diski işleme al — zaten işlemdeyse ikinci kez açtırma (PC-3000 mantığı)
        private void OpenForDisk(DiskInfo info, bool autoVideo = false)
        {
            if (string.IsNullOrEmpty(info.DeviceId))
            {
                MessageBox.Show("Bu disk için kimlik bulunamadı.");
                return;
            }
            if (info.IsBusy) return; // sessizce; buton zaten pasif + kırmızı ✕
            try
            {
                var view = new RecoveryWindow(info) { AutoVideoScan = autoVideo };
                info.IsBusy = true;                       // disk kilitlendi
                AddRecoveryTab(view, info.Model, info);
            }
            catch (Exception ex)
            {
                info.IsBusy = false;
                MessageBox.Show("Açılamadı: " + ex.Message);
            }
        }

        // Bir diskin bölümlerini (girmeden) listeye ekle — hızlı, sadece GPT/MBR okur
        private void ScanPartitions(DiskInfo disk)
        {
            try
            {
                int ss = disk.BytesPerSector <= 0 ? 512 : disk.BytesPerSector;
                System.Collections.Generic.List<DiskPartition> parts;
                using (var s = PhysicalDisk.OpenReadOnly(disk.DeviceId, (long)disk.SizeBytes, ss))
                    parts = PartitionReader.Read(s, ss);

                if (parts.Count == 0 && ss != 4096)
                {
                    ss = 4096;
                    using var s2 = PhysicalDisk.OpenReadOnly(disk.DeviceId, (long)disk.SizeBytes, ss);
                    parts = PartitionReader.Read(s2, ss);
                }

                foreach (DiskPartition p in parts)
                {
                    disk.Partitions.Add(new PartInfo
                    {
                        Name = p.Name,
                        SizeText = FormatSize((ulong)p.Length),
                        DeviceId = disk.DeviceId,
                        Offset = p.Offset,
                        Length = p.Length,
                        SectorSize = ss,
                        Owner = disk
                    });
                }
            }
            catch { }
        }

        // Her bölümün dosya sistemini ve etiketini arka planda tespit et
        private async void DetectPartitionFsAsync()
        {
            var all = new System.Collections.Generic.List<PartInfo>();
            foreach (DiskInfo d in Disks)
                foreach (PartInfo p in d.Partitions) all.Add(p);

            foreach (PartInfo p in all)
            {
                (string label, Brush color) = await System.Threading.Tasks.Task.Run(() => DetectOne(p));
                p.FsLabel = label;
                p.FsColor = color;
            }
        }

        private static (string, Brush) DetectOne(PartInfo p)
        {
            try
            {
                long total = (long)(p.Owner?.SizeBytes ?? 0);
                var dev = PhysicalDisk.OpenReadOnly(p.DeviceId, total, p.SectorSize);
                using var sub = new SubStream(dev, p.Offset, p.Length);
                var fs = VolumeMount.Open(sub, out string name, out _, out _);
                if (fs == null)
                {
                    // Küçük bölümler (MSR/EFI gibi sistem bölümleri) alarm değil; sadece büyükler RAID/şifreli adayı
                    if (p.Length < 2_000_000_000L)
                        return ("sistem bölümü", Theme.Brush("Dim"));
                    return ("— dosya sistemi yok (RAID/şifreli olabilir)", Theme.Brush("Bad"));
                }

                string vol = "";
                try { vol = fs.VolumeLabel ?? ""; } catch { }
                fs.Dispose();
                string label = name + (string.IsNullOrWhiteSpace(vol) ? "" : "  ·  " + vol.Trim());
                return (label, Theme.Brush("AccentText"));
            }
            catch
            {
                return ("okunamadı", Theme.Brush("Dim"));
            }
        }

        // Arka planda oluşturulan fırça, ana ekranda kullanılabilsin diye dondurulur
        private static Brush FrozenBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }

        // Modül seçimi (sol ray)
        private void OnModule(string tag)
        {
            if (ModuleStub == null || VeriKurtarmaPanel == null || CctvPanel == null || SqlPanel == null) return;   // kurulum anında koruma
            if (ImagerPanelCtl != null) ImagerPanelCtl.Visibility = tag == "imajal" ? Visibility.Visible : Visibility.Collapsed;
            if (tag == "imajal")
            {
                VeriKurtarmaPanel.Visibility = Visibility.Collapsed;
                CctvPanel.Visibility = Visibility.Collapsed;
                SqlPanel.Visibility = Visibility.Collapsed;
                ModuleStub.Visibility = Visibility.Collapsed;
                ImagerPanelCtl?.Init(Disks, (open, len, title, drives) => OpenBrowseWindow(open, len, title, drives));
                return;
            }
            if (tag == "veri")
            {
                VeriKurtarmaPanel.Visibility = Visibility.Visible;
                CctvPanel.Visibility = Visibility.Collapsed;
                SqlPanel.Visibility = Visibility.Collapsed;
                ModuleStub.Visibility = Visibility.Collapsed;
                return;
            }

            if (tag == "cctv")
            {
                VeriKurtarmaPanel.Visibility = Visibility.Collapsed;
                CctvPanel.Visibility = Visibility.Visible;
                SqlPanel.Visibility = Visibility.Collapsed;
                ModuleStub.Visibility = Visibility.Collapsed;
                return;
            }

            if (tag == "sql")
            {
                VeriKurtarmaPanel.Visibility = Visibility.Collapsed;
                CctvPanel.Visibility = Visibility.Collapsed;
                SqlPanel.Visibility = Visibility.Visible;
                ModuleStub.Visibility = Visibility.Collapsed;
                return;
            }

            VeriKurtarmaPanel.Visibility = Visibility.Collapsed;
            CctvPanel.Visibility = Visibility.Collapsed;
            SqlPanel.Visibility = Visibility.Collapsed;
            ModuleStub.Visibility = Visibility.Visible;
            StubTitle.Text = tag switch
            {
                "cctv" => "CCTV",
                "raid" => "RAID",
                "dosya" => "Dosya Onarım",
                "imajal" => "İmaj Al",
                "journal" => "Adli Bilişim Journal",
                "boot" => "Boot Onarım",
                "sql" => "SQL & Veritabanı",
                "imajboot" => "İmaj Boot",
                "usb" => "USB Dongle",
                _ => "Modül"
            };
            string extra = tag switch
            {
                "cctv" => "\n\nŞimdilik CCTV kurtarma, Veri Kurtarma'da bir DVR disk/imaj açıp\n'Video Tara (MP4)' ile çalışıyor.",
                "sql" => "\n\nMDF/.bak için diski tarayıp SQL sayfalarını birleştireceğiz.",
                _ => ""
            };
            StubText.Text = "Bu modül yapım aşamasında." + extra +
                            "\n\nÖncelik sırası: Veri Kurtarma → CCTV → RAID → SQL.";
        }

        // Soldaki disk satırına ÇİFT tıklama → sekmede aç.
        // Tek tıkla açmıyoruz: imaj seçme penceresinin çift tıkından sızan tek "mouse-up"
        // alttaki disk listesine düşüp kendiliğinden disk açmasın diye.
        private void DiskRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2) return;                 // sadece gerçek çift tık açar
            if (sender is FrameworkElement fe && fe.Tag is DiskInfo info) OpenForDisk(info);
        }

        // Bir bölüme ÇİFT tıklama → o diskin kurtarma sekmesi (disk satırını da tetiklemesin)
        private void Partition_Click(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;                             // disk satırının altına inmesin
            if (e.ClickCount < 2) return;
            if (sender is FrameworkElement fe && fe.Tag is PartInfo p && p.Owner != null)
                OpenForDisk(p.Owner);
        }

        // CCTV modülü: diske ÇİFT tıkla → aç ve video taramasını otomatik başlat
        private void Cctv_DiskRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount < 2) return;
            if (sender is FrameworkElement fe && fe.Tag is DiskInfo info) OpenForDisk(info);   // otomatik tarama yok; önce dosyalar gösterilir
        }

        private void LoadDisks()
        {
            Disks.Clear();
            int count = 0;
            bool anySmart = false;

            // SMART verilerini bir kere topla (InstanceName -> SmartData)
            Dictionary<string, SmartData> smartMap;
            try { smartMap = SmartReader.ReadAll(); }
            catch { smartMap = new Dictionary<string, SmartData>(); }

            try
            {
                using var searcher = new ManagementObjectSearcher("SELECT * FROM Win32_DiskDrive");
                foreach (ManagementObject mo in searcher.Get())
                {
                    string model = ((mo["Model"] as string) ?? "Bilinmeyen disk").Trim();
                    string serial = ((mo["SerialNumber"] as string) ?? "").Trim();
                    string iface = ((mo["InterfaceType"] as string) ?? "").Trim();
                    string status = ((mo["Status"] as string) ?? "").Trim();
                    string pnp = ((mo["PNPDeviceID"] as string) ?? "").Trim();
                    string deviceId = ((mo["DeviceID"] as string) ?? "").Trim();

                    ulong size = 0;
                    try { size = Convert.ToUInt64(mo["Size"]); } catch { }

                    int bps = 512;
                    try { bps = Convert.ToInt32(mo["BytesPerSector"]); } catch { }
                    if (bps <= 0) bps = 512;

                    var disk = new DiskInfo
                    {
                        Model = CleanModel(model),
                        Serial = string.IsNullOrEmpty(serial) ? "—" : serial,
                        Interface = string.IsNullOrEmpty(iface) ? "—" : iface,
                        SizeText = FormatSize(size),
                        DeviceId = deviceId,
                        SizeBytes = size,
                        BytesPerSector = bps,
                    };

                    smartMap.TryGetValue(SmartReader.Norm(pnp), out var smart);
                    if (smart != null && smart.HasData)
                    {
                        anySmart = true;
                        ApplySmart(disk, smart);
                    }
                    else
                    {
                        // SMART yok: Windows durumuna göre temel sağlık
                        SetBasicHealth(disk, status);
                        disk.SmartLine = "SMART okunamadı — ayrıntı için yönetici olarak çalıştır";
                    }

                    Disks.Add(disk);
                    ScanPartitions(disk);
                    count++;
                }

                string tail = anySmart
                    ? ""
                    : "   (SMART değerleri için: Visual Studio'yu ya da .exe'yi 'Yönetici olarak çalıştır'.)";
                // disk sayısı log'a düşer
                Log.Info(count + " disk bulundu.");
                if (!_autoScanned) { _autoScanned = true; Dispatcher.BeginInvoke(() => DevTree?.AutoScan()); }

                DetectPartitionFsAsync();
            }
            catch (Exception ex)
            {
                Log.Bad("Diskler okunamadı: " + ex.Message);
            }
        }

        // SMART değerlerinden sağlık yüzdesi + renk + özet satırı
        private void ApplySmart(DiskInfo disk, SmartData s)
        {
            int health = 100;

            ulong realloc = s.Reallocated ?? 0;
            ulong pending = s.Pending ?? 0;
            ulong uncorr = s.Uncorrectable ?? 0;
            ulong crc = s.Crc ?? 0;

            health -= (int)Math.Min(40, realloc * 2);
            health -= (int)Math.Min(40, pending * 5);
            health -= (int)Math.Min(40, uncorr * 5);
            health -= (int)Math.Min(10, crc / 10);
            if (s.PredictFailure) health = Math.Min(health, 25);

            if (health < 1) health = 1;
            if (health > 100) health = 100;

            disk.HealthPercent = health;
            disk.HealthBarWidth = 90.0 * health / 100.0;

            disk.IsRisky = (realloc + pending + uncorr) > 0 || s.PredictFailure;
            if (disk.IsRisky)
                disk.RiskDetail = "Yeniden atanmış: " + realloc + " · Bekleyen: " + pending +
                                  " · Düzeltilemeyen: " + uncorr + (s.PredictFailure ? " · SMART arıza uyarısı" : "");

            if (health >= 85) { disk.Health = "Sağlıklı"; disk.HealthColor = Hex("#22c55e"); }
            else if (health >= 60) { disk.Health = "Zayıflıyor"; disk.HealthColor = Hex("#eab308"); }
            else { disk.Health = "Riskli — imaj al"; disk.HealthColor = Hex("#ef4444"); }

            disk.SmartLine =
                "Realloc " + N(s.Reallocated) +
                " · Pending " + N(s.Pending) +
                " · Uncorrectable " + N(s.Uncorrectable) +
                " · CRC " + N(s.Crc) +
                " · " + (s.PowerOnHours.HasValue ? s.PowerOnHours.Value.ToString("N0", Tr) + " saat" : "— saat");
        }

        // SMART yoksa Windows disk durumundan basit sağlık
        private static void SetBasicHealth(DiskInfo disk, string status)
        {
            if (status.Equals("OK", StringComparison.OrdinalIgnoreCase))
            { disk.Health = "Sağlıklı"; disk.HealthColor = Hex("#22c55e"); }
            else if (status.Contains("Pred", StringComparison.OrdinalIgnoreCase)
                  || status.Contains("Fail", StringComparison.OrdinalIgnoreCase)
                  || status.Contains("Error", StringComparison.OrdinalIgnoreCase))
            { disk.Health = "Riskli — imaj al"; disk.HealthColor = Hex("#ef4444"); }
            else if (status.Contains("Degraded", StringComparison.OrdinalIgnoreCase))
            { disk.Health = "Zayıflıyor"; disk.HealthColor = Hex("#eab308"); }
            else
            { disk.Health = string.IsNullOrEmpty(status) ? "Bilinmiyor" : status; disk.HealthColor = Hex("#8aa0b2"); }
        }

        private static string N(ulong? v) => v.HasValue ? v.Value.ToString() : "—";

        private static string FormatSize(ulong bytes)
        {
            if (bytes == 0) return "—";
            double tb = bytes / 1_000_000_000_000.0;
            if (tb >= 1.0) return tb.ToString("0.00") + " TB";
            double gb = bytes / 1_000_000_000.0;
            if (gb >= 1.0) return gb.ToString("0.##") + " GB";
            double mb = bytes / 1_000_000.0;
            if (mb >= 1.0) return mb.ToString("0.#") + " MB";
            double kb = bytes / 1_000.0;
            return kb.ToString("0") + " KB";
        }

        private static Brush Hex(string hex) =>
            new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
