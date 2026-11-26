using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using DiscUtils;
using DiscUtils.Btrfs;
using DiscUtils.Ext;
using DiscUtils.ExFat;
using DiscUtils.Fat;
using DiscUtils.HfsPlus;
using DiscUtils.Ntfs;
using DiscUtils.Streams;
using DiscUtils.Xfs;

namespace VKM.App
{
    public partial class RecoveryWindow : System.Windows.Controls.UserControl
    {
        public string TabTitle { get; private set; } = "Disk";

        // CCTV modülünden açılınca disk açılır açılmaz içerik-tabanlı video taraması başlasın
        public bool AutoVideoScan { get; set; }

        private DiscFileSystem? _fs;        // NTFS veya FAT
        private Stream? _volStream;
        private bool _currentIsNtfs;
        private string _currentFsName = "";
        private string _currentPath = "";
        private int _clusterSize = 4096;
        private Func<Stream>? _openVolume;
        private bool _suppressSelect;
        private string _lastErr = "";

        // Kaynak: fiziksel disk veya imaj dosyası — ikisi de ham akış üretir
        private Func<int, Stream> _openRaw = _ => Stream.Null;
        private long _totalSize;

        // Bu disk için katalog dosyasının yolu (LocalAppData\VKM).
        // Diskin KENDİ imzasıyla (başlangıç bölgesinin özeti) + boyutla anahtarlanır →
        // aynı boyutta birden çok disk olsa bile her disk kendi kataloğunu kullanır.
        // Kaynak diske yazılmaz, oturumlar arası kalıcı.
        private string CatalogPath()
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VKM", "katalog");
            Directory.CreateDirectory(dir);

            string sig = "nosig";
            try
            {
                // Diskin ilk 64 KB'ı (DHFS başlık + DPT + benzersiz birim özeti) → kısa imza
                using Stream s = _openRaw(1 << 16);
                s.Position = 0;
                byte[] head = new byte[64 * 1024];
                int got = 0, r;
                while (got < head.Length && (r = s.Read(head, got, head.Length - got)) > 0) got += r;
                using var md5 = System.Security.Cryptography.MD5.Create();
                byte[] h = md5.ComputeHash(head, 0, got);
                sig = BitConverter.ToString(h, 0, 6).Replace("-", "");   // 12 hex
            }
            catch { }

            return Path.Combine(dir, "dhfs_" + sig + "_" + _totalSize + ".vkmcat");
        }
        private int _reportedSector = 512;
        private string _sourceLabel = "";
        private int[] _sourceDriveNos = Array.Empty<int>();   // kaynağın bulunduğu fiziksel disk(ler): kaynağa yazmayı engelle
        private bool _riskyDisk;
        private string _riskDetail = "";

        public ObservableCollection<FsEntry> Entries { get; } = new();  // ekranda görünen (süzülmüş)
        public ObservableCollection<PartItem> Parts { get; } = new();
        public ObservableCollection<Crumb> Crumbs { get; } = new();     // üstteki tıklanabilir yol
        public ObservableCollection<TreeNode> RootNodes { get; } = new(); // sol klasör ağacı
        private readonly List<FsEntry> _all = new();                    // süzme öncesi tam liste

        // DHFS (ve benzeri) için SANAL klasör modeli — _fs olmadan Root→CAM→gün→.dav ağacı
        private bool _vmode;
        private readonly Dictionary<string, List<FsEntry>> _vdir = new();   // klasör yolu → içindekiler (klasör+dosya)

        private System.Threading.CancellationTokenSource? _searchCts;
        private bool _searching;

        public RecoveryWindow(DiskInfo info)
        {
            InitializeComponent();
            CommonInit();
            _openRaw = ss => PhysicalDisk.OpenReadOnly(info.DeviceId, (long)info.SizeBytes, ss);
            _totalSize = (long)info.SizeBytes;
            _reportedSector = info.BytesPerSector;
            int dn = ParseDriveNo(info.DeviceId);
            _sourceDriveNos = dn >= 0 ? new[] { dn } : Array.Empty<int>();
            _riskyDisk = info.IsRisky;
            _riskDetail = info.RiskDetail;
            _sourceLabel = info.Model + "   ·   " + info.SizeText;
            TabTitle = info.Model;
            TitleText.Text = _sourceLabel;
            Loaded += (_, _) => { RetryStream.Reset(); if (_riskyDisk) WarnRisky(); OpenDisk(); if (AutoVideoScan) Video_Click(this, new RoutedEventArgs()); };
        }

        // İmaj dosyası aç (.img/.dd/.001/.bin/.raw/.vhd/.vhdx/.vmdk/.vdi)
        public RecoveryWindow(string imagePath)
        {
            InitializeComponent();
            CommonInit();

            string name = Path.GetFileName(imagePath);
            long size;
            string ext = Path.GetExtension(imagePath).ToLowerInvariant();
            if (ext is ".vhd" or ".vhdx" or ".vmdk" or ".vdi")
            {
                RegisterContainers();  // sanal disk sağlayıcılarını kaydet
                // Sanal disk: içerik akışını DiscUtils verir (akış kapanınca disk de kapanır)
                using (var vd0 = DiscUtils.VirtualDisk.OpenDisk(imagePath, FileAccess.Read))
                    size = vd0.Capacity;
                _openRaw = _ =>
                {
                    var vd = DiscUtils.VirtualDisk.OpenDisk(imagePath, FileAccess.Read);
                    return new OwningStream(vd.Content, vd);
                };
            }
            else
            {
                // Ham imaj: dosyanın kendisi diskin baytlarıdır.
                // Başka program (UFS vb.) açık tutsa bile okuyabilmek için paylaşımlı aç.
                size = new FileInfo(imagePath).Length;
                _openRaw = _ => new FileStream(imagePath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete);
            }

            _totalSize = size;
            _reportedSector = 512;
            // İmaj bir kopya olduğundan 'kaynağa yazma' engeli uygulanmaz (aynı diske kaydetmek sorun değil).
            // Yalnızca yer kontrolü yapılır. _sourceDriveNos boş kalır.
            _sourceLabel = name + "   ·   " + FormatSize((ulong)size) + "  (imaj)";
            TabTitle = name;
            TitleText.Text = _sourceLabel;
            Loaded += (_, _) => { RetryStream.Reset(); OpenDisk(); if (AutoVideoScan) Video_Click(this, new RoutedEventArgs()); };
        }

        // Sanal aygıt aç (RAID / LVM birimi / RAID içindeki bölüm) — RAID modülünden gelir
        public RecoveryWindow(Func<Stream> open, long size, string title, int[]? sourceDrives = null)
        {
            InitializeComponent();
            CommonInit();
            _openRaw = _ => open();
            _totalSize = size;
            _reportedSector = 512;
            _sourceLabel = title + "   ·   " + FormatSize((ulong)size) + "  (sanal RAID)";
            _sourceDriveNos = sourceDrives ?? Array.Empty<int>();   // RAID üyelerine kayıt yapılmasın
            TabTitle = title;
            TitleText.Text = _sourceLabel;
            Loaded += (_, _) => { RetryStream.Reset(); OpenDisk(); };
        }

        // DiscUtils sanal disk (VHD/VHDX/VMDK/VDI) sağlayıcılarını çalışma anında kaydet (yansıma ile — derleme bağımlılığı yok)
        private static bool _containersRegistered;
        private static void RegisterContainers()
        {
            if (_containersRegistered) return;
            _containersRegistered = true;
            try
            {
                var t = Type.GetType("DiscUtils.Containers.SetupHelper, DiscUtils.Containers");
                t?.GetMethod("SetupContainers")?.Invoke(null, null);
            }
            catch { }
            // Yedek: bilinen modülleri tek tek kaydetmeyi dene
            foreach (string asm in new[] { "DiscUtils.Vhd", "DiscUtils.Vhdx", "DiscUtils.Vmdk", "DiscUtils.Vdi" })
            {
                try
                {
                    var a = System.Reflection.Assembly.Load(asm);
                    var sh = Type.GetType("DiscUtils.Setup.SetupHelper, DiscUtils.Core");
                    sh?.GetMethod("RegisterAssembly", new[] { typeof(System.Reflection.Assembly) })?.Invoke(null, new object[] { a });
                }
                catch { }
            }
        }

        // "\\.\PHYSICALDRIVE2" → 2 (bulunamazsa -1)
        private static int ParseDriveNo(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return -1;
            int i = deviceId.Length - 1;
            while (i >= 0 && char.IsDigit(deviceId[i])) i--;
            string digits = deviceId.Substring(i + 1);
            return int.TryParse(digits, out int n) ? n : -1;
        }

