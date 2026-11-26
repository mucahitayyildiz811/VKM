using System;
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
    // PC-3000 / UFS tarzı tek "case" penceresi:
    // üstte ▶ (0→son sektör imaj, arka planda), ortada ağaç+dosyalar, altta Log | Harita | Durum.
    public partial class CasePanel : UserControl
    {
        static int C(byte r, byte g, byte b) => unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
        static readonly int PEND = C(0xd4, 0xdb, 0xe2), OK = C(0x2e, 0x9e, 0x52), BAD = C(0xd6, 0x45, 0x41),
                            WARN = C(0xe8, 0xa0, 0x08), SKIP = C(0xb9, 0xc2, 0xcc), CUR = C(0xd2, 0x3a, 0xd2);

        private readonly CaseInfo _case;
        private readonly DiskInfo? _src;
        private readonly ImageStore _store;
        private readonly Func<Stream>? _rawOpen;
        private readonly int _mapW, _mapH;
        static readonly int CBG = C(0x0b, 0x11, 0x18);   // ızgara arka planı (koyu)
        const int CELL = 11;                              // sektör karesi: 10 dolu + 1 boşluk
        private WriteableBitmap? _bmp;
        private int[]? _buf;
        private int _bw, _bh;
        private readonly List<Button> _heads = new();
        private ImageJob? _job;
        private RecoveryWindow? _view;
        public string TabTitle = "İş";
        private bool _stopReq;
        private int _tickN;
        private System.Windows.Threading.DispatcherTimer? _timer;

        public CasePanel(CaseInfo c, DiskInfo? src, ImageStore store, Func<Stream>? rawOpen, Func<Stream> readOpen, int[] drives, int mapW, int mapH)
        {
            InitializeComponent();
            _case = c; _src = src; _store = store; _rawOpen = rawOpen; _mapW = mapW; _mapH = mapH;

            string who = c.Name.Length > 0 ? c.Name : "(isim yok)";
            string model = src != null ? src.Model : (_case.ImagePath.Length > 0 ? "imajdan" : "");
            TabTitle = "İş #" + c.Number + "  ·  " + who;
            CaseLbl.Text = "İş #" + c.Number + "   ·   " + who + (model.Length > 0 ? "   ·   " + model : "");

            // ortadaki tarayıcı — UFS gibi sade (CCTV/carve/hex/log araçları gizli)
            _view = new RecoveryWindow(readOpen, store.Total, "İş #" + c.Number + " — " + model, drives);
            _view.SetCaseMode();
            _view.MapRequested += () => BottomTabs.SelectedIndex = 1;   // sağ tık → Harita sekmesi
            BrowseHost.Content = _view;

            // log
            LogList.ItemsSource = Log.Items;
            LogScroll.ScrollToEnd();

            // harita: kare sektör ızgarası (ekrana sığar, okudukça yeşil)
            MapImage.SizeChanged += (_, _) => PaintGrid();
            BuildHeads(_store.Heads);

            PlayBtn.IsEnabled = _rawOpen != null;
            if (_rawOpen == null) PlayBtn.ToolTip = "Disk takılı değil — sadece imajdan okunuyor";

            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += (_, _) => Tick();
            _timer.Start();
        }

        // sekme kapanınca çağrılır
        public void Cleanup()
        {
            try { _view?.Cleanup(); } catch { }
            _timer?.Stop(); _timer = null;
        }

        private void BuildHeads(int n)
        {
            HeadsPanel.Children.Clear(); _heads.Clear();
            string[] col = { "#e05b5b", "#e0a008", "#2e9e52", "#2a8fd0", "#8b5cf6", "#0f8f86", "#d23ad2", "#1d4ed8", "#e07b2a", "#4ca64c", "#9c27b0", "#00838f", "#5d4037", "#c2185b", "#455a64", "#7cb342" };
            for (int i = 0; i < n; i++)
            {
                int hi = i;
                var b = new Button
                {
                    Height = 34, Margin = new Thickness(0, 0, 0, 6), Cursor = Cursors.Hand,
                    Background = Theme.Brush("Card"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1),
                    Content = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Children = {
                        new System.Windows.Shapes.Ellipse { Width = 8, Height = 8, Margin = new Thickness(0,0,5,0), VerticalAlignment = VerticalAlignment.Center, Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(col[i % col.Length])! },
                        new TextBlock { Text = "H" + i, FontWeight = FontWeights.Bold, Foreground = Theme.Brush("Text"), VerticalAlignment = VerticalAlignment.Center } } }
                };
                b.Click += (_, _) => { _store.HeadOn[hi] = !_store.HeadOn[hi]; b.Opacity = _store.HeadOn[hi] ? 1 : 0.4; };
                _heads.Add(b); HeadsPanel.Children.Add(b);
            }
        }

        // kare sektör ızgarası — hücre sayısı ekrana göre; her hücre = toplam/hücre bayt (32GB≈küçük, 10TB≈büyük)
        private void PaintGrid()
        {
            int W = (int)MapImage.ActualWidth, H = (int)MapImage.ActualHeight;
            if (W < CELL || H < CELL) return;
            int cols = W / CELL, rows = H / CELL;
            if (cols < 1 || rows < 1) return;
            int cnt = cols * rows, bw = cols * CELL, bh = rows * CELL;
            if (_bmp == null || bw != _bw || bh != _bh)
            {
                _bw = bw; _bh = bh;
                _bmp = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
                _buf = new int[bw * bh];
                MapImage.Source = _bmp;
            }
            var buf = _buf!;
            for (int i = 0; i < buf.Length; i++) buf[i] = CBG;

            var st = _store.Status; int blocks = _store.Blocks; long cur = _store.CurBlock;
            for (int k = 0; k < cnt; k++)
            {
                long b0 = (long)k * blocks / cnt, b1 = (long)(k + 1) * blocks / cnt;
                if (b1 <= b0) b1 = b0 + 1;
                int read = 0, bad = 0, tot = 0; bool hasCur = false;
                for (long b = b0; b < b1 && b < blocks; b++)
                {
                    byte s = st[b]; tot++;
                    if (s == 2) bad++; else if (s == 1 || s == 3) read++;
                    if (cur == b) hasCur = true;
                }
                int clr = hasCur ? CUR : bad > 0 ? BAD : (tot > 0 && read == tot) ? OK : read > 0 ? WARN : PEND;
                int cx = (k % cols) * CELL, cy = (k / cols) * CELL;
                for (int y = 0; y < CELL - 1; y++) { int row = (cy + y) * bw + cx; for (int x = 0; x < CELL - 1; x++) buf[row + x] = clr; }
            }
            _bmp!.WritePixels(new Int32Rect(0, 0, bw, bh), buf, bw * 4, 0);
        }

        private void Tick()
        {
            if (++_tickN % 40 == 0) SaveMap();
            PaintGrid();
            long cur = _store.CurBlock;

            var (ok, bad, touched) = _store.Counts();
            OkLbl.Text = ok.ToString("N0"); BadLbl.Text = bad.ToString("N0");
            double pct = touched / (double)_store.Blocks * 100;
            PctLbl.Text = pct.ToString("0.0") + "%"; ProgFill.Width = 200 * pct / 100;
            if (cur >= 0) { LbaLbl.Text = _store.LbaOf(cur).ToString("N0"); HeadLbl.Text = "H" + _store.HeadOf(cur); }

            StatusTxt.Text =
                "Model        = " + (_src?.Model ?? "-") + "\n" +
                "Seri No      = " + (_src?.Serial ?? "-") + "\n" +
                "Aygıt        = " + (_src?.DeviceId ?? _case.ImagePath) + "\n" +
                "Toplam       = " + _store.Total.ToString("N0") + " bayt\n" +
                "Sektör       = " + _store.Sector + " B\n" +
                "Blok sayısı  = " + _store.Blocks.ToString("N0") + "\n" +
                "Blok boyutu  = " + _store.BlockBytes.ToString("N0") + " B\n" +
                "Okunan blok  = " + ok.ToString("N0") + "\n" +
                "Arızalı blok = " + bad.ToString("N0") + "\n" +
                "İşlenen      = " + pct.ToString("0.0") + "%\n" +
                "Durum        = " + (_job != null ? "imaj alınıyor…" : "beklemede");
        }

        private void SaveMap()
        {
            if (_src == null || _case.Folder.Length == 0) return;
            try { CaseFile.Save(_case.Folder, _case, _src, _store, _mapW, _mapH); } catch { }
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_rawOpen == null) { MessageBox.Show("Disk takılı değil — imaj alınamaz, sadece imajdan okunuyor."); return; }
            if (_job != null) return;
            _job = new ImageJob(_rawOpen!, _store, OnProgress) { SpeedDelayMs = (int)SpeedSlider.Value };
            _job.OnError = msg => Dispatcher.BeginInvoke(() => { Log.Bad("İmaj başlatılamadı: " + msg); MessageBox.Show("İmaj başlatılamadı: " + msg + "\n\nProgramı yönetici olarak açtığından emin ol.", "İmaj"); });
            PlayBtn.IsEnabled = false; PauseBtn.IsEnabled = true; StopBtn.IsEnabled = true; RetryBtn.IsEnabled = false;
            Log.Info("Tam imaj başladı (0→son) — " + (_src?.Model ?? ""));
            var job = _job; Task.Run(() => job.Run());
        }

        private void Pause_Click(object sender, RoutedEventArgs e)
        {
            if (_job == null) return;
            _job.Paused = !_job.Paused;
            PauseBtn.Content = _job.Paused ? "▶ Sürdür" : "⏸ Duraklat";
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            if (_job != null) { _stopReq = true; _job.Stopping = true; }
        }

        private void Retry_Click(object sender, RoutedEventArgs e)
        {
            if (_rawOpen == null || _job != null) return;
            _job = new ImageJob(_rawOpen!, _store, OnProgress);
            PlayBtn.IsEnabled = false; RetryBtn.IsEnabled = false; StopBtn.IsEnabled = true; _stopReq = false;
            Log.Info("Arızalı bloklar yeniden deneniyor…");
            var job = _job; Task.Run(() => job.RetryBad());
        }

        private void Speed_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_job != null) _job.SpeedDelayMs = (int)e.NewValue;
            if (SpeedTxt != null) SpeedTxt.Text = e.NewValue < 1 ? "hızlı" : e.NewValue < 60 ? "orta" : "yavaş (hassas)";
        }

        private void OnProgress(ImageProgress p)
        {
            Dispatcher.BeginInvoke(() =>
            {
                MbpsLbl.Text = p.Mbps > 0 ? p.Mbps.ToString("0") + " MB/s · kalan " + TimeSpan.FromSeconds(p.EtaSec).ToString(@"hh\:mm\:ss") : "";
                if (p.Finished)
                {
                    _job = null;
                    PlayBtn.IsEnabled = _rawOpen != null; PauseBtn.IsEnabled = false; StopBtn.IsEnabled = false; RetryBtn.IsEnabled = p.Bad > 0;
                    PauseBtn.Content = "⏸ Duraklat";
                    Tick(); SaveMap();
                    if (_stopReq) Log.Warn("İmaj durduruldu.");
                    else Log.Ok("İmaj bitti — okunan " + p.Ok.ToString("N0") + ", arızalı " + p.Bad.ToString("N0"));
                    _stopReq = false;
                }
            });
        }
    }
}
