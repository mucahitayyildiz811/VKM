using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;

namespace VKM.App
{
    // RAID ağacı için ayırt edici vektör simgeleri (taslaktaki UFS tarzı):
    // sabit disk, SSD, sunucu/RAID dizisi, LVM, üye rozetleri (1/5/6), bölümler, swap.
    public static class RaidIcons
    {
        static Brush B(string hex) { var c = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!; c.Freeze(); return c; }
        static readonly Brush White = Brushes.White;
        static readonly FontFamily Seg = new("Segoe UI");

        static Rectangle Rect(double x, double y, double w, double h, double rx, Brush fill, Brush? stroke = null, double sw = 1, double[]? dash = null)
        {
            var r = new Rectangle { Width = w, Height = h, RadiusX = rx, RadiusY = rx, Fill = fill };
            if (stroke != null) { r.Stroke = stroke; r.StrokeThickness = sw; }
            if (dash != null) r.StrokeDashArray = new DoubleCollection(dash);
            Canvas.SetLeft(r, x); Canvas.SetTop(r, y);
            return r;
        }
        static Ellipse Ell(double cx, double cy, double r, Brush fill, Brush? stroke = null, double sw = 1)
        {
            var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill };
            if (stroke != null) { e.Stroke = stroke; e.StrokeThickness = sw; }
            Canvas.SetLeft(e, cx - r); Canvas.SetTop(e, cy - r);
            return e;
        }
        static Path Pt(string data, Brush? fill, Brush? stroke = null, double sw = 1)
        {
            var p = new Path { Data = Geometry.Parse(data), StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
            if (fill != null) p.Fill = fill;
            if (stroke != null) { p.Stroke = stroke; p.StrokeThickness = sw; }
            return p;
        }
        static TextBlock Txt(double cx, double cy, double size, Brush fill, string s)
        {
            var t = new TextBlock { Text = s, FontSize = size, FontWeight = FontWeights.Bold, Foreground = fill, FontFamily = Seg, Width = size * 2.6, TextAlignment = TextAlignment.Center };
            Canvas.SetLeft(t, cx - size * 1.3); Canvas.SetTop(t, cy - size * 0.72);
            return t;
        }

        static readonly Brush DiskBody = B("#e3e8ed"), DiskLine = B("#7d8a97"), Platter = B("#b9c4cf"), Spindle = B("#f1f4f7"), Arm = B("#6b7785"), ArmHub = B("#9aa6b2");

        public static FrameworkElement Make(string key)
        {
            var c = new Canvas { Width = 32, Height = 32 };
            void A(UIElement e) => c.Children.Add(e);

            switch (key)
            {
                case "hdd":
                    A(Rect(3.5, 5.5, 25, 21, 2.4, DiskBody, DiskLine, 1.1));
                    A(Ell(14, 16, 8.4, Platter, DiskLine)); A(Ell(14, 16, 2.6, Spindle, DiskLine));
                    A(Pt("M25 7.5 18.5 14.5", null, Arm, 2.1)); A(Ell(25, 7.5, 1.7, ArmHub, Arm, .8));
                    break;
                case "ssd":
                    A(Rect(4, 5, 24, 22, 2.2, B("#dfeafa"), B("#5b86b8")));
                    A(Rect(8, 9, 7, 6, 1, B("#9fc0e8"))); A(Rect(17, 9, 7, 6, 1, B("#9fc0e8")));
                    A(Rect(8, 17, 16, 3, 1, B("#c4d8f0"))); A(Rect(8, 21, 11, 2.4, 1, B("#c4d8f0")));
                    break;
                case "array":   // SAĞLAM RAID — disk yığını + yeşil onay
                    A(Rect(3, 19.5, 25, 7, 2.2, B("#a9bcd2"), B("#5f84a8")));
                    A(Rect(3, 11.5, 25, 7, 2.2, B("#c0d2e6"), B("#5f84a8")));
                    A(Rect(3, 3.5, 25, 7, 2.2, B("#d8e5f3"), B("#5f84a8")));
                    A(Ell(7.5, 7, 1.7, B("#6f9acb"))); A(Ell(7.5, 15, 1.7, B("#6f9acb"))); A(Ell(7.5, 23, 1.7, B("#6f9acb")));
                    A(Ell(24.5, 24.5, 6.8, B("#2e9e52"), White, 1.4));
                    A(Pt("M21.4 24.6l2.1 2.1 3.6-4", null, White, 1.9));
                    break;
                case "arraywarn":   // EKSİK DİSKLİ RAID — bir kat boş + sarı ünlem
                    A(Rect(3, 19.5, 25, 7, 2.2, B("#a9bcd2"), B("#5f84a8")));
                    A(Rect(3, 11.5, 25, 7, 2.2, B("#eadbb6"), B("#c98a00"), 1, new[] { 2.4, 1.6 }));
                    A(Rect(3, 3.5, 25, 7, 2.2, B("#d8e5f3"), B("#5f84a8")));
                    A(Ell(7.5, 7, 1.7, B("#6f9acb"))); A(Ell(7.5, 23, 1.7, B("#6f9acb")));
                    A(Ell(24.5, 24.5, 6.8, B("#e0a008"), White, 1.4));
                    A(Pt("M24.5 20.8v4.1", null, White, 1.9)); A(Ell(24.5, 27.4, 1.05, White));
                    break;
                case "lvm":
                    A(Pt("M16 3.5 28.5 10 16 16.5 3.5 10Z", B("#c9b8e8"), B("#7a5bb8")));
                    A(Pt("M3.5 10.5v3.2L16 20l12.5-6.3v-3.2", B("#b7a2e0"), B("#7a5bb8")));
                    A(Pt("M3.5 16v3.2L16 25.5 28.5 19.2V16", B("#a68fd6"), B("#7a5bb8")));
                    break;
                case "volume":
                    A(Rect(4, 6, 24, 20, 2.4, B("#dfeafa"), B("#5b86b8")));
                    A(Ell(22, 20, 2.2, B("#5b86b8"))); A(Rect(8, 10, 12, 2.6, 1.3, B("#9fc0e8")));
                    break;
                case "fsntfs": FsBlock(c, B("#cfe0f5"), B("#8bb4e4"), B("#5b86b8"), B("#3a6ba8"), "N"); break;
                case "fsext": FsBlock(c, B("#d6ecd8"), B("#8fcc97"), B("#4c9a57"), B("#3a7a44"), "E"); break;
                case "fsbtrfs": FsBlock(c, B("#f0e2cf"), B("#e0bd87"), B("#c08a3a"), B("#9a6a24"), "B"); break;
                case "fsxfs": FsBlock(c, B("#e0dcf0"), B("#b8aee0"), B("#6a5bb0"), B("#4c3a8a"), "X"); break;
                case "fsfat": FsBlock(c, B("#f5e6cf"), B("#e6c488"), B("#c0903a"), B("#8a5f1f"), "F"); break;
                case "comp5": Member(c, B("#6a5bb0"), "5"); break;
                case "comp6": Member(c, B("#b0565b"), "6"); break;
                case "mirror": Member(c, B("#0f8f86"), "1"); break;
                case "swap":
                    A(Rect(4, 8, 24, 16, 2.2, B("#e6e9ed"), B("#9aa6b2")));
                    A(Pt("M11 13l-3.2 3.2L11 19.4M21 13l3.2 3.2L21 19.4", null, B("#6b7785"), 1.7));
                    A(Pt("M7.8 16.2h16.4", null, B("#aab4bf"), 1.3));
                    break;
                case "part":
                    A(Rect(4, 8, 24, 16, 2.2, B("#cdd7e2"), B("#8a97a4")));
                    A(Rect(4, 8, 7, 16, 2.2, B("#aebccb"), B("#8a97a4")));
                    break;
                default:   // bilinmeyen → soluk kutu
                    A(Rect(4, 8, 24, 16, 2.2, B("#eef1f4"), B("#c3ccd5"), 1, new[] { 3.0, 2.0 }));
                    break;
            }
            return new Viewbox { Width = 19, Height = 19, Child = c, Stretch = Stretch.Uniform };
        }

        static void FsBlock(Canvas c, Brush body, Brush stripe, Brush line, Brush txt, string letter)
        {
            c.Children.Add(Rect(4, 7, 24, 18, 2.2, body, line));
            c.Children.Add(Rect(4, 7, 6, 18, 2.2, stripe, line));
            c.Children.Add(Txt(20, 16, 8.5, txt, letter));
        }

        static void Member(Canvas c, Brush badge, string level)
        {
            c.Children.Add(Rect(2.5, 7, 19, 17, 2, DiskBody, DiskLine));
            c.Children.Add(Ell(11, 15.5, 6, Platter, DiskLine));
            c.Children.Add(Ell(11, 15.5, 1.8, Spindle, DiskLine));
            c.Children.Add(Ell(24, 22, 7, badge, White, 1.3));
            c.Children.Add(Txt(24, 22, 8.2, White, level));
        }
    }

    // {Binding Icon} string anahtarını vektör simgeye çevirir; bilinmeyen (emoji) ise metin olarak gösterir
    public class RaidIconConverter : IValueConverter
    {
        static readonly System.Collections.Generic.HashSet<string> Known = new()
        { "hdd","ssd","array","arraywarn","lvm","volume","fsntfs","fsext","fsbtrfs","fsxfs","fsfat","comp5","comp6","mirror","swap","part" };

        public object Convert(object value, Type t, object p, CultureInfo c)
        {
            string key = value as string ?? "";
            if (Known.Contains(key)) return RaidIcons.Make(key);
            if (key.Length == 0) return new FrameworkElement();
            return new TextBlock { Text = key, FontSize = 13, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        }
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => Binding.DoNothing;
    }
}
