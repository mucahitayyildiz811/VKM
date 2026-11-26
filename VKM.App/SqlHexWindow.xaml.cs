using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VKM.App
{
    public partial class SqlHexWindow : Window
    {
        private readonly string _path;
        private long _pageNo;
        private readonly long _pageCount;

        public SqlHexWindow(string path)
        {
            InitializeComponent();
            _path = path;
            FileLabel.Text = path;
            try { _pageCount = new FileInfo(path).Length / MssqlPage.PAGE; } catch { _pageCount = 0; }
            Loaded += (_, __) => LoadPage();
        }

        private void Go_Click(object sender, RoutedEventArgs e)
        {
            if (long.TryParse(PageBox.Text.Trim(), out long n)) { _pageNo = Math.Max(0, n); LoadPage(); }
        }
        private void Prev_Click(object sender, RoutedEventArgs e) { if (_pageNo > 0) { _pageNo--; LoadPage(); } }
        private void Next_Click(object sender, RoutedEventArgs e) { _pageNo++; LoadPage(); }

        private void LoadPage()
        {
            try
            {
                byte[] p = new byte[MssqlPage.PAGE];
                long off = _pageNo * MssqlPage.PAGE;
                using (var fs = File.OpenRead(_path))
                {
                    if (off >= fs.Length) { HdrText.Text = "Sayfa dosya dışında."; HexText.Text = ""; return; }
                    fs.Position = off;
                    int n = 0; while (n < p.Length) { int r = fs.Read(p, n, p.Length - n); if (r <= 0) break; n += r; }
                }
                PageBox.Text = _pageNo.ToString();

                var h = MssqlPage.Decode(p, (int)_pageNo);
                var sb = new StringBuilder();
                sb.Append("Sayfa ").Append(_pageNo).Append(" / ").Append(_pageCount)
                  .Append("   offset ").Append(off).Append(" (0x").Append(off.ToString("X")).Append(")\n");
                sb.Append("Tip: ").Append(h.TypeName).Append("   Level: ").Append(h.Level)
                  .Append("   SlotCnt: ").Append(h.SlotCnt).Append("   FreeData: ").Append(h.FreeData)
                  .Append("   Pminlen: ").Append(h.Pminlen).Append("   IndexId: ").Append(h.IndexId)
                  .Append("   ObjId: ").Append(h.ObjId);
                if (h.Slots.Length > 0)
                {
                    sb.Append("\nKayıt offsetleri (slot): ");
                    int show = Math.Min(h.Slots.Length, 24);
                    for (int i = 0; i < show; i++) sb.Append(h.Slots[i]).Append(' ');
                    if (h.Slots.Length > show) sb.Append("… +").Append(h.Slots.Length - show);
                }
                sb.Append("   (başlık yorumu — DBCC PAGE ile doğrulanabilir)");
                HdrText.Text = sb.ToString();

                HexText.Text = Hex(p, off, p.Length);
            }
            catch (Exception ex) { HdrText.Text = "Hata: " + ex.Message; HexText.Text = ""; }
        }

        private static string Hex(byte[] b, long baseOff, int len)
        {
            var sb = new StringBuilder(len / 16 * 80);
            for (int i = 0; i < len; i += 16)
            {
                sb.Append((baseOff + i).ToString("X8")).Append("  ");
                for (int j = 0; j < 16; j++)
                {
                    if (i + j < len) sb.Append(b[i + j].ToString("X2")).Append(' ');
                    else sb.Append("   ");
                    if (j == 7) sb.Append(' ');
                }
                sb.Append(' ');
                for (int j = 0; j < 16 && i + j < len; j++)
                {
                    byte c = b[i + j];
                    sb.Append(c >= 32 && c < 127 ? (char)c : '.');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        private async void Rows_Click(object sender, RoutedEventArgs e)
        {
            if (!long.TryParse(ObjBox.Text.Trim(), out long objId) || objId <= 0)
            { MessageBox.Show("Geçerli bir obj id yaz (Sayfa Analizi → en büyük nesneler listesinden)."); return; }

            var d = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Satırlar nereye kaydedilsin",
                Filter = "CSV (*.csv)|*.csv|Tüm dosyalar (*.*)|*.*",
                FileName = "obj" + objId + "_satirlar.csv"
            };
            if (d.ShowDialog() != true) return;
            string csv = d.FileName;

            RowsBtn.IsEnabled = false; TablesBtn.IsEnabled = false; AnalyzeBtn.IsEnabled = false;
            Busy.Visibility = Visibility.Visible; Busy.IsIndeterminate = false; Busy.Value = 0;
            var log = new StringBuilder();
            void Log(string s) { Dispatcher.Invoke(() => { log.Append(s).Append('\n'); HexText.Text = log.ToString(); HexText.ScrollToEnd(); }); }
            var prog = new Progress<double>(x => Busy.Value = x * 100);
            HdrText.Text = "Satırlar çıkarılıyor (obj " + objId + ")…";

            int n = 0; string err = "";
            await Task.Run(() =>
            {
                try { n = RecordExtract.ExtractTable(_path, objId, csv, Log, x => ((IProgress<double>)prog).Report(x), CancellationToken.None); }
                catch (Exception ex) { err = ex.Message; }
            });

            Busy.Visibility = Visibility.Collapsed; RowsBtn.IsEnabled = true; TablesBtn.IsEnabled = true; AnalyzeBtn.IsEnabled = true;
            if (!string.IsNullOrEmpty(err)) { Log("✗ Hata: " + err); HdrText.Text = "Satır çıkarma hatası."; }
            else { HdrText.Text = n.ToString("N0") + " satır çıktı → " + csv; MessageBox.Show(n.ToString("N0") + " satır CSV'ye yazıldı:\n\n" + csv, "Bitti", MessageBoxButton.OK, MessageBoxImage.Information); }
        }

        private async void Tables_Click(object sender, RoutedEventArgs e)
        {
            TablesBtn.IsEnabled = false; AnalyzeBtn.IsEnabled = false;
            Busy.Visibility = Visibility.Visible; Busy.IsIndeterminate = false; Busy.Value = 0;
            var log = new StringBuilder();
            void Log(string s) { Dispatcher.Invoke(() => { log.Append(s).Append('\n'); HexText.Text = log.ToString(); HexText.ScrollToEnd(); }); }
            var prog = new Progress<double>(x => Busy.Value = x * 100);
            HdrText.Text = "Tablo adları çıkarılıyor…";

            System.Collections.Generic.List<(long id, string name)>? objs = null; string err = "";
            await Task.Run(() =>
            {
                try { objs = MssqlPage.ExtractObjects(_path, Log, x => ((IProgress<double>)prog).Report(x), CancellationToken.None); }
                catch (Exception ex) { err = ex.Message; }
            });

            Busy.Visibility = Visibility.Collapsed; TablesBtn.IsEnabled = true; AnalyzeBtn.IsEnabled = true;
            if (objs == null) { HdrText.Text = "Hata: " + err; return; }

            var sb = new StringBuilder();
            sb.Append("══════ NESNE / TABLO LİSTESİ (sysschobjs) ══════\n");
            sb.Append("Bulunan nesne: ").Append(objs.Count).Append("\n\n");
            sb.Append(string.Format("{0,-12}  {1}\n", "obj id", "ad"));
            foreach (var (id, name) in objs) sb.Append(string.Format("{0,-12}  {1}\n", id, name));
            sb.Append("\n(ön liste — ad = kayıttaki en uzun metin. Sıradaki adım: kolonlar + satır çıkarma.)");
            HexText.Text = sb.ToString();
            HdrText.Text = "Tablo listesi çıktı (" + objs.Count + " nesne). Tanıdık tablo adlarını görüyor musun?";
        }

        private async void Analyze_Click(object sender, RoutedEventArgs e)
        {
            AnalyzeBtn.IsEnabled = false;
            Busy.Visibility = Visibility.Visible; Busy.IsIndeterminate = false; Busy.Value = 0;
            var log = new StringBuilder();
            void Log(string s) { Dispatcher.Invoke(() => { log.Append(s).Append('\n'); HexText.Text = log.ToString(); HexText.ScrollToEnd(); }); }

            var prog = new Progress<double>(x => Busy.Value = x * 100);
            MssqlPage.ScanResult? res = null; string err = "";
            HdrText.Text = "Sayfa analizi çalışıyor…";
            await Task.Run(() =>
            {
                try { res = MssqlPage.Scan(_path, x => ((IProgress<double>)prog).Report(x), Log, CancellationToken.None); }
                catch (Exception ex) { err = ex.Message; }
            });

            Busy.Visibility = Visibility.Collapsed; AnalyzeBtn.IsEnabled = true;
            if (res != null) { var l2 = new StringBuilder(log.ToString()); MssqlPage.Report(res, s => l2.Append(s).Append('\n')); HexText.Text = l2.ToString(); HdrText.Text = "Analiz bitti. (Sayfa görüntülemeye dönmek için üstten bir sayfa no girip Git'e bas.)"; }
            else { HdrText.Text = "Analiz hatası: " + err; }
        }
    }
}
