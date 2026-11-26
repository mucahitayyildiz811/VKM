using System;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Controls.Primitives;
using LibVLCSharp.Shared;

namespace VKM.App
{
    // Gömülü VLC motorlu oynatıcı — her codec (H.264/H.265/MJPEG), sistem codec'i gerekmez.
    // Zaman çizgisi (sar), oynat/durdur. Çıkarılan .mp4/.h265/.h264 dosyalarını oynatır.
    public partial class CctvPlayerWindow : Window
    {
        private LibVLC? _libvlc;
        private MediaPlayer? _player;
        private DispatcherTimer? _timer;
        private bool _dragging;

        public CctvPlayerWindow(string filePath)
        {
            InitializeComponent();
            Loaded += (_, __) => Start(filePath);
            Closed += (_, __) => Cleanup();
        }

        private void Start(string filePath)
        {
            try
            {
                Core.Initialize();                       // VLC motorunu bul/başlat
                _libvlc = new LibVLC();
                _player = new MediaPlayer(_libvlc);
                VideoView.MediaPlayer = _player;

                // Dosya yolunu doğrudan ver (Uri Türkçe/boşluklu yolda sorun çıkarıyordu).
                // Ham .h265/.h264 için demux ipucu ver (konteyner yok).
                string ext = System.IO.Path.GetExtension(filePath).ToLowerInvariant();
                Media media;
                if (ext == ".h265" || ext == ".hevc")
                    media = new Media(_libvlc, filePath, FromType.FromPath, ":demux=hevc");
                else if (ext == ".h264" || ext == ".264")
                    media = new Media(_libvlc, filePath, FromType.FromPath, ":demux=h264");
                else
                    media = new Media(_libvlc, filePath, FromType.FromPath);
                _player.Play(media);
                media.Dispose();

                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
                _timer.Tick += Tick;
                _timer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Oynatıcı başlatılamadı: " + ex.Message +
                    "\n\nVLC motoru paketi eksik olabilir — Visual Studio'da projeyi derlerken NuGet paketleri (LibVLCSharp, VideoLAN.LibVLC.Windows) geri yüklenmeli.",
                    "CCTV Oynatıcı", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Tick(object? s, EventArgs e)
        {
            if (_player == null || _dragging) return;
            long len = _player.Length;   // ms
            long t = _player.Time;       // ms
            if (len > 0) Seek.Value = (double)t / len * 1000.0;
            TimeText.Text = Fmt(t) + " / " + Fmt(len);
            PlayBtn.Content = _player.IsPlaying ? "⏸" : "▶";
        }

        private static string Fmt(long ms)
        {
            if (ms < 0) ms = 0;
            var ts = TimeSpan.FromMilliseconds(ms);
            return (ts.Hours > 0 ? ts.Hours + ":" : "") + ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00");
        }

        private void Play_Click(object sender, RoutedEventArgs e)
        {
            if (_player == null) return;
            if (_player.IsPlaying) _player.Pause(); else _player.Play();
        }

        private void Seek_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

        private void Seek_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            _dragging = false;
            if (_player != null && _player.Length > 0)
                _player.Time = (long)(Seek.Value / 1000.0 * _player.Length);
        }

        private void Cleanup()
        {
            try { _timer?.Stop(); } catch { }
            try { _player?.Stop(); } catch { }
            try { _player?.Dispose(); } catch { }
            try { _libvlc?.Dispose(); } catch { }
        }
    }
}
