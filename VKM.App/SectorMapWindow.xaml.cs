using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VKM.App
{
    // Üstte hızlı disk haritası, altta sektör hex içeriği. Tüm disk boyunca gezinme.
    public partial class SectorMapWindow : Window
    {
        private const int W = 384;
        private const int H = 96;          // genel harita hücre ızgarası
        private const int SECTOR = 512;

        private readonly Func<Stream> _open;
        private readonly List<(long start, long count)>? _fileExtents;

        private Stream? _stream;
        private long _totalBytes;
        private int _clusterSize = 4096;
        private long _offset;              // şu anki bayt konumu (512 hizalı)
        private HashSet<int> _fileCells = new();
        private bool _sliderGuard;
        private int[]? _basePx;            // sabit harita (bir kez çizilir)
        private WriteableBitmap? _wb;

        public SectorMapWindow(Func<Stream> open, string title)
            : this(open, title, null) { }

        public SectorMapWindow(Func<Stream> open, string title, List<(long start, long count)>? fileExtents)
        {
            InitializeComponent();
            _open = open;
            _fileExtents = fileExtents;
            TitleText.Text = title;
            Loaded += (_, _) => Init();
        }

        private void Init()
        {
            try
            {
                _stream = _open();
                _totalBytes = _stream.Length;
                var geo = NtfsBitmapReader.ReadGeometry(_stream);
                if (geo.clusterSize > 0) _clusterSize = geo.clusterSize;
                if (_totalBytes <= 0 && geo.totalClusters > 0)
                    _totalBytes = geo.totalClusters * (long)_clusterSize;
                if (_totalBytes <= 0) { Status.Text = "Birim boyutu okunamadı."; return; }

                int cells = W * H;
                _fileCells = new HashSet<int>();
                long firstOffset = 0;
                if (_fileExtents != null && _fileExtents.Count > 0)
                {
                    foreach (var (start, count) in _fileExtents)
                    {
                        if (count <= 0) continue;
                        long o0 = start * _clusterSize;
                        long o1 = (start + count) * _clusterSize;
                        int a = (int)(o0 / (double)_totalBytes * cells);
                        int b = (int)((o1 - 1) / (double)_totalBytes * cells);
                        if (a < 0) a = 0;
                        if (b >= cells) b = cells - 1;
                        for (int c = a; c <= b; c++) _fileCells.Add(c);
                    }
                    firstOffset = _fileExtents[0].start * _clusterSize;
                    HlLegend.Visibility = Visibility.Visible;
                    HlLegendText.Visibility = Visibility.Visible;
                }

                // Sabit haritayı bir kez çiz (sonra sadece kırmızı imleç değişir)
                int backdrop = unchecked((int)0xFF243040);
                int file = unchecked((int)0xFFFBBF24);
                _basePx = new int[cells];
                for (int i = 0; i < cells; i++)
                    _basePx[i] = _fileCells.Contains(i) ? file : backdrop;
                _wb = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgra32, null);
                MapImage.Source = _wb;

                VBar.Maximum = Math.Max(0, (_totalBytes / SECTOR) - 1);

                SetOffset(firstOffset);
            }
            catch (Exception ex)
            {
                Status.Text = "Hata: " + ex.Message;
            }
        }

        private void SetOffset(long offset)
        {
            if (offset < 0) offset = 0;
            if (offset > _totalBytes - 16) offset = Math.Max(0, _totalBytes - 16);
            _offset = offset - (offset % 16);   // satır (16 bayt) hizalı

            _sliderGuard = true;
            VBar.Value = _offset / SECTOR;
            _sliderGuard = false;

            RenderOverview();
            LoadHex();
        }

        // Fare tekerleği: disk boyunca aşağı/yukarı
        private void Hex_Wheel(object sender, MouseWheelEventArgs e)
        {
            int rows = e.Delta > 0 ? -4 : 4;   // bir çentik = 4 satır (64 bayt)
            SetOffset(_offset + rows * 16);
            e.Handled = true;
        }

        // Sağdaki dikey çubuk
        private void VBar_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_sliderGuard) return;
            SetOffset((long)e.NewValue * SECTOR);
        }

        private void RenderOverview()
        {
            if (_basePx == null || _wb == null) return;
            int cells = W * H;
            int cur = (int)(_offset / (double)_totalBytes * cells);
            if (cur < 0) cur = 0; if (cur >= cells) cur = cells - 1;

            // Sabit haritanın kopyası + küçük KIRMIZI kare imleç
            int[] px = (int[])_basePx.Clone();
            int red = unchecked((int)0xFFEF4444);
            int col = cur % W;
            px[cur] = red;
            if (col + 1 < W) px[cur + 1] = red;
            if (cur + W < cells) px[cur + W] = red;
            if (col + 1 < W && cur + W + 1 < cells) px[cur + W + 1] = red;

            _wb.WritePixels(new Int32Rect(0, 0, W, H), px, W * 4, 0);
        }

        private void LoadHex()
        {
            if (_stream == null) return;
            try
            {
                byte[] buf = new byte[640];   // ~40 satır, paneli doldurur
                _stream.Position = _offset;
                int got = 0;
                while (got < buf.Length)
                {
                    int r = _stream.Read(buf, got, buf.Length - got);
                    if (r <= 0) break;
                    got += r;
                }
                HexText.Text = HexDump(buf, got, _offset);

                long sector = _offset / SECTOR;
                double pctOfDisk = 100.0 * _offset / _totalBytes;
                Status.Text = "Ofset 0x" + _offset.ToString("X") + "  ·  sektör " + sector.ToString("N0") +
                              "  ·  diskin %" + pctOfDisk.ToString("0.0") + "'i";
            }
            catch (Exception ex)
            {
                Status.Text = "Okuma hatası: " + ex.Message;
            }
        }

        private void Map_MouseDown(object sender, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(MapImage);
            if (MapImage.ActualWidth <= 0 || MapImage.ActualHeight <= 0) return;
            int cx = (int)(p.X / MapImage.ActualWidth * W);
            int cy = (int)(p.Y / MapImage.ActualHeight * H);
            if (cx < 0) cx = 0; if (cx >= W) cx = W - 1;
            if (cy < 0) cy = 0; if (cy >= H) cy = H - 1;
            int cell = cy * W + cx;
            long offset = (long)((double)cell / (W * H) * _totalBytes);
            SetOffset(offset);
        }

        private static string HexDump(byte[] data, int len, long baseOffset)
        {
            var sb = new StringBuilder();
            int rows = (len + 15) / 16;
            for (int row = 0; row < rows; row++)
            {
                int off = row * 16;
                sb.Append((baseOffset + off).ToString("X8")).Append("  ");
                var ascii = new StringBuilder();
                for (int i = 0; i < 16; i++)
                {
                    if (off + i < len)
                    {
                        byte b = data[off + i];
                        sb.Append(b.ToString("X2")).Append(' ');
                        ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    else sb.Append("   ");
                    if (i == 7) sb.Append(' ');
                }
                sb.Append(' ').Append(ascii);
                if (row < rows - 1) sb.Append('\n');
            }
            return sb.ToString();
        }

        protected override void OnClosed(EventArgs e)
        {
            _stream?.Dispose();
            base.OnClosed(e);
        }
    }
}
