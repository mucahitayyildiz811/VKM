using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;

namespace VKM.App
{
    // Sol taraftaki klasör ağacında bir düğüm (+/- ile açılır, tembel yüklenir)
    public class TreeNode : INotifyPropertyChanged
    {
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDummy { get; set; }                 // sadece '+' görünsün diye yer tutucu
        public bool Loaded { get; set; }                  // alt klasörler yüklendi mi
        public ObservableCollection<TreeNode> Children { get; } = new();
        public ImageSource? IconImage => ShellIcon.Get(true, Name);

        private bool _expanded;
        public bool IsExpanded { get => _expanded; set { _expanded = value; OnChanged(nameof(IsExpanded)); } }

        // Ağaçta klasör boyutu (GB) — veri nerede, nokta atışı
        private string _sizeText = "";
        public string SizeText { get => _sizeText; set { _sizeText = value; OnChanged(nameof(SizeText)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    // Dosya/klasör listesindeki bir satır
    public class FsEntry : INotifyPropertyChanged
    {
        private bool _isChecked;
        public bool IsChecked { get => _isChecked; set { _isChecked = value; OnChanged(nameof(IsChecked)); } }  // kaydetmek için seçim
        public string Name { get; set; } = "";
        public string FullPath { get; set; } = "";
        public bool IsDir { get; set; }

        private string _sizeText = "";
        public string SizeText { get => _sizeText; set { _sizeText = value; OnChanged(nameof(SizeText)); } }  // klasör boyutu arka planda dolar
        public long SizeBytes { get; set; }               // sıralama için sayısal boyut
        public string TypeText { get; set; } = "";

        // Oluşturma / değiştirme tarihleri (PC-3000 gibi ayrı)
        public System.DateTime CreatedValue { get; set; }
        public System.DateTime ModifiedValue { get; set; }
        public string CreatedText => Fmt(CreatedValue);
        public string ModifiedText => Fmt(ModifiedValue);

        private static string Fmt(System.DateTime d) =>
            d.Year > 1601 ? d.ToString("dd.MM.yyyy HH:mm") : "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        public Brush RowColor { get; set; } = Theme.Brush("Text");  // normal = tema yazısı; silinenler kırmızı/amber
        public MftFile? Deleted { get; set; }                 // silinmiş dosya ise dolu

        // Hikvision/ham kayıt: diskte mutlak offset+uzunluk (dosya sistemi dışı)
        public bool IsRaw { get; set; }
        public long RawOffset { get; set; }
        public long RawLength { get; set; }
        public bool IsVideo { get; set; }   // video kaydı: kaydederken kodeke göre çıkar
        public string Codec { get; set; } = "";   // "h264" | "h265" | "mjpeg" (boş = Hikvision/varsayılan h264)
        public object? Tag { get; set; }     // carving kaydı için CarvedRec vb. taşır

        public string SubText { get; set; } = "";   // alt satır (ör. silinmişin klasör yolu)
        public bool HasSub => !string.IsNullOrEmpty(SubText);

        // Windows'un gerçek dosya-tipi ikonu (Office/PDF vb.)
        public ImageSource? IconImage => ShellIcon.Get(IsDir, Name);
    }

    // Üstteki tıklanabilir yol çubuğunda bir parça (Kök \ Users \ Desktop)
    public class Crumb
    {
        public string Text { get; set; } = "";
        public string Path { get; set; } = "";   // tıklanınca gidilecek klasör yolu
    }

    // Sol paneldeki bir bölüm (partition)
    public class PartItem
    {
        public int Index { get; set; }
        public string Label { get; set; } = "";
        public object? Partition { get; set; }        // DiscUtils.Partitions.PartitionInfo
        public bool WholeDisk { get; set; }            // bölümsüz (tüm disk)
        public System.Func<System.IO.Stream>? Open { get; set; }  // bu birim için akış açar
    }
}
