using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace VKM.App
{
    // Ana ekranda bir diskin bölümü (girmeden görünür)
    public class PartInfo : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string SizeText { get; set; } = "";
        public string DeviceId { get; set; } = "";
        public long Offset { get; set; }
        public long Length { get; set; }
        public int SectorSize { get; set; } = 512;
        public DiskInfo? Owner { get; set; }

        private string _fsLabel = "taranıyor…";
        public string FsLabel { get => _fsLabel; set { _fsLabel = value; OnChanged(nameof(FsLabel)); } }

        private Brush _fsColor = Brushes.Gray;
        public Brush FsColor { get => _fsColor; set { _fsColor = value; OnChanged(nameof(FsColor)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // Bir diskin ekranda gösterilecek bilgileri
    public class DiskInfo : INotifyPropertyChanged
    {
        public ObservableCollection<PartInfo> Partitions { get; } = new();

        // Disk başka bir pencerede işlemdeyken kilit (PC-3000 mantığı)
        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                OnChanged(nameof(IsBusy));
                OnChanged(nameof(IsNotBusy));
                OnChanged(nameof(BusyText));
                OnChanged(nameof(BusyVisibility));
            }
        }
        public bool IsNotBusy => !_isBusy;
        public string BusyText => _isBusy ? "✕" : "";
        public Visibility BusyVisibility => _isBusy ? Visibility.Visible : Visibility.Collapsed;

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public string Model { get; set; } = "";
        public string Pnp { get; set; } = "";   // SMART eşleşmesi için PNPDeviceID
        public string Serial { get; set; } = "";
        public string Interface { get; set; } = "";
        public string SizeText { get; set; } = "";

        // Diske erişim için teknik bilgiler
        public string DeviceId { get; set; } = "";     // \\.\PHYSICALDRIVE0
        public ulong SizeBytes { get; set; } = 0;
        public int BytesPerSector { get; set; } = 512;

        // Sağlık özeti
        public string Health { get; set; } = "";
        public Brush HealthColor { get; set; } = Brushes.Gray;

        public string SmartLine { get; set; } = "";
        public bool IsRisky { get; set; }            // SMART: bekleyen/yeniden atanmış/düzeltilemeyen sektör var
        public string RiskDetail { get; set; } = ""; // uyarı metni için kısa özet

        public int HealthPercent { get; set; } = -1;
        public string HealthPercentText => HealthPercent >= 0 ? "%" + HealthPercent : "";
        public double HealthBarWidth { get; set; } = 0;
        public Visibility BarVisibility => HealthPercent >= 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
