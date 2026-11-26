using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VKM.App
{
    // Yeni İş sihirbazı: ① iş no + ad soyad  ② kaydetme klasörü  ③ kaynak disk.
    // İmaj kaynak diske yazılamaz (kırmızı uyarı).
    public class NewCaseWindow : Window
    {
        public string Number = "", PersonName = "", Folder = "";
        public DiskInfo? Source;

        private readonly TextBox _no = new(), _nm = new(), _folder = new();
        private readonly ComboBox _disk = new();
        private readonly TextBlock _warn = new();
        private readonly ObservableCollection<DiskInfo> _disks;

        public NewCaseWindow(ObservableCollection<DiskInfo> disks)
        {
            _disks = disks;
            Title = "Yeni İş"; Width = 540; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.Brush("Bg");
            var sp = new StackPanel { Margin = new Thickness(18) };

            // ① önce: veri nereye kaydedilecek (PC-3000 mantığı — en başta bir kez)
            sp.Children.Add(Step("① Veri/imaj nereye kaydedilsin? (hedef disk · klasör)"));
            var fr = new Grid { Margin = new Thickness(0, 4, 0, 12) };
            fr.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            fr.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Field(_folder, "Klasör seç…"); _folder.IsReadOnly = true;
            var browse = Btn("Gözat"); browse.Click += (_, _) => Browse();
            Grid.SetColumn(_folder, 0); Grid.SetColumn(browse, 1); browse.Margin = new Thickness(8, 0, 0, 0);
            fr.Children.Add(_folder); fr.Children.Add(browse);
            sp.Children.Add(fr);

            // ② sonra: iş no + ad soyad
            sp.Children.Add(Step("② İş takip numarası ve ad soyad"));
            var g = new Grid { Margin = new Thickness(0, 4, 0, 12) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Field(_no, "İş no (ör. 00056299)"); Field(_nm, "Ad Soyad");
            Grid.SetColumn(_no, 0); Grid.SetColumn(_nm, 2);
            g.Children.Add(_no); g.Children.Add(_nm);
            sp.Children.Add(g);

            // ③ en son: kurtarılacak kaynak disk
            sp.Children.Add(Step("③ Hangi diski kurtaralım? (imaj alınacak kaynak disk)"));
            _disk.Height = 32; _disk.Margin = new Thickness(0, 4, 0, 4); _disk.VerticalContentAlignment = VerticalAlignment.Center;
            foreach (var d in disks) if (!string.IsNullOrEmpty(d.DeviceId) && d.SizeBytes > 0) _disk.Items.Add(new Row(d));
            if (_disk.Items.Count > 0) _disk.SelectedIndex = 0;
            _disk.SelectionChanged += (_, _) => Validate();
            sp.Children.Add(_disk);

            _warn.Foreground = Theme.Brush("Bad"); _warn.FontSize = 12; _warn.TextWrapping = TextWrapping.Wrap; _warn.Margin = new Thickness(0, 6, 0, 0);
            sp.Children.Add(_warn);

            var ok = Btn("İşi Aç →"); ok.Background = Theme.Brush("Accent"); ok.Foreground = Brushes.White; ok.BorderThickness = new Thickness(0);
            ok.Margin = new Thickness(0, 14, 0, 0); ok.HorizontalAlignment = HorizontalAlignment.Right; ok.IsDefault = true;
            ok.Click += (_, _) => Ok();
            sp.Children.Add(ok);

            _folder.TextChanged += (_, _) => Validate();
            Content = sp; _no.Focus();
            Validate();
        }

        private class Row
        {
            public DiskInfo D;
            public Row(DiskInfo d) { D = d; }
            public override string ToString() => D.Model + "  ·  " + D.SizeText + (D.HealthPercent >= 0 ? "  · sağlık %" + D.HealthPercent : "");
        }

        private TextBlock Step(string t) => new() { Text = t, Foreground = Theme.Brush("Text"), FontWeight = FontWeights.SemiBold, FontSize = 12.5, Margin = new Thickness(0, 6, 0, 0) };
        private void Field(TextBox t, string hint)
        {
            t.Height = 30; t.VerticalContentAlignment = VerticalAlignment.Center;
            t.Background = Theme.Brush("Card"); t.Foreground = Theme.Brush("Text"); t.BorderBrush = Theme.Brush("Line");
            t.ToolTip = hint; t.Tag = hint;
        }
        private Button Btn(string t) => new()
        {
            Content = t, Height = 30, Padding = new Thickness(14, 0, 14, 0),
            Background = Theme.Brush("Btn"), Foreground = Theme.Brush("Text"), BorderBrush = Theme.Brush("Line"), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand
        };

        private void Browse()
        {
            var d = new Microsoft.Win32.OpenFolderDialog { Title = "Kaydetme klasörü" };
            if (d.ShowDialog() == true) { _folder.Text = d.FolderName; Validate(); }
        }

        // imaj kaynak diske yazılamaz + yer kontrolü
        private bool Validate()
        {
            _warn.Text = "";
            if (_disk.SelectedItem is not Row r) { return false; }
            if (_folder.Text.Length == 0) { return false; }
            try
            {
                var dst = DiskGuard.PhysicalDriveNumbers(_folder.Text);
                int src = DriveNo(r.D.DeviceId);
                if (src >= 0 && dst.Contains(src))
                {
                    _warn.Text = "✕ Kaydetme klasörü KAYNAK DİSKİN ÜZERİNDE. İmajı kaynağa yazamazsın — başka bir disk/klasör seç.";
                    return false;
                }
            }
            catch { }
            return true;
        }

        static int DriveNo(string id)
        {
            var m = System.Text.RegularExpressions.Regex.Match(id ?? "", @"PHYSICALDRIVE(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        private void Ok()
        {
            if (_no.Text.Trim().Length == 0) { _warn.Text = "✕ İş takip numarası gerekli."; return; }
            if (_folder.Text.Length == 0) { _warn.Text = "✕ Kaydetme klasörü seç."; return; }
            if (_disk.SelectedItem is not Row r) { _warn.Text = "✕ Disk seç."; return; }
            if (!Validate()) return;
            Number = _no.Text.Trim(); PersonName = _nm.Text.Trim(); Folder = _folder.Text; Source = r.D;
            DialogResult = true;
        }
    }
}