        // Riskli disk: taramadan önce imaj almayı öner
        private void WarnRisky()
        {
            MessageBox.Show(
                "DİKKAT — bu disk SMART'a göre RİSKLİ:\n" + _riskDetail + "\n\n" +
                "Riskli diskte tarama, diski daha da yıpratabilir. En güvenlisi ÖNCE bir imaj (kopya) alıp " +
                "imaj üzerinden çalışmaktır.\n\n" +
                "Yine de tarama hatalı sektörleri atlayıp devam eder (bad-sektörler sıfırlanır ve sayılır).",
                "Önce imaj al", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void CommonInit()
        {
            FileList.ItemsSource = Entries;
            PartList.ItemsSource = Parts;
            Breadcrumb.ItemsSource = Crumbs;
            FolderTree.ItemsSource = RootNodes;
            UpdateHeaderArrows();
        }

        // Bölüm tablosunu KENDİMİZ okur (GPT/MBR), sektör boyutlarını sırayla dener
        private async void OpenDisk()
        {
            _vmode = false;              // varsayılan: normal (DHFS bulunursa aşağıda açılır)
            var diag = new StringBuilder();
            diag.AppendLine("Kaynak: " + _sourceLabel + "  ·  raporlanan sektör: " + _reportedSector + " B");

            foreach (int ss in DistinctSectorSizes(_reportedSector))
            {
                try
                {
                    List<DiskPartition> parts;
                    using (Stream probe = _openRaw(ss))
                        parts = PartitionReader.Read(probe, ss);

                    var realItems = new List<PartItem>();
                    int n = 1;
                    foreach (DiskPartition pp in parts)
                    {
                        DiskPartition cur = pp;
                        realItems.Add(new PartItem
                        {
                            Index = n,
                            Label = pp.Name + "  (" + FormatSize((ulong)pp.Length) + ")",
                            Open = () => new SubStream(_openRaw(ss), cur.Offset, cur.Length)
                        });
                        n++;
                    }

                    diag.AppendLine("Sektör " + ss + " B: " + parts.Count + " bölüm bulundu");

                    Parts.Clear();
                    foreach (PartItem pi in realItems) Parts.Add(pi);

                    PartItem? chosen = null;
                    foreach (PartItem pi in realItems)
                    {
                        if (OpenPartItem(pi)) { chosen = pi; break; }
                        diag.AppendLine("   · " + pi.Label + " → " + _lastErr);
                    }

                    // Bölümlerden açılan olmadıysa (ör. bölümsüz NTFS disk) tüm diski dene
                    if (chosen == null)
                    {
                        var whole = new PartItem
                        {
                            Index = 0,
                            Label = "Tüm disk (bölümsüz)",
                            WholeDisk = true,
                            Open = () => _openRaw(ss)
                        };
                        if (OpenPartItem(whole)) { Parts.Add(whole); chosen = whole; }
                        else diag.AppendLine("   · Tüm disk → " + _lastErr);
                    }

                    if (chosen != null)
                    {
                        _suppressSelect = true;
                        PartList.SelectedItem = chosen;
                        _suppressSelect = false;
                        Status.Text = Entries.Count + " öğe  ·  " + parts.Count + " bölüm  ·  " + _currentFsName +
                                      "  ·  sektör " + ss + " B. (Gerekirse soldan başka birim seç.)";
                        return;
                    }

                    Parts.Clear();
                }
                catch (Exception ex)
                {
                    diag.AppendLine("Sektör " + ss + " B: hata: " + Short(ex.Message));
                }
            }

            // Hikvision DVR (CCTV) mı? Öyleyse özel teşhis göster
            try
            {
                if (HikvisionReader.Detect(_openRaw))
                {
                    // Blok taraması UI'yi dondurmasın: arka planda çalış, EKRAN ORTASINDA % çubuk göster
                    ShowCenter("Hikvision blokları taranıyor…  (ilk açılış ~1-2 dk, disk okunuyor)");
                    long[] hprog = { 0 };
                    var hpt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    hpt.Tick += (_, __) => UpdateCenter(_totalSize > 0 ? (double)System.Threading.Interlocked.Read(ref hprog[0]) / _totalSize : 0);
                    hpt.Start();
                    HikInfo hik = await System.Threading.Tasks.Task.Run(() =>
                        HikvisionReader.Parse(_openRaw, _totalSize, p => System.Threading.Interlocked.Exchange(ref hprog[0], p)));
                    hpt.Stop(); HideCenter();
                    _all.Clear();
                    var red = new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x29)); red.Freeze(); // CCTV: amber
                    int ch = 1;
                    foreach (HikRecording r in hik.Recordings)
                    {
                        string nm = "CCTV " + r.Start.ToString("yyyy-MM-dd HH-mm-ss") + ".mp4";
                        _all.Add(new FsEntry
                        {
                            Name = nm,
                            IsDir = false,
                            TypeText = "Hikvision kayıt",
                            SizeText = FormatSize((ulong)r.Length),
                            SizeBytes = r.Length,
                            CreatedValue = r.Start,
                            ModifiedValue = r.End,
                            RowColor = red,
                            IsRaw = true,
                            IsVideo = true,
                            RawOffset = r.Offset,
                            RawLength = r.Length
                        });
                        ch++;
                    }
                    ApplyFilter();
                    SetLabelCrumb("(Hikvision DVR kayıtları)");
                    Parts.Clear();
                    Parts.Add(new PartItem { Index = 0, Label = "Hikvision DVR  (" + hik.Recordings.Count + " kayıt)", WholeDisk = true });

                    if (hik.Recordings.Count > 0)
                        Status.Text = "Hikvision DVR — " + hik.Recordings.Count +
                                      " kayıt DVR'ın kendi indeksinden ANINDA okundu (tarama yapılmadı). " +
                                      "İşaretle + 'Seçilenleri Kaydet' (.mp4 + .h264). " +
                                      "İndekste olmayan/silinmiş kayıtlar için yukarıdaki 'Video Tara (MP4)' ile tüm diski derin tara.";
                    else
                    {
                        Status.Text = "Hikvision DVR algılandı ama kayıt çıkarılamadı. Teşhis:";
                        MessageBox.Show(hik.Diagnostic, "Hikvision teşhis", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    return;
                }
            }
            catch { HideCenter(); }

            // Dahua DHFS (CCTV) mı? Öyleyse DVR'ın KENDİ indeksinden ANINDA listele (tarama yok)
            try
            {
                if (DhfsReader.Detect(_openRaw))
                {
                    SetLabelCrumb("(Dahua DHFS — index okunuyor…)");
                    // DVR'ın kendi indeksini oku — UI donmasın, ekran ortasında akan çubuk
                    ShowCenter("Dahua DHFS index okunuyor…  (DVR'ın kendi dizini, tarama yok)", indeterminate: true);
                    DhfsReader.DhfsResult dh = await System.Threading.Tasks.Task.Run(() =>
                        DhfsReader.Parse(_openRaw, _totalSize));

                    // İNDEKS BOŞSA (takılmış DVR): video sağlam ama dizin gitmiş →
                    // DHAV karelerini doğrudan tara, listeyi videodan yeniden kur.
                    bool carved = false;
                    if (dh.Files.Count == 0)
                    {
                        long tot = _totalSize;
                        _videoCts?.Dispose();
                        _videoCts = new System.Threading.CancellationTokenSource();
                        var ctok = _videoCts.Token;
                        ShowCenter("DHFS index boş → video haritalanıyor (hızlı örnekleme, takılmış DVR kurtarma)…", canStop: true, indeterminate: false);
                        var dh2 = await System.Threading.Tasks.Task.Run(() =>
                            DhfsReader.CarveDhav(_openRaw, _totalSize, (pos, found) =>
                            {
                                if (tot > 0)
                                    Dispatcher.Invoke(() =>
                                    {
                                        UpdateCenter((double)pos / tot);
                                        SetCenterInfo((pos / 1073741824.0).ToString("0.0") + " / " + (tot / 1073741824.0).ToString("0.0") + " GB · " + found + " saatlik kayıt bulundu");
                                    });
                            }, ctok));
                        _videoCts?.Dispose(); _videoCts = null;
                        if (dh2.Files.Count > 0) { dh = dh2; carved = true; }
                    }
                    HideCenter();

                    var amber = new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x29)); amber.Freeze();
                    _fs?.Dispose(); _fs = null;
                    _vmode = true;                                  // sanal klasör ağacı aç
                    CatalogButton.Visibility = Visibility.Visible;  // dağınık disk için kataloğa alma düğmesi
                    BuildDhfsVfs(dh, amber);                        // Root→CAM→gün→.dav
                    BuildVTree();                                  // sol klasör ağacı (+/-)
                    ShowDir("");                                   // kökü göster (kanal klasörleri)

                    Parts.Clear();
                    Parts.Add(new PartItem { Index = 0, Label = "Dahua DHFS  (" + dh.ChannelCount + " kanal, " + dh.Files.Count + " saatlik kayıt)", WholeDisk = true });

                    if (dh.Files.Count > 0 && carved)
                        Status.Text = "Dahua DHFS — İNDEKS BOŞTU (DVR takılmış), video içeriğinden kurtarıldı: " +
                                      dh.ChannelCount + " kanal · " + dh.Files.Count + " saatlik kayıt. " +
                                      "Aralık: " + dh.MinTime.ToString("yyyy-MM-dd HH:mm") + " → " + dh.MaxTime.ToString("yyyy-MM-dd HH:mm") + ". " +
                                      "İşaretle + 'Seçilenleri Kaydet'.";
                    else if (dh.Files.Count > 0)
                        Status.Text = "Dahua DHFS — " + dh.ChannelCount + " kanal · " + dh.TotalSegments.ToString("N0") +
                                      " segment · " + dh.Files.Count + " saatlik kayıt DVR'ın KENDİ indeksinden ANINDA okundu (tarama yok). " +
                                      "Aralık: " + dh.MinTime.ToString("yyyy-MM-dd") + " → " + dh.MaxTime.ToString("yyyy-MM-dd") + ". " +
                                      "İşaretle + 'Seçilenleri Kaydet' (içerikten .mp4). Silinmiş/bozuk için 'Video Tara' ile derin tara.";
                    else
                        Status.Text = "Dahua DHFS algılandı ama ne indeksten ne içerikten kayıt çıktı. " +
                                      (string.IsNullOrEmpty(dh.Diagnostic) ? "" : dh.Diagnostic) +
                                      " 'Video Tara' ile derin tarayabilirsin.";
                    return;
                }
            }
            catch { HideCenter(); }

            // "PP / uware" tipi DVR mı? (128 MB slot, PP başlığı, ham H.265) → slotları tara, ANINDA listele
            try
            {
                if (UwareDvrReader.Detect(_openRaw))
                {
                    ShowCenter("DVR slotları taranıyor…  (128 MB adım, DVR'ın kendi başlıkları — derin tarama yok)");
                    long[] uprog = { 0 };
                    var upt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                    upt.Tick += (_, __) => UpdateCenter(_totalSize > 0 ? (double)System.Threading.Interlocked.Read(ref uprog[0]) / _totalSize : 0);
                    upt.Start();
                    UwareInfo uw = await System.Threading.Tasks.Task.Run(() =>
                        UwareDvrReader.Parse(_openRaw, _totalSize, p => System.Threading.Interlocked.Exchange(ref uprog[0], p)));
                    upt.Stop(); HideCenter();

                    _all.Clear();
                    var amber2 = new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x29)); amber2.Freeze();
                    foreach (UwareRecording r in uw.Recordings)
                    {
                        string nm = r.Camera + " · " + r.Start.ToString("yyyy-MM-dd HH-mm-ss") + ".h265";
                        _all.Add(new FsEntry
                        {
                            Name = nm,
                            IsDir = false,
                            TypeText = "Uware kayıt",
                            SizeText = FormatSize((ulong)r.VideoLength),
                            SizeBytes = r.VideoLength,
                            CreatedValue = r.Start,
                            ModifiedValue = r.End,
                            RowColor = amber2,
                            IsRaw = true,
                            IsVideo = true,
                            RawOffset = r.VideoOffset,
                            RawLength = r.VideoLength
                        });
                    }
                    ApplyFilter();
                    SetLabelCrumb("(DVR kayıtları — PP/uware)");
                    Parts.Clear();
                    Parts.Add(new PartItem { Index = 0, Label = "DVR (PP/uware)  (" + uw.Recordings.Count + " kayıt)", WholeDisk = true });

                    if (uw.Recordings.Count > 0)
                        Status.Text = "DVR (PP/uware) — " + uw.Diagnostic +
                                      " DVR'ın kendi slot başlıklarından okundu (derin tarama yok). " +
                                      "Kamera adına göre sıralı; işaretle + 'Seçilenleri Kaydet' → ham .h265 (VLC oynatır).";
                    else
                        Status.Text = "DVR (PP/uware) imzası var ama slot bulunamadı. " + uw.Diagnostic;
                    return;
                }
            }
            catch { HideCenter(); }

            // Hikvision/DHFS değilse: imzadan başka DVR markası var mı? → kısa tarama, bulursa otomatik listele
            try
            {
                DvrBrand? brand = DvrDetect.Identify(_openRaw, _totalSize);
                if (brand != null)
                {
                    SetLabelCrumb("(" + brand.Name + " DVR)");
                    Parts.Clear();
                    Parts.Add(new PartItem { Index = 0, Label = brand.Name + " DVR tespit edildi", WholeDisk = true });
                    Status.Text = brand.Name + " DVR tespit edildi (imza: " + brand.Note + "). Kayıtlar içerikten otomatik listeleniyor…";
                    Video_Click(this, new RoutedEventArgs());   // marka bulundu → otomatik liste
                    return;
                }
            }
            catch { }

            // İlk sektörlerin hex'i — imajın ne olduğunu anlamak için
            try
            {
                byte[] b0 = new byte[512];
                byte[] b512 = new byte[512];
                using (Stream s = _openRaw(512))
                {
                    ReadFull(s, b0, 0);
                    ReadFull(s, b512, 512);
                }
                diag.AppendLine();
                diag.AppendLine("İlk sektör (offset 0), ilk 32 bayt:");
                diag.AppendLine(HexLine(b0, 0, 32));
                diag.AppendLine("MBR imzası (0x1FE): " + b0[510].ToString("X2") + " " + b0[511].ToString("X2") +
                                (b0[510] == 0x55 && b0[511] == 0xAA ? "  (55 AA var)" : "  (yok)"));
                string sig0 = System.Text.Encoding.ASCII.GetString(b0, 3, 8);
                diag.AppendLine("Offset 3 metin: \"" + Clean(sig0) + "\"  (NTFS/exFAT/MSDOS burada görünür)");
                string efi = System.Text.Encoding.ASCII.GetString(b512, 0, 8);
                diag.AppendLine("LBA1 (offset 512) metin: \"" + Clean(efi) + "\"  (GPT ise 'EFI PART')");
                diag.AppendLine();
                diag.AppendLine("Not: Tümü sıfır/rastgele ise imaj şifreli, RAID üyesi ya da farklı offsetli olabilir.");
            }
            catch (Exception ex) { diag.AppendLine("İlk sektör okunamadı: " + Short(ex.Message)); }

            Status.Text = "Okunabilir NTFS/FAT birim bulunamadı. Silinmiş değil, formatlı/FS'siz olabilir — " +
                          "yukarıdaki 'İmza Tara' (dosyalar) veya 'Video Tara' (CCTV) ile içerikten kurtar.";
            MessageBox.Show(diag.ToString(), "Teşhis — neden açılmadı", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ReadFull(Stream s, byte[] buf, long pos)
        {
            s.Position = pos;
            int off = 0;
            while (off < buf.Length)
            {
                int r = s.Read(buf, off, buf.Length - off);
                if (r <= 0) break;
                off += r;
            }
        }

        private static string HexLine(byte[] b, int start, int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count && start + i < b.Length; i++) sb.Append(b[start + i].ToString("X2")).Append(' ');
            return sb.ToString();
        }

        private static string Clean(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s) sb.Append(c >= 32 && c < 127 ? c : '.');
            return sb.ToString();
        }

        private static IEnumerable<int> DistinctSectorSizes(int reported)
        {
            var seen = new HashSet<int>();
            foreach (int s in new[] { reported <= 0 ? 512 : reported, 512, 4096 })
                if (seen.Add(s)) yield return s;
        }

        private bool OpenPartItem(PartItem pi)
        {
            _lastErr = "";
            if (pi.Open == null) { _lastErr = "opener yok"; return false; }

            Stream? s = null;
            try
            {
                s = pi.Open();

                DiscFileSystem? fs = VolumeMount.Open(s, out string fsName, out bool ntfs, out string err);
                if (fs == null)
                {
                    _lastErr = err;
                    s.Dispose();
                    return false;
                }

                _fs?.Dispose();
                _volStream?.Dispose();
                _fs = fs;
                _volStream = s;
                _openVolume = pi.Open;
                _currentIsNtfs = ntfs;
                _currentFsName = fsName;
                _vmode = false;              // gerçek dosya sistemi → sanal mod kapalı
                ShowDir("");
                BuildTree();
                return true;
            }
            catch (Exception e)
            {
                _lastErr = "akış: " + Short(e.Message);
                s?.Dispose();
                return false;
            }
        }


        private void PartList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (_suppressSelect) return;
            if (PartList.SelectedItem is not PartItem pi) return;
            if (!OpenPartItem(pi))
            {
                Entries.Clear();
                Status.Text = "Bu birim NTFS/FAT olarak okunamadı: " + _lastErr;
            }
        }

        private void ShowDir(string path)
        {
            if (_vmode) { ShowVDir(path); return; }     // DHFS sanal ağaç
            if (_fs == null) return;
            _all.Clear();
            _currentPath = path;
            BuildBreadcrumb(path);

            try
            {
                foreach (string d in _fs.GetDirectories(path))
                {
                    _all.Add(new FsEntry
                    {
                        Name = NameOf(d), FullPath = d, IsDir = true, SizeText = "…", TypeText = "Klasör",
                        CreatedValue = SafeDate(() => _fs.GetCreationTime(d)),
                        ModifiedValue = SafeDate(() => _fs.GetLastWriteTime(d))
                    });
                }

                foreach (string f in _fs.GetFiles(path))
                {
                    long len = 0;
                    try { len = _fs.GetFileLength(f); } catch { }
                    _all.Add(new FsEntry
                    {
                        Name = NameOf(f), FullPath = f, IsDir = false, SizeText = FormatSize((ulong)len),
                        SizeBytes = len, TypeText = "Dosya",
                        CreatedValue = SafeDate(() => _fs.GetCreationTime(f)),
                        ModifiedValue = SafeDate(() => _fs.GetLastWriteTime(f))
                    });
                }
                ApplyFilter();
                StartFolderSizes();   // klasör boyutlarını arka planda hesapla
            }
            catch (Exception ex)
            {
                Entries.Clear();
                Status.Text = "Klasör okunamadı: " + ex.Message;
            }
        }

        // ---- DHFS sanal klasör: yolu seçince içindekileri göster ----
        private void ShowVDir(string path)
        {
            _all.Clear();
            _currentPath = path;
            BuildBreadcrumb(path);
            if (_vdir.TryGetValue(path, out var items)) _all.AddRange(items);
            ApplyFilter();
        }

        // DHFS sonucundan sanal klasör ağacını kur: Root → CAMxx → gün → .dav dosyaları
        private void BuildDhfsVfs(DhfsReader.DhfsResult dh, Brush color)
        {
            _vdir.Clear();
            var camFolders = new SortedDictionary<string, FsEntry>(StringComparer.Ordinal);
            var camDates = new Dictionary<string, SortedDictionary<string, FsEntry>>();

            foreach (DhfsReader.DhfsFile f in dh.Files)
            {
                string cam = "CAM" + (f.Channel + 1).ToString("00");
                string date = f.Start.ToString("yyyy-MM-dd");
                string datePath = cam + "\\" + date;

                if (!camFolders.ContainsKey(cam))
                    camFolders[cam] = new FsEntry { Name = cam, FullPath = cam, IsDir = true, TypeText = "Kanal", SizeText = "", RowColor = color };

                if (!camDates.TryGetValue(cam, out var dates)) { dates = new SortedDictionary<string, FsEntry>(StringComparer.Ordinal); camDates[cam] = dates; }
                if (!dates.ContainsKey(date))
                    dates[date] = new FsEntry { Name = date, FullPath = datePath, IsDir = true, TypeText = "Gün", SizeText = "", RowColor = color };

                string nm = cam + "_" + f.Start.ToString("yyyyMMdd_HHmmss") + ".dav";
                var fe = new FsEntry
                {
                    Name = nm, FullPath = datePath + "\\" + nm, IsDir = false,
                    TypeText = "Dahua DHFS",
                    SizeText = FormatSize((ulong)Math.Max(0, f.Length)), SizeBytes = f.Length,
                    CreatedValue = f.Start, ModifiedValue = f.End, RowColor = color,
                    IsVideo = true, Codec = "", RawOffset = f.Offset, RawLength = f.Length
                };
                if (!_vdir.TryGetValue(datePath, out var lst)) { lst = new List<FsEntry>(); _vdir[datePath] = lst; }
                lst.Add(fe);
            }

            _vdir[""] = new List<FsEntry>(camFolders.Values);                       // kök = kanal klasörleri
            foreach (var kv in camDates) _vdir[kv.Key] = new List<FsEntry>(kv.Value.Values);  // kanal = gün klasörleri
        }

        // DHFS sanal ağacı için sol klasör ağacını kur (tembel yükleme)
        private void BuildVTree()
        {
            RootNodes.Clear();
            var root = new TreeNode { Name = "Root", FullPath = "" };
            RootNodes.Add(root);
            LoadVChildren(root);
            root.IsExpanded = true;
        }

        private void LoadVChildren(TreeNode node)
        {
            if (node.Loaded) return;
            node.Loaded = true;
            node.Children.Clear();
            if (_vdir.TryGetValue(node.FullPath, out var items))
                foreach (FsEntry en in items)
                    if (en.IsDir)
                    {
                        var child = new TreeNode { Name = en.Name, FullPath = en.FullPath };
                        // alt klasörü var mı? (gün klasörünün altı dosya; yine de '+' sadece klasör içerenlere)
                        if (_vdir.TryGetValue(en.FullPath, out var sub) && sub.Exists(x => x.IsDir))
                            child.Children.Add(new TreeNode { IsDummy = true });
                        node.Children.Add(child);
                    }
        }

        // ---- Klasör boyutlarını arka planda hesapla (UFS gibi GB göster) ----
        private System.Threading.CancellationTokenSource? _sizeCts;
        private bool _sizing;
        private int _dot;
        private System.Windows.Threading.DispatcherTimer? _dotTimer;

        // Sağ alt köşe: hesap sürüyorsa "Boyut hesaplanıyor...." + toplam, bitince "Boyut: X"
        private void RefreshSizeStatus()
        {
            long total = 0;
            foreach (FsEntry en in _all) total += en.SizeBytes;
            string sum = FormatSize((ulong)total);
            if (_sizing)
            {
                string dots = new string('.', _dot);           // 1..4 nokta
                SizeStatus.Text = "Boyut hesaplanıyor" + dots + "   " + sum;
            }
            else
            {
                SizeStatus.Text = _all.Count == 0 ? "" : "Boyut: " + sum;
            }
        }

        private async void StartFolderSizes()
        {
            _sizeCts?.Cancel();

            // Animasyon zamanlayıcısı (ilk kez kur)
            if (_dotTimer == null)
            {
                _dotTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(400) };
                _dotTimer.Tick += (_, __) => { _dot = (_dot % 4) + 1; RefreshSizeStatus(); };
            }

            if (_openVolume is null) { _sizing = false; _dotTimer.Stop(); RefreshSizeStatus(); return; }

            var dirEntries = new List<FsEntry>();
            foreach (FsEntry en in _all) if (en.IsDir) dirEntries.Add(en);

            // Klasör yoksa: sadece dosya toplamını göster
            if (dirEntries.Count == 0) { _sizing = false; _dotTimer.Stop(); RefreshSizeStatus(); return; }

            _sizeCts = new System.Threading.CancellationTokenSource();
            var tok = _sizeCts.Token;
            Func<Stream> open = _openVolume;

            _sizing = true; _dot = 1;
            _dotTimer.Start();
            RefreshSizeStatus();

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using Stream s = open();
                    DiscFileSystem? fs = VolumeMount.Open(s, out _, out _, out _);
                    if (fs == null) return;
                    foreach (FsEntry en in dirEntries)
                    {
                        if (tok.IsCancellationRequested) break;
                        FsEntry target = en;
                        // TreeSize gibi: hesaplanırken boyut canlı artsın
                        long size = DirSize(fs, en.FullPath, tok, partial =>
                        {
                            try { Dispatcher.Invoke(() => { if (!tok.IsCancellationRequested) { target.SizeBytes = partial; target.SizeText = FormatSize((ulong)partial); RefreshSizeStatus(); } }); }
                            catch { }
                        });
                        try { Dispatcher.Invoke(() => { if (!tok.IsCancellationRequested) { target.SizeBytes = size; target.SizeText = FormatSize((ulong)size); RefreshSizeStatus(); var tn = FindTreeNode(target.FullPath); if (tn != null) tn.SizeText = FormatSize((ulong)size); } }); }
                        catch { break; }
                    }
                    fs.Dispose();
                }, tok);
            }
            catch { }
            finally
            {
                // Sadece bu hesap hâlâ güncelse bitir (yeni klasöre geçilmediyse)
                if (!tok.IsCancellationRequested)
                {
                    _sizing = false;
                    _dotTimer.Stop();
                    RefreshSizeStatus();
                }
            }
        }

        // Bir klasörün toplam boyutu (alt klasörler dâhil) — ilerledikçe 'progress' ile canlı bildirir
        private static long DirSize(DiscFileSystem fs, string path, System.Threading.CancellationToken tok, Action<long> progress)
        {
            long total = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long lastMs = 0;
            var stack = new Stack<string>();
            stack.Push(path);
            while (stack.Count > 0)
            {
                if (tok.IsCancellationRequested) break;
                string d = stack.Pop();
                try
                {
                    foreach (string f in fs.GetFiles(d))
                    {
                        try { total += fs.GetFileLength(f); } catch { }
                        if (sw.ElapsedMilliseconds - lastMs > 250)   // saniyede ~4 güncelleme
                        {
                            lastMs = sw.ElapsedMilliseconds;
                            progress(total);
                        }
                    }
                }
                catch { }
                try { foreach (string sd in fs.GetDirectories(d)) stack.Push(sd); } catch { }
            }
            return total;
        }

        // Yüklenmiş ağaçta yolu bul (klasör boyutunu ağaçta da göstermek için)
        private TreeNode? FindTreeNode(string path)
        {
            var st = new Stack<TreeNode>();
            foreach (var r in RootNodes) st.Push(r);
            while (st.Count > 0)
            {
                var n = st.Pop();
                if (!n.IsDummy && string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase)) return n;
                foreach (var c in n.Children) st.Push(c);
            }
            return null;
        }

        // ---- Sol klasör ağacı (tembel yükleme) ----
        private void BuildTree()
        {
            RootNodes.Clear();
            var root = new TreeNode { Name = "Root", FullPath = "" };
            RootNodes.Add(root);
            LoadChildren(root);      // ilk seviyeyi hemen yükle
            root.IsExpanded = true;
        }

        private void LoadChildren(TreeNode node)
        {
            if (node.Loaded || _fs == null) return;
            node.Loaded = true;
            node.Children.Clear();   // yer tutucu '+' düğümünü temizle
            try
            {
                foreach (string d in _fs.GetDirectories(node.FullPath))
                {
                    var child = new TreeNode { Name = NameOf(d), FullPath = d };
                    child.Children.Add(new TreeNode { IsDummy = true });  // '+' görünsün
                    node.Children.Add(child);
                }
            }
            catch { }
        }

        private void Tree_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is System.Windows.Controls.TreeViewItem tvi && tvi.DataContext is TreeNode node)
            {
                if (_vmode) LoadVChildren(node); else LoadChildren(node);
            }
        }

        private void Tree_SelectedChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is TreeNode node && !node.IsDummy)
                ShowDir(node.FullPath);
        }

        // Üstteki tıklanabilir yol çubuğunu kur (Kök › Users › Desktop …)
        private void BuildBreadcrumb(string path)
        {
            Crumbs.Clear();
            Crumbs.Add(new Crumb { Text = "Root", Path = "" });
            if (!string.IsNullOrEmpty(path))
            {
                string cum = "";
                foreach (string p in path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                {
                    cum = cum.Length == 0 ? p : cum + "\\" + p;
                    Crumbs.Add(new Crumb { Text = p, Path = cum });
                }
            }
        }

        // Özel mod (arama/silinmiş) için tek, tıklanınca köke dönen etiket
        private void SetLabelCrumb(string text)
        {
            Crumbs.Clear();
            Crumbs.Add(new Crumb { Text = "Root", Path = "" });
            Crumbs.Add(new Crumb { Text = text, Path = "" });
        }

        // Yol çubuğundaki bir parçaya tıklanınca o klasöre git
        private void Crumb_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.Tag is string p)
                ShowDir(p);
        }

        // Arama kutusu / 'sadece silinmiş' değişince listeyi süz
        private void Filter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

        // ---- İmza (signature) tabanlı kurtarma — tüm diski/imajı tarar ----
        private System.Threading.CancellationTokenSource? _carveCts;
        private bool _carving;

        private async void Carve_Click(object sender, RoutedEventArgs e)
        {
            if (_carving) { _carveCts?.Cancel(); CarveButton.Content = "Durduruluyor…"; return; }

            Func<int, Stream> open = _openRaw;
            long len = _totalSize;
            if (len <= 0) { Status.Text = "Kaynak boyutu bilinmiyor."; return; }

            _carving = true;
            _carveCts = new System.Threading.CancellationTokenSource();
            var tok = _carveCts.Token;
            CarveButton.Content = "■ Durdur";
            OnlyDeleted.IsChecked = false;
            Status.Text = "İmza taraması başladı… (tüm disk/imaj taranır, sürebilir)";
            ScanBegin("İmza taraması — tüm disk içerikten taranıyor");

            try
            {
                List<CarvedFile> found = await System.Threading.Tasks.Task.Run(() =>
                    Carver.Scan(open, len, tok, (p, c) =>
                    {
                        try { Dispatcher.Invoke(() => ScanReport(p, c, "dosya")); }
                        catch { }
                    }), tok);

                var purple = new SolidColorBrush(Color.FromRgb(0xA7, 0x8B, 0xFA)); purple.Freeze();
                _all.Clear();
                int n = 1;
                foreach (CarvedFile cf in found)
                {
                    _all.Add(new FsEntry
                    {
                        Name = "carved_" + n.ToString("D5") + "." + cf.Ext,
                        IsDir = false,
                        TypeText = "Carved " + cf.Ext.ToUpperInvariant(),
                        SizeText = FormatSize((ulong)cf.Length),
                        SizeBytes = cf.Length,
                        RowColor = purple,
                        IsRaw = true,
                        RawOffset = cf.Offset,
                        RawLength = cf.Length
                    });
                    n++;
                }
                ApplyFilter();
                SetLabelCrumb("(imza taraması)");
                bool stopped = tok.IsCancellationRequested;
                ScanEnd(stopped, found.Count, "dosya");
                Status.Text = (stopped ? "Durduruldu — " : "") + found.Count +
                              " dosya imzadan kurtarıldı. İşaretle ve 'Seçilenleri Kaydet'.";
            }
            catch (Exception ex) { Status.Text = "İmza tarama hatası: " + ex.Message; ScanEnd(true, 0, "dosya"); }
            finally
            {
                _carving = false;
                _carveCts?.Dispose();
                _carveCts = null;
                CarveButton.Content = "İmza Tara";
            }
        }

        // ---- Evrensel video kurtarma (H.264 → .mp4) ----
        private System.Threading.CancellationTokenSource? _videoCts;
        private bool _videoScanning;

        // ---- UFS tarzı tarama ilerleme çubuğu (yeşil, %, geçen süre, kalan/ETA) ----
        private readonly System.Diagnostics.Stopwatch _scanSw = new();

        // Kaydetme ilerlemesi: yazıcılar işledikleri baytı buraya bildirir (gerçek MB + hız)
        private Action<long>? _onCopyBytes;

        private void ScanBegin(string what)
        {
            _scanSw.Restart();
            if (SelectAll != null) SelectAll.IsChecked = false;
            if (StopButton != null) StopButton.Visibility = Visibility.Visible;
            ScanPanel.Visibility = Visibility.Visible;
            ScanBar.Value = 0;
            ScanPct.Text = "%0";
            ScanInfo.Text = what;
        }

        // frac 0..1 ; her ilerleme bildiriminde çağrılır (UI iş parçacığından)
        private void ScanReport(double frac, int count, string noun)
        {
            if (frac < 0) frac = 0; if (frac > 1) frac = 1;
            double pf = frac * 100;
            ScanBar.Value = pf;
            // Büyük diskte % çok yavaş artar — küçükken ondalık göster (0'da takılı görünmesin)
            ScanPct.Text = pf < 10 ? "%" + pf.ToString("0.0") : "%" + (int)pf;

            var el = _scanSw.Elapsed;
            double secs = el.TotalSeconds;
            long total = _totalSize;
            long read = (long)(frac * total);
            string speed = secs > 0.5 ? FmtSpeed(read / secs) : "—";

            string eta = "";
            if (frac > 0.01 && secs > 0.5)
            {
                var rem = TimeSpan.FromSeconds(secs / frac) - el;
                if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
                eta = "  ·  kalan " + FmtDur(rem);
            }

            ScanInfo.Text = count + " " + noun
                + "  ·  " + FormatSize((ulong)read) + " / " + FormatSize((ulong)total)
                + "  ·  " + speed
                + "  ·  geçen " + FmtDur(el) + eta;
        }

        private static string FmtSpeed(double bytesPerSec)
        {
            double mb = bytesPerSec / (1024.0 * 1024.0);
            if (mb >= 1024) return (mb / 1024.0).ToString("0.0") + " GB/sn";
            return mb.ToString("0") + " MB/sn";
        }

        private void ScanEnd(bool stopped, int count, string noun)
        {
            if (!stopped) { ScanBar.Value = 100; ScanPct.Text = "%100"; }
            ScanInfo.Text = (stopped ? "durduruldu · " : "bitti · ") + count + " " + noun + "  ·  süre " + FmtDur(_scanSw.Elapsed);
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(stopped ? 2.5 : 4) };
            t.Tick += (_, __) => { t.Stop(); ScanPanel.Visibility = Visibility.Collapsed; };
            t.Start();
        }

        // Ekran ortasında büyük yeşil yüzdelik çubuk — TÜM uzun işlemler için
        // (Hikvision + Dahua tarama, kaydetme/çıkarma, format dökümü).
        // canStop: kartın içinde "■ Durdur" düğmesi gösterir (iptal edilebilen işlemler).
        // indeterminate: yüzde bilinmiyorsa akan çubuk (ör. Dahua index okuma).
        private void ShowCenter(string label, bool canStop = false, bool indeterminate = false)
        {
            CenterLabel.Text = label;
            CenterBar.IsIndeterminate = indeterminate;
            CenterBar.Value = 0;
            CenterPct.Text = indeterminate ? "" : "%0";
            CenterInfo.Text = "";
            if (CenterStop != null)
            {
                CenterStop.Content = "■ Durdur";
                CenterStop.IsEnabled = true;
                CenterStop.Visibility = canStop ? Visibility.Visible : Visibility.Collapsed;
            }
            CenterProgress.Visibility = Visibility.Visible;
        }
        private void UpdateCenter(double frac)
        {
            if (CenterBar.IsIndeterminate) CenterBar.IsIndeterminate = false;  // gerçek ilerleme geldi
            double p = Math.Max(0, Math.Min(1, frac)) * 100;
            CenterBar.Value = p; CenterPct.Text = "%" + (int)p;
        }
        private void SetCenterInfo(string info) { if (CenterInfo != null) CenterInfo.Text = info; }
        private void HideCenter() => CenterProgress.Visibility = Visibility.Collapsed;

        private static string FmtDur(TimeSpan t)
        {
            if (t.TotalHours >= 1) return (int)t.TotalHours + " sa " + t.Minutes + " dk";
            if (t.TotalMinutes >= 1) return t.Minutes + " dk " + t.Seconds + " sn";
            return t.Seconds + " sn";
        }

        private void StopScan_Click(object sender, RoutedEventArgs e)
        {
            _videoCts?.Cancel();
            _saveCts?.Cancel();          // kaydetme/çıkarma da durdurulabilsin
            StopButton.Content = "Durduruluyor…";
            if (CenterStop != null) { CenterStop.Content = "Durduruluyor…"; CenterStop.IsEnabled = false; }
        }

        private async void Video_Click(object sender, RoutedEventArgs e)
        {
            if (_videoScanning) return;   // zaten tarıyor; durdurmak için alttaki '■ Durdur'

            Func<int, Stream> open = _openRaw;
            long len = _totalSize;
            if (len <= 0) { Status.Text = "Kaynak boyutu bilinmiyor."; return; }

            _videoScanning = true;
            _videoCts = new System.Threading.CancellationTokenSource();
            var tok = _videoCts.Token;
            VideoButton.IsEnabled = false;        // etiket hep "🎬 Video Tara" kalır, pasifleşir
            StopButton.Content = "■ Durdur";
            OnlyDeleted.IsChecked = false;
            Status.Text = "Video taraması başladı… (tüm disk içerikten taranıyor: H.264 + H.265 + MJPEG)";
            ScanBegin("Video taraması — H.264 + H.265 + MJPEG (içerikten)");

            try
            {
                List<CarvedFile> found = await System.Threading.Tasks.Task.Run(() =>
                    VideoCarver.Scan(open, len, tok, (p, c) =>
                    {
                        try { Dispatcher.Invoke(() => ScanReport(p, c, "video")); }
                        catch { }
                    }), tok);

                var amber = new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x29)); amber.Freeze();
                _all.Clear();
                int n = 1;
                foreach (CarvedFile cf in found)
                {
                    string codec = string.IsNullOrEmpty(cf.Codec) ? "h264" : cf.Codec;
                    (string ext, string tip) = codec switch
                    {
                        "h265"  => (".mp4", "Video (H.265/HEVC)"),   // kayıt otomatik zaman çizgili .mp4
                        "mjpeg" => (".avi", "Video (MJPEG)"),
                        _        => (".mp4", "Video (H.264)")
                    };
                    _all.Add(new FsEntry
                    {
                        Name = "video_" + n.ToString("D5") + ext,
                        IsDir = false,
                        TypeText = tip,
                        SizeText = FormatSize((ulong)cf.Length),
                        SizeBytes = cf.Length,
                        RowColor = amber,
                        IsRaw = true,
                        IsVideo = true,
                        Codec = codec,
                        RawOffset = cf.Offset,
                        RawLength = cf.Length
                    });
                    n++;
                }
                ApplyFilter();
                SetLabelCrumb("(video taraması)");
                bool stopped = tok.IsCancellationRequested;
                ScanEnd(stopped, found.Count, "video");
                Status.Text = (stopped ? "Durduruldu — " : "") + found.Count +
                              " video bulundu (H.264/H.265/MJPEG). İşaretle ve 'Seçilenleri Kaydet'. Oynamazsa VLC ile aç.";
            }
            catch (Exception ex) { Status.Text = "Video tarama hatası: " + ex.Message; ScanEnd(true, 0, "video"); }
            finally
            {
                _videoScanning = false;
                _videoCts?.Dispose();
                _videoCts = null;
                VideoButton.IsEnabled = true;
            }
        }

        // Ara kutusunda Enter → disk genelinde ara
        private void FilterBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter) DiskSearch_Click(sender, e);
        }

        // Tüm klasörleri gezip eşleşen dosyaları bul (UFS/R-Studio tarzı) — Durdurulabilir
        private async void DiskSearch_Click(object sender, RoutedEventArgs e)
        {
            // Zaten aranıyorsa: bu tık = Durdur
            if (_searching) { _searchCts?.Cancel(); SearchButton.Content = "Durduruluyor…"; return; }

            if (_openVolume is null) { Status.Text = "Önce soldan bir birim seç."; return; }
            string q = (FilterBox.Text ?? "").Trim();
            if (q.Length == 0) { Status.Text = "Önce 'Ara:' kutusuna aranacak adı/uzantıyı yaz."; return; }
            Func<Stream> open = _openVolume;
            string ql = q.ToLowerInvariant();

            _searching = true;
            _searchCts = new System.Threading.CancellationTokenSource();
            var token = _searchCts.Token;
            SearchButton.Content = "■ Durdur";
            OnlyDeleted.IsChecked = false;  // sonuçlar silinmiş değil, gizlenmesin
            Status.Text = "Disk genelinde '" + q + "' aranıyor... (durdurmak için ■ Durdur)";
            bool stopped = false;
            try
            {
                List<FsEntry> hits = await System.Threading.Tasks.Task.Run(() =>
                {
                    var list = new List<FsEntry>();
                    using Stream s = open();
                    DiscFileSystem? fs = VolumeMount.Open(s, out _, out _, out _);
                    if (fs == null) return list;

                    var stack = new Stack<string>();
                    stack.Push("");
                    while (stack.Count > 0 && list.Count < 20000)
                    {
                        if (token.IsCancellationRequested) break;
                        string dir = stack.Pop();
                        IEnumerable<string> files, dirs;
                        try { files = fs.GetFiles(dir); } catch { files = Array.Empty<string>(); }
                        try { dirs = fs.GetDirectories(dir); } catch { dirs = Array.Empty<string>(); }

                        foreach (string f in files)
                        {
                            if (list.Count >= 20000) break;
                            if (NameOf(f).ToLowerInvariant().Contains(ql))
                            {
                                long len = 0; try { len = fs.GetFileLength(f); } catch { }
                                list.Add(new FsEntry { Name = f, FullPath = f, IsDir = false, SizeText = FormatSize((ulong)len), TypeText = "Dosya" });
                            }
                        }
                        foreach (string d in dirs) stack.Push(d);
                    }
                    fs.Dispose();
                    return list;
                });

                stopped = token.IsCancellationRequested;
                _all.Clear();
                _all.AddRange(hits);
                ApplyFilter();
                SetLabelCrumb("(arama: " + q + ")");
                string cap = hits.Count >= 20000 ? " (ilk 20000)" : "";
                string head = stopped ? "Arama durduruldu — " : "";
                Status.Text = head + hits.Count + " eşleşen dosya bulundu" + cap + ". İşaretle ve 'Seçilenleri Kaydet'.";
            }
            catch (Exception ex) { Status.Text = "Arama hatası: " + ex.Message; }
            finally
            {
                _searching = false;
                _searchCts?.Dispose();
                _searchCts = null;
                SearchButton.Content = "Diskte Ara";
                SearchButton.IsEnabled = true;
            }
        }

        // ---- Sıralama ----
        private string _sortKey = "name";
        private bool _sortAsc = true;

        private void Header_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.Button b || b.Tag is not string key) return;
            if (_sortKey == key) _sortAsc = !_sortAsc;   // aynı sütun → ters çevir
            else { _sortKey = key; _sortAsc = true; }
            UpdateHeaderArrows();
            ApplyFilter();
        }

        private void UpdateHeaderArrows()
        {
            string a = _sortAsc ? " ▲" : " ▼";
            HdrName.Content = "AD" + (_sortKey == "name" ? a : "");
            HdrCreated.Content = "OLUŞTURMA" + (_sortKey == "created" ? a : "");
            HdrModified.Content = "DEĞİŞTİRME" + (_sortKey == "modified" ? a : "");
            HdrSize.Content = "BOYUT" + (_sortKey == "size" ? a : "");
            HdrType.Content = "TÜR" + (_sortKey == "type" ? a : "");
        }

        private int Compare(FsEntry x, FsEntry y)
        {
            int c;
            switch (_sortKey)
            {
                case "size": c = x.SizeBytes.CompareTo(y.SizeBytes); break;
                case "created": c = x.CreatedValue.CompareTo(y.CreatedValue); break;
                case "modified": c = x.ModifiedValue.CompareTo(y.ModifiedValue); break;
                case "type": c = string.Compare(x.TypeText, y.TypeText, StringComparison.OrdinalIgnoreCase);
                             if (c == 0) c = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase); break;
                default: // name: klasörler önce, sonra alfabetik
                    if (x.IsDir != y.IsDir) return x.IsDir ? -1 : 1;
                    c = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase); break;
            }
            return _sortAsc ? c : -c;
        }

        private void ApplyFilter()
        {
            if (OnlyDeleted == null) return; // pencere kurulurken koruma
            string q = (FilterBox.Text ?? "").Trim().ToLowerInvariant();
            bool onlyDel = OnlyDeleted.IsChecked == true;

            var list = new List<FsEntry>();
            foreach (FsEntry en in _all)
            {
                if (onlyDel && en.Deleted == null) continue;
                if (q.Length > 0 && !en.Name.ToLowerInvariant().Contains(q)) continue;
                list.Add(en);
            }
            list.Sort(Compare);

            Entries.Clear();
            foreach (FsEntry en in list) Entries.Add(en);
            StatsText.Text = ComputeStats();
        }

        // Arama/filtre barını göster-gizle
        private void ShowFilter_Click(object sender, RoutedEventArgs e)
        {
            FilterBar.Visibility = Visibility.Visible;
            FilterBox.Focus();
        }

        private void HideFilter_Click(object sender, RoutedEventArgs e)
        {
            FilterBar.Visibility = Visibility.Collapsed;
            FilterBox.Text = "";
            OnlyDeleted.IsChecked = false;
            ApplyFilter();
        }

        private static DateTime SafeDate(Func<DateTime> f)
        {
            try { return f(); } catch { return DateTime.MinValue; }
        }

        // Tür istatistiği: uzantıya göre sayım (en çok 6 tür)
        private string ComputeStats()
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int dirs = 0;
            foreach (FsEntry en in _all)
            {
                if (en.IsDir) { dirs++; continue; }
                string ext = Path.GetExtension(en.Name);
                ext = string.IsNullOrEmpty(ext) ? "(uzantısız)" : ext.TrimStart('.').ToUpperInvariant();
                map.TryGetValue(ext, out int c);
                map[ext] = c + 1;
            }

            var top = new List<KeyValuePair<string, int>>(map);
            top.Sort((a, b) => b.Value.CompareTo(a.Value));

            var sb = new StringBuilder();
            sb.Append(_all.Count).Append(" öğe");
            if (dirs > 0) sb.Append("  ·  ").Append(dirs).Append(" klasör");
            int shown = 0;
            foreach (KeyValuePair<string, int> kv in top)
            {
                if (shown >= 6) break;
                sb.Append("  ·  ").Append(kv.Key).Append(' ').Append(kv.Value);
                shown++;
            }
            return sb.ToString();
        }

        private void FileList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is not FsEntry entry) return;
            if (entry.IsDir) ShowDir(entry.FullPath);
            else LoadQuickPreview(entry);   // çift tık = ikon/önizleme paneli (tek tık artık sadece seçer)
        }

        // Sağ tıklayınca altındaki öğeyi seç (menü doğru dosyaya uygulansın)
        private void FileList_RightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            DependencyObject? el = e.OriginalSource as DependencyObject;
            while (el != null && el is not System.Windows.Controls.ListBoxItem)
                el = VisualTreeHelper.GetParent(el);
            if (el is System.Windows.Controls.ListBoxItem item)
                item.IsSelected = true;
        }

        private void ViewSectors_Click(object sender, RoutedEventArgs e)
        {
            if (FileList.SelectedItem is FsEntry entry && !entry.IsDir)
                OpenFileSectors(entry);
            else
                Status.Text = "Haritada göstermek için bir dosya seç.";
        }

        // ---- Önizleme + yeşil/kırmızı sağlamlık ----
        private void Preview_Click(object sender, RoutedEventArgs e)
        {
            if (FileList.SelectedItem is FsEntry en && !en.IsDir) PreviewEntry(en);
            else Status.Text = "Önizlemek için bir dosya seç.";
        }

        private void PreviewSelected_Click(object sender, RoutedEventArgs e)
        {
            if (FileList.SelectedItem is FsEntry en && !en.IsDir) PreviewEntry(en);
        }

        // ---- Hızlı önizleme (sağ panel, tıkınca açılır) ----
        private System.Threading.CancellationTokenSource? _quickCts;

        private void CloseQuick_Click(object sender, RoutedEventArgs e)
        {
            _quickCts?.Cancel();
            QuickPanel.Visibility = Visibility.Collapsed;
            PreviewCol.Width = new GridLength(0);
        }

        private void OpenQuickPanel()
        {
            QuickPanel.Visibility = Visibility.Visible;
            PreviewCol.Width = new GridLength(320);
        }

        // Tek tık = SADECE seç. İkon/önizleme paneli tek tıkla AÇILMAZ — çift tıkla açılır.
        // Panel zaten açıksa, seçim değişince içeriği yeni dosyaya güncelle.
        private void FileList_SelChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            _quickCts?.Cancel();
            if (FileList.SelectedItem is not FsEntry en) return;
            if (en.IsDir) { QuickPanel.Visibility = Visibility.Collapsed; PreviewCol.Width = new GridLength(0); return; }
            if (QuickPanel.Visibility == Visibility.Visible) LoadQuickPreview(en);   // açıkken seçim takibi
        }

        // Çift tık = ikon/önizleme panelini aç ve içeriği yükle (eskiden tek tık yapardı).
        private async void LoadQuickPreview(FsEntry en)
        {
            _quickCts?.Cancel();

            // Panel açık değilse aç
            OpenQuickPanel();

            // İçerik alanını sıfırla
            QThumb.Visibility = Visibility.Collapsed; QThumb.Source = null;
            QText.Visibility = Visibility.Collapsed; QText.Text = "";
            QIconBox.Visibility = Visibility.Collapsed;
            QHint.Visibility = Visibility.Collapsed;
            QSound.Text = "";

            QName.Text = en.Name;
            QInfo.Text = en.SizeText +
                         (string.IsNullOrEmpty(en.CreatedText) ? "" : "\nOluşturma: " + en.CreatedText) +
                         (string.IsNullOrEmpty(en.ModifiedText) ? "" : "\nDeğiştirme: " + en.ModifiedText);

            if (en.IsDir)
            {
                QInfo.Text = "Klasör";
                QIcon.Source = en.IconImage; QIconBox.Visibility = Visibility.Visible;
                return;
            }

            // Önce ikon göster (yüklenene kadar)
            QIcon.Source = en.IconImage; QIconBox.Visibility = Visibility.Visible;

            long cap = IsImageName(en.Name) ? 12L * 1024 * 1024
                     : IsSmallPreview(en.Name) ? 1L * 1024 * 1024
                     : 64L * 1024 * 1024;

            _quickCts = new System.Threading.CancellationTokenSource();
            var tok = _quickCts.Token;

            // Ham kayıt (carved/video/Hikvision): diskten offsetten oku
            if (en.IsRaw)
            {
                long off = en.RawOffset, rlen = en.RawLength;
                Func<int, Stream> rawOpen = _openRaw;
                try
                {
                    byte[] data = await System.Threading.Tasks.Task.Run(() =>
                    {
                        using Stream s = rawOpen(512);
                        return ReadRawBytes(s, off, Math.Min(cap, rlen), tok);
                    }, tok);
                    if (!tok.IsCancellationRequested) ApplyQuick(en, data);
                }
                catch { }
                return;
            }

            if (_openVolume is null) return;

            Func<Stream> open = _openVolume;
            MftFile? del = en.Deleted;
            string path = en.FullPath;
            try
            {
                byte[] data = await System.Threading.Tasks.Task.Run(() =>
                {
                    using Stream s = open();
                    DiscFileSystem? fs = VolumeMount.Open(s, out _, out _, out _);
                    if (fs == null) return Array.Empty<byte>();
                    byte[] r = del != null ? ReadDeletedBytes(s, del, cap, tok) : ReadExistingBytes(fs, path, cap, tok);
                    fs.Dispose();
                    return r;
                }, tok);

                if (tok.IsCancellationRequested) return;
                ApplyQuick(en, data);
            }
            catch { /* iptal/okuma hatası — ikon+bilgi kalır */ }
        }

        private void ApplyQuick(FsEntry en, byte[] data)
        {
            // Video kaydı: ham veriyi .mp4 sanıp "bozuk" deme; düzgün mesaj
            if (en.IsVideo)
            {
                QIconBox.Visibility = Visibility.Visible;
                QSound.Text = "🎬 Video kaydı — '▶ Oynat' ile izle, 'Kaydet' ile diske al";
                QSound.Foreground = Theme.Brush("AccentText");
                return;
            }

            if (data.Length == 0) return;

            int hn = Math.Min(65536, data.Length);
            byte[] head = new byte[hn];
            Array.Copy(data, 0, head, 0, hn);
            bool whole = en.SizeBytes > 0 && data.Length >= en.SizeBytes;
            byte[] tail = whole ? head : Array.Empty<byte>();
            (bool? sound, string msg) = SoundCheck.Check(en.Name, head, tail, whole);

            string ext = ExtOf(en.Name);
            bool rendered = false;

            if (IsImageName(en.Name))
            {
                if (ShowQuickImage(data)) { sound = true; rendered = true; }
                else { sound = false; msg = "Görüntü açılamadı — bozuk olabilir."; }
            }
            else if (ext == "pdf")
            {
                var bs = PdfPreview.FirstPage(data, out _);
                if (bs != null) { QThumb.Source = bs; ShowQThumb(); if (sound == null) sound = true; rendered = true; }
            }
            else if (ext == "xlsx")
            {
                string? table = OfficePreview.Xlsx(data, out _);
                if (table != null) { ShowQText(table); if (sound == null) sound = true; rendered = true; }
            }
            else if (ext == "docx")
            {
                string? text = OfficePreview.Docx(data, out _);
                if (text != null) { ShowQText(text); if (sound == null) sound = true; rendered = true; }
            }
            else if (IsTextName(en.Name))
            {
                try { ShowQText(Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 512 * 1024))); rendered = true; } catch { }
            }

            // Hiçbiri render edilemediyse ikon + "çift tıkla" kalır
            if (!rendered) { QIconBox.Visibility = Visibility.Visible; }

            QSound.Text = (sound == true ? "✓ " : sound == false ? "✕ " : "• ") + msg;
            QSound.Foreground = sound == true
                ? new SolidColorBrush(Color.FromRgb(0x4a, 0xde, 0x80))
                : sound == false
                    ? new SolidColorBrush(Color.FromRgb(0xf8, 0x71, 0x71))
                    : new SolidColorBrush(Color.FromRgb(0x8a, 0xa0, 0xb2));
        }

        private bool ShowQuickImage(byte[] data)
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                using (var ms = new MemoryStream(data))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 300;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                }
                bmp.Freeze();
                QThumb.Source = bmp;
                ShowQThumb();
                return true;
            }
            catch { return false; }
        }

        private void ShowQThumb()
        {
            QThumb.Visibility = Visibility.Visible;
            QIconBox.Visibility = Visibility.Collapsed;
            QText.Visibility = Visibility.Collapsed;
        }

        private void ShowQText(string t)
        {
            QText.Text = t;
            QText.Visibility = Visibility.Visible;
            QIconBox.Visibility = Visibility.Collapsed;
            QThumb.Visibility = Visibility.Collapsed;
        }

        private static string ExtOf(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot >= 0 ? name.Substring(dot + 1).ToLowerInvariant() : "";
        }

        private static bool IsTextName(string name)
        {
            string e = ExtOf(name);
            return e is "txt" or "csv" or "log" or "xml" or "json" or "html" or "htm" or "md"
                or "ini" or "cfg" or "sql" or "srt" or "vtt" or "yaml" or "yml" or "tsv";
        }

        private void PreviewEntry(FsEntry en)
        {
            // Video kaydı → oynatma penceresi (kaydetmeden izle)
            if (en.IsVideo)
            {
                // DHFS kaydı: offset bozuk (DHAV'dan çıkarılır) → önizleme yok, kaydetmeye yönlendir
                if (en.TypeText.StartsWith("Dahua DHFS"))
                {
                    Status.Text = "DVR (DHFS) kaydı: 'Seçilenleri Kaydet' ile çıkar (Hızlı=ham .h265/.h264, Tam=.mp4), sonra aç.";
                    return;
                }
                // Dahili oynatıcı yalnız H.264 muxlar; H.265/MJPEG için kaydedip VLC'de aç
                if (en.Codec == "h265" || en.Codec == "mjpeg")
                {
                    Status.Text = (en.Codec == "h265" ? "H.265" : "MJPEG") +
                        " için 'Seçilenleri Kaydet' kullan — 'Zaman çizgili .mp4' işaretliyse süreli/akıcı .mp4, değilse ham " +
                        (en.Codec == "h265" ? ".h265" : ".avi") + " çıkar; VLC'de aç.";
                    return;
                }
                new VideoPreviewWindow(_openRaw, en.RawOffset, en.RawLength, en.Name).Show();
                return;
            }

            long full = en.SizeBytes;
            long cap = IsSmallPreview(en.Name) ? 1L * 1024 * 1024 : 64L * 1024 * 1024;

            // Carved/Hikvision/video gibi ham kayıtlar: dosya sistemi değil, diskten offsetten oku
            if (en.IsRaw)
            {
                long off = en.RawOffset, rlen = en.RawLength;
                Func<int, Stream> rawOpen = _openRaw;
                var pvr = new PreviewWindow(en.Name, full > 0 ? full : rlen, tok =>
                {
                    using Stream s = rawOpen(512);
                    return ReadRawBytes(s, off, Math.Min(cap, rlen), tok);
                });
                pvr.Show();
                return;
            }

            if (_openVolume is null) { Status.Text = "Önce bir birim seç."; return; }

            Func<Stream> open = _openVolume;
            MftFile? del = en.Deleted;
            string path = en.FullPath;

            var pv = new PreviewWindow(en.Name, full > 0 ? full : 0, tok =>
            {
                using Stream s = open();
                DiscFileSystem? fs = VolumeMount.Open(s, out _, out _, out _);
                if (fs == null) return Array.Empty<byte>();
                byte[] r = del != null ? ReadDeletedBytes(s, del, cap, tok) : ReadExistingBytes(fs, path, cap, tok);
                fs.Dispose();
                return r;
            });
            pv.Show();
        }

        private static bool IsImageName(string name)
        {
            int dot = name.LastIndexOf('.');
            string e = dot >= 0 ? name.Substring(dot + 1).ToLowerInvariant() : "";
            return e is "jpg" or "jpeg" or "png" or "gif" or "bmp" or "tif" or "tiff";
        }

        // Sadece başlık/az veri yeten türler (metin, video) → küçük oku
        private static bool IsSmallPreview(string name)
        {
            int dot = name.LastIndexOf('.');
            string e = dot >= 0 ? name.Substring(dot + 1).ToLowerInvariant() : "";
            return e is "txt" or "csv" or "log" or "xml" or "json" or "html" or "htm" or "md"
                or "ini" or "cfg" or "sql" or "srt" or "vtt" or "yaml" or "yml" or "tsv"
                or "mp4" or "mov" or "mkv" or "avi" or "wmv" or "flv" or "m4v" or "mpg" or "mpeg" or "3gp" or "ts";
        }

        // Diskten ham [offset, length] oku (carved/video/Hikvision önizlemesi)
        private static byte[] ReadRawBytes(Stream s, long offset, long length, System.Threading.CancellationToken tok)
        {
            s.Position = offset;
            int cap = (int)Math.Min(Math.Max(0, length), int.MaxValue);
            var buf = new byte[cap];
            int off = 0;
            while (off < buf.Length)
            {
                if (tok.IsCancellationRequested) break;
                int got = s.Read(buf, off, (int)Math.Min(1 << 20, buf.Length - off));
                if (got <= 0) break;
                off += got;
            }
            if (off < buf.Length) Array.Resize(ref buf, off);
            return buf;
        }

        private static byte[] ReadExistingBytes(DiscFileSystem fs, string path, long cap, System.Threading.CancellationToken tok)
        {
            using Stream s = fs.OpenFile(path, FileMode.Open, FileAccess.Read);
            long len = Math.Min(cap, s.Length);
            var buf = new byte[len];
            int off = 0;
            while (off < len)
            {
                if (tok.IsCancellationRequested) break;
                int got = s.Read(buf, off, (int)Math.Min(1 << 20, len - off));
                if (got <= 0) break;
                off += got;
            }
            if (off < len) Array.Resize(ref buf, off);
            return buf;
        }

        private byte[] ReadDeletedBytes(Stream raw, MftFile mf, long cap, System.Threading.CancellationToken tok)
        {
            using var ms = new MemoryStream();
            if (mf.Resident)
            {
                if (mf.ResidentData != null) ms.Write(mf.ResidentData, 0, mf.ResidentData.Length);
                return ms.ToArray();
            }
            long remaining = Math.Min((long)mf.Size, cap);
            byte[] buf = new byte[1 << 20];
            foreach (DataRun run in mf.Runs)
            {
                if (remaining <= 0 || tok.IsCancellationRequested) break;
                long bytes = run.Clusters * _clusterSize;
                if (run.Sparse)
                {
                    long z = Math.Min(bytes, remaining);
                    byte[] zero = new byte[(int)Math.Min(z, buf.Length)];
                    long w = 0;
                    while (w < z) { int c = (int)Math.Min(zero.Length, z - w); ms.Write(zero, 0, c); w += c; }
                    remaining -= z;
                }
                else
                {
                    raw.Position = run.Lcn * _clusterSize;
                    long toRead = Math.Min(bytes, remaining);
                    long r = 0;
                    while (r < toRead)
                    {
                        if (tok.IsCancellationRequested) break;
                        int c = (int)Math.Min(buf.Length, toRead - r);
                        int got = raw.Read(buf, 0, c);
                        if (got <= 0) break;
                        ms.Write(buf, 0, got);
                        r += got;
                    }
                    remaining -= r;
                }
            }
            return ms.ToArray();
        }

        // Seçili dosyayı tüm disk haritasında işaretli olarak aç
        private void OpenFileSectors(FsEntry entry)
        {
            if (_openVolume is null) { Status.Text = "Önce bir birim seç."; return; }

            List<(long start, long count)>? ext = FileExtents(entry);
            new SectorMapWindow(_openVolume, _sourceLabel + "  —  " + entry.Name, ext).Show();
        }

        // Bir dosyanın küme aralıklarını (diskteki yeri) çıkar
        private List<(long start, long count)>? FileExtents(FsEntry entry)
        {
            var list = new List<(long start, long count)>();
            try
            {
                if (entry.Deleted != null)
                {
                    foreach (DataRun r in entry.Deleted.Runs)
                        if (!r.Sparse) list.Add((r.Lcn, r.Clusters));
                }
                // Mevcut dosyaların küme konumu (vurgu) sonraki adımda eklenecek —
                // DiscUtils sürümüne göre API değiştiği için şimdilik devre dışı.
            }
            catch { }
            return list.Count > 0 ? list : null;
        }

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentPath)) return;
            string p = _currentPath.TrimEnd('\\');
            int idx = p.LastIndexOf('\\');
            ShowDir(idx <= 0 ? "" : p.Substring(0, idx));
        }

        private async void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (_openVolume is null) { Status.Text = "Önce soldan bir birim seç."; return; }
            if (!_currentIsNtfs) { Status.Text = "Silinmiş tarama şu an sadece NTFS birimlerde çalışır."; return; }
            Func<Stream> open = _openVolume;

            ScanButton.IsEnabled = false;
            Status.Text = "Silinmiş kayıtlar taranıyor... (büyük diskte birkaç dakika sürebilir)";
            try
            {
                ScanResult res = await System.Threading.Tasks.Task.Run(() =>
                {
                    using Stream s = open();
                    return new NtfsDeletedScanner().Scan(s);
                });

                _clusterSize = res.ClusterSize;
                _all.Clear();
                var red = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                red.Freeze();
                int shown = 0;
                foreach (MftFile mf in res.Files)
                {
                    if (shown >= 5000) break;
                    shown++;
                    string folder = mf.Orphan ? "↷ Lost & Found" : (mf.FolderPath.Length == 0 ? "\\ (kök)" : "\\" + mf.FolderPath);
                    _all.Add(new FsEntry
                    {
                        Name = mf.Name,
                        IsDir = false,
                        SizeText = FormatSize(mf.Size),
                        SizeBytes = (long)mf.Size,
                        TypeText = mf.Orphan ? "Silinmiş · kayıp" : "Silinmiş",
                        RowColor = red,
                        Deleted = mf,
                        SubText = folder
                    });
                }
                ApplyFilter();
                SetLabelCrumb("(silinmiş dosyalar)");
                string extra = res.Files.Count > 5000 ? "  (ilk 5000 gösteriliyor)" : "";
                Status.Text = res.Files.Count + " silinmiş dosya bulundu" + extra + ". İşaretle ve 'Seçilenleri Kaydet'.";
            }
            catch (Exception ex)
            {
                Status.Text = "Tarama hatası: " + ex.Message;
            }
            finally { ScanButton.IsEnabled = true; }
        }

        // Sektör Haritası penceresini aç
        private void OpenLog_Click(object sender, RoutedEventArgs e)
        {
            try { new LogWindow().Show(); } catch (Exception ex) { Status.Text = "Log açılamadı: " + ex.Message; }
        }

        // Dağınık DVR diskini bir kez baştan sona tarayıp kataloğa al (gece bırak).
        // Kaynak diske YAZILMAZ. Sonra her kayıt tarama olmadan anında çıkar.
        private async void BuildCatalog_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                    "Bu diski baştan sona tarayıp kataloğa alayım mı?\n\n" +
                    "• Tüm disk OKUNUR (saatler sürebilir — gece bırakabilirsin)\n" +
                    "• Kaynak diske YAZILMAZ, yalnızca okunur\n" +
                    "• Katalog bir kez kaydedilir; sonra her kayıt ANINDA çıkar\n\n" +
                    "Başlasın mı?",
                    "Diski kataloğa al", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            string catPath = CatalogPath();
            long[] pos = { 0 };
            Action<long> prog = p => System.Threading.Interlocked.Add(ref pos[0], p);  // artımlı topla

            ScanBegin("Kataloğa alınıyor…");
            if (StopButton != null) StopButton.Visibility = Visibility.Collapsed;
            CatalogButton.IsEnabled = false; SaveButton.IsEnabled = false;

            var pt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            pt.Tick += (_, __) =>
            {
                long p = System.Threading.Interlocked.Read(ref pos[0]);
                ScanReport(_totalSize > 0 ? (double)p / _totalSize : 0, 0, "tarandı");
            };
            pt.Start();

            string diag = "";
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    DhfsCatalog.BuildCatalog(_openRaw, _totalSize, catPath,
                        System.Threading.CancellationToken.None, prog, out string dd);
                    diag = dd;
                });
            }
            catch (Exception ex) { diag = "HATA: " + ex.Message; }

            pt.Stop();
            ScanEnd(false, 0, "tarandı");
            CatalogButton.IsEnabled = true; SaveButton.IsEnabled = true;
            Status.Text = "Katalog hazır — artık seçtiğin kayıtlar tarama olmadan anında çıkar.   " + diag;
            AppLog.Add("Katalog kuruldu: " + diag);
        }

        // Gömülü VLC oynatıcı: kurtarılan video dosyasını uygulama içinde oynat
        private void OpenPlayer_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Oynatılacak video",
                Filter = "Video (*.mp4;*.h265;*.h264;*.avi;*.dav;*.mkv)|*.mp4;*.h265;*.h264;*.avi;*.dav;*.mkv|Tüm dosyalar (*.*)|*.*"
            };
            if (dlg.ShowDialog() != true) return;
            try { new CctvPlayerWindow(dlg.FileName).Show(); }
            catch (Exception ex)
            {
                Status.Text = "Oynatıcı açılamadı: " + ex.Message;
                MessageBox.Show(
                    "Oynatıcı penceresi açılamadı:\n\n" + ex.Message +
                    "\n\nGenelde sebep: VLC motoru paketi (VideoLAN.LibVLC.Windows) geri yüklenmemiş.\n" +
                    "Visual Studio: Solution'a sağ tık → 'Restore NuGet Packages' → tekrar derle (F5).",
                    "CCTV Oynatıcı", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Map_Click(object sender, RoutedEventArgs e)
        {
            // Tanınan dosya sistemi varsa birim üzerinden; yoksa (DHFS/CCTV/ham disk) doğrudan ham diskten
            if (_openVolume != null)
            {
                new SectorMapWindow(_openVolume, _sourceLabel).Show();
                return;
            }
            if (_totalSize > 0)
            {
                new SectorMapWindow(() => _openRaw(512), _sourceLabel + "  —  ham disk (hex)").Show();
                return;
            }
            Status.Text = "Önce bir disk/imaj aç (soldan seç).";
        }

        // OTO-TEŞHİS: takılı diski otomatik tanı (marka/format/kamera/tarih/kurtarılabilir mi), sonra istersen listele.
        private async void OtoTeshis_Click(object sender, RoutedEventArgs e)
        {
            if (_openRaw == null || _totalSize <= 0) { Status.Text = "Önce bir disk/imaj aç."; return; }

            ShowCenter("Oto-Teşhis: disk taranıyor…", canStop: false, indeterminate: true);
            DiagButton.IsEnabled = false;
            DvrDiagnosis? d = null;
            try
            {
                var openRaw = _openRaw; long sz = _totalSize;
                d = await System.Threading.Tasks.Task.Run(() =>
                    DvrAutoDiagnose.Diagnose(openRaw, sz,
                        msg => Dispatcher.Invoke(() => CenterLabel.Text = "Oto-Teşhis: " + msg)));
            }
            catch (Exception ex)
            {
                HideCenter(); DiagButton.IsEnabled = true;
                MessageBox.Show("Teşhis hatası: " + ex.Message, "Oto-Teşhis", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            HideCenter();
            DiagButton.IsEnabled = true;
            if (d == null) return;

            var sb = new StringBuilder();
            sb.AppendLine(d.Recoverable ? "✓ KURTARILABİLİR" : "✗ Kurtarılabilir içerik bulunamadı");
            sb.AppendLine();
            sb.AppendLine("Marka : " + d.Brand);
            sb.AppendLine("Format: " + d.Format);
            if (d.Cameras >= 0) sb.AppendLine("Kamera: " + d.Cameras);
            if (!string.IsNullOrEmpty(d.DateRange)) sb.AppendLine("Tarih : " + d.DateRange);
            sb.AppendLine();
            sb.AppendLine(d.Summary);

            Status.Text = "Oto-Teşhis: " + d.Brand + " · " + d.Format + " · " +
                          (d.Recoverable ? "kurtarılabilir ✓" : "✗");

            bool knownList = d.Recoverable && (d.ReaderKey == "uware" || d.ReaderKey == "hik" || d.ReaderKey == "dhfs");
            bool carveList = d.Recoverable && d.ReaderKey == "mjpeg";
            if (knownList || carveList)
            {
                sb.AppendLine();
                sb.AppendLine(carveList
                    ? "Şimdi JPEG karelerini tarayıp kanalları listeleyeyim mi? (carving)"
                    : "Şimdi diski açıp kayıtları listeleyeyim mi?");
                if (MessageBox.Show(sb.ToString(), "Oto-Teşhis sonucu",
                        MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    if (carveList) await ListCarvedMjpeg();
                    else OpenDisk();
                }
            }
            else
            {
                MessageBox.Show(sb.ToString(), "Oto-Teşhis sonucu", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        // MJPEG/bilinmeyen disk: carving ile kanalları listele (çözünürlük = kanal).
        private async System.Threading.Tasks.Task ListCarvedMjpeg()
        {
            ShowCenter("JPEG kareler taranıyor (carving)…  (ilk 32 GB örnek)");
            long[] cprog = { 0 };
            var cpt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            long cap = Math.Min(_totalSize, 32L << 30);
            cpt.Tick += (_, __) => UpdateCenter(cap > 0 ? (double)System.Threading.Interlocked.Read(ref cprog[0]) / cap : 0);
            cpt.Start();
            CarveInfo ci;
            try
            {
                var openRaw = _openRaw; long sz = _totalSize;
                ci = await System.Threading.Tasks.Task.Run(() =>
                    CarvingReader.ParseJpeg(openRaw, sz, p => System.Threading.Interlocked.Exchange(ref cprog[0], p)));
            }
            finally { cpt.Stop(); HideCenter(); }

            _all.Clear();
            var amber = new SolidColorBrush(Color.FromRgb(0xF0, 0xB4, 0x29)); amber.Freeze();
            foreach (CarvedRec r in ci.Recordings)
            {
                string time = r.HasTime ? "  " + r.Start.ToString("yyyy-MM-dd HH:mm") + "–" + r.End.ToString("HH:mm") : "  (tarih: kaydederken OSD'den)";
                _all.Add(new FsEntry
                {
                    Name = r.Camera + time + "  ·  " + r.FrameCount + " kare.avi",
                    IsDir = false,
                    TypeText = "MJPEG carving",
                    SizeText = r.FrameCount + " kare",
                    SizeBytes = r.LastOffset - r.FirstOffset,
                    CreatedValue = r.HasTime ? r.Start : DateTime.MinValue,
                    ModifiedValue = r.HasTime ? r.End : DateTime.MinValue,
                    RowColor = amber,
                    IsVideo = true,
                    IsRaw = true,
                    Codec = "mjpeg-carve",
                    RawOffset = r.FirstOffset,
                    RawLength = r.LastOffset - r.FirstOffset,
                    Tag = r
                });
            }
            ApplyFilter();
            SetLabelCrumb("(MJPEG carving — çözünürlük = kanal)");
            Status.Text = "Carving: " + ci.Diagnostic +
                          ". İşaretle + 'Seçilenleri Kaydet' → kanal AVI olarak çıkar (kamera adı/tarih OSD'den OCR ile denenecek).";
        }

        // Yeni DVR markası/format tanıtmak için: diskin başından örnek böl (master blok + partition table + index).
        // Kaç MB? FPS kutusu değil; küçük bir sorgu ile al (varsayılan 256 MB — DHFS index + bitmap buna sığar).
        private void DumpSignature_Click(object sender, RoutedEventArgs e)
        {
            if (_openRaw == null || _totalSize <= 0) { Status.Text = "Önce bir disk/imaj aç."; return; }

            // Başlangıç offseti (MB). 0 = diskin başı (master blok + index). Veri bölgesi için ör. 4096 (≈4 GB).
            int startMb = PromptMb("Hangi MB'den başlasın?\n(0 = diskin başı: master blok + index\n veri/kayıt bölgesi için ör. 4096 = ~4 GB)", 0);
            if (startMb < 0) return;                   // iptal (negatif dönerse)
            int mb = PromptMb("Kaç MB dökülsün?\n(256 yeterli — chat'e yüklenebilir boyut)", 256);
            if (mb <= 0) return;                       // iptal
            long startByte = (long)startMb * 1024 * 1024;
            if (startByte >= _totalSize) { Status.Text = "Başlangıç offseti disk boyutundan büyük."; return; }
            long n = Math.Min((long)mb * 1024 * 1024, _totalSize - startByte);

            string baseName = "imza_" + Safe(string.IsNullOrEmpty(_sourceLabel) ? "disk" : _sourceLabel) + "_" + startMb + "mb+" + mb + "mb";
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Örnek sektör dökümünü kaydet",
                Filter = "Döküm (*.bin)|*.bin",
                FileName = baseName + ".bin"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                using Stream s = _openRaw(1 << 20);
                s.Position = startByte;
                using var outF = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write);
                byte[] buf = new byte[1 << 20];
                long left = n; long done = 0;
                while (left > 0)
                {
                    int want = (int)Math.Min(buf.Length, left);
                    int r = s.Read(buf, 0, want);
                    if (r <= 0) break;
                    outF.Write(buf, 0, r);
                    left -= r; done += r;
                }
                Status.Text = "Döküldü: " + FormatSize((ulong)done) + "  →  " + dlg.FileName + "  (bu dosyayı bana gönder)";
            }
            catch (Exception ex) { Status.Text = "Dökülemedi: " + ex.Message; }
        }

        // TEK TIKLA otomatik kalibrasyon dökümü: tanınmayan disk için gereken tüm bölgeleri
        // (baş = imza+index, + yayılmış veri örnekleri) tek seferde bir klasöre döker.
        private async void CalibDump_Click(object sender, RoutedEventArgs e)
        {
            if (_openRaw == null || _totalSize <= 0) { Status.Text = "Önce bir disk/imaj aç."; return; }

            var fdlg = new Microsoft.Win32.OpenFolderDialog { Title = "Kalibrasyon dökümlerini kaydedeceğin klasör" };
            if (fdlg.ShowDialog() != true) return;
            string dir = fdlg.FolderName;

            // (başlangıç MB, boyut MB): baş = imza+index; gerisi veri bölgesini yakalamak için yayılmış örnekler
            var regions = new (long startMb, long mb)[]
            {
                (0, 256), (4096, 128), (16384, 128), (65536, 128), (262144, 128), (1048576, 128)
            };
            string label = Safe(string.IsNullOrEmpty(_sourceLabel) ? "disk" : _sourceLabel);

            CalibButton.IsEnabled = false; SaveButton.IsEnabled = false;
            ScanBegin("Format dökümü alınıyor…");
            ShowCenter("Format dökümü alınıyor…");
            long[] prog = { 0 };
            long total = 0; foreach (var r in regions) if ((long)r.startMb * 1048576 < _totalSize) total += r.mb * 1048576;
            long totalF = total;

            var pt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            pt.Tick += (_, __) =>
            {
                double f = totalF > 0 ? (double)System.Threading.Interlocked.Read(ref prog[0]) / totalF : 0;
                ScanReport(f, 0, "MB");
                UpdateCenter(f);
            };
            pt.Start();

            int ok = 0; string err = "";
            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    using Stream s = _openRaw(1 << 20);
                    byte[] buf = new byte[1 << 20];
                    foreach (var (startMb, mb) in regions)
                    {
                        long startByte = startMb * 1024 * 1024;
                        if (startByte >= _totalSize) continue;
                        long n = Math.Min(mb * 1024 * 1024, _totalSize - startByte);
                        string fn = System.IO.Path.Combine(dir, "kalib_" + label + "_" + startMb + "mb_" + mb + "mb.bin");
                        s.Position = startByte;
                        using var outF = new FileStream(fn, FileMode.Create, FileAccess.Write);
                        long left = n;
                        while (left > 0)
                        {
                            int want = (int)Math.Min(buf.Length, left);
                            int r = s.Read(buf, 0, want);
                            if (r <= 0) break;
                            outF.Write(buf, 0, r);
                            left -= r; System.Threading.Interlocked.Add(ref prog[0], r);
                        }
                        ok++;
                    }
                });
            }
            catch (Exception ex) { err = ex.Message; }

            pt.Stop();
            HideCenter();
            ScanEnd(false, ok, "bölge");
            CalibButton.IsEnabled = true; SaveButton.IsEnabled = true;
            Status.Text = string.IsNullOrEmpty(err)
                ? ok + " bölge döküldü → " + dir + "   (bu klasördeki dosyaları bana gönder)"
                : "Kalibrasyon dökümü hatası: " + err;
            AppLog.Add("Format dökümü: " + ok + " bölge → " + dir);
        }

        // Basit sayı sorgu penceresi (WPF'te hazır InputBox yok) — 0..1048576 MB kabul; iptalde -1 döner.
        private static int PromptMb(string message, int def)
        {
            var win = new Window
            {
                Title = "MB değeri",
                Width = 420, Height = 180, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ResizeMode = ResizeMode.NoResize, Background = System.Windows.Media.Brushes.White
            };
            var sp = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
            sp.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
            var tb = new System.Windows.Controls.TextBox { Text = def.ToString(), Height = 26 };
            sp.Children.Add(tb);
            var row = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            var ok = new System.Windows.Controls.Button { Content = "Dök", Width = 80, Height = 28, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new System.Windows.Controls.Button { Content = "İptal", Width = 80, Height = 28, IsCancel = true };
            row.Children.Add(ok); row.Children.Add(cancel);
            sp.Children.Add(row);
            win.Content = sp;
            int result = -1;     // iptal
            ok.Click += (_, __) => { if (int.TryParse(tb.Text.Trim(), out int v) && v >= 0 && v <= 1048576) { result = v; win.DialogResult = true; } };
            win.ShowDialog();
            return result;
        }

        // "CAM06_..." → 5 (0 tabanlı kanal). Bulamazsa -1.
        private static int ParseCamChannel(string name)
        {
            try
            {
                int i = name.IndexOf("CAM", StringComparison.OrdinalIgnoreCase);
                if (i < 0) return -1;
                i += 3;
                int j = i;
                while (j < name.Length && char.IsDigit(name[j])) j++;
                if (j == i) return -1;
                int camNo = int.Parse(name.Substring(i, j - i));
                return camNo - 1;      // CAM01 → kanal 0
            }
            catch { return -1; }
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            bool on = SelectAll.IsChecked == true;
            foreach (FsEntry en in Entries) en.IsChecked = on;   // görünen tüm satırlar
        }

        private System.Threading.CancellationTokenSource? _saveCts;

        private async void SaveSelected_Click(object sender, RoutedEventArgs e)
        {
            var selected = new List<FsEntry>();
            foreach (FsEntry en in _all)           // süzmeden bağımsız: tüm işaretliler
                if (en.IsChecked) selected.Add(en);

            if (selected.Count == 0) { Status.Text = "Kaydetmek için en az bir şey işaretle."; return; }

            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Kurtarılan veriler nereye kaydedilsin?" };
            if (dlg.ShowDialog() != true) return;

            string target = dlg.FolderName;
            AppLog.Add("Kaydetme başladı: " + selected.Count + " öğe → " + target);

            // Dışa aktarma uzantısı: kutuda değer varsa dosya/raw/silinmiş çıktılarının uzantısını değiştir.
            // (Video carve sonuçları kendi kodeğiyle yazılır; onlara dokunmaz.)
            string ovrExt = (ExtBox?.Text ?? "").Trim().TrimStart('.');
            string OutName(string name) => string.IsNullOrEmpty(ovrExt) ? Safe(name) : Safe(Path.ChangeExtension(name, ovrExt));

            // 1) Kaynağa yazma koruması — hem fiziksel disk hem imaj için:
            //    hedef, kaynağın (ya da imaj dosyasının) bulunduğu diskteyse engelle
            if (_sourceDriveNos.Length > 0)
            {
                int[] tnums = DiskGuard.PhysicalDriveNumbers(target);
                int clash = -1;
                foreach (int t in tnums)
                    if (Array.IndexOf(_sourceDriveNos, t) >= 0) { clash = t; break; }

                if (clash >= 0)
                {
                    MessageBox.Show(
                        "Seçtiğin hedef, KURTARMA YAPILAN DİSKİN ÜZERİNDE (PHYSICALDRIVE" + clash + ").\n\n" +
                        "Kurtarılan veriyi kaynak diske yazmak, henüz kurtarılmamış veriyi kalıcı olarak EZER.\n" +
                        "Lütfen BAŞKA bir diske kaydet.",
                        "Tehlike: kaynak diske yazma", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // 2) Hedefte yeterli yer var mı?
            long need = 0;
            foreach (FsEntry en in selected) if (en.SizeBytes > 0) need += en.SizeBytes;
            long free = DiskGuard.FreeSpace(target);
            if (free >= 0 && need > 0 && free < need)
            {
                var r = MessageBox.Show(
                    "Hedefte yeterli yer olmayabilir.\nGerekli ≈ " + FormatSize((ulong)need) +
                    "\nBoş alan ≈ " + FormatSize((ulong)free) + "\n\nYine de devam edilsin mi?",
                    "Yetersiz alan", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }

            // Video kaydı otomatik zaman çizgili .mp4 (süre + akıcı sarma). Tek ayar: FPS (UI'den önce oku)
            bool timed = true;
            int fps = 25;
            { string fv = (FpsBox.Text ?? "").Trim();
              if (int.TryParse(fv, out int fp) && fp >= 1 && fp <= 120) fps = fp; }

            // DHFS kaydı: program kendi karar verir — katalog varsa anında, yoksa hızlı dener,
            // bulamazsa kullanıcıya "kataloğa alayım mı?" diye sorar (aşağıda).

            // Kaydetme arka planda çalışır (UI donmaz), altta yeşil çubuk + hız + GB gider
            long totalBytes = need;
            int total = selected.Count;
            ScanBegin("Kaydediliyor…");
            ShowCenter("Veriler kaydediliyor / çıkarılıyor…  (" + total + " öğe)", canStop: true);
            _saveCts = new System.Threading.CancellationTokenSource();
            if (StopButton != null) { StopButton.Content = "■ Durdur"; StopButton.Visibility = Visibility.Visible; }
            SaveButton.IsEnabled = false;

            int[] st = new int[4];      // 0=files 1=errors 2=vids 3=vidMp4
            string[] dhfsDiag = { "" };  // DHFS çıkarma teşhisi (son kayıt)
            long[] live = { 0 };        // gerçek işlenen bayt (yazıcılardan)
            _onCopyBytes = d => System.Threading.Interlocked.Add(ref live[0], d);
            Action<long> prog = _onCopyBytes;

            // Çubuğu düzenli güncelle (gerçek bayt → düzgün dolar, MB + hız doğru)
            var pt = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            pt.Tick += (_, __) => SaveReport(System.Threading.Interlocked.Read(ref live[0]), totalBytes, st[0], total);
            pt.Start();

            // DHFS klipleri TEK taramada toplanır (parçalı disk) → foreach'te topla, sonra bir kez çıkar
            var dhfsClips = new System.Collections.Generic.List<DhfsExtract.Clip>();

            try
            {
                await System.Threading.Tasks.Task.Run(() =>
                {
                    foreach (FsEntry en in selected)
                    {
                        try
                        {
                            if (en.IsVideo && en.TypeText.StartsWith("Dahua DHFS"))
                            {
                                // DHFS kaydını toplu listeye ekle (tek disk taramasında hepsi birden çıkar)
                                string baseName = Path.GetFileNameWithoutExtension(Safe(en.Name));
                                int ch = ParseCamChannel(en.Name);     // CAM06_... → 5 (0 tabanlı)
                                if (ch >= 0)
                                    dhfsClips.Add(new DhfsExtract.Clip
                                    {
                                        Channel = ch, Start = en.CreatedValue, End = en.ModifiedValue,
                                        DstMp4 = Path.Combine(target, baseName + ".mp4"),
                                        SeekHint = en.RawOffset        // indeksten blok×2MB → doğrudan oraya git
                                    });
                            }
                            else if (en.IsVideo && en.TypeText == "Hikvision kayıt")
                            {
                                // Hikvision: blok offsetinden HAM H265/H264 Annex-B kopyala (zaten ham, oynar)
                                string baseName = Path.GetFileNameWithoutExtension(Safe(en.Name));
                                string dst = Path.Combine(target, baseName + ".mp4");
                                if (HikvisionReader.Extract(_openRaw, en.RawOffset, en.RawLength, dst, _saveCts.Token, out string hdg, prog))
                                { st[0]++; st[3]++; }
                                dhfsDiag[0] = "Hikvision · " + hdg;
                                st[2]++;
                            }
                            else if (en.IsVideo && en.TypeText == "Uware kayıt")
                            {
                                // PP/uware DVR: slotun ham H.265 bölgesini bayt bayt kopyala (zaten oynar)
                                string baseName = Path.GetFileNameWithoutExtension(Safe(en.Name));
                                string dst = Path.Combine(target, baseName + ".mp4");
                                if (UwareDvrReader.Extract(_openRaw, en.RawOffset, en.RawLength, dst, _saveCts.Token, out string udg, prog))
                                { st[0]++; st[3]++; }
                                dhfsDiag[0] = "Uware · " + udg;
                                st[2]++;
                            }
                            else if (en.IsVideo && en.Codec == "mjpeg-carve" && en.Tag is CarvedRec cr)
                            {
                                // Carving: JPEG kareleri taşı → MJPEG AVI (kamera adı/tarih OSD'den OCR ile)
                                string baseName = Path.GetFileNameWithoutExtension(Safe(en.Name));
                                string dst = Path.Combine(target, baseName + ".avi");
                                if (CarvingReader.ExtractJpeg(_openRaw, cr, dst, _saveCts.Token, out string cdg, prog))
                                { st[0]++; st[3]++; }
                                dhfsDiag[0] = "Carving · " + cdg;
                                st[2]++;
                            }
                            else if (en.IsVideo)
                            {
                                var noTok = System.Threading.CancellationToken.None;
                                string baseName = Path.GetFileNameWithoutExtension(Safe(en.Name));
                                if (timed && en.Codec != "mjpeg")
                                {
                                    // Zaman çizgili .mp4 (süre + akıcı sarma + doğru FPS) — ağır çekim/0sn çözülür
                                    string dst = Path.Combine(target, baseName + ".mp4");
                                    if (en.Codec == "h265") { if (HevcMp4.Mux(_openRaw, en.RawOffset, en.RawLength, dst, noTok, prog, fps)) st[3]++; }
                                    else { if (Mp4Muxer.Mux(_openRaw, en.RawOffset, en.RawLength, dst, noTok, prog, fps)) st[3]++; }
                                }
                                else if (en.Codec == "mjpeg")
                                {
                                    // MJPEG → .avi (FPS damgalı)
                                    string dst = Path.Combine(target, baseName + ".avi");
                                    VideoSave.SaveMjpegAvi(_openRaw, en.RawOffset, en.RawLength, dst, noTok, prog, fps);
                                }
                                else
                                {
                                    // Ham elementer: zaman + alan tasarrufu, VLC oynatır (süre 0)
                                    string dst = Path.Combine(target, Safe(en.Name));
                                    if (en.Codec == "h265") VideoSave.SaveH265(_openRaw, en.RawOffset, en.RawLength, dst, noTok, prog);
                                    else VideoSave.SaveH264(_openRaw, en.RawOffset, en.RawLength, dst, noTok, prog);
                                }
                                st[2]++; st[0]++;
                            }
                            else if (en.IsRaw)
                            {
                                ExtractRaw(en.RawOffset, en.RawLength, Path.Combine(target, OutName(en.Name)));
                                st[0]++;
                            }
                            else if (en.Deleted != null)
                            {
                                string sub = en.Deleted.Orphan ? "Lost & Found" : SafePath(en.Deleted.FolderPath);
                                string dir = string.IsNullOrEmpty(sub) ? target : Path.Combine(target, sub);
                                Directory.CreateDirectory(dir);
                                RecoverDeleted(en.Deleted, Path.Combine(dir, OutName(en.Name)));
                                st[0]++;
                            }
                            else if (en.IsDir)
                                CopyDir(en.FullPath, Path.Combine(target, Safe(en.Name)), ref st[0], ref st[1]);
                            else
                            {
                                CopyFile(en.FullPath, Path.Combine(target, OutName(en.Name)));
                                st[0]++;
                            }
                        }
                        catch { st[1]++; }
                    }

                    // DHFS: kullanıcının seçimine göre çıkar
                    if (dhfsClips.Count > 0)
                    {
                        var noTok = _saveCts.Token;     // Durdur düğmesiyle iptal edilebilir
                        int okn = 0;
                        string catPath = CatalogPath();

                        // 1) ÖNCE DOĞRUDAN: indeksteki blok×2MB konumunun etrafında küçük pencere (tüm disk değil).
                        //    Sağlam diskte saniyeler, tarama/katalog YOK.
                        string lastDg = "";
                        var missed = new System.Collections.Generic.List<DhfsExtract.Clip>();
                        foreach (var c in dhfsClips)
                        {
                            if (DhfsExtract.ExtractDirect(_openRaw, _totalSize, c.Channel,
                                    c.Start, c.End, c.SeekHint, c.DstMp4, fps, noTok, out lastDg, prog)) okn++;
                            else missed.Add(c);
                        }

                        if (missed.Count > 0 && DhfsCatalog.CatalogMatches(catPath, _totalSize))
                        {
                            // 2) Doğrudan bulunamayanlar için katalog varsa oradan (anında)
                            var cat = DhfsCatalog.LoadCatalog(catPath);
                            foreach (var c in missed.ToArray())
                            {
                                long su = DhfsExtract.ToUnix(c.Start) - 120, eu = DhfsExtract.ToUnix(c.End) + 120;
                                if (DhfsCatalog.ExtractFromCatalog(_openRaw, cat, c.Channel, su, eu, c.DstMp4, noTok, out lastDg, prog))
                                { okn++; missed.Remove(c); }
                            }
                        }

                        {
                            if (missed.Count > 0)
                            {
                                // HIZLI bulamadı → kullanıcıya kataloğu öner
                                bool build = false;
                                Dispatcher.Invoke(() =>
                                {
                                    build = MessageBox.Show(
                                        "Bu disk dağınık — " + missed.Count + " klip hızlı yolla bulunamadı.\n\n" +
                                        "Diski BİR KEZ kataloğa alayım mı? Tüm disk taranır (saatler sürebilir), " +
                                        "ama katalog kaydedilir ve bir daha taranmaz — sonraki tüm çıkarmalar ANINDA olur.\n\n" +
                                        "EVET = tara ve kataloğa al, sonra çıkar\nHAYIR = şimdilik bırak",
                                        "Diski kataloğa al", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
                                });

                                if (build)
                                {
                                    DhfsCatalog.BuildCatalog(_openRaw, _totalSize, catPath, noTok, prog, out string bdg);
                                    var cat = DhfsCatalog.LoadCatalog(catPath);
                                    string last = "";
                                    foreach (var c in missed)
                                    {
                                        long su = DhfsExtract.ToUnix(c.Start) - 120, eu = DhfsExtract.ToUnix(c.End) + 120;
                                        if (DhfsCatalog.ExtractFromCatalog(_openRaw, cat, c.Channel, su, eu, c.DstMp4, noTok, out last, prog)) okn++;
                                    }
                                    lastDg = bdg + " · " + last;
                                }
                                else lastDg = missed.Count + " klip bulunamadı (katalog reddedildi) · " + lastDg;
                            }
                            dhfsDiag[0] = "DOĞRUDAN · " + okn + "/" + dhfsClips.Count + " · " + lastDg;
                        }
                        st[2] += dhfsClips.Count; st[3] += okn; st[0] += okn;
                    }
                }, _saveCts.Token);
            }
            catch (OperationCanceledException) { Status.Text = "Kaydetme durduruldu."; }
            catch (Exception ex) { Status.Text = "Kaydetme hatası: " + ex.Message; }

            _onCopyBytes = null;
            pt.Stop();
            HideCenter();
            ScanEnd(false, st[0], "dosya");
            if (StopButton != null) { StopButton.Content = "■ Durdur"; StopButton.Visibility = Visibility.Collapsed; }
            try { _saveCts?.Dispose(); } catch { } _saveCts = null;
            SaveButton.IsEnabled = true;

            string bad = RetryStream.TotalBadSectors > 0 ? "  ·  " + RetryStream.TotalBadSectors + " hatalı sektör atlandı" : "";
            string vidNote = st[2] > 0
                ? "  ·  " + st[2] + " video " + (timed
                    ? "(zaman çizgili .mp4, " + fps + " FPS — süre + akıcı sarma; MJPEG .avi)"
                    : "(ham: .h264/.h265/.avi — VLC ile aç, süre 0)")
                : "";
            string dvrDiag = string.IsNullOrEmpty(dhfsDiag[0]) ? "" : "   [DHFS teşhis: " + dhfsDiag[0] + "]";
            Status.Text = st[0] + " dosya kaydedildi" + (st[1] > 0 ? "  ·  " + st[1] + " hata" : "") + bad + vidNote + "  →  " + target + dvrDiag;
            AppLog.Add("Kaydetme bitti: " + Status.Text);
        }

        // Kaydetme ilerlemesi (yeşil çubuk + kopyalanan/toplam + hız)
        private void SaveReport(long done, long total, int filesDone, int filesTotal)
        {
            double frac = total > 0 ? (double)done / total : (filesTotal > 0 ? (double)filesDone / filesTotal : 0);
            if (frac < 0) frac = 0; if (frac > 1) frac = 1;
            int pct = (int)(frac * 100);
            ScanBar.Value = pct;
            ScanPct.Text = "%" + pct;

            var el = _scanSw.Elapsed;
            double secs = el.TotalSeconds;
            string speed = secs > 0.5 && done > 0 ? FmtSpeed(done / secs) : "—";
            string sz = total > 0 ? "  ·  " + FormatSize((ulong)done) + " / " + FormatSize((ulong)total) : "";
            ScanInfo.Text = filesDone + "/" + filesTotal + " dosya" + sz + "  ·  " + speed + "  ·  geçen " + FmtDur(el);

            // Ekran ortasındaki büyük çubuğa da yansıt (donmuş mu bitti mi hep görünsün)
            UpdateCenter(frac);
            SetCenterInfo(filesDone + "/" + filesTotal + " dosya" + sz + "  ·  " + speed);
        }

        // Diskten ham [offset, length] bölgesini dosyaya yaz (Hikvision kaydı)
        private void ExtractRaw(long offset, long length, string dstPath)
        {
            using Stream s = _openRaw(512);
            using var outF = new FileStream(dstPath, FileMode.Create, FileAccess.Write);
            s.Position = offset;
            byte[] buf = new byte[1 << 20];
            long remaining = length;
            while (remaining > 0)
            {
                int want = (int)Math.Min(buf.Length, remaining);
                int got = s.Read(buf, 0, want);
                if (got <= 0) break;
                outF.Write(buf, 0, got);
                remaining -= got;
                _onCopyBytes?.Invoke(got);
            }
        }

        private void CopyDir(string srcDir, string dstDir, ref int files, ref int errors)
        {
            if (_fs == null) return;
            Directory.CreateDirectory(dstDir);
            foreach (string f in _fs.GetFiles(srcDir))
            {
                try { CopyFile(f, Path.Combine(dstDir, Safe(NameOf(f)))); files++; }
                catch { errors++; }
            }
            foreach (string d in _fs.GetDirectories(srcDir))
            {
                try { CopyDir(d, Path.Combine(dstDir, Safe(NameOf(d))), ref files, ref errors); }
                catch { errors++; }
            }
        }

        private void CopyFile(string srcPath, string dstPath)
        {
            if (_fs == null) return;
            using Stream src = _fs.OpenFile(srcPath, FileMode.Open, FileAccess.Read);
            using FileStream dst = File.Create(dstPath);
            byte[] buf = new byte[1 << 20];
            int got;
            while ((got = src.Read(buf, 0, buf.Length)) > 0)
            {
                dst.Write(buf, 0, got);
                _onCopyBytes?.Invoke(got);
            }
        }

        private void RecoverDeleted(MftFile mf, string dstPath)
        {
            using FileStream dst = File.Create(dstPath);

            if (mf.Resident)
            {
                if (mf.ResidentData != null) dst.Write(mf.ResidentData, 0, mf.ResidentData.Length);
                return;
            }
            if (_openVolume is null) return;

            using Stream ps = _openVolume();
            long remaining = (long)mf.Size;
            byte[] buf = new byte[1 << 20];

            foreach (DataRun run in mf.Runs)
            {
                if (remaining <= 0) break;
                long bytes = run.Clusters * _clusterSize;

                if (run.Sparse)
                {
                    long z = Math.Min(bytes, remaining);
                    byte[] zero = new byte[(int)Math.Min(z, buf.Length)];
                    long w = 0;
                    while (w < z) { int chunk = (int)Math.Min(zero.Length, z - w); dst.Write(zero, 0, chunk); w += chunk; _onCopyBytes?.Invoke(chunk); }
                    remaining -= z;
                }
                else
                {
                    ps.Position = run.Lcn * _clusterSize;
                    long toRead = Math.Min(bytes, remaining);
                    long r = 0;
                    while (r < toRead)
                    {
                        int chunk = (int)Math.Min(buf.Length, toRead - r);
                        int got = ps.Read(buf, 0, chunk);
                        if (got <= 0) break;
                        dst.Write(buf, 0, got);
                        r += got;
                        _onCopyBytes?.Invoke(got);
                    }
                    remaining -= r;
                }
            }
        }

        private static string Safe(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return string.IsNullOrWhiteSpace(name) ? "adsiz" : name;
        }

        // Klasör yolunu segment segment güvenli yap ("a\b\c" → güvenli alt yol)
        private static string SafePath(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return "";
            var parts = folder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++) parts[i] = Safe(parts[i]);
            return string.Join(Path.DirectorySeparatorChar, parts);
        }

        private static string NameOf(string path)
        {
            string t = path.TrimEnd('\\');
            int i = t.LastIndexOf('\\');
            return i < 0 ? t : t.Substring(i + 1);
        }

        private static string Short(string s)
        {
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 120 ? s.Substring(0, 120) : s;
        }

        private static string FormatSize(ulong bytes)
        {
            if (bytes == 0) return "0";
            double tb = bytes / 1_000_000_000_000.0;
            if (tb >= 1.0) return tb.ToString("0.00") + " TB";
            double gb = bytes / 1_000_000_000.0;
            if (gb >= 1.0) return gb.ToString("0.0") + " GB";
            double mb = bytes / 1_000_000.0;
            if (mb >= 1.0) return mb.ToString("0.0") + " MB";
            double kb = bytes / 1000.0;
            return kb.ToString("0") + " KB";
        }

        // Sekme kapanınca çağrılır (kaynakları serbest bırak)
        // İmaj/kurtarma CASE sekmesinde: CCTV/carve/hex/log araçlarını gizle (UFS gibi sade)
        // case içinde: sağ tık → alttaki Harita sekmesi (full sektör); dışarıda eski hex görünümü
        public event Action? MapRequested;
        private void ShowCaseMap_Click(object sender, RoutedEventArgs e)
        {
            if (MapRequested != null) MapRequested.Invoke();
            else Map_Click(sender, e);
        }

        public void SetCaseMode()
        {
            foreach (var e in new System.Windows.UIElement?[] { VideoButton, MapButton, DumpButton, CalibButton, DiagButton, PlayerButton, LogButton, ExtBorder, FpsBorder })
                if (e != null) e.Visibility = System.Windows.Visibility.Collapsed;
        }

        public void Cleanup()
        {
            _sizeCts?.Cancel();
            _searchCts?.Cancel();
            _quickCts?.Cancel();
            _carveCts?.Cancel();
            _videoCts?.Cancel();
            _dotTimer?.Stop();
            _fs?.Dispose();
            _volStream?.Dispose();
        }
    }
}
