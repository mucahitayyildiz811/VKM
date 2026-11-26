using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VKM.App
{
    public partial class PreviewWindow : Window
    {
        private readonly string _name;
        private readonly long _fullSize;
        private readonly Func<CancellationToken, byte[]> _loader;
        private CancellationTokenSource? _cts;

        public PreviewWindow(string name, long fullSize, Func<CancellationToken, byte[]> loader)
        {
            InitializeComponent();
            _name = name;
            _fullSize = fullSize;
            _loader = loader;
            Title = "Önizleme — " + name;
            NameText.Text = name + (fullSize > 0 ? "   ·   " + FormatSize((ulong)fullSize) : "");
            Loaded += async (_, __) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            _cts = new CancellationTokenSource();
            CancellationToken tok = _cts.Token;
            SetBanner(null, "Yükleniyor…");
            StopButton.Visibility = Visibility.Visible;

            try
            {
                byte[] data = await Task.Run(() => _loader(tok), tok);
                if (tok.IsCancellationRequested) { SetBanner(null, "İptal edildi."); return; }
                Render(data);
            }
            catch (OperationCanceledException) { SetBanner(null, "İptal edildi."); }
            catch (Exception ex) { SetBanner(false, "Okuma hatası: " + ex.Message); }
            finally { StopButton.Visibility = Visibility.Collapsed; }
        }

        private void Stop_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void Render(byte[] data)
        {
            bool haveWhole = _fullSize > 0 && data.Length >= _fullSize;

            int hn = Math.Min(65536, data.Length);
            byte[] head = new byte[hn];
            Array.Copy(data, 0, head, 0, hn);
            byte[] tail;
            if (haveWhole && data.Length > 0)
            {
                int tn = Math.Min(65536, data.Length);
                tail = new byte[tn];
                Array.Copy(data, data.Length - tn, tail, 0, tn);
            }
            else tail = Array.Empty<byte>();

            (bool? sound, string msg) = SoundCheck.Check(_name, head, tail, haveWhole);

            // Her zaman Hex sekmesini doldur (dosya verisinin hex'i) — en çok 256 KB
            int hexCount = Math.Min(data.Length, 262144);
            HexBox.Text = HexDump(data, hexCount) +
                          (data.Length > hexCount ? "\n… (ilk " + FormatSize((ulong)hexCount) + " gösteriliyor)" : "");

            if (data.Length == 0)
            {
                ShowInfo("Dosya okunamadı (0 bayt). Hex boş.");
                SetBanner(false, "Dosya okunamadı.");
                return;
            }

            // Görselleştirme sekmesi — önce içeriğe göre resim dene (uzantı ne olursa olsun)
            if (TryShowImage(data))
            {
                sound = true;
                msg = "Görüntü açıldı — dosya sağlam.";
            }
            else if (IsImageExt(_name))
            {
                // Uzantı resim ama açılamadı → bozuk
                ShowInfo("Görüntü açılamadı — dosya bozuk veya eksik. Hex sekmesine bakabilirsiniz.");
                sound = false;
                msg = "Görüntü açılamadı — dosya bozuk veya eksik.";
            }
            else if (Ext(_name) == "pdf")
            {
                var bs = PdfPreview.FirstPage(data, out string err);
                if (bs != null)
                {
                    Img.Source = bs;
                    Img.Visibility = Visibility.Visible;
                    if (sound == null) sound = true;
                    msg = "PDF ilk sayfa gösteriliyor.";
                }
                else ShowInfo("PDF açılamadı: " + err + "\nHex sekmesine bakabilirsiniz.");
            }
            else if (Ext(_name) == "xlsx")
            {
                string? table = OfficePreview.Xlsx(data, out string err);
                if (table != null)
                {
                    TextView.Text = table;
                    TextView.Visibility = Visibility.Visible;
                    if (sound == null) sound = true;
                }
                else ShowInfo("Excel okunamadı: " + err + "\nHex sekmesine bakabilirsiniz.");
            }
            else if (Ext(_name) == "docx")
            {
                string? text = OfficePreview.Docx(data, out string err);
                if (text != null)
                {
                    TextView.Text = text;
                    TextView.Visibility = Visibility.Visible;
                    TextView.TextWrapping = TextWrapping.Wrap;
                    if (sound == null) sound = true;
                }
                else ShowInfo("Word okunamadı: " + err + "\nHex sekmesine bakabilirsiniz.");
            }
            else if (IsTextExt(_name))
            {
                try
                {
                    string text = Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 1024 * 1024));
                    TextView.Text = text;
                    TextView.Visibility = Visibility.Visible;
                }
                catch { ShowInfo("Metin çözümlenemedi — Hex sekmesine bakın."); }
            }
            else
            {
                ShowInfo("Bu tür için görsel önizleme yok.\nDosyanın verisini Hex sekmesinden görebilir ve işaretleyebilirsiniz.");
            }

            SetBanner(sound, msg);
        }

        // İçeriğe göre resim açmayı dener (JPEG/PNG/GIF/BMP/TIFF imzalarını WPF tanır)
        private bool TryShowImage(byte[] data)
        {
            if (!LooksLikeImage(data)) return false;
            try
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(data))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                Img.Source = bmp;
                Img.Visibility = Visibility.Visible;
                return true;
            }
            catch { return false; }
        }

        private static bool LooksLikeImage(byte[] d)
        {
            if (d.Length < 4) return false;
            // JPEG
            if (d[0] == 0xFF && d[1] == 0xD8) return true;
            // PNG
            if (d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47) return true;
            // GIF
            if (d[0] == 0x47 && d[1] == 0x49 && d[2] == 0x46) return true;
            // BMP
            if (d[0] == 0x42 && d[1] == 0x4D) return true;
            // TIFF
            if ((d[0] == 0x49 && d[1] == 0x49 && d[2] == 0x2A) || (d[0] == 0x4D && d[1] == 0x4D && d[2] == 0x2A)) return true;
            return false;
        }

        private void ShowInfo(string t)
        {
            InfoText.Text = t;
            InfoText.Visibility = Visibility.Visible;
        }

        private void SetBanner(bool? sound, string msg)
        {
            BannerText.Text = (sound == true ? "✓  " : sound == false ? "✕  " : "•  ") + msg;
            if (sound == true)
            {
                Banner.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x33, 0x1f));
                BannerText.Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80));
            }
            else if (sound == false)
            {
                Banner.Background = new SolidColorBrush(Color.FromRgb(0x3a, 0x15, 0x19));
                BannerText.Foreground = new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71));
            }
            else
            {
                Banner.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x23, 0x30));
                BannerText.Foreground = new SolidColorBrush(Color.FromRgb(0x8a, 0xa0, 0xb2));
            }
        }

        private static bool IsImageExt(string name)
        {
            string e = Ext(name);
            return e is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "tif" or "tiff";
        }

        private static bool IsTextExt(string name)
        {
            string e = Ext(name);
            return e is "txt" or "csv" or "log" or "xml" or "json" or "html" or "htm" or "md"
                or "ini" or "cfg" or "c" or "cpp" or "h" or "cs" or "js" or "py" or "sql"
                or "bat" or "cmd" or "ps1" or "srt" or "vtt" or "yaml" or "yml" or "tsv";
        }

        private static string Ext(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot >= 0 ? name.Substring(dot + 1).ToLowerInvariant() : "";
        }

        private static string HexDump(byte[] data, int count)
        {
            var sb = new StringBuilder(count * 4);
            for (int i = 0; i < count; i += 16)
            {
                sb.Append(i.ToString("X8")).Append("  ");
                var ascii = new StringBuilder();
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < count)
                    {
                        byte b = data[i + j];
                        sb.Append(b.ToString("X2")).Append(' ');
                        ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    else sb.Append("   ");
                }
                sb.Append(' ').Append(ascii).Append('\n');
            }
            return sb.ToString();
        }

        private static string FormatSize(ulong bytes)
        {
            if (bytes == 0) return "0";
            double tb = bytes / 1_000_000_000_000.0; if (tb >= 1.0) return tb.ToString("0.00") + " TB";
            double gb = bytes / 1_000_000_000.0; if (gb >= 1.0) return gb.ToString("0.0") + " GB";
            double mb = bytes / 1_000_000.0; if (mb >= 1.0) return mb.ToString("0.0") + " MB";
            double kb = bytes / 1000.0; return kb.ToString("0") + " KB";
        }
    }
}
