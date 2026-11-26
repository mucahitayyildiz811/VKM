using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VKM.App
{
    public partial class SmartPanel : UserControl
    {
        private ObservableCollection<DiskInfo>? _disks;

        public SmartPanel()
        {
            InitializeComponent();
        }

        public void Init(ObservableCollection<DiskInfo> disks)
        {
            _disks = disks;
            DiskCombo.ItemsSource = disks;
            if (disks.Count > 0 && DiskCombo.SelectedIndex < 0) DiskCombo.SelectedIndex = 0;
            else Load();
        }

        private void Disk_Changed(object sender, SelectionChangedEventArgs e) { Load(); ResetSurf(); }
        private void Refresh_Click(object sender, RoutedEventArgs e) => Load();

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "SMART özniteliklerini (reallocated/pending gibi) gerçekten sıfırlamak ÜRETİCİ-ÖZEL komut ister " +
                "(WD/Seagate/Toshiba… her biri farklı). Bu, SATA üzerinden VSC / ROM-SA kanalıyla aile aile eklenecek — " +
                "bir sonraki adım. Şu an panel tüm SMART değerlerini okuyup gösteriyor.",
                "SMART sıfırla", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Load()
        {
            if (AttrList == null) return;
            AttrList.ItemsSource = null;
            if (DiskCombo.SelectedItem is not DiskInfo d) { SummaryText.Text = ""; return; }

            var map = SmartReader.ReadAll();
            map.TryGetValue(SmartReader.Norm(d.Pnp), out SmartData? sd);
            if (sd == null || !sd.HasData || sd.Attrs.Count == 0)
            {
                SummaryText.Text = "SMART okunamadı — yönetici olarak çalıştır, ya da bu disk/USB köprüsü SMART geçirmiyor.";
                return;
            }

            AttrList.ItemsSource = sd.Attrs;
            string poh = sd.PowerOnHours.HasValue ? sd.PowerOnHours.Value.ToString("N0") + " saat" : "-";
            string temp = sd.TempC.HasValue ? sd.TempC.Value + " °C" : "-";
            SummaryText.Text =
                "Durum: " + (sd.PredictFailure ? "⚠ ARIZA TAHMİNİ" : "✓ Normal") +
                "      Çalışma: " + poh + "      Sıcaklık: " + temp + "\n" +
                "Yeniden atanan (5): " + (sd.Reallocated?.ToString() ?? "-") +
                "      Bekleyen (197): " + (sd.Pending?.ToString() ?? "-") +
                "      Düzeltilemez (198): " + (sd.Uncorrectable?.ToString() ?? "-") +
                "      CRC (199): " + (sd.Crc?.ToString() ?? "-");
        }

        // ---------------- Yüzey taraması ----------------
        static int C(byte r, byte g, byte b) => unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
        static int ColInt(Color c) => unchecked((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
        const int CELL = 11;
        private WriteableBitmap? _bmp;
        private int[]? _buf;
        private int _bw, _bh;
        private SurfaceScan? _scan;
        private System.Windows.Threading.DispatcherTimer? _timer;

        private void ResetSurf()
        {
            _scan = null;
            if (ScanBtn != null) { ScanBtn.IsEnabled = true; ScanStopBtn.IsEnabled = false; }
            if (ScanStat != null) ScanStat.Text = "";
            _bmp = null; _bw = _bh = 0;
            if (SurfMap != null) SurfMap.Source = null;
        }

        private void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (DiskCombo.SelectedItem is not DiskInfo d) { MessageBox.Show("Önce bir disk seç."); return; }
            if (_scan != null) return;
            long total = (long)d.SizeBytes;
            int sector = d.BytesPerSector <= 0 ? 512 : d.BytesPerSector;
            string dev = d.DeviceId;
            _scan = new SurfaceScan(() => PhysicalDisk.OpenReadOnly(dev, total, sector), total, sector);

            ScanBtn.IsEnabled = false; ScanStopBtn.IsEnabled = true;
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => PaintSurf();
            _timer.Start();

            var sc = _scan;
            Task.Run(() => sc.Run(() => { }));
        }

        private void ScanStop_Click(object sender, RoutedEventArgs e)
        {
            if (_scan != null) _scan.Stopping = true;
        }

        private void PaintSurf()
        {
            if (_scan == null || SurfMap == null) return;
            int W = (int)SurfMap.ActualWidth, H = (int)SurfMap.ActualHeight;
            if (W < CELL || H < CELL) return;
            int cols = W / CELL, rows = H / CELL;
            if (cols < 1 || rows < 1) return;
            int cnt = cols * rows, bw = W, bh = H;
            if (_bmp == null || bw != _bw || bh != _bh)
            {
                _bw = bw; _bh = bh;
                _bmp = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
                _buf = new int[bw * bh];
                SurfMap.Source = _bmp;
            }
            int bg = ColInt(Theme.Col("Line")), cPend = ColInt(Theme.Col("Card")),
                cOk = ColInt(Theme.Col("Ok")), cWarn = ColInt(Theme.Col("Warn")), cBad = ColInt(Theme.Col("Bad"));
            var buf = _buf!;
            for (int i = 0; i < buf.Length; i++) buf[i] = bg;

            var st = _scan.Status; int blocks = _scan.Blocks; long cur = _scan.Cur;
            for (int k = 0; k < cnt; k++)
            {
                long b0 = (long)k * blocks / cnt, b1 = (long)(k + 1) * blocks / cnt;
                if (b1 <= b0) b1 = b0 + 1;
                int bad = 0, slow = 0, ok = 0, pend = 0;
                for (long b = b0; b < b1 && b < blocks; b++)
                {
                    byte s = st[b];
                    if (s == 3) bad++; else if (s == 2) slow++; else if (s == 1) ok++; else pend++;
                }
                int clr = bad > 0 ? cBad : slow > 0 ? cWarn : ok > 0 ? cOk : cPend;
                int cx = (k % cols) * CELL, cy = (k / cols) * CELL;
                for (int y = 0; y < CELL - 1; y++) { int row = (cy + y) * bw + cx; for (int x = 0; x < CELL - 1; x++) buf[row + x] = clr; }
            }
            _bmp!.WritePixels(new Int32Rect(0, 0, bw, bh), buf, bw * 4, 0);

            double pct = blocks > 0 ? _scan.Done / (double)blocks * 100 : 0;
            ScanStat.Text = "%" + pct.ToString("0.0") + "   iyi " + _scan.OkCount.ToString("N0") +
                            "   yavaş " + _scan.SlowCount.ToString("N0") + "   bad " + _scan.BadCount.ToString("N0");

            if (_scan.Cur < 0 && (_scan.Done >= blocks || _scan.Stopping))   // bitti ya da durduruldu
            {
                _timer?.Stop();
                ScanBtn.IsEnabled = true; ScanStopBtn.IsEnabled = false;
                _scan = null;
            }
        }
    }
}
