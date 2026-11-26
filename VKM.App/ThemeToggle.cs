using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace VKM.App
{
    // Sağ üstteki ay/güneş anahtarı: "Karanlık mod" yazısı + tatlı on/off pili.
    public class ThemeToggle : Border
    {
        private readonly Path _glyph;
        private readonly Border _track;
        private readonly Ellipse _thumb;
        private readonly TextBlock _label;

        const string Moon = "M21 12.8A9 9 0 1 1 11.2 3 7 7 0 0 0 21 12.8Z";
        const string Sun  = "M12 7a5 5 0 1 0 0 10 5 5 0 0 0 0-10Z";

        public ThemeToggle()
        {
            Cursor = System.Windows.Input.Cursors.Hand;
            Padding = new Thickness(10, 5, 10, 5);
            CornerRadius = new CornerRadius(8);
            VerticalAlignment = VerticalAlignment.Center;
            ToolTip = "Açık / koyu tema";

            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _glyph = new Path { Width = 16, Height = 16, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            _label = new TextBlock { Text = "Karanlık mod", FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 9, 0) };

            _thumb = new Ellipse { Width = 16, Height = 16, Fill = Brushes.White, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2, 0, 0, 0) };
            _track = new Border { Width = 38, Height = 20, CornerRadius = new CornerRadius(10), VerticalAlignment = VerticalAlignment.Center, Child = _thumb };

            row.Children.Add(_glyph);
            row.Children.Add(_label);
            row.Children.Add(_track);
            Child = row;

            MouseLeftButtonUp += (_, _) => { Theme.Toggle(); Paint(true); };
            Theme.Changed += () => Paint(false);
            Loaded += (_, _) => Paint(false);
            Paint(false);
        }

        private void Paint(bool animate)
        {
            bool dark = Theme.IsDark;
            Background = Theme.Brush("Btn");
            BorderBrush = Theme.Brush("Line");
            BorderThickness = new Thickness(1);
            _label.Foreground = Theme.Brush("Dim");
            _glyph.Data = Geometry.Parse(dark ? Sun : Moon);
            _glyph.Fill = dark ? Theme.Brush("Warn") : Theme.Brush("Dim");
            _track.Background = dark ? Theme.Brush("Accent") : new SolidColorBrush(Color.FromRgb(0xcf, 0xd6, 0xde));
            double to = dark ? 20 : 2;
            if (animate)
            {
                var a = new ThicknessAnimation(new Thickness(to, 0, 0, 0), TimeSpan.FromMilliseconds(140)) { AccelerationRatio = .3, DecelerationRatio = .5 };
                _thumb.BeginAnimation(MarginProperty, a);
            }
            else { _thumb.BeginAnimation(MarginProperty, null); _thumb.Margin = new Thickness(to, 0, 0, 0); }
        }
    }
}
