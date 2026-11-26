using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace VKM.App
{
    // Genel hex görüntüleyici: disk, bölüm, sanal RAID, LVM birimi. Salt okunur.
    // RAID'de her sektörün hangi diskin hangi LBA'sından geldiğini gösterir.
    public class HexViewWindow : Window
    {
        private readonly Func<Stream> _open;
        private readonly long _len;
        private readonly Func<long, string>? _locate;
        private Stream? _s;
        private long _pos;
        private const int PAGE = 4096;
        private readonly TextBox _view = new();
        private readonly TextBox _go = new();
        private readonly TextBox _find = new();
        private readonly TextBlock _where = new();
        private readonly TextBlock _stat = new();
        private CancellationTokenSource? _cts;

        public HexViewWindow(Func<Stream> open, long length, string title, Func<long, string>? locate)
        {
            _open = open; _len = length; _locate = locate;
            Title = "Hex · " + title + " · " + Fmt.Size(length);
            Width = 980; Height = 760;
            Background = new SolidColorBrush(Color.FromRgb(0x0e, 0x13, 0x19));
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(10) };
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(bar, Dock.Top);
            bar.Children.Add(Lbl("Sektör:"));
            _go.Width = 150; Sty(_go); _go.ToolTip = "Sektör no (ondalık) ya da 0x… bayt adresi";
            _go.KeyDown += (_, e) => { if (e.Key == Key.Enter) GoTo(); };
            bar.Children.Add(_go);
            bar.Children.Add(Btn("Git", GoTo));
            bar.Children.Add(Btn("⏮ Baş", () => ShowAt(0)));
            bar.Children.Add(Btn("◀◀ 1 MB", () => ShowAt(_pos - (1 << 20))));
            bar.Children.Add(Btn("◀ sektör", () => ShowAt(_pos - 512)));
            bar.Children.Add(Btn("sektör ▶", () => ShowAt(_pos + 512)));
            bar.Children.Add(Btn("1 MB ▶▶", () => ShowAt(_pos + (1 << 20))));
            bar.Children.Add(Btn("Son ⏭", () => ShowAt(_len - PAGE)));
            bar.Children.Add(Lbl("   Ara:"));
            _find.Width = 170; Sty(_find); _find.ToolTip = "Metin ya da hex (ör. 55 AA veya 4E544653)";
            bar.Children.Add(_find);
            bar.Children.Add(Btn("Metin ▶", () => Find(false)));
            bar.Children.Add(Btn("Hex ▶", () => Find(true)));
            bar.Children.Add(Btn("■", () => _cts?.Cancel()));
            root.Children.Add(bar);

            _where.Foreground = new SolidColorBrush(Color.FromRgb(0x5e, 0xea, 0xd4));
            _where.FontFamily = new FontFamily("Consolas"); _where.FontSize = 12; _where.TextWrapping = TextWrapping.Wrap;
            _where.Margin = new Thickness(0, 0, 0, 6);
            DockPanel.SetDock(_where, Dock.Top);
            root.Children.Add(_where);

            _stat.Foreground = new SolidColorBrush(Color.FromRgb(0x8a, 0xa0, 0xb6)); _stat.Margin = new Thickness(0, 6, 0, 0);
            DockPanel.SetDock(_stat, Dock.Bottom);
            root.Children.Add(_stat);

            _view.IsReadOnly = true; _view.FontFamily = new FontFamily("Consolas"); _view.FontSize = 13;
            _view.Background = new SolidColorBrush(Color.FromRgb(0x05, 0x08, 0x0b));
            _view.Foreground = new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80));
            _view.BorderThickness = new Thickness(0); _view.Padding = new Thickness(8);
            _view.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _view.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _view.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.PageDown) { ShowAt(_pos + PAGE); e.Handled = true; }
                else if (e.Key == Key.PageUp) { ShowAt(_pos - PAGE); e.Handled = true; }
            };
            root.Children.Add(_view);
            Content = root;

            Loaded += (_, _) =>
            {
                try { _s = _open(); } catch (Exception ex) { _stat.Text = "Açılamadı: " + ex.Message; return; }
                ShowAt(0);
            };
            Closed += (_, _) => { _cts?.Cancel(); try { _s?.Dispose(); } catch { } };
        }

        private static TextBlock Lbl(string t) => new() { Text = t, Foreground = Brushes.LightGray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        private static void Sty(TextBox t) { t.Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x23, 0x30)); t.Foreground = Brushes.White; t.Margin = new Thickness(0, 0, 6, 0); t.VerticalContentAlignment = VerticalAlignment.Center; t.Height = 28; }
        private static Button Btn(string t, Action a)
        {
            var b = new Button
            {
                Content = t, Height = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(0, 0, 6, 0),
                Background = new SolidColorBrush(Color.FromRgb(0x1a, 0x23, 0x30)), Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2a, 0x36, 0x42)), Cursor = Cursors.Hand
            };
            b.Click += (_, _) => a();
            return b;
        }

        private void GoTo()
        {
            string t = _go.Text.Trim().Replace(".", "").Replace(" ", "");
            try
            {
                long p = t.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(t[2..], 16) : long.Parse(t) * 512;
                ShowAt(p);
            }
            catch { _stat.Text = "Geçersiz adres."; }
        }

        private void ShowAt(long p)
        {
            if (_s == null) return;
            p = Math.Max(0, Math.Min(p, Math.Max(0, _len - 1)));
            p = p / 512 * 512;
            _pos = p;
            byte[] b = Fmt.ReadAt(_s, p, (int)Math.Min(PAGE, Math.Max(0, _len - p)));
            var sb = new StringBuilder();
            for (int i = 0; i < b.Length; i += 16)
            {
                if (i % 512 == 0) sb.AppendLine($"── sektör {(p + i) / 512:N0} ──────────────────────────────────────────────");
                sb.Append((p + i).ToString("X12")).Append("  ");
                for (int k = 0; k < 16; k++)
                {
                    sb.Append(i + k < b.Length ? b[i + k].ToString("X2") : "  ").Append(' ');
                    if (k == 7) sb.Append(' ');
                }
                sb.Append(" ");
                for (int k = 0; k < 16 && i + k < b.Length; k++) { byte c = b[i + k]; sb.Append(c >= 32 && c < 127 ? (char)c : '.'); }
                sb.AppendLine();
            }
            _view.Text = sb.ToString();
            _view.ScrollToHome();
            _go.Text = (p / 512).ToString();
            _where.Text = _locate != null ? "Bu sektör → " + _locate(p) : "";
            _stat.Text = $"Adres 0x{p:X} · sektör {p / 512:N0} / {_len / 512:N0} · PageUp/PageDown ile gezin";
        }

        private void Find(bool hex)
        {
            if (_s == null) return;
            string q = _find.Text;
            byte[] pat;
            try
            {
                pat = hex ? Convert.FromHexString(new string(q.Where(Uri.IsHexDigit).ToArray())) : Encoding.ASCII.GetBytes(q);
            }
            catch { _stat.Text = "Hex yanlış."; return; }
            if (pat.Length == 0) return;
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var tok = _cts.Token;
            long start = _pos + 1;
            var open = _open; long len = _len;
            _stat.Text = "aranıyor…";
            Task.Run(() =>
            {
                using var s = open();
                const int CH = 4 << 20;
                var buf = new byte[CH + pat.Length];
                long p = start;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (p < len && !tok.IsCancellationRequested)
                {
                    int want = (int)Math.Min(buf.Length, len - p);
                    byte[] b = Fmt.ReadAt(s, p, want);
                    int idx = b.AsSpan(0, want).IndexOf(pat);
                    if (idx >= 0) { long hit = p + idx; Dispatcher.Invoke(() => { ShowAt(hit); _stat.Text = $"Bulundu: 0x{hit:X} (sektör {hit / 512:N0}, +{hit % 512})"; }); return; }
                    p += CH;
                    if (sw.ElapsedMilliseconds > 300) { sw.Restart(); long pp = p; Dispatcher.InvokeAsync(() => _stat.Text = $"aranıyor… %{pp * 100.0 / len:0.0}"); }
                }
                Dispatcher.InvokeAsync(() => _stat.Text = tok.IsCancellationRequested ? "Arama durduruldu." : "Bulunamadı.");
            });
        }
    }
}
