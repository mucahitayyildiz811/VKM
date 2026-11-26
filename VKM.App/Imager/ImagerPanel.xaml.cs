using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VKM.App
{
    public partial class ImagerPanel : UserControl
    {
        private ObservableCollection<DiskInfo>? _disks;
        private Action<Func<Stream>, long, string, int[]>? _openRecovery;
        private CaseInfo? _case;
        private DiskInfo? _src;
        private ImageStore? _store;
        private ImageJob? _job;
        private WriteableBitmap? _bmp;
        private int _mapW = 220, _mapH = 150;
        private int[]? _px;
        private System.Windows.Threading.DispatcherTimer? _timer;
        private List<Button> _heads = new();

        static int C(byte r, byte g, byte b) => unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
        static readonly int PEND = C(0xd4, 0xdb, 0xe2), OK = C(0x2e, 0x9e, 0x52), BAD = C(0xd6, 0x45, 0x41),
                            WARN = C(0xe8, 0xa0, 0x08), SKIP = C(0xb9, 0xc2, 0xcc), CUR = C(0xd2, 0x3a, 0xd2);

        public ImagerPanel()
        {
            InitializeComponent();
            for (int i = 1; i <= 16; i++) HeadCountBox.Items.Add(i);
            HeadCountBox.SelectedItem = 2;
            BuildLegend();
        }

        public void Init(ObservableCollection<DiskInfo> disks, Action<Func<Stream>, long, string, int[]> openRecovery)
        {
            _disks = disks; _openRecovery = openRecovery;
        }

        private void ShowDevice(DiskInfo c)
        {
            DevBox.Children.Clear();
            void Add(string k, string v)
            {
                var sp = new StackPanel { Margin = new Thickness(0, 0, 18, 0) };
                sp.Children.Add(new TextBlock { Text = k, FontSize = 10.5, Foreground = Theme.Brush("Dim") });
                sp.Children.Add(new TextBlock { Text = v, FontSize = 12, FontFamily = new FontFamily("Consolas"), Foreground = Theme.Brush("Text") });
                DevBox.Children.Add(sp);
            }
            Add("Model", c.Model);
            if (c.Serial.Length > 0) Add("Seri", c.Serial);
            Add("Kapasite", c.SizeText + " · " + ((long)c.SizeBytes / Math.Max(1, c.BytesPerSector)).ToString("N0") + " sektör");
            if (c.HealthPercent >= 0) Add("Sağlık", "%" + c.HealthPercent);
            string temp = ExtractTemp(c.SmartLine);
            TempLbl.Text = temp.Length > 0 ? "🌡 " + temp : "";
        }

        static string ExtractTemp(string smart)
        {
            if (string.IsNullOrEmpty(smart)) return "";
            var m = System.Text.RegularExpressions.Regex.Match(smart, @"(\d{1,2})\s*°?\s*C", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value + " °C" : "";
        }

        static string Safe(string s) => string.Join("_", (s ?? "").Split(Path.GetInvalidFileNameChars()));

        private Func<Stream> SrcOpen() { var d = _src!; int sec = d.BytesPerSector <= 0 ? 512 : d.BytesPerSector; string id = d.DeviceId; long len = (long)d.SizeBytes; return () => PhysicalDisk.OpenReadOnly(id, len, sec); }

        // ---------------- yeni iş: root HEMEN gelir, arkada tarama YOK ----------------
        private void NewCase_Click(object sender, RoutedEventArgs e)
        {
            if (_disks == null || _disks.Count == 0) { MessageBox.Show("Disk bulunamadı. Ana ekranda 'Yenile' deyip tekrar dene."); return; }
            var w = new NewCaseWindow(_disks) { Owner = Window.GetWindow(this) };
            if (w.ShowDialog() != true || w.Source == null) return;

            // önceki iş varsa kapat
            _timer?.Stop(); _timer = null; _job = null;
            _store?.Dispose();

            _src = w.Source;
            string folder = Path.Combine(w.Folder, "CASE_" + Safe(w.Number + "_" + w.PersonName));
            _case = new CaseInfo { Number = w.Number, Name = w.PersonName, Folder = folder };
            _case.ImagePath = Path.Combine(folder, "CASE_" + Safe(w.Number) + ".img");
            CaseNo.Text = "İş #" + w.Number;
            CaseName.Text = w.PersonName.Length > 0 ? w.PersonName : "(isim yok)";
            ShowDevice(_src);

            int heads = HeadCountBox.SelectedItem is int h ? h : 2;
            int sec = _src.BytesPerSector <= 0 ? 512 : _src.BytesPerSector;
            long len = (long)_src.SizeBytes;
            try { _store = new ImageStore(_case.ImagePath, len, sec, _mapW * _mapH, heads); }
            catch (Exception ex) { MessageBox.Show("İmaj dosyası oluşturulamadı: " + ex.Message); return; }

            CaseFile.Save(folder, _case, _src, _store, _mapW, _mapH);
            Log.Ok("Yeni iş #" + w.Number + " — " + _src.Model);
            OpenCase(() => new ImagingStream(SrcOpen(), _store!), new[] { DriveNo(_src.DeviceId) });
        }

        // iş hazır: PC-3000/UFS tarzı tek case penceresinde aç (ağaç+dosya+Log/Harita/Durum, üstte ▶ imaj)
        private void OpenCase(Func<Stream> readOpen, int[] drives)
        {
            Func<Stream>? raw = _src != null ? SrcOpen() : null;
            var panel = new CasePanel(_case!, _src, _store!, raw, readOpen, drives, _mapW, _mapH);
            (Window.GetWindow(this) as MainWindow)?.AddCaseTab(panel, panel.TabTitle, _src);
            StartBtn.IsEnabled = false; OpenCaseBtn.IsEnabled = true;
        }

        // iş hazır: harita/kafalar kurulur, root açılır (hem Yeni hem Eski İş bunu kullanır)
        private void Activate(string title, Func<Stream> readOpen, int[] drives)
        {
            _px = new int[_mapW * _mapH];
            for (int i = 0; i < _px.Length; i++) _px[i] = PEND;
            _bmp = new WriteableBitmap(_mapW, _mapH, 96, 96, PixelFormats.Bgra32, null);
            MapImage.Source = _bmp;
            BuildHeads(_store!.Heads);
            StartBtn.IsEnabled = _src != null; RetryBtn.IsEnabled = false; OpenCaseBtn.IsEnabled = true;

            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();

            _openRecovery?.Invoke(readOpen, _store.Total, title, drives);
        }

        // ---------------- eski işi aç ----------------
        private void OpenOld_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Eski işi aç — CASE.vkm seç", Filter = "VKM İş|CASE.vkm;*.vkm|Tüm dosyalar|*.*" };
            if (dlg.ShowDialog() != true) return;
            var f = CaseFile.Load(dlg.FileName);
            if (f == null) { MessageBox.Show("İş dosyası okunamadı."); return; }

            _timer?.Stop(); _timer = null; _job = null; _store?.Dispose();

            string img = f.ImagePath;
            if (!File.Exists(img))
            {
                // imaj taşınmışsa aynı klasörde ara
                string alt = Path.Combine(Path.GetDirectoryName(dlg.FileName)!, Path.GetFileName(img));
                if (File.Exists(alt)) img = alt;
                else { MessageBox.Show("İmaj dosyası bulunamadı:\n" + img); return; }
            }
            _mapW = f.MapW > 0 ? f.MapW : _mapW; _mapH = f.MapH > 0 ? f.MapH : _mapH;
            try { _store = new ImageStore(img, f.Total, f.Sector, f.MapW * f.MapH, f.Heads); }
            catch (Exception ex) { MessageBox.Show("İmaj açılamadı: " + ex.Message); return; }
            _store.LoadStatus(f.Map);
            _case = new CaseInfo { Number = f.Number, Name = f.Name, Folder = Path.GetDirectoryName(dlg.FileName)!, ImagePath = img };
            CaseNo.Text = "İş #" + f.Number; CaseName.Text = f.Name.Length > 0 ? f.Name : "(isim yok)";
            Log.Ok("Eski iş açıldı #" + f.Number + " — " + f.Model);

            // disk hâlâ takılı mı? (seri no ile eşle) → takılıysa eksik bölgeler diskten de okunabilir
            _src = null;
            if (_disks != null && f.Serial.Length > 0)
                foreach (var d in _disks) if (d.Serial.Length > 0 && (d.Serial == f.Serial)) { _src = d; break; }

            if (_src != null) { ShowDevice(_src); OpenCase(() => new ImagingStream(SrcOpen(), _store!), new[] { DriveNo(_src.DeviceId) }); }
            else
            {
                DevBox.Children.Clear();
                DevBox.Children.Add(new TextBlock { Text = f.Model + " — disk takılı değil, imajdan okunuyor", Foreground = Theme.Brush("Dim"), VerticalAlignment = VerticalAlignment.Center });
                string path = img;
                OpenCase(() => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), Array.Empty<int>());
            }
        }

        private void SaveCaseMap()
        {
            if (_store == null || _case == null || _src == null) return;
            try { CaseFile.Save(_case.Folder, _case, _src, _store, _mapW, _mapH); } catch { }
        }

        static int DriveNo(string id)
        {
            var m = System.Text.RegularExpressions.Regex.Match(id ?? "", @"PHYSICALDRIVE(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        // ---------------- elle tam imaj (Başlat) ----------------
        private void Start_Click(object sender, RoutedEventArgs e)
        {
            if (_store == null || _src == null) { MessageBox.Show("Önce '＋ Yeni İş' ile iş aç."); return; }
            if (_job != null) return;
            _job = new ImageJob(SrcOpen(), _store, OnProgress) { SpeedDelayMs = (int)SpeedSlider.Value, Reverse = RevChk.IsChecked == true };
            _job.OnError = msg => Dispatcher.BeginInvoke(() => { Log.Bad("İmaj başlatılamadı: " + msg); MessageBox.Show("İmaj başlatılamadı: " + msg + "\n\nProgramı yönetici olarak açtığından emin ol.", "İmaj"); });
            StartBtn.IsEnabled = false; PauseBtn.IsEnabled = true; StopBtn.IsEnabled = true; NewCaseBtn.IsEnabled = false;
            Log.Info("Tam imaj başladı — " + _src.Model);
            var job = _job;
            Task.Run(() => job.Run());
        }

        private void BuildHeads(int n)
        {
            HeadsPanel.Children.Clear();
            _heads = new();
            string[] col = { "#e05b5b", "#e0a008", "#2e9e52", "#2a8fd0", "#8b5cf6", "#0f8f86", "#d23ad2", "#1d4ed8", "#e07b2a", "#4ca64c", "#9c27b0", "#00838f", "#5d4037", "#c2185b", "#455a64", "#7cb342" };
            for (int i = 0; i < n; i++)
            {
                int hi = i;
                var b = new Button
                {
                    Height = 36, Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand,
                    Background = Theme.Brush("Card"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1),
                    Content = new StackPanel { Children = { new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(col[i % col.Length])!, HorizontalAlignment = HorizontalAlignment.Left }, new TextBlock { Text = "H" + i, FontWeight = FontWeights.Bold, Foreground = Theme.Brush("Text"), HorizontalAlignment = HorizontalAlignment.Center } } }
                };
                b.Click += (_, _) => { if (_store != null) { _store.HeadOn[hi] = !_store.HeadOn[hi]; b.Opacity = _store.HeadOn[hi] ? 1 : 0.45; } };
                _heads.Add(b); HeadsPanel.Children.Add(b);
            }
        }

        // harita + durum çubuğu (talep üzerine okuma + tam imaj ortak)
        private int _tickN;
        private void Tick()
        {
            if (_store == null || _bmp == null || _px == null) return;
            if (++_tickN % 40 == 0) SaveCaseMap();   // ~8 sn'de bir haritayı kaydet
            var st = _store.Status; long cur = _store.CurBlock;
            for (int i = 0; i < _px.Length && i < st.Length; i++)
                _px[i] = st[i] switch { 1 => OK, 2 => BAD, 3 => WARN, 4 => SKIP, _ => PEND };
            if (cur >= 0 && cur < _px.Length) _px[(int)cur] = CUR;
            _bmp.WritePixels(new Int32Rect(0, 0, _mapW, _mapH), _px, _mapW * 4, 0);

            var (ok, bad, touched) = _store.Counts();
            OkLbl.Text = ok.ToString("N0"); BadLbl.Text = bad.ToString("N0");
            double pct = touched / (double)_store.Blocks * 100;
            PctLbl.Text = pct.ToString("0.0") + "%"; ProgFill.Width = 260 * pct / 100;
            if (cur >= 0) { LbaLbl.Text = _store.LbaOf(cur).ToString("N0"); HeadLbl.Text = "H" + _store.HeadOf(cur); }
        }

        private void OnProgress(ImageProgress p)
        {
            Dispatcher.BeginInvoke(() =>
            {
                MbpsLbl.Text = p.Mbps > 0 ? p.Mbps.ToString("0") + " MB/s · kalan " + TimeSpan.FromSeconds(p.EtaSec).ToString(@"hh\:mm\:ss") : "";
                if (p.Finished)
                {
                    _job = null;
                    StartBtn.IsEnabled = true; PauseBtn.IsEnabled = false; StopBtn.IsEnabled = false; RetryBtn.IsEnabled = p.Bad > 0; NewCaseBtn.IsEnabled = true;
                    Tick(); SaveCaseMap();
                    if (StopRequested) Log.Warn("İmaj durduruldu.");
                    else Log.Ok("İmaj bitti — okunan " + p.Ok.ToString("N0") + ", arızalı " + p.Bad.ToString("N0"));
                    if (!StopRequested)
                        MessageBox.Show($"Tam imaj bitti.\nOkunan blok: {p.Ok:N0} · Arızalı: {p.Bad:N0}" + (p.Bad > 0 ? "\n\n'Arızalıları yeniden dene' ile tekrar deneyebilirsin." : ""), "İmaj tamam");
                    StopRequested = false;
                }
            });
        }
        private bool StopRequested;

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (_job == null) return;
            _job.Paused = !_job.Paused;
            PauseBtn.Content = _job.Paused ? "▶ Sürdür" : "⏸ Duraklat";
        }
        private void Stop_Click(object sender, RoutedEventArgs e) { if (_job != null) { StopRequested = true; _job.Stopping = true; } }
        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            if (_store == null || _src == null || _job != null) return;
            _job = new ImageJob(SrcOpen(), _store, OnProgress);
            RetryBtn.IsEnabled = false; StopBtn.IsEnabled = true; StartBtn.IsEnabled = false; NewCaseBtn.IsEnabled = false; StopRequested = false;
            var job = _job;
            Task.Run(() => job.RetryBad());
        }
        private void Speed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_job != null) _job.SpeedDelayMs = (int)e.NewValue;
            SpeedTxt.Text = e.NewValue < 1 ? "hızlı" : e.NewValue < 60 ? "orta" : "yavaş (hassas)";
        }

        // ---------------- harita etkileşim ----------------
        private long BlockAt(Point p)
        {
            if (MapImage.ActualWidth < 1) return -1;
            int px = (int)(p.X / MapImage.ActualWidth * _mapW);
            int py = (int)(p.Y / MapImage.ActualHeight * _mapH);
            px = Math.Clamp(px, 0, _mapW - 1); py = Math.Clamp(py, 0, _mapH - 1);
            return (long)py * _mapW + px;
        }
        private void Map_Move(object sender, MouseEventArgs e)
        {
            if (_store == null) return;
            long b = BlockAt(e.GetPosition(MapImage));
            if (b < 0) return;
            MapImage.ToolTip = $"LBA {_store.LbaOf(b):N0} · Kafa H{_store.HeadOf(b)}";
        }
        private void Map_Leave(object sender, MouseEventArgs e) { }
        private void Map_Click(object sender, MouseButtonEventArgs e)
        {
            if (_store == null || _src == null) return;
            long b = BlockAt(e.GetPosition(MapImage));
            if (b < 0) return;
            long off = b * _store.BlockBytes;
            long len = Math.Min(16L << 20, _store.Total - off);
            var store = _store; var srcOpen = SrcOpen();
            // tıklayınca o bölge talep üzerine okunur → hem hex hem harita dolar
            Func<Stream> open = () => new SubStream(new ImagingStream(srcOpen, store), off, len);
            var hv = new HexViewWindow(open, len, $"Sektör · LBA {_store.LbaOf(b):N0} (H{_store.HeadOf(b)})", null) { Owner = Window.GetWindow(this) };
            hv.Show();
        }

        private void BuildLegend()
        {
            (string, int)[] L = { ("tam okundu", OK), ("bekliyor", PEND), ("kısmi / talep üzerine", WARN), ("okunamadı", BAD), ("şu an", CUR), ("kapalı kafa", SKIP) };
            foreach (var (t, c) in L)
            {
                var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 16, 0) };
                var col = Color.FromRgb((byte)((c >> 16) & 0xFF), (byte)((c >> 8) & 0xFF), (byte)(c & 0xFF));
                sp.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(col), Margin = new Thickness(0, 0, 5, 0) });
                sp.Children.Add(new TextBlock { Text = t, FontSize = 11, Foreground = Theme.Brush("Dim") });
                Legend.Children.Add(sp);
            }
        }
    }
}
