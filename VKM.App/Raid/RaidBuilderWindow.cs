using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VKM.App
{
    // Elle RAID kurma / otomatik bulunanı düzeltme penceresi
    public class RaidBuilderWindow : Window
    {
        public RaidConfig? Result;
        private readonly List<RaidMember> _avail;
        private readonly Action<string> _log;
        private readonly ObservableCollection<SlotItem> _slots = new();

        private readonly ComboBox _level = new(), _chunk = new(), _layout = new(), _span = new(), _add = new();
        private readonly TextBox _delay = new(), _groups = new(), _copies = new(), _start = new(), _len = new();
        private readonly ListBox _list = new();
        private readonly TextBlock _info = new();
        private CancellationTokenSource? _cts;

        public class SlotItem
        {
            public RaidMember? M;
            public long Start;
            public override string ToString() => M == null ? "— EKSİK (pariteden hesaplanır) —" : M.Name + (Start > 0 ? "   · veri başı " + Fmt.Size(Start) : "") + "   · " + Fmt.Size(M.Length);
        }

        static readonly (string text, RaidKind k, int groups, RaidLayout? lay)[] Levels =
        {
            ("RAID 0 (şerit)", RaidKind.Raid0, 1, null),
            ("RAID 1 (ayna)", RaidKind.Raid1, 1, null),
            ("RAID 10 / 1E (near)", RaidKind.Raid10, 1, RaidLayout.Near),
            ("RAID 10 far (md)", RaidKind.Raid10, 1, RaidLayout.Far),
            ("RAID 3 / 4 (sabit parite)", RaidKind.Raid5, 1, RaidLayout.ParityLast),
            ("RAID 5", RaidKind.Raid5, 1, null),
            ("RAID 6", RaidKind.Raid6, 1, null),
            ("RAID 50", RaidKind.Raid5, 2, null),
            ("RAID 60", RaidKind.Raid6, 2, null),
            ("JBOD / Span (uç uca)", RaidKind.Jbod, 1, null),
        };
        static readonly RaidLayout[] Layouts = { RaidLayout.LeftSym, RaidLayout.LeftAsym, RaidLayout.RightSym, RaidLayout.RightAsym, RaidLayout.ParityFirst, RaidLayout.ParityLast, RaidLayout.DdfNRestart, RaidLayout.DdfNContinue };

        public RaidBuilderWindow(RaidConfig init, List<RaidMember> available, Action<string> log)
        {
            _avail = available; _log = log;
            Title = "RAID kur / düzelt";
            Width = 860; Height = 640;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Theme.Col("Bg"));

            var g = new Grid { Margin = new Thickness(14) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(320) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // sol: ayarlar
            var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
            foreach (var l in Levels) _level.Items.Add(l.text);
            foreach (long c in new long[] { 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1 << 20, 2 << 20, 4 << 20 }) _chunk.Items.Add(Fmt.Size(c));
            _chunk.IsEditable = true;
            foreach (var l in Layouts) _layout.Items.Add(RaidConfig.LayoutText(l));
            _span.Items.Add("blok blok (strip)"); _span.Items.Add("şerit şerit (stripe)"); _span.Items.Add("uç uca");
            Row(left, "Seviye", _level);
            Row(left, "Blok (stripe) boyutu", _chunk);
            Row(left, "Parite yerleşimi", _layout);
            Row(left, "Parite gecikmesi (satır)", _delay);
            Row(left, "Grup sayısı (50/60)", _groups);
            Row(left, "Gruplar arası dağıtım", _span);
            Row(left, "Kopya (RAID 10)", _copies);
            Row(left, "Veri başlangıcı (sektör, tümü)", _start);
            Row(left, "Üye başına uzunluk (sektör, 0=oto)", _len);
            var auto = Btn("🔍 Eksik ayarları otomatik bul", AutoFind, true);
            auto.ToolTip = "Seviyeyi ve diskleri sen seç; blok boyutu, yerleşim, sıra ve başlangıcı çözücü bulsun";
            left.Children.Add(auto);
            _info.Foreground = new SolidColorBrush(Theme.Col("Dim")); _info.TextWrapping = TextWrapping.Wrap; _info.Margin = new Thickness(0, 10, 0, 0);
            left.Children.Add(_info);
            Grid.SetColumn(left, 0);
            g.Children.Add(left);

            // sağ: üyeler
            var right = new DockPanel();
            var top = new TextBlock { Text = "Üyeler (sıra önemli: üstteki 1. disk)", Foreground = Theme.Brush("Text"), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(top, Dock.Top); right.Children.Add(top);
            var btns = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(btns, Dock.Bottom);
            foreach (var m in _avail) _add.Items.Add(m.Name + " · " + Fmt.Size(m.Length));
            _add.Width = 330; _add.Margin = new Thickness(0, 0, 6, 6);
            btns.Children.Add(_add);
            btns.Children.Add(Btn("＋ Ekle", () => { if (_add.SelectedIndex >= 0) _slots.Add(new SlotItem { M = _avail[_add.SelectedIndex] }); }));
            btns.Children.Add(Btn("＋ EKSİK disk", () => _slots.Add(new SlotItem())));
            btns.Children.Add(Btn("↑", () => Move(-1)));
            btns.Children.Add(Btn("↓", () => Move(1)));
            btns.Children.Add(Btn("✕ Çıkar", () => { if (_list.SelectedItem is SlotItem s) _slots.Remove(s); }));
            btns.Children.Add(Btn("⇄ Ters çevir", () => { var r = _slots.Reverse().ToList(); _slots.Clear(); foreach (var x in r) _slots.Add(x); }));
            right.Children.Add(btns);
            _list.ItemsSource = _slots;
            _list.Background = new SolidColorBrush(Theme.Col("Card")); _list.Foreground = new SolidColorBrush(Theme.Col("Text"));
            _list.FontFamily = new FontFamily("Consolas"); _list.FontSize = 12.5;
            right.Children.Add(_list);
            Grid.SetColumn(right, 1);
            g.Children.Add(right);

            var bottom = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            bottom.Children.Add(Btn("İptal", () => { _cts?.Cancel(); DialogResult = false; }));
            bottom.Children.Add(Btn("✓ Kur ve aç", Ok, true));
            Grid.SetRow(bottom, 1); Grid.SetColumnSpan(bottom, 2);
            g.Children.Add(bottom);
            Content = g;

            Load(init);
            Closed += (_, _) => _cts?.Cancel();
        }

        private static void Row(Panel p, string label, Control c)
        {
            p.Children.Add(new TextBlock { Text = label, Foreground = new SolidColorBrush(Theme.Col("Dim")), Margin = new Thickness(0, 6, 0, 2) });
            c.Height = 28;
            if (c is TextBox t) { t.Background = new SolidColorBrush(Theme.Col("Card")); t.Foreground = new SolidColorBrush(Theme.Col("Text")); t.VerticalContentAlignment = VerticalAlignment.Center; }
            p.Children.Add(c);
        }

        private static Button Btn(string t, Action a, bool main = false)
        {
            var b = new Button
            {
                Content = t, Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 6, 6, 0),
                Background = main ? new SolidColorBrush(Theme.Col("Accent")) : new SolidColorBrush(Theme.Col("Btn")),
                Foreground = main ? Brushes.White : new SolidColorBrush(Theme.Col("Text")), BorderThickness = new Thickness(main ? 0 : 1),
                BorderBrush = new SolidColorBrush(Theme.Col("Line"))
            };
            b.Click += (_, _) => a();
            return b;
        }

        private void Move(int d)
        {
            int i = _list.SelectedIndex; int j = i + d;
            if (i < 0 || j < 0 || j >= _slots.Count) return;
            _slots.Move(i, j); _list.SelectedIndex = j;
        }

        private void Load(RaidConfig c)
        {
            int li = Array.FindIndex(Levels, l => l.k == c.Kind && (c.Groups > 1) == (l.groups > 1) && (l.lay == null || l.lay == c.Layout));
            if (li < 0) li = Array.FindIndex(Levels, l => l.k == c.Kind);
            _level.SelectedIndex = Math.Max(0, li);
            _chunk.Text = Fmt.Size(c.Chunk);
            int lay = Array.IndexOf(Layouts, c.Layout); _layout.SelectedIndex = lay < 0 ? 0 : lay;
            _delay.Text = c.Delay.ToString();
            _groups.Text = Math.Max(1, c.Groups).ToString();
            _span.SelectedIndex = (int)c.Span;
            _copies.Text = c.Copies.ToString();
            _start.Text = (c.DataStart.Length > 0 && c.DataStart.All(x => x == c.DataStart[0]) ? c.DataStart[0] / 512 : 0).ToString();
            _len.Text = (c.MemberDataLength / 512).ToString();
            _slots.Clear();
            for (int i = 0; i < c.Members.Length; i++) _slots.Add(new SlotItem { M = c.Members[i], Start = c.StartOf(i) });
            _info.Text = c.Source.Length > 0 ? "Kaynak: " + c.Source + (c.Confidence < 0.999 ? $" · güven %{c.Confidence * 100:0}" : "") : "";
        }

        private static long ParseSize(string t)
        {
            t = t.Trim().ToUpperInvariant().Replace(" ", "").Replace(',', '.');
            double mul = 1;
            if (t.EndsWith("KB")) { mul = 1024; t = t[..^2]; }
            else if (t.EndsWith("MB")) { mul = 1 << 20; t = t[..^2]; }
            else if (t.EndsWith("K")) { mul = 1024; t = t[..^1]; }
            else if (t.EndsWith("B")) t = t[..^1];
            return (long)(double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) * mul);
        }

        private RaidConfig? Build(bool forSolve = false)
        {
            try
            {
                var lv = Levels[Math.Max(0, _level.SelectedIndex)];
                var c = new RaidConfig { Kind = lv.k, Source = "Elle kuruldu" };
                c.Chunk = ParseSize(_chunk.Text);
                c.Layout = lv.lay ?? Layouts[Math.Max(0, _layout.SelectedIndex)];
                if (lv.k == RaidKind.Raid10 && lv.lay == null) c.Layout = RaidLayout.Near;
                c.Delay = Math.Max(1, int.Parse(_delay.Text));
                c.Groups = lv.groups > 1 ? Math.Max(2, int.Parse(_groups.Text)) : 1;
                c.Span = (SpanMode)Math.Max(0, _span.SelectedIndex);
                c.Copies = Math.Max(2, int.Parse(_copies.Text));
                long st = long.Parse(_start.Text) * 512;
                c.MemberDataLength = long.Parse(_len.Text) * 512;
                c.Members = _slots.Select(s => s.M).ToArray();
                c.DataStart = _slots.Select(s => s.Start > 0 && st == 0 ? s.Start : st).ToArray();
                if (c.Members.Length == 0) { _info.Text = "Üye ekleyin."; return null; }
                if (c.Groups > 1 && c.Members.Length % c.Groups != 0) { _info.Text = $"Disk sayısı ({c.Members.Length}) grup sayısına ({c.Groups}) tam bölünmeli."; return null; }
                c.DisksPerGroup = c.Members.Length / c.Groups;
                int min = c.Kind switch { RaidKind.Raid5 => 3, RaidKind.Raid6 => 4, RaidKind.Raid10 => 2, _ => 1 };
                if (!forSolve && c.DisksPerGroup < min) { _info.Text = $"{lv.text} için grup başına en az {min} disk gerekir."; return null; }
                if (c.Chunk < 512) { _info.Text = "Blok boyutu geçersiz."; return null; }
                return c;
            }
            catch (Exception ex) { _info.Text = "Ayar okunamadı: " + ex.Message; return null; }
        }

        private void Ok()
        {
            var c = Build(); if (c == null) return;
            int redundancy = c.Kind switch { RaidKind.Raid5 => c.Groups, RaidKind.Raid6 => 2 * c.Groups, RaidKind.Raid1 => c.Total - 1, RaidKind.Raid10 => c.Total / 2, _ => 0 };
            if (c.Missing > redundancy && MessageBox.Show($"{c.Missing} disk eksik, bu seviye en fazla {redundancy} eksik tolere eder. Yine de kurulsun mu? (eksik bölgeler sıfır okunur)", "Uyarı", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            Result = c; DialogResult = true;
        }

        // Seviye + diskler belli → blok/yerleşim/sıra/başlangıç çözücüden
        private void AutoFind()
        {
            var c = Build(true); if (c == null) return;
            var present = c.Members.Where(m => m != null).Select(m => m!).ToList();
            if (present.Count < 2) { _info.Text = "En az 2 disk ekleyin."; return; }
            var hints = new SolverHints { Kind = c.Kind, TotalSlots = c.Members.Length, Groups = c.Groups };
            _info.Text = "çözücü çalışıyor… (birkaç dakika sürebilir)";
            _cts?.Cancel(); _cts = new CancellationTokenSource(); var tok = _cts.Token;
            Task.Run(() =>
            {
                var r = RaidSolver.Solve(present, hints, _log, () => tok.IsCancellationRequested);
                Dispatcher.Invoke(() =>
                {
                    if (r.Config == null) { _info.Text = "Bulunamadı. " + r.Report; return; }
                    Load(r.Config);
                    _info.Text = r.Report;
                });
            });
        }
    }
}
