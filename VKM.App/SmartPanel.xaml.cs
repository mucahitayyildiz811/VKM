using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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

        private void Disk_Changed(object sender, SelectionChangedEventArgs e) { Load(); ResetSurf(); }
        private void Refresh_Click(object sender, RoutedEventArgs e) => Load();

        private void SelfTestShort_Click(object sender, RoutedEventArgs e) => StartSelfTest(0x01, "Kısa öz-test başladı (~1-2 dk, diskte arka planda). Sonucu 'Yenile' ile Öznitelikler'de gör.");
        private void SelfTestLong_Click(object sender, RoutedEventArgs e) => StartSelfTest(0x02, "Uzun öz-test başladı (tüm yüzey, saatler sürebilir). Disk kullanılabilir; durumu 'Yenile' ile izle.");

        private void StartSelfTest(byte sub, string msg)
        {
            if (DiskCombo.SelectedItem is not DiskInfo d) { MessageBox.Show("Önce bir disk seç."); return; }
            if (AtaSmart.StartSelfTest(d.DeviceId, sub)) MessageBox.Show(msg, "Öz-test");
            else MessageBox.Show("Öz-test başlatılamadı — yönetici gerekir ya da disk ATA öz-test geçirmiyor (USB köprüsü olabilir).", "Öz-test");
        }

        // SMART öz-test durum baytını metne çevir
        static string SelfTestText(int b)
        {
            if (b < 0) return "";
            int st = (b >> 4) & 0xF;
            if (st == 15) return "öz-test DEVAM EDİYOR (kalan ~%" + ((b & 0xF) * 10) + ")";
            return st switch
            {
                0 => "son öz-test: ✓ sorunsuz (ya da hiç yapılmadı)",
                1 => "son öz-test: kullanıcı iptal etti",
                2 => "son öz-test: reset ile kesildi",
                3 => "son öz-test: ölümcül hata",
                4 => "son öz-test: tamamlanamadı",
                5 => "son öz-test: ⚠ elektriksel hata",
                6 => "son öz-test: ⚠ servo/seek hatası",
                7 => "son öz-test: ⚠ OKUMA HATASI (bad sektör bulundu)",
                8 => "son öz-test: ⚠ fiziksel hasar",
                _ => "son öz-test: durum " + st
            };
        }

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
                sd = SmartReader.ReadViaAta(d.DeviceId);   // WMI boşsa ATA pass-through ile dene
            if (sd == null || !sd.HasData || sd.Attrs.Count == 0)
            {
                SummaryText.Text = IsAdmin()
                    ? "SMART okunamadı — program yönetici (✓) ama bu disk SMART geçirmiyor: SAS/RAID denetleyici ya da USB köprüsü ardında olabilir. SATA'ya doğrudan takıp dene."
                    : "⚠ Program YÖNETİCİ DEĞİL. Visual Studio'dan test ediyorsan: VS'yi kapat → 'Yönetici olarak çalıştır' ile aç → tekrar başlat. Ya da derlenmiş VeriKurtarmaMerkezi.exe'yi çift tıkla (UAC sorar, yönetici açılır).";
                return;
            }

            AttrList.ItemsSource = sd.Attrs;
            string poh = sd.PowerOnHours.HasValue ? sd.PowerOnHours.Value.ToString("N0") + " saat" : "-";
            string temp = sd.TempC.HasValue ? sd.TempC.Value + " °C" : "-";
            var id = AtaSmart.Identify(d.DeviceId);
            string devLine = "";
            if (id.HasValue)
            {
                string t = id.Value.rotation == 1 ? "SSD" : id.Value.rotation > 1 ? "HDD " + id.Value.rotation + " RPM" : "disk";
                devLine = "Tür: " + t + "   ·   FW " + id.Value.fw + "   ·   Seri " + id.Value.serial + "\n";
            }
            int stB = AtaSmart.SelfTestStatusByte(d.DeviceId);
            string stLine = stB >= 0 ? SelfTestText(stB) + "\n" : "";
            SummaryText.Text = devLine + stLine +
                "Durum: " + (sd.PredictFailure ? "⚠ ARIZA TAHMİNİ" : "✓ Normal") +
                "      Çalışma: " + poh + "      Sıcaklık: " + temp + "\n" +
                "Yeniden atanan (5): " + (sd.Reallocated?.ToString() ?? "-") +
                "      Bekleyen (197): " + (sd.Pending?.ToString() ?? "-") +
                "      Düzeltilemez (198): " + (sd.Uncorrectable?.ToString() ?? "-") +
                "      CRC (199): " + (sd.Crc?.ToString() ?? "-") + "\n" +
                "🧠 Tanı: " + DiagnoseHealth(sd);
        }

        static bool IsAdmin()
        {
            try
            {
                using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
                return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        // SMART tanısı: sağlık kötüyse NEDEN + ne yapmalı (bad yokken bile)
        private static ulong RawOf(SmartData sd, int id) { foreach (var a in sd.Attrs) if (a.Id == id) return a.Raw; return 0; }
        private static int CurOf(SmartData sd, int id) { foreach (var a in sd.Attrs) if (a.Id == id) return a.Current; return -1; }

        private static string DiagnoseHealth(SmartData sd)
        {
            ulong realloc = sd.Reallocated ?? 0, pending = sd.Pending ?? 0, crc = sd.Crc ?? 0;
            ulong rep187 = RawOf(sd, 187), to188 = RawOf(sd, 188), spinretry = RawOf(sd, 10);
            int wearCur = CurOf(sd, 233); if (wearCur < 0) wearCur = CurOf(sd, 177);

            if (sd.PredictFailure) return "⚠ PredictFailure — disk 'ölüyorum' diyor. ÖNCE VERİYİ KURTAR, maskeleme çözmez.";
            if (rep187 > 0 || to188 > 0) return "Düzeltilemez (187)/timeout (188) var — kafa/elektronik bozuluyor. Veriyi kurtar.";
            if (spinretry > 0) return "Spin retry (10) var — motor/yatak sorunu. Veriyi kurtar.";
            if (wearCur >= 0 && wearCur <= 15) return "SSD aşınması yüksek — yeni SSD'ye klonla, 'tamir' yok.";
            if (crc > 0 && realloc == 0 && pending == 0) return "CRC (199) var, bad yok — KABLO/bağlantı sorunu. Kabloyu/portu değiştir, tekrar ölç.";
            if (pending > 0 && realloc == 0) return "Bekleyen sektör (197) var, realloc 0 — Refresh (yeniden yaz) temizler, kapasite aynı.";
            if (realloc > 0) return "Yeniden atanmış sektör (5) var — Yüzey Tara, sonra remap/kes. Sebebe göre karar.";
            return "Belirgin kritik SMART sinyali yok. Performans kötüyse Yüzey Tara + kablo/port kontrol.";
        }

        // ---------------- Yüzey taraması ----------------
        static int C(byte r, byte g, byte b) => unchecked((int)0xFF000000 | (r << 16) | (g << 8) | b);
        static int ColInt(Color c) => unchecked((c.A << 24) | (c.R << 16) | (c.G << 8) | c.B);
        const int CELL = 11;
        private WriteableBitmap? _bmp;
        private int[]? _buf;
        private int _bw, _bh;
        private SurfaceScan? _scan;
        private System.Windows.Threading.DispatcherTimer? _timer;

        private void ResetSurf()
        {
            _scan = null;
            if (ScanBtn != null) { ScanBtn.IsEnabled = true; ScanStopBtn.IsEnabled = false; }
            if (ScanStat != null) ScanStat.Text = "";
            if (RecoBorder != null) RecoBorder.Visibility = Visibility.Collapsed;
            _bmp = null; _bw = _bh = 0;
            if (SurfMap != null) SurfMap.Source = null;
        }

        private void Scan_Click(object sender, RoutedEventArgs e)
        {
            if (DiskCombo.SelectedItem is not DiskInfo d) { MessageBox.Show("Önce bir disk seç."); return; }
            if (_scan != null) return;
            long total = (long)d.SizeBytes;
            int sector = d.BytesPerSector <= 0 ? 512 : d.BytesPerSector;
            string dev = d.DeviceId;
            _scan = new SurfaceScan(() => PhysicalDisk.OpenReadOnly(dev, total, sector), total, sector);
            Log.Info("Yüzey Tara: " + d.Model + " · kutu " + (int)SurfBox.ActualWidth + "x" + (int)SurfBox.ActualHeight +
                     " · blok " + (_scan.BlockBytes / 1024 / 1024) + "MB · " + _scan.Blocks.ToString("N0") + " blok");

            if (RecoBorder != null) RecoBorder.Visibility = Visibility.Collapsed;
            ScanBtn.IsEnabled = false; ScanStopBtn.IsEnabled = true;
            _timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => PaintSurf();
            _timer.Start();

            var sc = _scan;
            Task.Run(() => sc.Run(() => { }));
        }

        private void ScanStop_Click(object sender, RoutedEventArgs e)
        {
            if (_scan != null) _scan.Stopping = true;
            _refreshCancel = true;
        }

        // ---------------- HPA kes / geri al ----------------
        private void HpaCut_Click(object sender, RoutedEventArgs e)
        {
            if (DiskCombo.SelectedItem is not DiskInfo d) { MessageBox.Show("Önce bir disk seç."); return; }
            if (_recoCutLba <= 0) { MessageBox.Show("Önce 'Yüzey Tara' çalıştır — kesim noktası taramadan gelir."); return; }
            int sector = d.BytesPerSector <= 0 ? 512 : d.BytesPerSector;
            try
            {
                long nat = HpaControl.ReadNativeMax(d.DeviceId);
                long newMax = _recoCutLba;
                if (newMax >= nat) { MessageBox.Show("Kesim noktası zaten diskin sonunda — kesecek bir şey yok."); return; }
                long lostBytes = (nat - newMax) * sector;
                var r = MessageBox.Show(
                    "HPA KES — " + d.Model + "\n\n" +
                    "Yeni azami LBA: " + newMax.ToString("N0") + "\n" +
                    "Şu anki (native) azami LBA: " + nat.ToString("N0") + "\n" +
                    "Gizlenecek kapasite: ~" + Fmt(lostBytes) + "\n\n" +
                    "Sondaki bad bölge kullanıcı alanı DIŞINA çıkar → kalan %100.\n" +
                    "GERİ ALINABİLİR ('↩ Tam kapasite' ile).\n\n" +
                    "Uygulansın mı?",
                    "HPA Kes", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
                HpaControl.SetMax(d.DeviceId, newMax);
                MessageBox.Show("Yapıldı — yeni azami LBA ayarlandı. Diski çıkar-tak ya da 'Yenile' ile yeni kapasiteyi gör.", "HPA");
            }
            catch (Exception ex) { MessageBox.Show("HPA kes başarısız: " + ex.Message, "HPA"); }
        }

        private void HpaRestore_Click(object sender, RoutedEventArgs e)
        {
            if (DiskCombo.SelectedItem is not DiskInfo d) { MessageBox.Show("Önce bir disk seç."); return; }
            var r = MessageBox.Show("Diskin TAM (native) kapasitesi geri getirilecek (HPA kaldırılır). Devam?",
                                    "Tam kapasite", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) return;
            try
            {
                long nat = HpaControl.RestoreNative(d.DeviceId);
                MessageBox.Show("Tam kapasite geri getirildi. Native azami LBA: " + nat.ToString("N0") + "\n(Diski çıkar-tak ki Windows yeni kapasiteyi görsün.)", "HPA");
            }
            catch (Exception ex) { MessageBox.Show("Geri alma başarısız: " + ex.Message, "HPA"); }
        }

        // ---------------- Refresh (pending temizle, kapasite aynı) ----------------
        private bool _refreshing, _refreshCancel;
        private void DoRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_refreshing || _scan != null) return;
            if (DiskCombo.SelectedItem is not DiskInfo d) { MessageBox.Show("Önce bir disk seç."); return; }

            var r = MessageBox.Show(
                "REFRESH — " + d.Model + "  (" + d.SizeText + ")\n\n" +
                "Diskin her sektörü AYNI veriyle yeniden yazılacak (VERİ KAYBI YOK). " +
                "Zayıf/bekleyen sektörler düzelir, kapasite aynı kalır.\n\n" +
                "• Yönetici gerekir.\n" +
                "• Kullanımdaki/bağlı bölgeler atlanabilir — en iyi sonuç için diski Disk Yönetimi'nden ÇEVRİMDIŞI yap.\n" +
                "• Doğru diski seçtiğinden emin ol.\n\n" +
                "Devam edilsin mi?",
                "Refresh", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (r != MessageBoxResult.Yes) return;

            _refreshing = true; _refreshCancel = false;
            if (ScanBtn != null) ScanBtn.IsEnabled = false;
            RefreshSurfBtn.IsEnabled = false; ScanStopBtn.IsEnabled = true;
            if (RecoBorder != null) RecoBorder.Visibility = Visibility.Collapsed;

            string dev = d.DeviceId; long total = (long)d.SizeBytes; int sector = d.BytesPerSector <= 0 ? 512 : d.BytesPerSector;
            Task.Run(() =>
            {
                try
                {
                    DiskRepair.Refresh(dev, total, sector,
                        (w, f, done, blocks) => Dispatcher.BeginInvoke(() =>
                        {
                            double pct = blocks > 0 ? done / (double)blocks * 100 : 0;
                            ScanStat.Text = "Refresh %" + pct.ToString("0.0") + "   yazıldı " + w.ToString("N0") + "   atlandı " + f.ToString("N0");
                        }),
                        () => _refreshCancel);
                }
                catch (Exception ex) { Dispatcher.BeginInvoke(() => MessageBox.Show("Refresh hatası: " + ex.Message)); }
                Dispatcher.BeginInvoke(() =>
                {
                    _refreshing = false;
                    RefreshSurfBtn.IsEnabled = true;
                    if (ScanBtn != null) ScanBtn.IsEnabled = true;
                    ScanStopBtn.IsEnabled = false;
                    ScanStat.Text += _refreshCancel ? "  — durduruldu" : "  — bitti";
                });
            });
        }

        private void PaintSurf()
        {
            if (_scan == null || SurfMap == null || SurfBox == null) return;
            int W = (int)SurfBox.ActualWidth - 12, H = (int)SurfBox.ActualHeight - 12;   // dıştaki kutudan ölç (Image boşken 0 ölçüyor)
            if (W < CELL || H < CELL) return;
            int cols = W / CELL, rows = H / CELL;
            if (cols < 1 || rows < 1) return;
            int cnt = cols * rows, bw = W, bh = H;
            if (_bmp == null || bw != _bw || bh != _bh)
            {
                _bw = bw; _bh = bh;
                _bmp = new WriteableBitmap(bw, bh, 96, 96, PixelFormats.Bgra32, null);
                _buf = new int[bw * bh];
                SurfMap.Source = _bmp;
            }
            int bg = C(0xe6, 0xea, 0xee), cPend = C(0xc6, 0xce, 0xd6);   // ızgara + okunmamış (görünür gri)
            int cOk = ColInt(Theme.Col("Ok")), cWarn = ColInt(Theme.Col("Warn")), cBad = ColInt(Theme.Col("Bad"));
            var buf = _buf!;
            for (int i = 0; i < buf.Length; i++) buf[i] = bg;

            var st = _scan.Status; int blocks = _scan.Blocks; long cur = _scan.Cur;
            for (int k = 0; k < cnt; k++)
            {
                long b0 = (long)k * blocks / cnt, b1 = (long)(k + 1) * blocks / cnt;
                if (b1 <= b0) b1 = b0 + 1;
                int bad = 0, slow = 0, ok = 0, pend = 0;
                for (long b = b0; b < b1 && b < blocks; b++)
                {
                    byte s = st[b];
                    if (s == 3) bad++; else if (s == 2) slow++; else if (s == 1) ok++; else pend++;
                }
                int clr = bad > 0 ? cBad : slow > 0 ? cWarn : ok > 0 ? cOk : cPend;
                int cx = (k % cols) * CELL, cy = (k / cols) * CELL;
                for (int y = 0; y < CELL - 1; y++) { int row = (cy + y) * bw + cx; for (int x = 0; x < CELL - 1; x++) buf[row + x] = clr; }
            }
            _bmp!.WritePixels(new Int32Rect(0, 0, bw, bh), buf, bw * 4, 0);

            double pct = blocks > 0 ? _scan.Done / (double)blocks * 100 : 0;
            ScanStat.Text = "%" + pct.ToString("0.0") + "   iyi " + _scan.OkCount.ToString("N0") +
                            "   yavaş " + _scan.SlowCount.ToString("N0") + "   bad " + _scan.BadCount.ToString("N0");

            if (_scan.Cur < 0 && (_scan.Done >= blocks || _scan.Stopping))   // bitti ya da durduruldu
            {
                _timer?.Stop();
                ScanBtn.IsEnabled = true; ScanStopBtn.IsEnabled = false;
                var done = _scan;
                _scan = null;
                if (!done.Stopping) ShowDecision(done);
            }
        }

        static string Fmt(long b)
        {
            double v = b; string[] u = { "B", "KB", "MB", "GB", "TB", "PB" }; int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return v.ToString(v < 10 ? "0.0" : "0") + " " + u[i];
        }

        // Karar motoru: bad dağılımına göre "ne yapmalı" önerir (uygulama bir sonraki adım, hep onaylı)
        private long _recoCutLba = -1;
        private void ShowDecision(SurfaceScan sc)
        {
            int blocks = sc.Blocks; var st = sc.Status;
            int firstBad = -1;
            for (int b = 0; b < blocks; b++) if (st[b] == 3) { firstBad = b; break; }
            double badPct = blocks > 0 ? sc.BadCount / (double)blocks * 100 : 0;
            _recoCutLba = -1;
            if (HpaCutBtn != null) HpaCutBtn.IsEnabled = false;

            string msg;
            if (sc.BadCount == 0 && sc.SlowCount == 0)
                msg = "✓ Disk temiz — işlem gerekmez.";
            else if (sc.BadCount == 0)
                msg = "Zayıf (yavaş) bölgeler var, bad yok — Refresh ile toparlanabilir. (kapasite aynı)";
            else
            {
                long cutBytes = sc.Total - (long)firstBad * sc.BlockBytes;
                double lostPct = blocks > 0 ? (blocks - firstBad) / (double)blocks * 100 : 0;
                if (badPct > 15)
                    msg = "⚠ Ağır hasar (%" + badPct.ToString("0.0") + " bad). Maskeleme kalıcı olmaz — ÖNCE VERİYİ KURTAR, sonra karar ver.";
                else if (firstBad > blocks * 0.5 && lostPct <= 20)
                {
                    msg = "Öneri: bad diskin SONUNDA. Son ~" + Fmt(cutBytes) + " kesilsin (HPA) → kalan %100. Kapasite düşer ama geri alınabilir. → '✂ HPA Kes' ile uygula.";
                    _recoCutLba = sc.LbaOf(firstBad) - 1;   // son sağlam LBA
                    if (HpaCutBtn != null) HpaCutBtn.IsEnabled = _recoCutLba > 0;
                }
                else if (sc.BadCount <= Math.Max(50, (int)(blocks * 0.003)))
                    msg = "Öneri: az ve dağınık (" + sc.BadCount + " blok). REMAP/gizle (yeniden yaz) → spare'e taşınır, kapasite AYNI kalır.";
                else
                    msg = "Bad ORTADA / çok. HPA ~" + Fmt(cutBytes) + " sağlam alanı götürür → önerilmez. Vendor/kafa dışlama gerekir (WD adımı).";
            }
            RecoText.Text = msg + "   —   (uygulama/onay sıradaki adımda ekleniyor)";
            RecoBorder.Visibility = Visibility.Visible;
        }
    }
}
