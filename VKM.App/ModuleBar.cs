using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace VKM.App
{
    // Üstteki modül çubuğu: her modül bir kutu. Yalnız SEÇİLİ kutunun kenarında
    // turkuaz LED döner (buradasın göstergesi). Boştakiler sade.
    public class ModuleBar : ItemsControl
    {
        public event Action<string>? ModuleChanged;
        public string SelectedTag { get; private set; } = "";
        private readonly List<ModuleBox> _boxes = new();

        public ModuleBar()
        {
            var wrap = new FrameworkElementFactory(typeof(WrapPanel));
            wrap.SetValue(WrapPanel.OrientationProperty, Orientation.Horizontal);
            ItemsPanel = new ItemsPanelTemplate(wrap);
            Theme.Changed += () => { foreach (var b in _boxes) b.Repaint(); };
        }

        public void AddModule(string tag, string icon, string label)
        {
            var box = new ModuleBox(icon, label) { Margin = new Thickness(3, 2, 3, 2) };
            box.Tag = tag;
            box.MouseLeftButtonUp += (_, _) => Select(tag);
            _boxes.Add(box);
            Items.Add(box);
        }

        public void Select(string tag)
        {
            if (SelectedTag == tag && _boxes.Exists(b => (string)b.Tag == tag && b.Selected)) { ModuleChanged?.Invoke(tag); return; }
            SelectedTag = tag;
            foreach (var b in _boxes) b.SetSelected((string)b.Tag == tag);
            ModuleChanged?.Invoke(tag);
        }
    }

    public class ModuleBox : Border
    {
        private readonly TextBlock _ico, _lbl;
        private readonly Canvas _ledLayer;
        private readonly Ellipse _led;
        public bool Selected { get; private set; }

        public ModuleBox(string icon, string label)
        {
            Width = 98; Height = 60; CornerRadius = new CornerRadius(12);
            Cursor = System.Windows.Input.Cursors.Hand;
            SnapsToDevicePixels = true;

            _ico = new TextBlock { Text = icon, FontSize = 19, HorizontalAlignment = HorizontalAlignment.Center };
            _lbl = new TextBlock { Text = label, FontSize = 10.5, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 5, 0, 0), TextAlignment = TextAlignment.Center };
            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(_ico); stack.Children.Add(_lbl);

            _led = new Ellipse
            {
                Width = 8, Height = 8,
                RenderTransform = new MatrixTransform(),
                Visibility = Visibility.Collapsed
            };
            _ledLayer = new Canvas { IsHitTestVisible = false };
            _ledLayer.Children.Add(_led);

            var grid = new Grid();
            grid.Children.Add(stack);
            grid.Children.Add(_ledLayer);
            Child = grid;

            Loaded += (_, _) => { Repaint(); if (Selected) StartLed(); };
            Repaint();
        }

        public void SetSelected(bool sel)
        {
            if (Selected == sel) return;
            Selected = sel;
            Repaint();
            if (sel) StartLed(); else StopLed();
        }

        public void Repaint()
        {
            Background = Theme.Brush("Card");
            BorderBrush = Selected ? Theme.Brush("Led") : Theme.Brush("Line");
            BorderThickness = new Thickness(Selected ? 1.4 : 1);
            _ico.Foreground = Selected ? Theme.Brush("Text") : Theme.Brush("Dim");
            _lbl.Foreground = Selected ? Theme.Brush("AccentText") : Theme.Brush("Dim");
            _led.Fill = Theme.Brush("Led");
            _led.Effect = new DropShadowEffect { Color = Theme.Col("Led"), BlurRadius = 12, ShadowDepth = 0, Opacity = 1 };
            Effect = Selected ? new DropShadowEffect { Color = Theme.Col("Led"), BlurRadius = 16, ShadowDepth = 0, Opacity = .25 } : null;
        }

        private void StartLed()
        {
            _led.Visibility = Visibility.Visible;
            if (!IsLoaded || ActualWidth < 4) { Dispatcher.BeginInvoke(new Action(StartLed), System.Windows.Threading.DispatcherPriority.Loaded); return; }
            double w = ActualWidth, h = ActualHeight, r = 12;
            var rr = new RectangleGeometry(new Rect(1, 1, w - 2, h - 2), r, r);
            var pg = PathGeometry.CreateFromGeometry(rr);
            var mt = (MatrixTransform)_led.RenderTransform;
            Canvas.SetLeft(_led, -4); Canvas.SetTop(_led, -4);
            var anim = new MatrixAnimationUsingPath
            {
                PathGeometry = pg,
                Duration = TimeSpan.FromSeconds(2.4),
                RepeatBehavior = RepeatBehavior.Forever,
                DoesRotateWithTangent = false
            };
            mt.BeginAnimation(MatrixTransform.MatrixProperty, anim);
        }

        private void StopLed()
        {
            ((MatrixTransform)_led.RenderTransform).BeginAnimation(MatrixTransform.MatrixProperty, null);
            _led.Visibility = Visibility.Collapsed;
        }
    }
}
