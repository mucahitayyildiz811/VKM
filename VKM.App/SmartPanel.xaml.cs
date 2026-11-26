using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;

namespace VKM.App
{
    public partial class SmartPanel : UserControl
    {
        private ObservableCollection<DiskInfo>? _disks;

        public SmartPanel()
        {
            InitializeComponent();
        }

        public void Init(ObservableCollection<DiskInfo> disks)
        {
            _disks = disks;
            DiskCombo.ItemsSource = disks;
            if (disks.Count > 0 && DiskCombo.SelectedIndex < 0) DiskCombo.SelectedIndex = 0;
            else Load();
        }

        private void Disk_Changed(object sender, SelectionChangedEventArgs e) => Load();
        private void Refresh_Click(object sender, RoutedEventArgs e) => Load();

        private void Reset_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "SMART özniteliklerini (reallocated/pending gibi) gerçekten sıfırlamak ÜRETİCİ-ÖZEL komut ister " +
                "(WD/Seagate/Toshiba… her biri farklı). Bu, SATA üzerinden VSC / ROM-SA kanalıyla aile aile eklenecek — " +
                "bir sonraki adım. Şu an panel tüm SMART değerlerini okuyup gösteriyor.",
                "SMART sıfırla", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void Load()
        {
            if (AttrList == null) return;
            AttrList.ItemsSource = null;
            if (DiskCombo.SelectedItem is not DiskInfo d) { SummaryText.Text = ""; return; }

            var map = SmartReader.ReadAll();
            map.TryGetValue(SmartReader.Norm(d.Pnp), out SmartData? sd);
            if (sd == null || !sd.HasData || sd.Attrs.Count == 0)
            {
                SummaryText.Text = "SMART okunamadı — yönetici olarak çalıştır, ya da bu disk/USB köprüsü SMART geçirmiyor.";
                return;
            }

            AttrList.ItemsSource = sd.Attrs;
            string poh = sd.PowerOnHours.HasValue ? sd.PowerOnHours.Value.ToString("N0") + " saat" : "-";
            string temp = sd.TempC.HasValue ? sd.TempC.Value + " °C" : "-";
            SummaryText.Text =
                "Durum: " + (sd.PredictFailure ? "⚠ ARIZA TAHMİNİ" : "✓ Normal") +
                "      Çalışma: " + poh + "      Sıcaklık: " + temp + "\n" +
                "Yeniden atanan (5): " + (sd.Reallocated?.ToString() ?? "-") +
                "      Bekleyen (197): " + (sd.Pending?.ToString() ?? "-") +
                "      Düzeltilemez (198): " + (sd.Uncorrectable?.ToString() ?? "-") +
                "      CRC (199): " + (sd.Crc?.ToString() ?? "-");
        }
    }
}
