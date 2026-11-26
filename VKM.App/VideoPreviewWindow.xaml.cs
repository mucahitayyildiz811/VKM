using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VKM.App
{
    // Kurtarılan videoyu kaydetmeden oynatır: segmenti geçici .mp4'e muxlar, MediaElement ile çalar.
    public partial class VideoPreviewWindow : Window
    {
        private readonly Func<int, Stream> _openRaw;
        private readonly long _offset, _length;
        private readonly string _name;
        private string _temp = "";
        private CancellationTokenSource? _cts;

        public VideoPreviewWindow(Func<int, Stream> openRaw, long offset, long length, string name)
        {
            InitializeComponent();
            _openRaw = openRaw; _offset = offset; _length = length; _name = name;
            Title = "Video önizleme — " + name;
            NameText.Text = name;
            Loaded += async (_, __) => await Prepare();
        }

        private async Task Prepare()
        {
            _cts = new CancellationTokenSource();
            var tok = _cts.Token;
            Status.Text = "Video hazırlanıyor… (önizleme için ilk parça muxlanıyor)";

            _temp = Path.Combine(Path.GetTempPath(), "vkm_preview_" + Guid.NewGuid().ToString("N") + ".mp4");
            long cap = Math.Min(_length, 64L * 1024 * 1024);   // önizleme için ilk ~64 MB yeter

            try
            {
                bool ok = await Task.Run(() => Mp4Muxer.Mux(_openRaw, _offset, cap, _temp, tok), tok);
                if (tok.IsCancellationRequested) return;

                if (!ok)
                {
                    Status.Text = "MP4 oluşturulamadı (SPS/PPS bulunamadı). Kaydedip VLC'de deneyin.";
                    CleanTemp();
                    return;
                }

                Player.Source = new Uri(_temp);
                Player.Play();
                Status.Text = "Oynatılıyor (önizleme ilk parça). Tamamı için 'Kaydet' → .mp4";
            }
            catch (Exception ex) { Status.Text = "Önizleme hatası: " + ex.Message; }
        }

        private void Play_Click(object sender, RoutedEventArgs e) => Player.Play();
        private void Pause_Click(object sender, RoutedEventArgs e) => Player.Pause();
        private void Stop_Click(object sender, RoutedEventArgs e) => Player.Stop();

        private void Player_MediaOpened(object sender, RoutedEventArgs e) => Status.Text = "Oynatılıyor.";
        private void Player_MediaEnded(object sender, RoutedEventArgs e) { Player.Position = TimeSpan.Zero; Player.Play(); }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            Status.Text = "Oynatılamadı — bu Windows'ta H.264 kod çözücü (Media Foundation) yok olabilir. " +
                          "Dosyayı 'Kaydet' ile .mp4 çıkar, VLC'de aç.";
        }

        protected override void OnClosed(EventArgs e)
        {
            try { _cts?.Cancel(); } catch { }
            try { Player.Stop(); Player.Source = null; } catch { }
            CleanTemp();
            base.OnClosed(e);
        }

        private void CleanTemp()
        {
            try { if (!string.IsNullOrEmpty(_temp) && File.Exists(_temp)) File.Delete(_temp); } catch { }
            try { string h = Path.ChangeExtension(_temp, ".h264"); if (File.Exists(h)) File.Delete(h); } catch { }
        }
    }
}
