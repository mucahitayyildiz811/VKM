using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace VKM.App
{
    public enum RaidNodeKind { Disk, Part, Raid, LvmVg, Volume, Info }

    // Ağaçtaki bir satır (disk, bölüm, sanal RAID, LVM birimi, dosya sistemi)
    public class RaidNode : INotifyPropertyChanged
    {
        public RaidNodeKind Kind;
        public string Icon { get; set; } = "";
        private string _name = "";
        public string Name { get => _name; set { _name = value; On(nameof(Name)); } }
        private string _detail = "";
        public string Detail { get => _detail; set { _detail = value; On(nameof(Detail)); } }
        public string StartText { get; set; } = "";
        public string SizeText { get; set; } = "";
        private string _status = "";
        public string Status { get => _status; set { _status = value; On(nameof(Status)); } }
        private Brush _statusBrush = Brushes.Gray;
        public Brush StatusBrush { get => _statusBrush; set { _statusBrush = value; On(nameof(StatusBrush)); } }
        public Brush NameBrush { get; set; } = Brs.Text;
        public int Depth { get; set; }
        public GridLength Indent => new(Depth * 18);
        public ObservableCollection<RaidNode> Children { get; } = new();
        private bool _exp = true;
        public bool IsExpanded { get => _exp; set { _exp = value; On(nameof(IsExpanded)); } }
        private bool _checked;
        public bool IsChecked { get => _checked; set { _checked = value; On(nameof(IsChecked)); } }
        public Visibility CheckVisibility => Kind == RaidNodeKind.Disk ? Visibility.Visible : Visibility.Collapsed;

        public RaidMember? Member;          // disk / bölüm
        public RaidConfig? Config;          // sanal RAID
        public DiskInfo? Disk;              // SMART
        public Func<Stream>? Open;          // bu düğümün içeriği (salt okunur)
        public long Length;
        public long Start;                  // üst aygıtta başlangıç (bayt)
        public string Info = "";            // sağ paneldeki ayrıntı
        public Func<long, string>? Locate;  // sanal adres → hangi diskin neresi
        public string Fs = "";              // dosya sistemi (varsa)

        public event PropertyChangedEventHandler? PropertyChanged;
        private void On(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));

        public void SetDepth(int d) { Depth = d; foreach (var c in Children) c.SetDepth(d + 1); }
    }

    public static class Brs
    {
        public static readonly Brush Ok = Freeze(0x1f, 0x9e, 0x55);
        public static readonly Brush Warn = Freeze(0xc9, 0x8a, 0x00);
        public static readonly Brush Bad = Freeze(0xd2, 0x43, 0x3a);
        public static readonly Brush Info = Freeze(0x2a, 0x8f, 0xd0);
        public static readonly Brush Dim = Freeze(0x85, 0x93, 0xa2);
        public static readonly Brush Teal = Freeze(0x0f, 0x8f, 0x86);
        public static readonly Brush Text = Freeze(0x1d, 0x27, 0x33);
        static Brush Freeze(byte r, byte g, byte b) { var x = new SolidColorBrush(Color.FromRgb(r, g, b)); x.Freeze(); return x; }
    }

    // ─────────────── tüm diskleri tarar, RAID/LVM'leri otomatik kurar ───────────────
    public class RaidScanner
    {
        private readonly Action<string> _log;
        private readonly Func<bool> _cancel;
        public readonly List<RaidNode> Roots = new();      // diskler
        public readonly List<RaidNode> Virtual = new();    // kurulan RAID/LVM'ler

        // havuzlar
        private readonly List<(RaidMember m, MdInfo md, RaidNode node)> _md = new();
        private readonly List<(RaidMember m, LvmPv pv, RaidNode node)> _pv = new();
        private readonly List<(RaidMember m, DdfInfo d, RaidNode node)> _ddf = new();
        private readonly List<(RaidMember m, ImsmInfo d, RaidNode node)> _imsm = new();
        private readonly HashSet<string> _claimed = new();   // üst verisi olan tüm diskler (Key)

        public RaidScanner(Action<string> log, Func<bool> cancel) { _log = log; _cancel = cancel; }

        // sağ tık → eski RAID yapısı için havuzlara erişim
        public IEnumerable<(RaidMember m, MdInfo md)> MdPool => _md.Select(x => (x.m, x.md)).ToList();
        public IEnumerable<(RaidMember m, DdfInfo d)> DdfPool => _ddf.Select(x => (x.m, x.d)).ToList();
        public IEnumerable<(RaidMember m, ImsmInfo d)> ImsmPool => _imsm.Select(x => (x.m, x.d)).ToList();
        public IEnumerable<(RaidMember m, LvmPv pv)> PvPool => _pv.Select(x => (x.m, x.pv)).ToList();

        // ---- simge seçimi ----
        static string FsIcon(string fs)
        {
            fs = (fs ?? "").ToLowerInvariant();
            if (fs.Contains("ntfs")) return "fsntfs";
            if (fs.Contains("ext")) return "fsext";
            if (fs.Contains("btrfs")) return "fsbtrfs";
            if (fs.Contains("xfs")) return "fsxfs";
            if (fs.Contains("fat")) return "fsfat";
            if (fs.Contains("swap")) return "swap";
            return "volume";
        }
        static string IconFor(Probe pr)
        {
            if (pr.Md != null) return pr.Md.Level switch { 1 => "mirror", 10 => "mirror", 5 => "comp5", 6 => "comp6", 0 => "part", -1 => "part", _ => "comp5" };
            if (pr.Ddf != null || pr.Imsm != null) return "comp5";
            if (pr.Lvm != null) return "lvm";
            if (pr.Fs.Length > 0) return FsIcon(pr.Fs);
            return "part";
        }

        // ---------------- 1) diskler + bölümler ----------------
        public RaidNode AddDisk(RaidMember m, DiskInfo? info, int index)
        {
            var node = new RaidNode
            {
                Kind = RaidNodeKind.Disk, Icon = (m.Model + " " + m.ImagePath).ToUpperInvariant().Contains("SSD") || m.Model.ToUpperInvariant().Contains("NVME") ? "ssd" : "hdd",
                Name = (m.ImagePath.Length > 0 ? "İmaj: " : "Disk " + index + ": ") + (m.Model.Length > 0 ? m.Model : Path.GetFileName(m.ImagePath)),
                Detail = m.Serial, SizeText = Fmt.Size(m.Length), Member = m, Disk = info, Open = m.Open, Length = m.Length
            };
            if (m.HealthPercent >= 0)
            {
                node.Status = "Sağlık %" + m.HealthPercent + (m.Risky ? " ⚠" : "");
                node.StatusBrush = m.HealthPercent >= 90 && !m.Risky ? Brs.Ok : m.HealthPercent >= 60 ? Brs.Warn : Brs.Bad;
            }
            else if (m.HealthText.Length > 0) { node.Status = m.HealthText; node.StatusBrush = m.Risky ? Brs.Bad : Brs.Dim; }
            node.Info = DiskInfoText(m, info);
            Roots.Add(node);

            try
            {
                using var s = m.Open();
                var whole = RaidMeta.Identify(s, m.Length, true);
                if (whole.Ddf != null) { _ddf.Add((m, whole.Ddf, node)); _claimed.Add(m.Key); node.Detail = whole.Label; }
                else if (whole.Imsm != null) { _imsm.Add((m, whole.Imsm, node)); _claimed.Add(m.Key); node.Detail = whole.Label; }

                var parts = whole.Ddf != null || whole.Imsm != null ? new List<DiskPartition>() : PartitionReader.Read(s, m.Sector);
                // bölüm tablosu varsa disk sonundaki md izi son bölüme aittir (tüm diske değil)
                if (parts.Count > 0) { whole.Md = null; whole.Lvm = null; }
                if (parts.Any(p => p.Offset + p.Length > m.Length + (1 << 20)))
                {
                    node.Detail = (m.Serial.Length > 0 ? m.Serial + " · " : "") + "bölüm tablosu diskten büyük → donanım RAID'in 1. diski";
                    node.Status = node.Status.Length > 0 ? node.Status : "RAID üyesi?"; 
                }
                if (parts.Count == 0 && whole.Ddf == null && whole.Imsm == null)
                {
                    // bölümsüz disk (md/LVM doğrudan diskte ya da tek dosya sistemi)
                    Register(m, whole, node);
                    node.Detail = (m.Serial.Length > 0 ? m.Serial + " · " : "") + whole.Label;
                    if (whole.Fs.Length > 0) node.Fs = whole.Fs;
                }
                foreach (var p in parts)
                {
                    if (_cancel()) break;
                    if (p.Length <= 0 || p.Offset + p.Length > m.Length + (1 << 20)) continue;
                    var sub = m.Sub(p.Offset, Math.Min(p.Length, m.Length - p.Offset), node.Name + " › " + p.Name);
                    Probe pr;
                    using (var ss = sub.Open()) pr = RaidMeta.Identify(ss, sub.Length, false);
                    var pn = new RaidNode
                    {
                        Kind = RaidNodeKind.Part, Icon = IconFor(pr),
                        Name = pr.Label, Detail = pr.VolName, StartText = (p.Offset / 512).ToString("N0", Fmt.Tr),
                        SizeText = Fmt.Size(sub.Length), Member = sub, Open = sub.Open, Length = sub.Length, Start = p.Offset, Fs = pr.Fs
                    };
                    pn.Info = $"{pr.Label}\nBaşlangıç: sektör {p.Offset / 512:N0} ({Fmt.Size(p.Offset)})\nBoyut: {Fmt.Size(sub.Length)}" + MdText(pr.Md) + LvmTextInfo(pr.Lvm);
                    node.Children.Add(pn);
                    Register(sub, pr, pn);
                }
            }
            catch (Exception ex) { node.Status = "okunamadı"; node.StatusBrush = Brs.Bad; node.Info += "\nHata: " + ex.Message; }
            return node;
        }

        // kayıp alan aramasında bulunan parça: md/LVM ise havuza kat (sonra AssembleMd/AssembleLvm)
        public bool AddFound(RaidMember m, RaidNode node)
        {
            try
            {
                Probe pr;
                using (var s = m.Open()) pr = RaidMeta.Identify(s, m.Length, false);
                if (pr.Md == null && pr.Lvm == null) return false;
                if (_md.Any(x => x.m.Key == m.Key) || _pv.Any(x => x.m.Key == m.Key)) return false;
                Register(m, pr, node);
                return true;
            }
            catch { return false; }
        }

        private void Register(RaidMember m, Probe pr, RaidNode node)
        {
            if (pr.Md != null) { _md.Add((m, pr.Md, node)); _claimed.Add(m.Key); }
            if (pr.Lvm != null) { _pv.Add((m, pr.Lvm, node)); _claimed.Add(m.Key); }
        }

        public bool IsClaimed(RaidMember whole) =>
            _claimed.Contains(whole.Key) || _claimed.Any(k => k.StartsWith((whole.DeviceId.Length > 0 ? whole.DeviceId : whole.ImagePath) + "@"));

        // ---------------- 2) üst veriden kur ----------------
        public void AssembleAll()
        {
            AssembleMd();
            AssembleDdf();
            AssembleImsm();
            AssembleMd();          // DDF/IMSM içinde md olabilir (nadir)
            AssembleLvm();
        }

        private readonly HashSet<string> _mdDone = new();

        public void AssembleMd()
        {
            foreach (var grp in _md.GroupBy(x => x.md.Uuid).ToList())
            {
                if (!_mdDone.Add(grp.Key)) continue;
                var list = grp.ToList();
                ulong maxEv = list.Max(x => x.md.Events);
                var refMd = list.First(x => x.md.Events == maxEv).md;
                int n = Math.Max(1, refMd.RaidDisks);
                var cfg = new RaidConfig { Name = refMd.Name.Length > 0 ? refMd.Name : "md", Chunk = Math.Max(4096, refMd.Chunk), Source = "Linux md " + refMd.Variant + " (Synology / QNAP / WD / Netgear / Asustor / Linux)" };
                var notes = new StringBuilder();
                switch (refMd.Level)
                {
                    case 0: cfg.Kind = RaidKind.Raid0; break;
                    case 1: cfg.Kind = RaidKind.Raid1; break;
                    case 4: cfg.Kind = RaidKind.Raid5; cfg.Layout = RaidLayout.ParityLast; break;
                    case 5: cfg.Kind = RaidKind.Raid5; cfg.Layout = refMd.Layout switch { 0 => RaidLayout.LeftAsym, 1 => RaidLayout.RightAsym, 3 => RaidLayout.RightSym, 4 => RaidLayout.ParityFirst, 5 => RaidLayout.ParityLast, _ => RaidLayout.LeftSym }; break;
                    case 6: cfg.Kind = RaidKind.Raid6; cfg.Layout = refMd.Layout switch { 0 => RaidLayout.LeftAsym, 1 => RaidLayout.RightAsym, 3 => RaidLayout.RightSym, 8 => RaidLayout.RightAsym, 9 => RaidLayout.DdfNRestart, 10 => RaidLayout.DdfNContinue, _ => RaidLayout.LeftSym }; break;
                    case 10:
                        {
                            int near = refMd.Layout & 0xFF, far = (refMd.Layout >> 8) & 0xFF;
                            cfg.Kind = RaidKind.Raid10;
                            if (far > 1) { cfg.Layout = (refMd.Layout & 0x10000) != 0 ? RaidLayout.Offset : RaidLayout.Far; cfg.Copies = far; }
                            else { cfg.Layout = RaidLayout.Near; cfg.Copies = Math.Max(1, near); }
                            break;
                        }
                    case -1: cfg.Kind = RaidKind.Jbod; break;
                    default: notes.AppendLine("Desteklenmeyen md seviyesi: " + refMd.Level); break;
                }
                cfg.DisksPerGroup = n;
                var mem = new RaidMember?[n]; var ds = new long[n];
                var stale = new List<string>();
                foreach (var x in list.OrderByDescending(x => x.md.Events))
                {
                    if (x.md.Role < 0 || x.md.Role >= n) { x.node.Detail = "yedek / arızalı işaretli"; continue; }
                    if (x.md.Events + 1 < maxEv) { stale.Add(x.m.Name + $" (olay {x.md.Events} < {maxEv})"); x.node.Detail = "ESKİ ÜYE (diziden önce düşmüş) — devre dışı"; x.node.StatusBrush = Brs.Warn; x.node.Status = "eski"; continue; }
                    if (mem[x.md.Role] != null) continue;
                    mem[x.md.Role] = x.m; ds[x.md.Role] = x.md.DataOffset;
                }
                int redundancy = cfg.Kind switch { RaidKind.Raid1 => n - 1, RaidKind.Raid5 => 1, RaidKind.Raid6 => 2, RaidKind.Raid10 => cfg.Copies - 1, _ => 0 };
                if (mem.Count(z => z == null) > redundancy && stale.Count > 0)
                {
                    // eski üyeyi çıkarınca dizi kurulamıyor → yine de kat (en iyisi bu)
                    foreach (var x in list.Where(x => x.md.Role >= 0 && x.md.Role < n && mem[x.md.Role] == null)) { mem[x.md.Role] = x.m; ds[x.md.Role] = x.md.DataOffset; }
                    notes.AppendLine("Eski üye yine de kullanıldı (yoksa dizi kurulamıyordu) — son yazılanlarda tutarsızlık olabilir.");
                }
                else if (stale.Count > 0) notes.AppendLine("Devre dışı bırakılan eski üye(ler): " + string.Join(", ", stale) + " → eksik yerine pariteden hesaplanır.");
                if (cfg.Kind == RaidKind.Raid1 || cfg.Kind == RaidKind.Jbod)
                {
                    // ayna/JBOD: yalnız mevcut üyeler, rol sırasıyla
                    var keep = Enumerable.Range(0, n).Where(i => mem[i] != null || cfg.Kind == RaidKind.Jbod).ToList();
                    if (cfg.Kind == RaidKind.Raid1) { mem = keep.Select(i => mem[i]).ToArray(); ds = keep.Select(i => ds[i]).ToArray(); cfg.DisksPerGroup = mem.Length; }
                }
                cfg.Members = mem; cfg.DataStart = ds;
                cfg.MemberDataLength = refMd.DataSize;
                if (cfg.Kind == RaidKind.Jbod) cfg.MemberDataLength = 0;
                cfg.Notes = notes.ToString();
                foreach (var x in list) if (x.node.Status.Length == 0) { x.node.Status = "üye " + (x.md.Role + 1) + "/" + n; x.node.StatusBrush = Brs.Teal; }
                AddVirtual(cfg, "md: " + cfg.Name);
            }
        }

        private void AssembleDdf()
        {
            foreach (var grp in _ddf.GroupBy(x => x.d.HeaderGuid))
            {
                var list = grp.ToList();
                var byRef = new Dictionary<uint, RaidMember>();
                foreach (var x in list) byRef[x.d.MyRef] = x.m;
                // her VD için kayıtlar (RAID 50/60: aynı VD_GUID, farklı ikincil sıra)
                var cfgs = list.SelectMany(x => x.d.Configs).GroupBy(v => v.Guid);
                foreach (var vg in cfgs)
                {
                    var spans = vg.GroupBy(v => v.SecSeq).Select(g => g.First()).OrderBy(v => v.SecSeq).ToList();
                    var v0 = spans[0];
                    var cfg = new RaidConfig { Name = v0.Name.Length > 0 ? v0.Name : "VD", Chunk = v0.StripBytes, Source = "DDF üst verisi (" + list[0].d.Vendor + ")" };
                    int n = v0.PrimaryCount;
                    switch (v0.Prl)
                    {
                        case 0x00: cfg.Kind = RaidKind.Raid0; break;
                        case 0x01: cfg.Kind = RaidKind.Raid1; break;
                        case 0x03: case 0x04: cfg.Kind = RaidKind.Raid5; cfg.Layout = RaidLayout.ParityLast; break;
                        case 0x05: cfg.Kind = RaidKind.Raid5; cfg.Layout = v0.Rlq switch { 0 => RaidLayout.RightAsym, 2 => RaidLayout.LeftAsym, _ => RaidLayout.LeftSym }; break;
                        case 0x06: cfg.Kind = RaidKind.Raid6; cfg.Layout = v0.Rlq switch { 0 => RaidLayout.RightAsym, 2 => RaidLayout.DdfNRestart, _ => RaidLayout.DdfNContinue }; break;
                        case 0x11: cfg.Kind = RaidKind.Raid10; cfg.Layout = RaidLayout.Near; cfg.Copies = 2; break;
                        case 0x09: cfg.Kind = RaidKind.Jbod; break;
                        default: cfg.Notes = "DDF seviye kodu 0x" + v0.Prl.ToString("X2") + " — çözücüye bırakıldı."; break;
                    }
                    if (v0.Prl == 0x01 && spans.Count > 1) { cfg.Kind = RaidKind.Raid10; cfg.Layout = RaidLayout.Near; cfg.Copies = n; }
                    if (v0.RotateParity > 0 && cfg.Kind is RaidKind.Raid5 or RaidKind.Raid6) cfg.Delay = 1 << v0.RotateParity;
                    var mem = new List<RaidMember?>(); var ds = new List<long>();
                    foreach (var sp in spans)
                        for (int i = 0; i < sp.PrimaryCount; i++)
                        {
                            uint r = i < sp.PdSeq.Length ? sp.PdSeq[i] : 0xFFFFFFFF;
                            mem.Add(byRef.TryGetValue(r, out var mm) ? mm : null);
                            ds.Add(i < sp.StartLba.Length ? sp.StartLba[i] * 512 : 0);
                        }
                    cfg.Groups = spans.Count > 1 && v0.Prl != 0x01 ? spans.Count : 1;
                    cfg.DisksPerGroup = cfg.Groups > 1 ? n : mem.Count;
                    if (v0.Prl == 0x01 && spans.Count > 1) { // RAID 10 (aynalar üstünde şerit): çiftler yan yana
                        cfg.Groups = 1; cfg.DisksPerGroup = mem.Count;
                    }
                    cfg.Members = mem.ToArray(); cfg.DataStart = ds.ToArray();
                    cfg.MemberDataLength = v0.BlockCount * 512;
                    cfg.Span = v0.Srl == 0 ? SpanMode.Strip : SpanMode.Concat;
                    foreach (var x in list) { x.node.Status = "DDF üyesi"; x.node.StatusBrush = Brs.Teal; }
                    // doğrulama: tanınmazsa alternatifleri dene
                    var alt = new List<RaidConfig> { cfg };
                    if (cfg.Groups > 1) { var c2 = cfg.Clone(); c2.Span = SpanMode.Stripe; alt.Add(c2); }
                    if (cfg.Kind is RaidKind.Raid5 or RaidKind.Raid6)
                        foreach (var l in new[] { RaidLayout.LeftSym, RaidLayout.LeftAsym, RaidLayout.RightAsym, RaidLayout.RightSym })
                            if (l != cfg.Layout) { var c3 = cfg.Clone(); c3.Layout = l; alt.Add(c3); }
                    AddVirtual(PickRecognized(alt), "DDF: " + cfg.Name);
                }
            }
        }

        private void AssembleImsm()
        {
            foreach (var grp in _imsm.GroupBy(x => x.d.Family))
            {
                var list = grp.ToList();
                var info = list[0].d;
                foreach (var v in info.Vols)
                {
                    var cfg = new RaidConfig { Name = v.Name, Chunk = Math.Max(4096, v.StripSectors * 512L), Source = "Intel RST (IMSM) üst verisi" };
                    int n = v.Members;
                    cfg.Kind = v.Level switch { 0 => RaidKind.Raid0, 1 => n >= 4 ? RaidKind.Raid10 : RaidKind.Raid1, 10 => RaidKind.Raid10, 5 => RaidKind.Raid5, _ => RaidKind.Raid0 };
                    if (cfg.Kind == RaidKind.Raid5) cfg.Layout = RaidLayout.LeftAsym;     // IMSM RAID 5 = sol-asimetrik
                    if (cfg.Kind == RaidKind.Raid10) { cfg.Layout = RaidLayout.Near; cfg.Copies = 2; }
                    cfg.DisksPerGroup = n;
                    var mem = new RaidMember?[n];
                    int matched = 0;
                    for (int i = 0; i < n; i++)
                    {
                        int di = v.Ord[i];
                        string ser = di < info.Serials.Count ? Norm(info.Serials[di]) : "";
                        var hit = list.FirstOrDefault(x => ser.Length > 0 && Norm(x.m.Serial).Length > 0 && (Norm(x.m.Serial).EndsWith(ser) || ser.EndsWith(Norm(x.m.Serial))));
                        if (hit.m != null) { mem[i] = hit.m; matched++; }
                    }
                    cfg.DataStart = Enumerable.Repeat(v.Pba * 512, n).ToArray();
                    cfg.MemberDataLength = v.BlocksPerMember * 512;
                    if (matched < list.Count && matched < n)
                    {
                        // seri no eşleşmedi (imaj dosyası vb.) → sırayı çözücü bulsun
                        _log("IMSM: disk sırası seri numarasından bulunamadı → çözücü ile sıralanıyor…");
                        var sr = RaidSolver.Solve(list.Select(x => x.m).ToList(),
                            new SolverHints { Kind = cfg.Kind, TotalSlots = n, Chunk = cfg.Chunk, Layout = cfg.Kind == RaidKind.Raid5 ? RaidLayout.LeftAsym : null, DataStart = v.Pba * 512 }, _log, _cancel);
                        if (sr.Config != null) { sr.Config.Name = v.Name; sr.Config.Source = cfg.Source + " + çözücü (sıra)"; sr.Config.MemberDataLength = cfg.MemberDataLength; cfg = sr.Config; }
                    }
                    else cfg.Members = mem;
                    foreach (var x in list) { x.node.Status = "RST üyesi"; x.node.StatusBrush = Brs.Teal; }
                    AddVirtual(cfg, "Intel RST: " + v.Name);
                }
            }
        }

        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        private readonly HashSet<string> _vgDone = new();
        public void AssembleLvm()
        {
            foreach (var grp in _pv.Where(p => p.pv.VgId.Length > 0).GroupBy(p => p.pv.VgId).ToList())
            {
                if (!_vgDone.Add(grp.Key)) continue;
                var list = grp.ToList();
                var bestPv = list.OrderByDescending(p => p.pv.Seq).First().pv;
                var root = LvmText.Parse(bestPv.Text);
                if (LvmText.Sub(root, bestPv.VgName) is not Dictionary<string, object> vg) continue;
                long extBytes = LvmText.Num(vg, "extent_size") * 512;
                var pvs = LvmText.Sub(vg, "physical_volumes") ?? new();
                var pvNames = pvs.Keys.ToList();
                var open = new Func<Stream>?[pvNames.Count];
                var peStart = new long[pvNames.Count];
                int found = 0;
                for (int i = 0; i < pvNames.Count; i++)
                {
                    var pd = pvs[pvNames[i]] as Dictionary<string, object>;
                    if (pd == null) continue;
                    string id = LvmText.Str(pd, "id").Replace("-", "");
                    peStart[i] = LvmText.Num(pd, "pe_start") * 512;
                    var hit = list.FirstOrDefault(p => p.pv.PvUuid == id);
                    if (hit.m != null) { open[i] = hit.m.Open; found++; }
                }
                var vgNode = new RaidNode
                {
                    Kind = RaidNodeKind.LvmVg, Icon = "lvm", Name = "LVM birim grubu: " + bestPv.VgName,
                    Detail = $"{found}/{pvNames.Count} fiziksel birim bulundu", Status = found == pvNames.Count ? "tam" : "EKSİK PV",
                    StatusBrush = found == pvNames.Count ? Brs.Ok : Brs.Bad,
                    Info = $"LVM birim grubu: {bestPv.VgName}\nExtent: {Fmt.Size(extBytes)}\nFiziksel birimler: {string.Join(", ", pvNames)}\nBulunan: {found}/{pvNames.Count}"
                };
                foreach (var p in list) { p.node.Status = "LVM: " + bestPv.VgName; p.node.StatusBrush = Brs.Teal; }
                var lvs = LvmText.Sub(vg, "logical_volumes") ?? new();
                foreach (var kv in lvs)
                {
                    if (kv.Value is not Dictionary<string, object> lv) continue;
                    var segs = new List<LvmStream.Seg>();
                    string unsupported = "";
                    foreach (var sk in lv.Where(x => x.Key.StartsWith("segment") && x.Value is Dictionary<string, object>))
                    {
                        var sd = (Dictionary<string, object>)sk.Value;
                        string type = LvmText.Str(sd, "type");
                        if (type != "striped" && type != "linear") { unsupported = type; continue; }
                        var seg = new LvmStream.Seg { Start = LvmText.Num(sd, "start_extent"), Count = LvmText.Num(sd, "extent_count"), StripeSize = LvmText.Num(sd, "stripe_size") * 512 };
                        if (sd.TryGetValue("stripes", out var so) && so is List<object> sl)
                            for (int i = 0; i + 1 < sl.Count; i += 2)
                                seg.Stripes.Add((pvNames.IndexOf(sl[i]?.ToString() ?? ""), sl[i + 1] is long l ? l : 0));
                        segs.Add(seg);
                    }
                    long len = segs.Count == 0 ? 0 : segs.Max(s => (s.Start + s.Count) * extBytes);
                    var segsCopy = segs; var openCopy = open; var pe = peStart; long eb = extBytes;
                    Func<Stream> op = () => new LvmStream(segsCopy, eb, pe, openCopy);
                    var lvNode = new RaidNode
                    {
                        Kind = RaidNodeKind.Volume, Icon = FsIcon(""), Name = bestPv.VgName + "-" + kv.Key, SizeText = Fmt.Size(len), Length = len, Open = segs.Count > 0 ? op : null,
                        Info = $"LVM mantıksal birim: {kv.Key}\nBoyut: {Fmt.Size(len)}\nParça: {segs.Count}" + (unsupported.Length > 0 ? "\nDesteklenmeyen parça türü: " + unsupported : "")
                    };
                    if (unsupported.Length > 0 && segs.Count == 0) { lvNode.Detail = "tür: " + unsupported + " (thin/raid — henüz okunmuyor)"; lvNode.StatusBrush = Brs.Warn; lvNode.Status = "desteklenmiyor"; }
                    else ProbeContent(lvNode, 0);
                    vgNode.Children.Add(lvNode);
                }
                Virtual.Add(vgNode);
            }
        }

        // ---------------- 3) üst verisiz diskler: çözücü ----------------
        public void SolveLeftovers(IList<RaidNode> diskNodes)
        {
            var cand = diskNodes.Where(d => d.Member != null && !IsClaimed(d.Member) && d.Length > 64L << 20).OrderBy(d => d.Length).ToList();
            // boyutu %1 içinde olan diskler aynı dizinin üyesi sayılır (değişen disk biraz büyük olabilir)
            var groups = new List<List<RaidNode>>();
            foreach (var d in cand)
            {
                if (groups.Count > 0 && d.Length <= groups[^1][0].Length * 1.01) groups[^1].Add(d);
                else groups.Add(new List<RaidNode> { d });
            }
            foreach (var g in groups.Where(g => g.Count >= 2 && g.Count <= 48))
            {
                if (_cancel()) return;
                var members = g.Select(d => d.Member!).ToList();
                _log($"Üst verisi olmayan {members.Count} eş boyutlu disk ({Fmt.Size(g[0].Length)}) → otomatik RAID çözücü…");
                var r = RaidSolver.Solve(members, null, _log, _cancel);
                _log(r.Report.TrimEnd());
                if (r.Config == null || r.Score < 0.6) { _log("→ RAID yapısı bulunamadı (bağımsız diskler olabilir). Gerekirse 'Seçili diskleri çöz' / 'Elle kur'."); continue; }
                r.Config.Name = "Bulunan RAID";
                var node = AddVirtual(r.Config, "Bulunan " + r.Config.LevelText());
                foreach (var d in g) { d.Status = "RAID üyesi (çözücü)"; d.StatusBrush = Brs.Teal; }
                node.Info += "\n\nÇözücü raporu:\n" + r.Report;
            }
        }

        // ---------------- yardımcılar ----------------
        public RaidNode AddVirtual(RaidConfig cfg, string title)
        {
            var c = cfg;
            long cap = c.Capacity();
            var node = new RaidNode
            {
                Kind = RaidNodeKind.Raid, Icon = c.Missing > 0 ? "arraywarn" : "array", Name = title, Detail = c.Summary(), SizeText = Fmt.Size(cap),
                Config = c, Length = cap, Open = () => new RaidStream(c), Locate = v => RaidStream.Describe(c, v)
            };
            int redundancy = c.Kind switch { RaidKind.Raid1 => Math.Max(0, c.Total - 1), RaidKind.Raid5 => c.Groups, RaidKind.Raid6 => 2 * c.Groups, RaidKind.Raid10 => c.Total / 2, _ => 0 };
            if (c.Missing == 0) { node.Status = c.Confidence < 0.9 ? $"kuruldu · güven %{c.Confidence * 100:0}" : "SAĞLAM"; node.StatusBrush = c.Confidence < 0.75 ? Brs.Warn : Brs.Ok; }
            else if (c.Missing <= redundancy) { node.Status = $"{c.Missing} disk eksik · pariteden"; node.StatusBrush = Brs.Warn; }
            else { node.Status = $"{c.Missing} disk eksik · BOZUK"; node.StatusBrush = Brs.Bad; }
            var worst = c.Members.Where(m => m != null && m.HealthPercent >= 0).Select(m => m!.HealthPercent).DefaultIfEmpty(-1).Min();
            if (worst >= 0 && worst < 90) node.Status += $" · en zayıf disk %{worst}";
            node.Info = RaidInfoText(c);
            ProbeContent(node, 0);
            Virtual.Add(node);
            // md/LVM katmanı: sanal aygıtın kendisi de üye olabilir (Synology: md → LVM)
            return node;
        }

        // Sanal aygıtın içini tanı: bölümler, dosya sistemi, LVM
        public void ProbeContent(RaidNode node, int depth)
        {
            if (node.Open == null || depth > 2) return;
            try
            {
                using var s = node.Open();
                var asMember = new RaidMember { Name = node.Name, Length = node.Length, Open = node.Open, OpenStrict = node.Open, DeviceId = "virt:" + node.Name + ":" + node.GetHashCode() };
                var pr = RaidMeta.Identify(s, node.Length, false);
                if (pr.Lvm != null) { _pv.Add((asMember, pr.Lvm, node)); node.Detail += " · LVM PV (" + pr.Lvm.VgName + ")"; return; }
                if (pr.Md != null) { _md.Add((asMember, pr.Md, node)); node.Detail += " · md üyesi"; return; }
                var parts = PartitionReader.Read(s, 512);
                if (parts.Count == 0)
                {
                    if (pr.Fs.Length > 0)
                    {
                        node.Fs = pr.Fs;
                        node.Children.Add(new RaidNode
                        {
                            Kind = RaidNodeKind.Volume, Icon = FsIcon(pr.Fs), Name = pr.Label + (pr.VolName.Length > 0 ? " · " + pr.VolName : ""), SizeText = Fmt.Size(node.Length),
                            StartText = "0", Open = node.Open, Length = node.Length, Fs = pr.Fs, Locate = node.Locate, Info = pr.Label + "\nBoyut: " + Fmt.Size(node.Length) + "\n(çift tıkla: Veri Kurtarma'da aç)"
                        });
                    }
                    else node.Children.Add(new RaidNode { Kind = RaidNodeKind.Info, Icon = "?", Name = pr.Label, NameBrush = Brs.Warn, Info = "İçerik tanınmadı. Ayarlar yanlış olabilir: blok boyutu / sıra / yerleşim değiştirip tekrar deneyin ya da Hex ile bakın." });
                    return;
                }
                foreach (var p in parts)
                {
                    long off = p.Offset, len = Math.Min(p.Length, Math.Max(0, node.Length - p.Offset));
                    if (len <= 0) continue;
                    var parent = node.Open; var loc = node.Locate;
                    Func<Stream> op = () => new SubStream(parent(), off, len);
                    Probe pp;
                    using (var ss = op()) pp = RaidMeta.Identify(ss, len, false);
                    var ch = new RaidNode
                    {
                        Kind = RaidNodeKind.Volume, Icon = IconFor(pp), Name = pp.Label + (pp.VolName.Length > 0 ? " · " + pp.VolName : ""),
                        StartText = (off / 512).ToString("N0", Fmt.Tr), SizeText = Fmt.Size(len), Open = op, Length = len, Start = off, Fs = pp.Fs,
                        Locate = loc == null ? null : v => loc(v + off),
                        Info = $"{pp.Label}\nBaşlangıç: sektör {off / 512:N0}\nBoyut: {Fmt.Size(len)}\n(çift tıkla: Veri Kurtarma'da aç)"
                    };
                    node.Children.Add(ch);
                    var am = new RaidMember { Name = ch.Name, Length = len, Open = op, OpenStrict = op, DeviceId = "virt:" + node.Name + ":" + off };
                    if (pp.Lvm != null) _pv.Add((am, pp.Lvm, ch));
                    if (pp.Md != null) _md.Add((am, pp.Md, ch));
                }
            }
            catch (Exception ex) { node.Children.Add(new RaidNode { Kind = RaidNodeKind.Info, Name = "içerik okunamadı: " + ex.Message, NameBrush = Brs.Bad }); }
        }

        // DDF alternatiflerinden içeriği tanınanı seç
        private RaidConfig PickRecognized(List<RaidConfig> alts)
        {
            foreach (var c in alts)
            {
                try
                {
                    using var s = new RaidStream(c);
                    var pr = RaidMeta.Identify(s, c.Capacity(), false);
                    if (pr.Fs.Length > 0 || pr.HasPartTable || pr.Lvm != null) return c;
                }
                catch { }
            }
            return alts[0];
        }

        public static string RaidInfoText(RaidConfig c)
        {
            var sb = new StringBuilder();
            sb.AppendLine(c.LevelText() + " — " + c.Name);
            sb.AppendLine("Kaynak: " + c.Source);
            if (c.Confidence < 0.999) sb.AppendLine($"Güven: %{c.Confidence * 100:0}");
            sb.AppendLine("Kapasite: " + Fmt.Size(c.Capacity()));
            if (c.Kind is not (RaidKind.Raid1 or RaidKind.Jbod)) sb.AppendLine("Blok (stripe) boyutu: " + Fmt.Size(c.Chunk));
            if (c.Kind is RaidKind.Raid5 or RaidKind.Raid6) sb.AppendLine("Parite yerleşimi: " + RaidConfig.LayoutText(c.Layout) + (c.Delay > 1 ? " · gecikme " + c.Delay : ""));
            if (c.Kind == RaidKind.Raid10) sb.AppendLine($"Kopya: {c.Copies} · {RaidConfig.LayoutText(c.Layout)}");
            if (c.Groups > 1) sb.AppendLine($"Gruplar: {c.Groups} × {c.DisksPerGroup} disk · dağıtım: {(c.Span == SpanMode.Strip ? "blok blok" : c.Span == SpanMode.Stripe ? "şerit şerit" : "uç uca")}");
            sb.AppendLine("Üyeler (sırayla):");
            for (int i = 0; i < c.Members.Length; i++)
            {
                var m = c.Members[i];
                string g = c.Groups > 1 ? $"G{i / c.DisksPerGroup + 1} " : "";
                sb.AppendLine(m == null
                    ? $"  {g}{i + 1}. EKSİK — pariteden hesaplanır"
                    : $"  {g}{i + 1}. {m.Name}  · veri başı {Fmt.Size(c.StartOf(i))}" + (m.HealthPercent >= 0 ? $" · sağlık %{m.HealthPercent}" : "") + (m.Risky ? " ⚠ riskli" : ""));
            }
            if (c.Notes.Length > 0) sb.AppendLine(c.Notes.TrimEnd());
            return sb.ToString();
        }

        static string MdText(MdInfo? m) => m == null ? "" :
            $"\n\nLinux md {m.Variant}: {RaidMeta.MdLevelName(m)} · {m.RaidDisks} disk · blok {Fmt.Size(m.Chunk)}\nDizi: {m.Name} ({m.Uuid})\nBu diskin rolü: {(m.Role < 0 ? "yedek/arızalı" : (m.Role + 1).ToString())}\nOlay sayacı: {m.Events}\nVeri başı: {Fmt.Size(m.DataOffset)}";

        static string LvmTextInfo(LvmPv? p) => p == null ? "" : $"\n\nLVM PV · birim grubu: {p.VgName}\nPV uuid: {p.PvUuid}";

        static string DiskInfoText(RaidMember m, DiskInfo? d)
        {
            var sb = new StringBuilder();
            sb.AppendLine(m.Name);
            if (m.Model.Length > 0) sb.AppendLine("Model: " + m.Model);
            if (m.Serial.Length > 0) sb.AppendLine("Seri: " + m.Serial);
            sb.AppendLine("Boyut: " + Fmt.Size(m.Length) + $" ({m.Length / 512:N0} sektör)");
            if (m.DeviceId.Length > 0) sb.AppendLine("Aygıt: " + m.DeviceId);
            if (m.ImagePath.Length > 0) sb.AppendLine("İmaj: " + m.ImagePath);
            if (d != null)
            {
                if (d.Interface.Length > 0) sb.AppendLine("Arayüz: " + d.Interface);
                if (d.Health.Length > 0) sb.AppendLine("Sağlık: " + d.Health + (d.HealthPercent >= 0 ? " (%" + d.HealthPercent + ")" : ""));
                if (d.SmartLine.Length > 0) sb.AppendLine("SMART: " + d.SmartLine);
                if (d.RiskDetail.Length > 0) sb.AppendLine("⚠ " + d.RiskDetail);
            }
            return sb.ToString();
        }
    }
}
