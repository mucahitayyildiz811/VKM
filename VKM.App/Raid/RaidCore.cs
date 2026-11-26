using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace VKM.App
{
    // ───────────────────────── RAID modeli ─────────────────────────
    // Tüm RAID türleri tek bir yapılandırma ile anlatılır; okuyucu (RaidStream) bunu sanal diske çevirir.
    // Kaynak diskler HER ZAMAN salt okunur açılır.

    public enum RaidKind { Raid0, Raid1, Raid10, Raid5, Raid6, Jbod }

    // Parite yerleşimi (Linux md / DDF adlandırması)
    public enum RaidLayout
    {
        LeftAsym = 0, RightAsym = 1, LeftSym = 2, RightSym = 3,
        ParityFirst = 4, ParityLast = 5,          // RAID 3/4 (sabit parite diski)
        DdfNRestart = 6, DdfNContinue = 7,        // RAID 6 DDF çeşitleri
        Near = 10, Far = 11, Offset = 12           // RAID 10 (md) kopya yerleşimleri
    }

    // Üst katman (RAID 50/60): alt RAID gruplarına veri nasıl dağılır
    public enum SpanMode { Strip = 0, Stripe = 1, Concat = 2 }

    // Bir RAID üyesi: fiziksel disk, disk bölümü ya da imaj dosyası
    public class RaidMember
    {
        public string Name = "";            // ekranda: "Disk 3 · WDC WD40EFRX · WD-WCC..."
        public string Model = "";
        public string Serial = "";
        public string DeviceId = "";        // \\.\PHYSICALDRIVE3 (imaj ise boş)
        public string ImagePath = "";
        public long BaseOffset;             // disk içinde (bölüm ise) başlangıç
        public long Length;                 // bayt
        public int Sector = 512;
        public int HealthPercent = -1;      // SMART (-1: bilinmiyor)
        public string HealthText = "";
        public bool Risky;

        // Okuyucu: hata olursa İSTİSNA atar (RaidStream pariteden tamamlar)
        public Func<Stream> OpenStrict = () => Stream.Null;
        // Normal okuyucu (bad sektörü sıfırla geçer)
        public Func<Stream> Open = () => Stream.Null;

        public string Key => (DeviceId.Length > 0 ? DeviceId : ImagePath) + "@" + BaseOffset;

        // okunamayan sektör haritası (yüklenince okuyucular sarılır; RAID'de pariteden tamamlanır)
        public List<(long start, long end)>? BadRanges;
        private Func<Stream>? _baseOpen, _baseStrict;
        public void SetBadMap(List<(long start, long end)>? map)
        {
            if (_baseOpen == null) { _baseOpen = Open; _baseStrict = OpenStrict; }
            BadRanges = map;
            if (map == null || map.Count == 0) { Open = _baseOpen; OpenStrict = _baseStrict!; BadRanges = null; return; }
            var bo = _baseOpen; var bs = _baseStrict!;
            Open = () => new MapStream(bo(), map, false);
            OpenStrict = () => new MapStream(bs(), map, true);
        }

        public RaidMember Sub(long offset, long length, string name)
        {
            var p = this;
            return new RaidMember
            {
                Name = name, Model = Model, Serial = Serial, DeviceId = DeviceId, ImagePath = ImagePath,
                BaseOffset = BaseOffset + offset, Length = length, Sector = Sector,
                HealthPercent = HealthPercent, HealthText = HealthText, Risky = Risky,
                OpenStrict = () => new SubStream(p.OpenStrict(), offset, length),
                Open = () => new SubStream(p.Open(), offset, length)
            };
        }
    }

    public class RaidConfig
    {
        public string Name = "RAID";
        public RaidKind Kind = RaidKind.Raid5;
        public RaidLayout Layout = RaidLayout.LeftSym;
        public int Groups = 1;                 // RAID 50/60 için alt grup sayısı
        public int DisksPerGroup = 1;          // her gruptaki disk (eksikler dâhil)
        public int Copies = 2;                 // RAID 10 kopya sayısı
        public SpanMode Span = SpanMode.Strip;
        public long Chunk = 65536;             // şerit (stripe/strip) boyutu
        public int Delay = 1;                  // parite kaç satırda bir döner (gecikmeli parite)
        public long[] DataStart = Array.Empty<long>();   // her üyede verinin başladığı yer (bayt)
        public long MemberDataLength;          // her üyede kullanılan veri uzunluğu (0: otomatik)
        public RaidMember?[] Members = Array.Empty<RaidMember?>();   // yuva sırası: grup*N + yuva; null = eksik
        public string Source = "";             // "Linux md (Synology/QNAP)", "DDF (LSI/Dell)", "Otomatik çözücü"…
        public double Confidence = 1.0;
        public string Notes = "";

        public int Total => Members.Length;
        public int Missing => Members.Count(m => m == null);

        public int DataDisksPerRow
        {
            get
            {
                int n = DisksPerGroup;
                return Kind switch
                {
                    RaidKind.Raid5 => n - 1,
                    RaidKind.Raid6 => n - 2,
                    _ => n
                };
            }
        }

        public long StartOf(int slot) => slot < DataStart.Length ? DataStart[slot] : (DataStart.Length > 0 ? DataStart[0] : 0);

        public long PerMemberLength()
        {
            if (MemberDataLength > 0) return MemberDataLength;
            long best = long.MaxValue;
            for (int i = 0; i < Members.Length; i++)
                if (Members[i] != null) best = Math.Min(best, Members[i]!.Length - StartOf(i));
            return best == long.MaxValue ? 0 : Math.Max(0, best);
        }

        // Sanal diskin boyutu
        public long Capacity()
        {
            long per = PerMemberLength();
            if (Kind == RaidKind.Jbod)
            {
                long sum = 0;
                for (int i = 0; i < Members.Length; i++) sum += Members[i] != null ? Members[i]!.Length - StartOf(i) : 0;
                return sum;
            }
            if (Kind == RaidKind.Raid1) return per;
            long rows = per / Math.Max(1, Chunk);
            if (Kind == RaidKind.Raid10)
            {
                long chunks = rows * DisksPerGroup / Math.Max(1, Copies);
                if (Layout == RaidLayout.Far) chunks = (rows / Math.Max(1, Copies)) * DisksPerGroup;
                return chunks * Chunk * Groups;
            }
            return rows * DataDisksPerRow * Groups * Chunk;
        }

        public string LevelText()
        {
            string b = Kind switch
            {
                RaidKind.Raid0 => "RAID 0",
                RaidKind.Raid1 => "RAID 1",
                RaidKind.Raid10 => DisksPerGroup % 2 == 1 && Layout == RaidLayout.Near ? "RAID 1E" : "RAID 10",
                RaidKind.Raid5 => Layout is RaidLayout.ParityFirst or RaidLayout.ParityLast ? "RAID 4" : "RAID 5",
                RaidKind.Raid6 => "RAID 6",
                _ => "JBOD"
            };
            if (Groups > 1)
            {
                if (Kind == RaidKind.Raid5) b = "RAID 50";
                else if (Kind == RaidKind.Raid6) b = "RAID 60";
                else if (Kind == RaidKind.Raid1) b = "RAID 10";
            }
            return b;
        }

        public static string LayoutText(RaidLayout l) => l switch
        {
            RaidLayout.LeftAsym => "sol-asimetrik",
            RaidLayout.RightAsym => "sağ-asimetrik",
            RaidLayout.LeftSym => "sol-simetrik",
            RaidLayout.RightSym => "sağ-simetrik",
            RaidLayout.ParityFirst => "parite ilk diskte",
            RaidLayout.ParityLast => "parite son diskte",
            RaidLayout.DdfNRestart => "DDF N-restart",
            RaidLayout.DdfNContinue => "DDF N-continue",
            RaidLayout.Near => "near",
            RaidLayout.Far => "far",
            RaidLayout.Offset => "offset",
            _ => l.ToString()
        };

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(LevelText());
            if (Groups > 1) sb.Append($" ({Groups}×{DisksPerGroup} disk)"); else sb.Append($" · {Total} disk");
            if (Kind is not (RaidKind.Raid1 or RaidKind.Jbod)) sb.Append(" · blok " + Fmt.Size(Chunk));
            if (Kind is RaidKind.Raid5 or RaidKind.Raid6) sb.Append(" · " + LayoutText(Layout));
            if (Delay > 1) sb.Append(" · gecikme " + Delay);
            if (Missing > 0) sb.Append($" · {Missing} disk EKSİK");
            return sb.ToString();
        }

        public RaidConfig Clone()
        {
            var c = (RaidConfig)MemberwiseClone();
            c.DataStart = (long[])DataStart.Clone();
            c.Members = (RaidMember?[])Members.Clone();
            return c;
        }

        // ---- .vkmraid dosyası (yapılandırmayı kaydet / yükle) ----
        public string ToText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# VKM RAID yapılandırması");
            sb.AppendLine("name=" + Name);
            sb.AppendLine("kind=" + Kind);
            sb.AppendLine("layout=" + Layout);
            sb.AppendLine("groups=" + Groups);
            sb.AppendLine("disksPerGroup=" + DisksPerGroup);
            sb.AppendLine("copies=" + Copies);
            sb.AppendLine("span=" + Span);
            sb.AppendLine("chunk=" + Chunk);
            sb.AppendLine("delay=" + Delay);
            sb.AppendLine("memberDataLength=" + MemberDataLength);
            for (int i = 0; i < Members.Length; i++)
                sb.AppendLine("member" + i + "=" + (Members[i] == null ? "EKSIK" : Members[i]!.Key) + "|" + StartOf(i));
            return sb.ToString();
        }

        public static RaidConfig FromText(string text, IList<RaidMember> available)
        {
            var c = new RaidConfig();
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in text.Split('\n'))
            {
                string l = raw.Trim();
                if (l.Length == 0 || l[0] == '#') continue;
                int e = l.IndexOf('=');
                if (e > 0) kv[l[..e]] = l[(e + 1)..];
            }
            string G(string k, string d) => kv.TryGetValue(k, out var v) ? v : d;
            c.Name = G("name", "RAID");
            c.Kind = Enum.Parse<RaidKind>(G("kind", "Raid5"));
            c.Layout = Enum.Parse<RaidLayout>(G("layout", "LeftSym"));
            c.Groups = int.Parse(G("groups", "1"));
            c.DisksPerGroup = int.Parse(G("disksPerGroup", "1"));
            c.Copies = int.Parse(G("copies", "2"));
            c.Span = Enum.Parse<SpanMode>(G("span", "Strip"));
            c.Chunk = long.Parse(G("chunk", "65536"));
            c.Delay = int.Parse(G("delay", "1"));
            c.MemberDataLength = long.Parse(G("memberDataLength", "0"));
            int n = c.Groups * c.DisksPerGroup;
            if (c.Kind is RaidKind.Raid1 or RaidKind.Jbod) n = kv.Keys.Count(k => k.StartsWith("member", StringComparison.OrdinalIgnoreCase) && !k.Equals("memberDataLength", StringComparison.OrdinalIgnoreCase));
            c.Members = new RaidMember?[n];
            c.DataStart = new long[n];
            for (int i = 0; i < n; i++)
            {
                string v = G("member" + i, "EKSIK|0");
                var p = v.Split('|');
                c.DataStart[i] = p.Length > 1 ? long.Parse(p[1]) : 0;
                c.Members[i] = p[0] == "EKSIK" ? null : available.FirstOrDefault(m => m.Key == p[0]);
            }
            c.Source = "Kayıtlı yapılandırma";
            return c;
        }
    }

    // ───────────────────────── yerleşim hesabı ─────────────────────────
    public static class RaidMap
    {
        // Bir satırda (row) j'inci veri bloğu hangi yuvada, parite/Q nerede
        public static void Row5(int n, RaidLayout l, long row, int delay, int j, out int dd, out int pd)
        {
            long pr = row / Math.Max(1, delay);
            switch (l)
            {
                case RaidLayout.LeftAsym: pd = n - 1 - (int)(pr % n); dd = j >= pd ? j + 1 : j; break;
                case RaidLayout.RightAsym: pd = (int)(pr % n); dd = j >= pd ? j + 1 : j; break;
                case RaidLayout.RightSym: pd = (int)(pr % n); dd = (pd + 1 + j) % n; break;
                case RaidLayout.ParityFirst: pd = 0; dd = j + 1; break;
                case RaidLayout.ParityLast: pd = n - 1; dd = j; break;
                default: pd = n - 1 - (int)(pr % n); dd = (pd + 1 + j) % n; break;   // LeftSym
            }
        }

        public static void Row6(int n, RaidLayout l, long row, int delay, int j, out int dd, out int pd, out int qd)
        {
            long pr = row / Math.Max(1, delay);
            switch (l)
            {
                case RaidLayout.LeftAsym:
                case RaidLayout.DdfNRestart:
                    if (l == RaidLayout.DdfNRestart) pr += 1;
                    pd = n - 1 - (int)(pr % n); qd = pd + 1;
                    if (pd == n - 1) { qd = 0; dd = j + 1; } else dd = j >= pd ? j + 2 : j;
                    break;
                case RaidLayout.RightAsym:
                    pd = (int)(pr % n); qd = pd + 1;
                    if (pd == n - 1) { qd = 0; dd = j + 1; } else dd = j >= pd ? j + 2 : j;
                    break;
                case RaidLayout.RightSym:
                    pd = (int)(pr % n); qd = (pd + 1) % n; dd = (pd + 2 + j) % n; break;
                case RaidLayout.DdfNContinue:
                    pd = n - 1 - (int)(pr % n); qd = (pd + n - 1) % n; dd = (pd + 1 + j) % n; break;
                case RaidLayout.ParityFirst:
                    pd = 0; qd = 1; dd = j + 2; break;
                case RaidLayout.ParityLast:
                    pd = n - 2; qd = n - 1; dd = j; break;
                default:
                    pd = n - 1 - (int)(pr % n); qd = (pd + 1) % n; dd = (pd + 2 + j) % n; break;   // LeftSym
            }
        }

        // Sanal blok no (V) → grup, grup içi blok no
        public static void Span(RaidConfig c, long v, out int g, out long vg)
        {
            int G = Math.Max(1, c.Groups);
            if (G == 1) { g = 0; vg = v; return; }
            if (c.Span == SpanMode.Strip) { g = (int)(v % G); vg = v / G; return; }
            if (c.Span == SpanMode.Stripe)
            {
                int D = Math.Max(1, PerRow(c));
                long s = v / D; long j = v % D;
                g = (int)(s % G); vg = (s / G) * D + j; return;
            }
            // Concat: gruplar uç uca
            long per = c.PerMemberLength() / Math.Max(1, c.Chunk) * PerRow(c);
            g = (int)Math.Min(G - 1, v / Math.Max(1, per)); vg = v - g * per;
        }

        public static int PerRow(RaidConfig c) => c.Kind == RaidKind.Raid10 ? Math.Max(1, c.DisksPerGroup / Math.Max(1, c.Copies)) : Math.Max(1, c.DataDisksPerRow);

        // V → (yuva, satır). copy: RAID10 kopya no
        public static bool Locate(RaidConfig c, long v, int copy, out int slot, out long row)
        {
            Span(c, v, out int g, out long vg);
            int n = c.DisksPerGroup;
            slot = -1; row = 0;
            switch (c.Kind)
            {
                case RaidKind.Raid0:
                    row = vg / n; slot = g * n + (int)(vg % n); return true;
                case RaidKind.Raid10:
                    {
                        int k = Math.Max(1, c.Copies);
                        if (copy >= k) return false;
                        if (c.Layout == RaidLayout.Far)
                        {
                            long rowsPer = c.PerMemberLength() / Math.Max(1, c.Chunk) / k;
                            long r0 = vg / n; int d0 = (int)(vg % n);
                            row = r0 + copy * rowsPer; slot = g * n + (d0 + copy) % n; return true;
                        }
                        if (c.Layout == RaidLayout.Offset)
                        {
                            long r0 = vg / n; int d0 = (int)(vg % n);
                            row = r0 * k + copy; slot = g * n + (d0 + copy) % n; return true;
                        }
                        long pos = vg * k + copy;
                        row = pos / n; slot = g * n + (int)(pos % n); return true;
                    }
                case RaidKind.Raid5:
                    {
                        int D = n - 1; row = vg / D; int j = (int)(vg % D);
                        Row5(n, c.Layout, row, c.Delay, j, out int dd, out _);
                        slot = g * n + dd; return true;
                    }
                case RaidKind.Raid6:
                    {
                        int D = n - 2; row = vg / D; int j = (int)(vg % D);
                        Row6(n, c.Layout, row, c.Delay, j, out int dd, out _, out _);
                        slot = g * n + dd; return true;
                    }
            }
            return false;
        }
    }

    // ───────────────────────── GF(256) (RAID 6 Q hesabı) ─────────────────────────
    public static class Gf256
    {
        public static readonly byte[] Exp = new byte[512];
        public static readonly int[] Log = new int[256];
        static Gf256()
        {
            int x = 1;
            for (int i = 0; i < 255; i++) { Exp[i] = (byte)x; Log[x] = i; x <<= 1; if ((x & 0x100) != 0) x ^= 0x11d; }
            for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
        }
        public static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];
        public static byte Div(byte a, byte b) => a == 0 ? (byte)0 : Exp[(Log[a] - Log[b] + 255) % 255];
        public static byte Pow2(int e) => Exp[((e % 255) + 255) % 255];
        public static void MulInto(byte[] dst, byte[] src, int len, byte coef)
        {
            if (coef == 0) return;
            int lc = Log[coef];
            for (int i = 0; i < len; i++) { byte s = src[i]; if (s != 0) dst[i] ^= Exp[Log[s] + lc]; }
        }
    }

    // ───────────────────────── sanal RAID diski ─────────────────────────
    public sealed class RaidStream : Stream
    {
        private readonly RaidConfig _c;
        private readonly Stream?[] _m;
        private readonly Stream?[] _soft;      // bad sektörü sıfırla geçen yedek okuyucu (yedeklilik yoksa)
        private readonly long _len;
        private long _pos;
        private readonly byte[] _tmp;
        private readonly byte[] _acc;
        private readonly byte[] _acc2;
        public long Reconstructed;     // pariteden tamamlanan bayt
        public long Unreadable;        // hiç okunamayan bayt (sıfırla dolduruldu)
        public static long TotalReconstructed, TotalUnreadable;

        public RaidStream(RaidConfig c)
        {
            _c = c;
            _m = new Stream?[c.Members.Length];
            _soft = new Stream?[c.Members.Length];
            for (int i = 0; i < _m.Length; i++)
            {
                try { _m[i] = c.Members[i]?.OpenStrict(); } catch { _m[i] = null; }
            }
            _len = c.Capacity();
            int cap = (int)Math.Min(Math.Max(c.Chunk, 4096), 1 << 22);
            _tmp = new byte[cap]; _acc = new byte[cap]; _acc2 = new byte[cap];
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => _len + offset };
            return _pos;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var s in _m) try { s?.Dispose(); } catch { }
                foreach (var s in _soft) try { s?.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }

        // Sanal adres → hangi üye, üyede hangi bayt (hex penceresi için "nokta atışı")
        public static string Describe(RaidConfig c, long vofs)
        {
            if (c.Kind == RaidKind.Jbod)
            {
                long acc = 0;
                for (int i = 0; i < c.Members.Length; i++)
                {
                    long l = c.Members[i] != null ? c.Members[i]!.Length - c.StartOf(i) : 0;
                    if (vofs < acc + l) return Where(c, i, c.StartOf(i) + vofs - acc, "veri");
                    acc += l;
                }
                return "(sanal diskin dışında)";
            }
            if (c.Kind == RaidKind.Raid1) return Where(c, 0, c.StartOf(0) + vofs, "veri (aynalı)");
            long v = vofs / c.Chunk, w = vofs % c.Chunk;
            if (!RaidMap.Locate(c, v, 0, out int slot, out long row)) return "";
            string extra = "";
            if (c.Kind is RaidKind.Raid5 or RaidKind.Raid6)
            {
                int n = c.DisksPerGroup, g = slot / n;
                int pd, qd = -1;
                if (c.Kind == RaidKind.Raid5) RaidMap.Row5(n, c.Layout, row, c.Delay, 0, out _, out pd);
                else RaidMap.Row6(n, c.Layout, row, c.Delay, 0, out _, out pd, out qd);
                extra = " · bu satırın paritesi: üye " + (g * n + pd + 1) + (qd >= 0 ? ", Q: üye " + (g * n + qd + 1) : "");
            }
            return Where(c, slot, c.StartOf(slot) + row * c.Chunk + w, "veri") + " · satır " + row.ToString("N0", Fmt.Tr) + extra;
        }

        private static string Where(RaidConfig c, int slot, long phys, string what)
        {
            var m = slot >= 0 && slot < c.Members.Length ? c.Members[slot] : null;
            string who = m == null ? "EKSİK DİSK (pariteden hesaplanır)" : m.Name;
            long lba = (phys + (m?.BaseOffset ?? 0)) / 512;
            return $"Üye {slot + 1}: {who} · disk LBA {lba:N0} (0x{phys + (m?.BaseOffset ?? 0):X}) · {what}";
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _len || count <= 0) return 0;
            count = (int)Math.Min(count, _len - _pos);
            int done = 0;
            while (done < count)
            {
                long p = _pos + done;
                int n = ReadPiece(p, buffer, offset + done, count - done);
                if (n <= 0) break;
                done += n;
            }
            _pos += done;
            return done;
        }

        private int ReadPiece(long p, byte[] buf, int off, int want)
        {
            var c = _c;
            if (c.Kind == RaidKind.Jbod)
            {
                long acc = 0;
                for (int i = 0; i < _m.Length; i++)
                {
                    long l = c.Members[i] != null ? c.Members[i]!.Length - c.StartOf(i) : 0;
                    if (p < acc + l)
                    {
                        int n = (int)Math.Min(want, acc + l - p);
                        if (!ReadMember(i, c.StartOf(i) + p - acc, buf, off, n) && !ReadSoft(i, c.StartOf(i) + p - acc, buf, off, n)) { Array.Clear(buf, off, n); Unreadable += n; TotalUnreadable += n; }
                        return n;
                    }
                    acc += l;
                }
                return 0;
            }
            if (c.Kind == RaidKind.Raid1)
            {
                int n = (int)Math.Min(want, 1 << 20);
                for (int i = 0; i < _m.Length; i++)
                    if (ReadMember(i, c.StartOf(i) + p, buf, off, n)) return n;
                for (int i = 0; i < _m.Length; i++)
                    if (ReadSoft(i, c.StartOf(i) + p, buf, off, n)) return n;
                Array.Clear(buf, off, n); Unreadable += n; TotalUnreadable += n;
                return n;
            }

            long v = p / c.Chunk, w = p % c.Chunk;
            int len = (int)Math.Min(want, c.Chunk - w);
            len = Math.Min(len, _tmp.Length);

            if (c.Kind == RaidKind.Raid10 || c.Kind == RaidKind.Raid0)
            {
                for (int copy = 0; copy < (c.Kind == RaidKind.Raid10 ? c.Copies : 1); copy++)
                {
                    if (!RaidMap.Locate(c, v, copy, out int slot, out long row)) break;
                    if (ReadMember(slot, c.StartOf(slot) + row * c.Chunk + w, buf, off, len)) return len;
                }
                if (RaidMap.Locate(c, v, 0, out int s0, out long r0) && ReadSoft(s0, c.StartOf(s0) + r0 * c.Chunk + w, buf, off, len)) return len;
                Array.Clear(buf, off, len); Unreadable += len; TotalUnreadable += len;
                return len;
            }

            // RAID 5 / 6
            RaidMap.Locate(c, v, 0, out int sl, out long rw);
            if (ReadMember(sl, c.StartOf(sl) + rw * c.Chunk + w, buf, off, len)) return len;
            if (Rebuild(sl, rw, w, len))
            {
                Buffer.BlockCopy(_acc, 0, buf, off, len);
                Reconstructed += len; TotalReconstructed += len;
            }
            else if (!ReadSoft(sl, c.StartOf(sl) + rw * c.Chunk + w, buf, off, len)) { Array.Clear(buf, off, len); Unreadable += len; TotalUnreadable += len; }
            return len;
        }

        // Yedeklilik yoksa: bad sektörleri sıfırla geçerek oku (sadece bozuk sektör kaybolur)
        private bool ReadSoft(int slot, long pos, byte[] buf, int off, int len)
        {
            if (slot < 0 || slot >= _soft.Length || _c.Members[slot] == null) return false;
            try
            {
                _soft[slot] ??= _c.Members[slot]!.Open();
                var s = _soft[slot]!;
                s.Position = pos;
                int got = 0;
                while (got < len) { int r = s.Read(buf, off + got, len - got); if (r <= 0) break; got += r; }
                if (got < len) Array.Clear(buf, off + got, len - got);
                return true;
            }
            catch { return false; }
        }

        private bool ReadMember(int slot, long pos, byte[] buf, int off, int len)
        {
            var s = slot >= 0 && slot < _m.Length ? _m[slot] : null;
            if (s == null) return false;
            try
            {
                s.Position = pos;
                int got = 0;
                while (got < len)
                {
                    int r = s.Read(buf, off + got, len - got);
                    if (r <= 0) break;
                    got += r;
                }
                if (got < len) Array.Clear(buf, off + got, len - got);
                return true;
            }
            catch { return false; }
        }

        // Eksik/okunamayan bloğu aynı satırdaki diğer üyelerden hesapla → _acc
        private bool Rebuild(int lostSlot, long row, long w, int len)
        {
            var c = _c;
            int n = c.DisksPerGroup, g = lostSlot / n, lost = lostSlot % n;
            int pd, qd = -1;
            if (c.Kind == RaidKind.Raid5) RaidMap.Row5(n, c.Layout, row, c.Delay, 0, out _, out pd);
            else RaidMap.Row6(n, c.Layout, row, c.Delay, 0, out _, out pd, out qd);
            long phys(int s) => c.StartOf(g * n + s) + row * c.Chunk + w;

            // 1) Tek kayıp: P ile XOR
            Array.Clear(_acc, 0, len);
            var failed = new List<int>();
            for (int s = 0; s < n; s++)
            {
                if (s == lost || s == qd) continue;
                if (!ReadMember(g * n + s, phys(s), _tmp, 0, len)) { failed.Add(s); continue; }
                Xor(_acc, _tmp, len);
            }
            if (failed.Count == 0) return true;
            if (c.Kind != RaidKind.Raid6 || failed.Count > 1) return false;

            // 2) RAID 6 çift kayıp: Q ile çöz
            int other = failed[0];
            var coef = SyndromeOrder(n, pd, qd, c.Layout);
            if (!ReadMember(g * n + qd, phys(qd), _tmp, 0, len)) return false;
            Buffer.BlockCopy(_tmp, 0, _acc2, 0, len);              // Q
            Array.Clear(_acc, 0, len);                              // P'siz XOR (bilinen veriler)
            for (int s = 0; s < n; s++)
            {
                if (s == lost || s == other || s == pd || s == qd) continue;
                if (!ReadMember(g * n + s, phys(s), _tmp, 0, len)) return false;
                Xor(_acc, _tmp, len);
                Gf256.MulInto(_acc2, _tmp, len, Gf256.Pow2(coef[s]));   // Qxy = Q ⊕ Σ g^c·D
            }
            if (other == pd)
            {
                // kayıp: bir veri + P → Dx = Qx / g^a
                byte inv = Gf256.Div(1, Gf256.Pow2(coef[lost]));
                for (int i = 0; i < len; i++) _acc[i] = Gf256.Mul(_acc2[i], inv);
                return true;
            }
            // iki veri kayıp: Pxy = P ⊕ Σ; Dx = (Qxy ⊕ g^b·Pxy) / (g^a ⊕ g^b)
            if (!ReadMember(g * n + pd, phys(pd), _tmp, 0, len)) return false;
            Xor(_acc, _tmp, len);                                   // Pxy
            byte ga = Gf256.Pow2(coef[lost]), gb = Gf256.Pow2(coef[other]);
            byte den = (byte)(ga ^ gb);
            for (int i = 0; i < len; i++)
            {
                byte num = (byte)(_acc2[i] ^ Gf256.Mul(gb, _acc[i]));
                _acc[i] = Gf256.Div(num, den);
            }
            return true;
        }

        // md: Q katsayıları Q'dan sonraki diskten başlayarak sayılır (DDF: diskin sırasıyla)
        private static int[] SyndromeOrder(int n, int pd, int qd, RaidLayout l)
        {
            var r = new int[n];
            int k = 0;
            bool ddf = l is RaidLayout.DdfNRestart or RaidLayout.DdfNContinue;
            for (int i = 0; i < n; i++)
            {
                int d = ddf ? i : (qd + 1 + i) % n;
                if (d == pd || d == qd) continue;
                r[d] = k++;
            }
            return r;
        }

        private static void Xor(byte[] a, byte[] b, int len)
        {
            int i = 0;
            var va = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(a.AsSpan(0, len & ~7));
            var vb = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(b.AsSpan(0, len & ~7));
            for (int k = 0; k < va.Length; k++) va[k] ^= vb[k];
            i = len & ~7;
            for (; i < len; i++) a[i] ^= b[i];
        }

        // Parite kontrolü: N satırdan örnek alır, XOR tutuyor mu (%)
        public static (int ok, int total) ParityCheck(RaidConfig c, int samples, Func<bool> cancel)
        {
            if (c.Kind is not (RaidKind.Raid5 or RaidKind.Raid6) || c.Missing > 0) return (0, 0);
            int n = c.DisksPerGroup;
            var ms = c.Members.Select(m => m?.Open()).ToArray();
            try
            {
                long rows = c.PerMemberLength() / c.Chunk;
                var acc = new byte[4096]; var tmp = new byte[4096];
                int ok = 0, tot = 0;
                var rnd = new Random(7);
                for (int k = 0; k < samples && !cancel(); k++)
                {
                    long row = rows <= 1 ? 0 : (long)(rnd.NextDouble() * (rows - 1));
                    for (int g = 0; g < c.Groups; g++)
                    {
                        int qd = -1;
                        if (c.Kind == RaidKind.Raid6) RaidMap.Row6(n, c.Layout, row, c.Delay, 0, out _, out _, out qd);
                        Array.Clear(acc);
                        bool any = false;
                        for (int s = 0; s < n; s++)
                        {
                            if (s == qd) continue;
                            var st = ms[g * n + s]; if (st == null) continue;
                            st.Position = c.StartOf(g * n + s) + row * c.Chunk;
                            int got = st.Read(tmp, 0, 4096);
                            if (got < 4096) Array.Clear(tmp, got, 4096 - got);
                            for (int i = 0; i < 4096; i++) { acc[i] ^= tmp[i]; if (tmp[i] != 0) any = true; }
                        }
                        if (!any) continue;            // boş satır: ölçüye katma
                        tot++;
                        if (acc.All(b => b == 0)) ok++;
                    }
                }
                return (ok, tot);
            }
            finally { foreach (var s in ms) try { s?.Dispose(); } catch { } }
        }
    }

    // ───────────────────────── yardımcılar ─────────────────────────
    public static class Fmt
    {
        public static readonly CultureInfo Tr = new("tr-TR");
        public static string Size(long b)
        {
            if (b < 0) b = 0;
            double v = b;
            string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
            int i = 0;
            while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
            return (i == 0 ? v.ToString("0", Tr) : v.ToString(v >= 100 ? "0" : v >= 10 ? "0.0" : "0.00", Tr)) + " " + u[i];
        }
        public static ushort U16(byte[] b, int o) => (ushort)(b[o] | b[o + 1] << 8);
        public static uint U32(byte[] b, int o) => (uint)(b[o] | b[o + 1] << 8 | b[o + 2] << 16 | b[o + 3] << 24);
        public static ulong U64(byte[] b, int o) => U32(b, o) | (ulong)U32(b, o + 4) << 32;
        public static ushort B16(byte[] b, int o) => (ushort)(b[o] << 8 | b[o + 1]);
        public static uint B32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        public static ulong B64(byte[] b, int o) => (ulong)B32(b, o) << 32 | B32(b, o + 4);
        public static string Hex(byte[] b, int o, int n) { var sb = new StringBuilder(); for (int i = 0; i < n; i++) sb.Append(b[o + i].ToString("x2")); return sb.ToString(); }
        public static string Ascii(byte[] b, int o, int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < n && o + i < b.Length; i++) { byte c = b[o + i]; if (c == 0) break; sb.Append(c >= 32 && c < 127 ? (char)c : '?'); }
            return sb.ToString().Trim();
        }

        public static byte[] ReadAt(Stream s, long pos, int count)
        {
            var buf = new byte[count];
            try
            {
                if (pos < 0) return buf;
                s.Position = pos;
                int got = 0;
                while (got < count) { int r = s.Read(buf, got, count - got); if (r <= 0) break; got += r; }
            }
            catch { }
            return buf;
        }
    }
}
