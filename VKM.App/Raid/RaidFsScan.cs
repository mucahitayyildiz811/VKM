using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace VKM.App
{
    // ─────────────────────────────────────────────────────────────────────────
    //  KAYIP BÖLÜM / DOSYA SİSTEMİ / RAID-LVM ÜYESİ ARAMA (UFS "Scan for lost data" benzeri)
    //  Silinmiş bölüm tablosu, formatlanmış disk, NAS'ın içindeki birimler, md/LVM parçaları.
    //  Bulunan md/LVM üyeleri otomatik dizi/birim olarak kurulur.
    // ─────────────────────────────────────────────────────────────────────────
    public class FsFound
    {
        public string Fam = "";
        public long Start;          // bayt
        public long Length;         // bayt (bilinmiyorsa aygıt sonuna kadar)
        public string Label = "";
        public string Key = "";     // tekrarı önlemek için
    }

    public static class RaidFsScan
    {
        public static readonly (string key, string text)[] Families =
        {
            ("ntfs", "Microsoft NTFS"), ("refs", "Microsoft ReFS"), ("exfat", "Microsoft exFAT"), ("fat", "FAT / FAT32"),
            ("ext", "Ext2 / Ext3 / Ext4"), ("xfs", "SGI XFS"), ("btrfs", "Btrfs"), ("reiser", "ReiserFS"), ("jfs", "JFS"),
            ("ufs", "UFS / UFS2 (BSD)"), ("f2fs", "F2FS"), ("hfs", "Mac HFS+ / HFSX"), ("apfs", "Apple APFS"),
            ("zfs", "ZFS / OpenZFS"), ("vmfs", "VMware VMFS"), ("md", "Linux RAID (md) üyesi"), ("lvm", "LVM fiziksel birim"),
            ("crypt", "Şifreli: BitLocker / LUKS"), ("cctv", "CCTV: Dahua DHFS / Hikvision"), ("mbr", "Bölüm tablosu (MBR/GPT)")
        };

        public static void Scan(Func<Stream> open, long from, long length, HashSet<string> fams,
                                Action<FsFound> found, Action<double> progress, Func<bool> cancel)
        {
            const int CH = 4 << 20, OV = 128 << 10;
            var seen = new HashSet<string>();
            using var s = open();
            long end = Math.Min(s.Length > 0 ? s.Length : long.MaxValue, from + length);
            long devEnd = s.Length > 0 ? s.Length : end;
            var buf = new byte[CH + OV];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (long pos = from; pos < end && !cancel(); pos += CH)
            {
                int want = (int)Math.Min(buf.Length, devEnd - pos);
                if (want <= 0) break;
                byte[] b = Fmt.ReadAt(s, pos, want);
                Buffer.BlockCopy(b, 0, buf, 0, want);
                if (want < buf.Length) Array.Clear(buf, want, buf.Length - want);
                int lim = (int)Math.Min(CH, end - pos);
                for (int o = 0; o < lim; o += 512)
                {
                    foreach (var f in Check(buf, o, pos + o, devEnd, fams))
                    {
                        if (f.Start < 0 || f.Start >= devEnd) continue;
                        if (f.Length <= 0 || f.Start + f.Length > devEnd) f.Length = devEnd - f.Start;
                        if (seen.Add(f.Key)) found(f);
                    }
                }
                if (sw.ElapsedMilliseconds > 400) { sw.Restart(); progress((pos + CH - from) / (double)Math.Max(1, end - from)); }
            }
            progress(1);
        }

        static IEnumerable<FsFound> Check(byte[] b, int o, long abs, long devEnd, HashSet<string> F)
        {
            bool s55 = b[o + 510] == 0x55 && b[o + 511] == 0xAA;
            string oem = Encoding.ASCII.GetString(b, o + 3, 8);
            // --- sektörün başında boot sektörü olanlar ---
            if (s55 && oem == "NTFS    " && F.Contains("ntfs"))
            {
                int bps = Fmt.U16(b, o + 0x0B); long tot = (long)Fmt.U64(b, o + 0x28);
                if (bps is 512 or 1024 or 2048 or 4096 && tot > 0)
                {
                    string ser = Fmt.U64(b, o + 0x48).ToString("X16");
                    // birincil mi yedek mi? (yedek, bölümün sonundadır)
                    long len = (tot + 1) * bps;
                    yield return new FsFound { Fam = "NTFS", Start = abs, Length = len, Label = $"NTFS · seri {ser[^8..]}", Key = "ntfs" + abs };
                    long back = abs - tot * bps;
                    if (back >= 0) yield return new FsFound { Fam = "NTFS", Start = back, Length = len, Label = $"NTFS (yedek boot sektöründen) · seri {ser[^8..]}", Key = "ntfs" + back };
                }
            }
            if (F.Contains("refs") && b[o] == 0 && b[o + 1] == 0 && b[o + 2] == 0 && oem.StartsWith("ReFS"))
                yield return new FsFound { Fam = "ReFS", Start = abs, Length = (long)Fmt.U64(b, o + 0x18) * Math.Max(512u, Fmt.U32(b, o + 0x20)), Label = "ReFS", Key = "refs" + abs };
            if (F.Contains("exfat") && s55 && oem == "EXFAT   ")
                yield return new FsFound { Fam = "exFAT", Start = abs, Length = (long)Fmt.U64(b, o + 0x48) << b[o + 0x6C], Label = "exFAT", Key = "exfat" + abs };
            if (F.Contains("fat") && s55 && Encoding.ASCII.GetString(b, o + 0x52, 5) == "FAT32")
                yield return new FsFound { Fam = "FAT32", Start = abs, Length = Fmt.U32(b, o + 0x20) * (long)Math.Max(512, (int)Fmt.U16(b, o + 0x0B)), Label = "FAT32 · " + Fmt.Ascii(b, o + 0x47, 11), Key = "fat" + abs };
            if (F.Contains("fat") && s55 && Encoding.ASCII.GetString(b, o + 0x36, 5) is "FAT16" or "FAT12")
            {
                long ts = Fmt.U16(b, o + 0x13); if (ts == 0) ts = Fmt.U32(b, o + 0x20);
                yield return new FsFound { Fam = "FAT", Start = abs, Length = ts * Math.Max(512, (int)Fmt.U16(b, o + 0x0B)), Label = Encoding.ASCII.GetString(b, o + 0x36, 5), Key = "fat" + abs };
            }
            if (F.Contains("crypt") && oem.StartsWith("-FVE-FS-"))
                yield return new FsFound { Fam = "BitLocker", Start = abs, Label = "BitLocker şifreli birim", Key = "bl" + abs };
            if (F.Contains("crypt") && b[o] == 'L' && b[o + 1] == 'U' && b[o + 2] == 'K' && b[o + 3] == 'S' && b[o + 4] == 0xBA && b[o + 5] == 0xBE)
                yield return new FsFound { Fam = "LUKS", Start = abs, Label = "LUKS şifreli birim", Key = "luks" + abs };
            if (F.Contains("mbr") && abs % 4096 == 0 && Encoding.ASCII.GetString(b, o + 512, 8) == "EFI PART" && Fmt.U64(b, o + 512 + 0x18) == 1)
                yield return new FsFound { Fam = "GPT", Start = abs, Label = "GPT bölüm tablosu (disk başı)", Key = "gpt" + abs };
            // XFS: birincil süper blok (AG 0) — arkasından AGF seqno 0 gelir
            if (F.Contains("xfs") && Fmt.U32(b, o) == 0x42534658 && Fmt.B32(b, o + 0x200) == 0x58414746 && Fmt.B32(b, o + 0x208) == 0)
                yield return new FsFound { Fam = "XFS", Start = abs, Length = (long)Fmt.B64(b, o + 8) * Fmt.B32(b, o + 4), Label = "XFS · " + Fmt.Ascii(b, o + 0x6C, 12), Key = "xfs" + Fmt.Hex(b, o + 0x20, 16) };
            // --- süper bloğu içeride olanlar ---
            if (F.Contains("ext") && Fmt.U16(b, o + 0x38) == 0xEF53)
            {
                uint lbs = Fmt.U32(b, o + 0x18); uint bpg = Fmt.U32(b, o + 0x20); int grp = Fmt.U16(b, o + 0x5A);
                if (lbs <= 6 && bpg == 8192u << (int)lbs)
                {
                    long bs = 1024L << (int)lbs;
                    long rel = grp == 0 ? 1024 : (bs == 1024 ? (1 + (long)grp * bpg) * 1024 : (long)grp * bpg * bs);
                    ulong blocks = Fmt.U32(b, o + 4) | ((Fmt.U32(b, o + 0x60) & 0x80) != 0 ? (ulong)Fmt.U32(b, o + 0x150) << 32 : 0);
                    string uuid = Fmt.Hex(b, o + 0x68, 16);
                    yield return new FsFound { Fam = "ext", Start = abs - rel, Length = (long)blocks * bs, Label = "Ext2/3/4 · " + Fmt.Ascii(b, o + 0x78, 16) + (grp > 0 ? $" (yedek süper blok #{grp})" : ""), Key = "ext" + uuid };
                }
            }
            if (F.Contains("btrfs") && Encoding.ASCII.GetString(b, o + 0x40, 8) == "_BHRfS_M")
            {
                long bytenr = (long)Fmt.U64(b, o + 0x30);
                yield return new FsFound { Fam = "Btrfs", Start = abs - bytenr, Length = (long)Fmt.U64(b, o + 0x70), Label = "Btrfs · " + Fmt.Ascii(b, o + 0x12B, 32), Key = "btrfs" + Fmt.Hex(b, o + 0x20, 16) };
            }
            if (F.Contains("hfs") && b[o] == 'H' && (b[o + 1] == '+' || b[o + 1] == 'X') && (Fmt.B16(b, o + 2) is 4 or 5))
                yield return new FsFound { Fam = "HFS+", Start = abs - 1024, Length = (long)Fmt.B32(b, o + 0x2C) * Fmt.B32(b, o + 0x28), Label = "HFS+", Key = "hfs" + (abs - 1024) };
            if (F.Contains("apfs") && Encoding.ASCII.GetString(b, o + 32, 4) == "NXSB")
                yield return new FsFound { Fam = "APFS", Start = abs, Length = (long)Fmt.U64(b, o + 0x28) * Fmt.U32(b, o + 0x24), Label = "APFS kapsayıcı", Key = "apfs" + Fmt.Hex(b, o + 0x48, 16) };
            if (F.Contains("vmfs") && Fmt.U32(b, o) == 0xC001D00D && abs >= 1 << 20)
                yield return new FsFound { Fam = "VMFS", Start = abs - (1 << 20), Label = "VMware VMFS", Key = "vmfs" + (abs - (1 << 20)) };
            if (F.Contains("jfs") && Encoding.ASCII.GetString(b, o, 4) == "JFS1")
                yield return new FsFound { Fam = "JFS", Start = abs - 32768, Label = "JFS", Key = "jfs" + (abs - 32768) };
            if (F.Contains("reiser") && Encoding.ASCII.GetString(b, o + 0x34, 6) == "ReIsEr")
                yield return new FsFound { Fam = "ReiserFS", Start = abs - 65536, Length = Fmt.U32(b, o) * (long)Fmt.U16(b, o + 0x2C), Label = "ReiserFS", Key = "reiser" + (abs - 65536) };
            if (F.Contains("ufs") && Fmt.U32(b, o + 0x55C) == 0x19540119)
                yield return new FsFound { Fam = "UFS2", Start = abs - 65536, Label = "UFS2 (FreeBSD)", Key = "ufs" + (abs - 65536) };
            if (F.Contains("f2fs") && Fmt.U32(b, o) == 0xF2F52010)
                yield return new FsFound { Fam = "F2FS", Start = abs - 1024, Label = "F2FS", Key = "f2fs" + (abs - 1024) };
            if (F.Contains("zfs") && (Fmt.U64(b, o) == 0x00bab10c || Fmt.B64(b, o) == 0x00bab10c))
            {
                long st = (abs - 131072) / 262144 * 262144;
                yield return new FsFound { Fam = "ZFS", Start = st, Label = "ZFS vdev etiketi", Key = "zfs" + (st >> 20) };
            }
            if (F.Contains("md") && Fmt.U32(b, o) == RaidMeta.MdMagic && Fmt.U32(b, o + 4) == 1)
            {
                long sbOff = (long)Fmt.U64(b, o + 144) * 512;
                long st = abs - sbOff;
                long dataOff = (long)Fmt.U64(b, o + 128) * 512, dataSize = (long)Fmt.U64(b, o + 136) * 512;
                long len = sbOff > dataOff ? sbOff + 8192 : dataOff + dataSize;
                yield return new FsFound { Fam = "md", Start = st, Length = len, Label = $"Linux RAID (md) üyesi · {Fmt.Ascii(b, o + 32, 32)} · seviye {(int)Fmt.U32(b, o + 72)}", Key = "md" + st };
            }
            if (F.Contains("lvm") && Encoding.ASCII.GetString(b, o, 8) == "LABELONE" && Encoding.ASCII.GetString(b, o + 24, 8) == "LVM2 001")
            {
                long st = abs - (long)Fmt.U64(b, o + 8) * 512;
                uint pvo = Fmt.U32(b, o + 20);
                long dev = pvo >= 32 && pvo <= 400 ? (long)Fmt.U64(b, o + (int)pvo + 32) : 0;
                yield return new FsFound { Fam = "LVM", Start = st, Length = dev, Label = "LVM fiziksel birim", Key = "lvm" + st };
            }
            if (F.Contains("cctv") && Encoding.ASCII.GetString(b, o, 7) == "DHFS4.1")
                yield return new FsFound { Fam = "DHFS", Start = abs, Label = "Dahua DHFS (CCTV)", Key = "dhfs" + abs };
            if (F.Contains("cctv") && o + 0x222 <= b.Length && Encoding.ASCII.GetString(b, o + 0x210, 18) == "HIKVISION@HANGZHOU")
                yield return new FsFound { Fam = "HIKVISION", Start = abs, Label = "Hikvision (CCTV)", Key = "hik" + abs };
        }
    }

    // ---------------- arama penceresi (aile seçimi + aralık) ----------------
    public class FsScanDialog : Window
    {
        public long From, Count;
        public HashSet<string> Fams = new();
        private readonly TextBox _from = new(), _count = new();
        private readonly List<(CheckBox cb, string key)> _cbs = new();

        public FsScanDialog(long devBytes, string title)
        {
            Title = "Kayıp bölüm / dosya sistemi ara — " + title;
            Width = 640; SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = new SolidColorBrush(Theme.Col("Bg"));
            var root = new StackPanel { Margin = new Thickness(16) };
            var row = new WrapPanel();
            row.Children.Add(L("Başlangıç sektörü:")); _from.Text = "0"; Tb(_from); row.Children.Add(_from);
            row.Children.Add(L("   Sektör sayısı:")); _count.Text = (devBytes / 512).ToString(); Tb(_count); row.Children.Add(_count);
            root.Children.Add(row);
            root.Children.Add(new TextBlock { Text = "Aranacak aileler:", Foreground = Theme.Brush("Text"), Margin = new Thickness(0, 14, 0, 6), FontWeight = FontWeights.SemiBold });
            var grid = new UniformGrid3();
            foreach (var (k, t) in RaidFsScan.Families)
            {
                var cb = new CheckBox { Content = t, IsChecked = true, Foreground = new SolidColorBrush(Theme.Col("Text")), Margin = new Thickness(0, 3, 12, 3) };
                _cbs.Add((cb, k)); grid.Children.Add(cb);
            }
            root.Children.Add(grid);
            var bar = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            bar.Children.Add(B("Tümünü seç", () => _cbs.ForEach(x => x.cb.IsChecked = true)));
            bar.Children.Add(B("Hiçbiri", () => _cbs.ForEach(x => x.cb.IsChecked = false)));
            bar.Children.Add(B("Sadece Windows", () => _cbs.ForEach(x => x.cb.IsChecked = x.key is "ntfs" or "refs" or "exfat" or "fat" or "crypt" or "mbr")));
            bar.Children.Add(B("Sadece NAS / Linux", () => _cbs.ForEach(x => x.cb.IsChecked = x.key is "ext" or "xfs" or "btrfs" or "md" or "lvm" or "zfs" or "reiser" or "jfs")));
            root.Children.Add(bar);
            root.Children.Add(new TextBlock
            {
                Text = "Disk baştan sona okunur (salt okunur). Bulunan bölümler ağaçta seçili öğenin altına canlı eklenir; md/LVM parçaları bulunursa dizi/birim otomatik kurulur. Dosya düzeyinde derin tarama için bulunan bölümü Veri Kurtarma'da açın.",
                Foreground = new SolidColorBrush(Theme.Col("Dim")), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0)
            });
            var ok = B("🔍 Aramayı başlat", Ok); ok.Background = new SolidColorBrush(Theme.Col("Accent")); ok.HorizontalAlignment = HorizontalAlignment.Right;
            ok.Margin = new Thickness(0, 14, 0, 0);
            root.Children.Add(ok);
            Content = root;
        }

        // 3 sütunlu basit ızgara
        class UniformGrid3 : System.Windows.Controls.Primitives.UniformGrid { public UniformGrid3() { Columns = 3; } }

        static TextBlock L(string t) => new() { Text = t, Foreground = Theme.Brush("Dim"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        static void Tb(TextBox t) { t.Width = 160; t.Height = 28; t.VerticalContentAlignment = VerticalAlignment.Center; t.Background = new SolidColorBrush(Theme.Col("Card")); t.Foreground = new SolidColorBrush(Theme.Col("Text")); }
        static Button B(string t, Action a)
        {
            var b = new Button { Content = t, Height = 30, Padding = new Thickness(12, 0, 12, 0), Margin = new Thickness(0, 0, 6, 0), Foreground = new SolidColorBrush(Theme.Col("Text")), Background = new SolidColorBrush(Theme.Col("Btn")), BorderBrush = new SolidColorBrush(Theme.Col("Line")) };
            b.Click += (_, _) => a(); return b;
        }

        void Ok()
        {
            if (!long.TryParse(_from.Text.Replace(".", ""), out long f) || !long.TryParse(_count.Text.Replace(".", ""), out long c) || c <= 0) { MessageBox.Show("Sektör değerleri geçersiz."); return; }
            From = f * 512; Count = c * 512;
            Fams = _cbs.Where(x => x.cb.IsChecked == true).Select(x => x.key).ToHashSet();
            if (Fams.Count == 0) { MessageBox.Show("En az bir aile seçin."); return; }
            DialogResult = true;
        }
    }

    // ---------------- okunamayan sektör haritası (ddrescue mapfile / PC-3000 / LBA listesi) ----------------
    // Haritadaki sektörler RAID'de "okunamadı" sayılır → RAID 5/6'da PARİTEDEN gerçek veriyle tamamlanır
    // (UFS gibi desenle maskelemek yerine). Yedeklilik yoksa sıfırla doldurulur.
    public static class BadMap
    {
        public static List<(long start, long end)> Load(string path, int sector = 512)
        {
            var list = new List<(long, long)>();
            foreach (var raw in File.ReadLines(path))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                var p = l.Split(new[] { ' ', '\t', ',', ';', '-' }, StringSplitOptions.RemoveEmptyEntries);
                // ddrescue: "0x00000000  0x00010000  +"  (pos size durum)
                if (p.Length >= 3 && p[0].StartsWith("0x") && p[1].StartsWith("0x") && p[2].Length == 1)
                {
                    if (p[2] == "+") continue;
                    long pos = Convert.ToInt64(p[0][2..], 16), size = Convert.ToInt64(p[1][2..], 16);
                    list.Add((pos, pos + size));
                    continue;
                }
                if (p.Length >= 2 && p[0].StartsWith("0x") && !p[1].StartsWith("0x") && p[1].Length == 1) continue;   // ddrescue durum satırı
                // "başlangıçLBA bitişLBA" ya da "LBA" ya da "LBA sayı"
                if (p.Length >= 1 && long.TryParse(p[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long a))
                {
                    long e = a + 1;
                    if (p.Length >= 2 && long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long b2)) e = b2 >= a ? (l.Contains('-') ? b2 + 1 : (b2 > a ? b2 + 1 : a + b2)) : a + b2;
                    list.Add((a * sector, e * sector));
                }
            }
            list.Sort();
            // birleştir
            var m = new List<(long, long)>();
            foreach (var r in list)
                if (m.Count > 0 && r.Item1 <= m[^1].Item2) m[^1] = (m[^1].Item1, Math.Max(m[^1].Item2, r.Item2)); else m.Add(r);
            return m;
        }

        public static long Total(List<(long start, long end)> m) => m.Sum(r => r.end - r.start);
    }

    // haritadaki bölgeye denk gelen okuma: strict → hata (parite devreye girer), soft → sıfır
    public sealed class MapStream : Stream
    {
        private readonly Stream _b; private readonly List<(long start, long end)> _m; private readonly bool _strict;
        public MapStream(Stream b, List<(long start, long end)> map, bool strict) { _b = b; _m = map; _strict = strict; }
        public override bool CanRead => true; public override bool CanSeek => true; public override bool CanWrite => false;
        public override long Length => _b.Length;
        public override long Position { get => _b.Position; set => _b.Position = value; }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin so) => _b.Seek(o, so);
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool d) { if (d) _b.Dispose(); base.Dispose(d); }
        public override int Read(byte[] buf, int off, int count)
        {
            long p = _b.Position;
            int idx = Find(p + count);
            bool hit = false;
            for (int i = Math.Max(0, idx - 1); i < _m.Count && _m[i].start < p + count; i++) if (_m[i].end > p) { hit = true; break; }
            if (hit && _strict) throw new IOException("Haritada okunamaz işaretli sektör");
            int r = _b.Read(buf, off, count);
            if (hit)
                foreach (var (s, e) in _m)
                {
                    if (e <= p || s >= p + r) continue;
                    long a = Math.Max(s, p), z = Math.Min(e, p + r);
                    Array.Clear(buf, off + (int)(a - p), (int)(z - a));
                }
            return r;
        }
        int Find(long x) { int lo = 0, hi = _m.Count; while (lo < hi) { int mid = (lo + hi) / 2; if (_m[mid].start < x) lo = mid + 1; else hi = mid; } return lo; }
    }
}
