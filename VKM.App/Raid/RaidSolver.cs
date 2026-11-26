using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VKM.App
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  ÜST VERİSİZ RAID ÇÖZÜCÜ (HP Smart Array, IBM, Adaptec, bozuk kontrolcü, silinmiş metadata…)
    //
    //  1) Parite: aynı adresteki blokların XOR ilişkisini GF(2) cebiriyle bulur →
    //     hangi diskler bir parite grubu (RAID 5/50), hangisinde Q dönüyor (RAID 6/60),
    //     hangileri ayna (RAID 1/10), hiç ilişki yok (RAID 0 / eksik diskli RAID 5).
    //  2) Çapalar: diskte bulunan ve sanal diskteki yeri kesin bilinen yapılar —
    //     MBR/GPT/boot sektörü (mutlak), NTFS MFT kayıt no, ext süper blok grup no,
    //     XFS AGF no, Btrfs süper blok adresi (göreli).
    //  3) Her olasılık (seviye × blok boyutu × yerleşim × başlangıç × grup düzeni)
    //     çapalarla puanlanır; disk sırası çapalardan çıkar. En yüksek puan kazanır.
    // ─────────────────────────────────────────────────────────────────────────────
    public class SolverHints
    {
        public RaidKind? Kind;
        public int? TotalSlots;          // eksik diskler dâhil yuva sayısı
        public long? Chunk;
        public RaidLayout? Layout;
        public long? DataStart;
        public int? Groups;
    }

    public class SolverResult
    {
        public RaidConfig? Config;
        public double Score;
        public string Report = "";
    }

    public static class RaidSolver
    {
        const int BLK = 4096;
        const int MB = 1 << 20;

        struct Anchor { public string Fam; public int Disk; public long Phys; public long X; }

        public static SolverResult Solve(IList<RaidMember> disks, SolverHints? hints, Action<string> log, Func<bool> cancel)
        {
            hints ??= new SolverHints();
            var res = new SolverResult();
            int n = disks.Count;
            if (n < 2) { res.Report = "En az 2 disk gerekir."; return res; }
            var rep = new StringBuilder();

            var streams = new Stream?[n];
            for (int i = 0; i < n; i++) try { streams[i] = disks[i].Open(); } catch { streams[i] = null; }
            try
            {
                long minLen = disks.Min(d => d.Length);
                var anchors = new List<Anchor>();
                var samples = new List<(long off, byte[][] blocks)>();
                var read = new HashSet<long>();      // okunmuş MB indeksleri
                var targets = new List<long>();      // sanal adres hedefleri (bölüm başı, MFT)
                var xfsInfo = new Dictionary<string, (long agBlocks, int bs, int sect)>();
                var xagf = new List<(int disk, long phys, long seq, string uuid)>();

                var keep = new List<(long off, byte[][] data)>();     // içerik devamlılığı için saklanan bölgeler
                int maxKeep = Math.Clamp(96 / Math.Max(1, n), 4, 12);
                bool keepOn = false;

                void ReadRange(long from, long to, int sampleEvery)
                {
                    from = Math.Max(0, from / MB * MB); to = Math.Min(minLen, to);
                    var bufs = new byte[n][];
                    for (int i = 0; i < n; i++) bufs[i] = new byte[MB];
                    int k = 0;
                    for (long o = from; o + MB <= to && !cancel(); o += MB, k++)
                    {
                        if (!read.Add(o / MB)) continue;
                        Parallel.For(0, n, i =>
                        {
                            var s = streams[i];
                            if (s == null) { Array.Clear(bufs[i]); return; }
                            try
                            {
                                lock (s)
                                {
                                    s.Position = o; int got = 0;
                                    while (got < MB) { int r = s.Read(bufs[i], got, MB - got); if (r <= 0) break; got += r; }
                                    if (got < MB) Array.Clear(bufs[i], got, MB - got);
                                }
                            }
                            catch { Array.Clear(bufs[i]); }
                        });
                        for (int i = 0; i < n; i++) ScanAnchors(bufs[i], i, o, anchors, targets, xfsInfo, xagf);
                        if (keepOn && keep.Count < maxKeep && bufs.Any(x => x.Any(v => v != 0)))
                            keep.Add((o, bufs.Select(x => (byte[])x.Clone()).ToArray()));
                        if (sampleEvery > 0 && k % sampleEvery == 0 && samples.Count < 700)
                        {
                            foreach (int so in new[] { 0, MB / 2 })
                            {
                                var bl = new byte[n][];
                                for (int i = 0; i < n; i++) { bl[i] = new byte[BLK]; Buffer.BlockCopy(bufs[i], so, bl[i], 0, BLK); }
                                samples.Add((o + so, bl));
                            }
                        }
                    }
                }

                log($"Çözücü: {n} disk okunuyor (baş bölge)…");
                ReadRange(0, 64L * MB, 1);
                // hedeflere göre pencere oku (bölüm başları, MFT) — 2 tur
                for (int round = 0; round < 2 && !cancel(); round++)
                {
                    var t = targets.Distinct().Where(x => x > 32L * MB).Take(12).ToList();
                    targets.Clear();
                    foreach (long v in t)
                    {
                        long lo = v / n - 8L * MB, hi = v / Math.Max(1, n - 2) + 24L * MB;
                        if (hi - lo > 384L * MB) hi = lo + 384L * MB;
                        ReadRange(lo, hi, 4);
                    }
                }
                // dağınık örnekler (parite analizi + uzak çapalar)
                int spread = 40;
                keepOn = true;
                for (int k = 0; k < spread && !cancel(); k++)
                {
                    long o = 64L * MB + (long)((minLen - 128L * MB) * (k / (double)spread));
                    ReadRange(o, o + MB, 1);
                }
                keepOn = false;
                if (cancel()) { res.Report = "İptal edildi."; return res; }

                // XFS AGF → çapa (AG boyutu süper bloktan)
                foreach (var a in xagf)
                    if (xfsInfo.TryGetValue(a.uuid, out var xi) || (xfsInfo.Count == 1 && (xi = xfsInfo.Values.First()).bs > 0))
                        anchors.Add(new Anchor { Fam = "xfs", Disk = a.disk, Phys = a.phys, X = a.seq * xi.agBlocks * xi.bs + xi.sect });

                // ---- 1) parite yapısı ----
                var (groups, mirrors, qobs, allMirror, stat) = ParityStructure(samples, n);
                rep.AppendLine($"Örnek blok: {samples.Count} · çapa: {anchors.Count} ({string.Join(", ", anchors.GroupBy(a => a.Fam.Split(':')[0]).Select(g => g.Key + " " + g.Count()))})");
                rep.AppendLine("Parite analizi: " + stat);
                log("Parite analizi: " + stat);

                if (allMirror && hints.Kind == null)
                {
                    var c1 = new RaidConfig { Kind = RaidKind.Raid1, Members = disks.Cast<RaidMember?>().ToArray(), DataStart = new long[n], DisksPerGroup = n, Source = "Otomatik çözücü", Confidence = 0.99 };
                    res.Config = c1; res.Score = 0.99; res.Report = rep + "Tüm diskler birbirinin aynısı → RAID 1 (ayna).";
                    return res;
                }

                // ---- 2) çapa aileleri ----
                var fams = new Dictionary<string, List<Anchor>>();
                foreach (var a in anchors)
                {
                    if (!fams.TryGetValue(a.Fam, out var l)) fams[a.Fam] = l = new List<Anchor>();
                    l.Add(a);
                }
                foreach (var key in fams.Keys.ToList())
                {
                    var xs = fams[key].Select(a => a.X).Distinct().OrderBy(x => x).ToList();
                    if (xs.Count > 300)
                    {
                        var keepX = new HashSet<long>(Enumerable.Range(0, 300).Select(i => xs[(int)(i * (long)xs.Count / 300)]));
                        fams[key] = fams[key].Where(a => keepX.Contains(a.X)).ToList();
                    }
                }
                int disk0 = anchors.Where(a => a.Fam == "abs" && a.X == 0).Select(a => a.Disk).DefaultIfEmpty(-1).First();
                long ds0 = hints.DataStart ?? anchors.Where(a => a.Fam == "abs" && a.X == 0).Select(a => a.Phys).DefaultIfEmpty(0).First();
                SolverResult? ByContent()
                {
                    log("Dosya sistemi izi yetersiz → içerik devamlılığı yöntemi (FS'den bağımsız)…");
                    var (cc, csc, crep) = RaidContinuity.Solve(keep, disks, groups, ds0, disk0, cancel);
                    rep.Append(crep);
                    if (cc == null) return null;
                    return new SolverResult { Config = cc, Score = csc, Report = rep + $"Sonuç: {cc.Summary()} · güven %{csc * 100:0}" };
                }
                if (fams.Values.Sum(l => l.Count) < 3)
                {
                    if (hints.Kind == null && ByContent() is SolverResult rc) return rc;
                    res.Report = rep + "Yeterli iz bulunamadı (disk boş, şifreli ya da çok düzgün veri). Elle kurun.";
                    return res;
                }

                // ---- 3) olasılıklar ----
                var hyps = new List<(RaidKind kind, int G, int N, int[]? unit, string note)>();
                if (hints.Kind != null)
                {
                    int tot = hints.TotalSlots ?? n; int G = hints.Groups ?? 1;
                    hyps.Add((hints.Kind.Value, G, tot / G, null, "üst veri ipucu"));
                }
                else if (groups.Count > 0)
                {
                    var lv = groups.Any(g => g.six) ? RaidKind.Raid6 : RaidKind.Raid5;
                    int G = groups.Count; int N = groups.Max(g => g.disks.Count);
                    hyps.Add((lv, G, N, null, "parite bulundu"));
                    if (G == 1 && N == n) hyps.Add((RaidKind.Raid6, 1, n + 1, null, "RAID 6, bir disk eksik"));
                    if (G > 1) hyps.Add((lv, G, N + 1, null, "gruplarda bir disk eksik"));
                }
                else if (mirrors.Count > 0 && mirrors.Count * 2 == n)
                {
                    var unit = new int[n];
                    for (int u = 0; u < mirrors.Count; u++) { unit[mirrors[u].a] = u; unit[mirrors[u].b] = u; }
                    hyps.Add((RaidKind.Raid0, 1, mirrors.Count, unit, "ayna çiftleri → RAID 10"));
                }
                else
                {
                    hyps.Add((RaidKind.Raid0, 1, n, null, "RAID 0"));
                    hyps.Add((RaidKind.Raid10, 1, n, null, "RAID 1E / 10 (near)"));
                    hyps.Add((RaidKind.Raid5, 1, n + 1, null, "RAID 5, bir disk eksik"));
                    hyps.Add((RaidKind.Raid6, 1, n + 1, null, "RAID 6, bir disk eksik"));
                    hyps.Add((RaidKind.Raid6, 1, n + 2, null, "RAID 6, iki disk eksik"));
                }

                var dsCands = new HashSet<long> { 0 };
                if (hints.DataStart != null) { dsCands.Clear(); dsCands.Add(hints.DataStart.Value); }
                else foreach (var a in anchors.Where(a => a.Fam == "abs" && a.X == 0)) dsCands.Add(a.Phys);
                if (dsCands.Count > 6) dsCands = new HashSet<long>(dsCands.Take(6));

                var chunks = hints.Chunk != null ? new[] { hints.Chunk.Value } : Enumerable.Range(0, 10).Select(i => 4096L << i).ToArray();
                double best = 0; RaidConfig? bestCfg = null; string bestNote = ""; int[]? bestUnit = null; Dictionary<int, int>? bestPerm = null;
                int evals = 0;
                foreach (var h in hyps)
                {
                    RaidLayout[] Ls = h.kind switch
                    {
                        RaidKind.Raid5 => new[] { RaidLayout.LeftSym, RaidLayout.LeftAsym, RaidLayout.RightSym, RaidLayout.RightAsym, RaidLayout.ParityLast },
                        RaidKind.Raid6 => new[] { RaidLayout.LeftSym, RaidLayout.LeftAsym, RaidLayout.RightSym, RaidLayout.RightAsym, RaidLayout.DdfNRestart, RaidLayout.DdfNContinue },
                        RaidKind.Raid10 => new[] { RaidLayout.Near },
                        _ => new[] { RaidLayout.LeftSym }
                    };
                    if (hints.Layout != null) Ls = new[] { hints.Layout.Value };
                    var spans = h.G > 1 ? new[] { SpanMode.Strip, SpanMode.Stripe } : new[] { SpanMode.Strip };
                    var delays = new List<int> { 1 };
                    for (int pass = 0; pass < 2; pass++)
                    {
                        foreach (long C in chunks)
                            foreach (var L in Ls)
                                foreach (var sp in spans)
                                    foreach (int dl in delays)
                                        foreach (long ds in dsCands)
                                        {
                                            if (cancel()) { res.Report = "İptal edildi."; return res; }
                                            var cfg = new RaidConfig { Kind = h.kind, Layout = L, Groups = h.G, DisksPerGroup = h.N, Copies = 2, Span = sp, Chunk = C, Delay = dl };
                                            double sc = Evaluate(cfg, fams, ds, h.unit, h.kind == RaidKind.Raid6 ? qobs : null, out var perm);
                                            evals++;
                                            if (sc > best + 0.02)
                                            {
                                                best = sc; bestCfg = cfg; bestNote = h.note; bestUnit = h.unit; bestPerm = perm;
                                                cfg.DataStart = new long[] { ds };
                                            }
                                        }
                        // gecikmeli parite yalnız düşük puanda denenir
                        if (pass == 0 && best < 0.85 && h.kind is RaidKind.Raid5 or RaidKind.Raid6 && hints.Layout == null)
                            delays = new List<int> { 2, 4, 8, 16, 32, 64, 128, 256 };
                        else break;
                    }
                }

                // çapalar zayıf kaldıysa içerik yöntemiyle karşılaştır
                if (best < 0.5 && hints.Kind == null && ByContent() is SolverResult rc2 && rc2.Score > best) return rc2;

                // JBOD olasılığı (parite/ayna yoksa)
                if (groups.Count == 0 && mirrors.Count == 0 && hints.Kind == null)
                {
                    var jb = TryJbod(fams, disks, out double jsc);
                    if (jb != null && jsc > best + 0.05) { res.Config = jb; res.Score = jsc; res.Report = rep + $"JBOD (uç uca) · güven %{jsc * 100:0}"; return res; }
                }

                if (bestCfg == null || bestPerm == null) { res.Report = rep + "Uygun yapı bulunamadı."; return res; }

                // sonuç yapılandırması: yuva → disk
                int total = bestCfg.Groups * bestCfg.DisksPerGroup;
                long dStart = bestCfg.DataStart[0];
                if (bestUnit != null)
                {
                    // RAID 10 (ayna çiftleri üstünde RAID 0) → md near-2 olarak kur: çift(u) → yuva 2u, 2u+1
                    var c10 = new RaidConfig { Kind = RaidKind.Raid10, Layout = RaidLayout.Near, Copies = 2, Groups = 1, DisksPerGroup = n, Chunk = bestCfg.Chunk };
                    var mem = new RaidMember?[n];
                    foreach (var kv in bestPerm)
                    {
                        int u = kv.Value;   // birim no
                        var pair = mirrors[u];
                        mem[kv.Key * 2] = disks[pair.a]; mem[kv.Key * 2 + 1] = disks[pair.b];
                    }
                    c10.Members = mem; c10.DataStart = Enumerable.Repeat(dStart, n).ToArray();
                    bestCfg = c10;
                }
                else
                {
                    var mem = new RaidMember?[total];
                    foreach (var kv in bestPerm) if (kv.Key >= 0 && kv.Key < total) mem[kv.Key] = disks[kv.Value];
                    // kullanılmamış diskler (çapa düşmemiş) boş yuvalara — RAID 5/6'da parite yönünden doğrulanamaz, sıradan doldur
                    var unused = Enumerable.Range(0, n).Where(i => !bestPerm.ContainsValue(i)).ToList();
                    int emptySlots = mem.Count(m => m == null);
                    int redundancy = (bestCfg.Kind == RaidKind.Raid5 ? 1 : bestCfg.Kind == RaidKind.Raid6 ? 2 : 0) * bestCfg.Groups;
                    if (unused.Count > 0 && emptySlots > 0)
                    {
                        // grup bilgisi varsa diskleri kendi grubunun boş yuvasına koy
                        foreach (int d in unused.ToList())
                        {
                            int gi = groups.FindIndex(g => g.disks.Contains(d));
                            for (int s = 0; s < total; s++)
                            {
                                if (mem[s] != null) continue;
                                if (gi >= 0 && bestCfg.Groups > 1)
                                {
                                    int g = s / bestCfg.DisksPerGroup;
                                    var present = Enumerable.Range(g * bestCfg.DisksPerGroup, bestCfg.DisksPerGroup).Where(x => mem[x] != null).Select(x => Array.IndexOf(disks.ToArray(), mem[x]!)).ToList();
                                    if (present.Count > 0 && !present.All(x => groups[gi].disks.Contains(x))) continue;
                                }
                                mem[s] = disks[d]; unused.Remove(d); break;
                            }
                        }
                        if (emptySlots > 1 && unused.Count == 0 && mem.Count(m => m == null) > redundancy)
                            rep.AppendLine("Uyarı: bazı disklerin yeri çapalarla doğrulanamadı.");
                    }
                    bestCfg.Members = mem;
                    bestCfg.DataStart = Enumerable.Repeat(dStart, total).ToArray();
                }
                bestCfg.Source = "Otomatik çözücü (üst verisiz)";
                bestCfg.Confidence = best;
                bestCfg.Notes = bestNote;
                rep.AppendLine($"Sonuç: {bestCfg.Summary()} · veri başlangıcı {Fmt.Size(dStart)} · güven %{best * 100:0} ({evals} olasılık denendi)");
                res.Config = bestCfg; res.Score = best; res.Report = rep.ToString();
                return res;
            }
            finally { foreach (var s in streams) try { s?.Dispose(); } catch { } }
        }

        // ---------------- çapa tarayıcı ----------------
        static void ScanAnchors(byte[] b, int disk, long baseOff, List<Anchor> A, List<long> targets,
                                Dictionary<string, (long, int, int)> xfs, List<(int, long, long, string)> xagf)
        {
            for (int o = 0; o + 512 <= b.Length; o += 512)
            {
                uint m0 = Fmt.U32(b, o);
                long phys = baseOff + o;
                // NTFS MFT kaydı: "FILE" + kayıt no
                if (m0 == 0x454C4946)
                {
                    int usa = Fmt.U16(b, o + 4); int alloc = (int)Fmt.U32(b, o + 0x1C); int used = (int)Fmt.U32(b, o + 0x18);
                    if (usa == 0x30 && (alloc == 1024 || alloc == 4096) && used > 0 && used <= alloc)
                        A.Add(new Anchor { Fam = "mft", Disk = disk, Phys = phys, X = Fmt.U32(b, o + 0x2C) * (long)alloc });
                    continue;
                }
                // GPT başlığı: kendi LBA'sını yazar → mutlak çapa
                if (m0 == 0x20494645 && Fmt.U32(b, o + 4) == 0x54524150)   // "EFI PART"
                {
                    long my = (long)Fmt.U64(b, o + 0x18);
                    if (my > 0 && my < 1L << 40)
                    {
                        A.Add(new Anchor { Fam = "abs", Disk = disk, Phys = phys, X = my * 512 });
                        if (my == 1)
                        {
                            A.Add(new Anchor { Fam = "abs", Disk = disk, Phys = phys - 512, X = 0 });
                            long ent = (long)Fmt.U64(b, o + 0x48); int cnt = (int)Fmt.U32(b, o + 0x50), esz = (int)Fmt.U32(b, o + 0x54);
                            int eo = o + (int)((ent - 1) * 512);
                            if (esz == 128 && cnt > 0 && eo > 0 && eo + 128 <= b.Length)
                                for (int e = 0; e < Math.Min(cnt, 32) && eo + (e + 1) * 128 <= b.Length; e++)
                                {
                                    long first = (long)Fmt.U64(b, eo + e * 128 + 0x20);
                                    if (first > 0) targets.Add(first * 512);
                                }
                        }
                    }
                    continue;
                }
                // ext süper blok (grup no içerir)
                if (Fmt.U16(b, o + 0x38) == 0xEF53 && o + 0x80 <= b.Length)
                {
                    uint lbs = Fmt.U32(b, o + 0x18); uint bpg = Fmt.U32(b, o + 0x20); int grp = Fmt.U16(b, o + 0x5A);
                    if (lbs <= 6 && bpg > 0 && bpg == 8192u << (int)lbs && Fmt.U32(b, o + 0x28) > 0)
                    {
                        long bs = 1024L << (int)lbs;
                        long x = grp == 0 ? 1024 : (bs == 1024 ? (1 + (long)grp * bpg) * 1024 : (long)grp * bpg * bs);
                        // birincil süper blok bloğun 1024. baytında: burada o, süper bloğun kendisi
                        A.Add(new Anchor { Fam = "ext:" + Fmt.Hex(b, o + 0x68, 16), Disk = disk, Phys = phys, X = x });
                    }
                }
                // XFS
                if (m0 == 0x42534658)   // "XFSB"
                {
                    xfs[Fmt.Hex(b, o + 0x20, 16)] = ((long)Fmt.B32(b, o + 0x54), (int)Fmt.B32(b, o + 4), Fmt.B16(b, o + 0x66));
                    continue;
                }
                if (m0 == 0x46474158)   // "XAGF"
                {
                    xagf.Add((disk, phys, Fmt.B32(b, o + 8), xfs.Count > 0 ? xfs.Keys.First() : ""));
                    continue;
                }
                // Btrfs süper blok (kendi adresini yazar)
                if (o >= 0x40 && Fmt.U64(b, o + 0x40) == 0x4D5F53665248425FUL)
                {
                    A.Add(new Anchor { Fam = "btrfs:" + Fmt.Hex(b, o + 0x20, 16), Disk = disk, Phys = phys, X = (long)Fmt.U64(b, o + 0x30) });
                    continue;
                }
                // 55AA: boot sektörü / MBR
                if (b[o + 510] == 0x55 && b[o + 511] == 0xAA)
                {
                    string oem = Encoding.ASCII.GetString(b, o + 3, 8);
                    if (oem == "NTFS    ")
                    {
                        int bps = Fmt.U16(b, o + 0x0B); int spc = b[o + 0x0D]; if (spc > 0x80) spc = 1 << (256 - spc);
                        long hidden = Fmt.U32(b, o + 0x1C); long mft = (long)Fmt.U64(b, o + 0x30);
                        if (bps is 512 or 4096 && spc > 0 && hidden > 0)
                        {
                            A.Add(new Anchor { Fam = "abs", Disk = disk, Phys = phys, X = hidden * bps });
                            targets.Add(hidden * bps + mft * spc * bps);
                        }
                    }
                    else if (oem == "EXFAT   ")
                    {
                        long po = (long)Fmt.U64(b, o + 0x40);
                        if (po > 0) A.Add(new Anchor { Fam = "abs", Disk = disk, Phys = phys, X = po * 512 });
                    }
                    else if (Encoding.ASCII.GetString(b, o + 0x52, 5) == "FAT32")
                    {
                        long hidden = Fmt.U32(b, o + 0x1C);
                        if (hidden > 0) A.Add(new Anchor { Fam = "abs", Disk = disk, Phys = phys, X = hidden * Math.Max(512, (int)Fmt.U16(b, o + 0x0B)) });
                    }
                    else if (LooksLikeMbr(b, o))
                    {
                        A.Add(new Anchor { Fam = "abs", Disk = disk, Phys = phys, X = 0 });
                        for (int e = 0; e < 4; e++)
                        {
                            int eo = o + 0x1BE + e * 16; byte t = b[eo + 4];
                            if (t != 0 && t != 0xEE && t != 0x05 && t != 0x0F) targets.Add(Fmt.U32(b, eo + 8) * 512L);
                        }
                    }
                }
            }
        }

        static bool LooksLikeMbr(byte[] b, int o)
        {
            int ok = 0;
            for (int e = 0; e < 4; e++)
            {
                int eo = o + 0x1BE + e * 16;
                byte st = b[eo], t = b[eo + 4];
                if (t == 0) { if (Fmt.U32(b, eo + 8) != 0) return false; continue; }
                if (st != 0 && st != 0x80) return false;
                uint start = Fmt.U32(b, eo + 8), cnt = Fmt.U32(b, eo + 12);
                if (start == 0 || cnt == 0) return false;
                ok++;
            }
            if (ok == 0) return false;
            // genişletilmiş bölüm kaydı (EBR) değil: ilk girdi bölüm başını 1 MB'den uzak göstermez
            return true;
        }

        // ---------------- GF(2) parite analizi ----------------
        static (List<(List<int> disks, bool six)> groups, List<(int a, int b)> mirrors, List<(long off, int qdisk)> qobs, bool allMirror, string stat)
            ParityStructure(List<(long off, byte[][] blocks)> samples, int n)
        {
            var count = new Dictionary<ulong, int>();
            var per = new List<(long off, List<ulong> cs, ulong nz)>();
            foreach (var (off, blocks) in samples)
            {
                var idx = new List<int>(); var vecs = new List<ulong[]>();
                for (int i = 0; i < n; i++)
                {
                    var v = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(blocks[i]).ToArray();
                    if (v.Any(x => x != 0)) { idx.Add(i); vecs.Add(v); }
                }
                if (idx.Count < 2) continue;
                var deps = Kernel(vecs);
                var cs = new List<ulong>();
                foreach (ulong c in Circuits(deps))
                {
                    ulong real = 0;
                    for (int k = 0; k < idx.Count; k++) if (((c >> k) & 1) != 0) real |= 1UL << idx[k];
                    cs.Add(real); count[real] = count.GetValueOrDefault(real) + 1;
                }
                ulong nz = 0; foreach (int i in idx) nz |= 1UL << i;
                per.Add((off, cs, nz));
            }
            int total = Math.Max(1, per.Count);
            double thr = 0.15 * total;
            var w = new double[n, n];
            var pairs = new Dictionary<(int, int), int>();
            foreach (var kv in count)
            {
                var mem = Enumerable.Range(0, n).Where(i => ((kv.Key >> i) & 1) != 0).ToList();
                if (mem.Count == 2) pairs[(mem[0], mem[1])] = pairs.GetValueOrDefault((mem[0], mem[1])) + kv.Value;
                else if (mem.Count >= 3) foreach (int a in mem) foreach (int b in mem) w[a, b] += kv.Value;
            }
            var groups = new List<(List<int>, bool)>();
            var seen = new HashSet<int>();
            for (int a = 0; a < n; a++)
            {
                if (seen.Contains(a) || w[a, a] < thr) continue;
                var comp = new HashSet<int> { a }; var st = new Stack<int>(); st.Push(a);
                while (st.Count > 0) { int x = st.Pop(); for (int b = 0; b < n; b++) if (!comp.Contains(b) && w[x, b] >= thr) { comp.Add(b); st.Push(b); } }
                foreach (int x in comp) seen.Add(x);
                ulong gm = 0; foreach (int x in comp) gm |= 1UL << x;
                int full = 0, miss1 = 0;
                foreach (var (off, cs, nz) in per)
                {
                    ulong exp = gm & nz;
                    foreach (ulong c in cs)
                    {
                        if ((c & ~gm) != 0) continue;
                        if (c == exp) full++;
                        else if ((c & ~exp) == 0 && System.Numerics.BitOperations.PopCount(exp & ~c) == 1) miss1++;
                    }
                }
                groups.Add((comp.OrderBy(x => x).ToList(), miss1 > full));
            }
            var mirrors = pairs.Where(p => p.Value >= thr && !seen.Contains(p.Key.Item1) && !seen.Contains(p.Key.Item2))
                               .OrderByDescending(p => p.Value).Select(p => p.Key).ToList();
            // çakışmayan çiftler
            var used = new HashSet<int>(); var mir = new List<(int, int)>();
            foreach (var (a, b) in mirrors) if (!used.Contains(a) && !used.Contains(b)) { used.Add(a); used.Add(b); mir.Add((a, b)); }
            // hepsi birbirinin aynası mı (RAID 1, 2+ disk)
            bool allMirror = n >= 2 && groups.Count == 0 && Enumerable.Range(1, n - 1).All(i => pairs.GetValueOrDefault((0, i)) >= 0.5 * total);
            // RAID 6 Q gözlemleri
            var qobs = new List<(long, int)>();
            foreach (var (disks, six) in groups)
            {
                if (!six) continue;
                ulong gm = 0; foreach (int x in disks) gm |= 1UL << x;
                foreach (var (off, cs, nz) in per)
                {
                    ulong exp = gm & nz;
                    foreach (ulong c in cs)
                    {
                        if ((c & ~gm) != 0 || (c & ~exp) != 0) continue;
                        ulong miss = exp & ~c;
                        if (System.Numerics.BitOperations.PopCount(miss) == 1) qobs.Add((off, System.Numerics.BitOperations.TrailingZeroCount(miss)));
                    }
                }
            }
            string stat = groups.Count > 0
                ? string.Join(" + ", groups.Select(g => (g.Item2 ? "RAID 6 grubu" : "RAID 5 grubu") + " [" + string.Join(",", g.Item1.Select(x => x + 1)) + "]"))
                : mir.Count > 0 ? "ayna çiftleri: " + string.Join(" ", mir.Select(p => $"[{p.Item1 + 1}={p.Item2 + 1}]"))
                : allMirror ? "tüm diskler aynı (ayna)" : "parite/ayna ilişkisi yok (RAID 0, JBOD ya da eksik diskli dizi)";
            return (groups, mir, qobs, allMirror, stat);
        }

        static List<ulong> Kernel(List<ulong[]> vecs)
        {
            var piv = new List<(ulong[] v, ulong m, int w, int bit)>();
            var deps = new List<ulong>();
            for (int i = 0; i < vecs.Count && i < 64; i++)
            {
                var v = (ulong[])vecs[i].Clone(); ulong m = 1UL << i;
                foreach (var (pv, pm, w, bit) in piv)
                    if (((v[w] >> bit) & 1) != 0) { for (int k = 0; k < v.Length; k++) v[k] ^= pv[k]; m ^= pm; }
                int nzw = Array.FindIndex(v, x => x != 0);
                if (nzw < 0) deps.Add(m);
                else piv.Add((v, m, nzw, System.Numerics.BitOperations.TrailingZeroCount(v[nzw])));
            }
            return deps;
        }

        static List<ulong> Circuits(List<ulong> deps)
        {
            if (deps.Count == 0) return new List<ulong>();
            if (deps.Count > 8) deps = deps.Take(8).ToList();
            var all = new HashSet<ulong>();
            int d = deps.Count;
            for (int mask = 1; mask < 1 << d; mask++)
            {
                ulong m = 0;
                for (int i = 0; i < d; i++) if (((mask >> i) & 1) != 0) m ^= deps[i];
                if (m != 0) all.Add(m);
            }
            var res = new List<ulong>();
            foreach (ulong m in all.OrderBy(x => System.Numerics.BitOperations.PopCount(x)))
                if (!res.Any(c => (c & m) == c)) res.Add(m);
            return res;
        }

        // ---------------- olasılık puanlama ----------------
        static double Evaluate(RaidConfig cfg, Dictionary<string, List<Anchor>> fams, long ds, int[]? unit, List<(long off, int qdisk)>? qobs, out Dictionary<int, int> perm)
        {
            int N = cfg.DisksPerGroup, G = cfg.Groups;
            long C = cfg.Chunk;
            double P = (cfg.Kind == RaidKind.Raid10 ? N / (double)cfg.Copies : cfg.DataDisksPerRow) * G;
            var votes = new Dictionary<int, Dictionary<int, int>>();
            double total = 0;
            var chosen = new List<(string fam, List<(int slot, int disk, long x)> loc)>();
            foreach (var kv in fams.OrderBy(k => k.Key == "abs" ? 0 : 1).ThenByDescending(k => k.Value.Count))
            {
                string fam = kv.Key;
                var an = kv.Value.Where(a => a.Phys >= ds).Select(a => (disk: unit != null ? unit[a.Disk] : a.Disk, a.Phys, a.X)).ToList();
                if (an.Count == 0) continue;
                long ph = 0;
                if (fam != "abs")
                {
                    var phi = new Dictionary<long, int>();
                    foreach (var a in an) { long f = (((a.Phys - ds) % C - a.X) % C + C) % C; phi[f] = phi.GetValueOrDefault(f) + 1; }
                    ph = phi.OrderByDescending(p => p.Value).First().Key;
                }
                var cons = an.Where(a => ((((a.Phys - ds) % C - a.X) % C + C) % C) == ph)
                             .Select(a => (a.disk, R: (a.Phys - ds) / C, Vr: (a.X + ph) / C, a.X)).ToList();
                int w = fam == "abs" ? 20 : 1;
                total += an.Select(a => a.X).Distinct().Count() * w;
                List<long> ks;
                if (fam == "abs") ks = new List<long> { 0 };
                else
                {
                    var kc = new Dictionary<long, int>();
                    int span = (int)Math.Ceiling(P) + 3;
                    foreach (var a in cons)
                    {
                        long lo = (long)Math.Floor(a.R * P) - a.Vr - 1;
                        for (long k = lo; k < lo + span; k++) kc[k] = kc.GetValueOrDefault(k) + 1;
                    }
                    if (kc.Count == 0) continue;
                    int mx = kc.Values.Max();
                    ks = kc.Where(p => p.Value >= mx - 1).Select(p => p.Key).ToList();
                }
                double bestSc = -1; List<(int, int, long)>? bestLoc = null;
                foreach (long k in ks)
                {
                    var loc = new List<(int, int, long)>();
                    foreach (var a in cons)
                    {
                        long v = a.Vr + k; if (v < 0) continue;
                        for (int copy = 0; copy < (cfg.Kind == RaidKind.Raid10 ? cfg.Copies : 1); copy++)
                            if (RaidMap.Locate(cfg, v, copy, out int slot, out long row) && row == a.R) loc.Add((slot, a.disk, a.X));
                    }
                    var tmp = new Dictionary<int, Dictionary<int, int>>();
                    foreach (var (s, d, _) in loc) { if (!tmp.TryGetValue(s, out var t)) tmp[s] = t = new(); t[d] = t.GetValueOrDefault(d) + 1; }
                    int internalSc = tmp.Values.Sum(t => t.Values.Max());
                    int ext = loc.Count(l => votes.TryGetValue(l.Item1, out var vv) && vv.Count > 0 && vv.OrderByDescending(p => p.Value).First().Key == l.Item2);
                    double sc = internalSc + 2 * ext;
                    if (sc > bestSc) { bestSc = sc; bestLoc = loc; }
                }
                if (bestLoc == null) continue;
                foreach (var (s, d, _) in bestLoc) { if (!votes.TryGetValue(s, out var t)) votes[s] = t = new(); t[d] = t.GetValueOrDefault(d) + 1; }
                chosen.Add((fam, bestLoc));
            }
            perm = new Dictionary<int, int>();
            var usedD = new HashSet<int>();
            foreach (var kv in votes.OrderByDescending(v => v.Value.Values.Max()))
                foreach (var dv in kv.Value.OrderByDescending(p => p.Value))
                    if (!usedD.Contains(dv.Key)) { perm[kv.Key] = dv.Key; usedD.Add(dv.Key); break; }
            double good = 0;
            for (int fi = 0; fi < chosen.Count; fi++)
            {
                var xs = new HashSet<long>();
                foreach (var (s, d, x) in chosen[fi].loc) if (perm.TryGetValue(s, out int pd) && pd == d) xs.Add(x);
                good += xs.Count * (chosen[fi].fam == "abs" ? 20 : 1);
            }
            double score = total > 0 ? good / total : 0;
            if (qobs != null && qobs.Count > 0 && score > 0 && cfg.Kind == RaidKind.Raid6)
            {
                int hit = 0, tot = 0;
                foreach (var (off, qd) in qobs)
                {
                    if (off < ds) continue;
                    long row = (off - ds) / C;
                    RaidMap.Row6(N, cfg.Layout, row, cfg.Delay, 0, out _, out _, out int qs);
                    for (int g = 0; g < G; g++)
                        if (perm.TryGetValue(g * N + qs, out int d)) { tot++; if (d == qd) hit++; }
                }
                if (tot > 0) score = score * 0.8 + 0.2 * hit / tot;
            }
            return score;
        }

        // JBOD: her diskin sanal başlangıcı çapalardan
        static RaidConfig? TryJbod(Dictionary<string, List<Anchor>> fams, IList<RaidMember> disks, out double score)
        {
            score = 0;
            int n = disks.Count;
            if (!fams.TryGetValue("abs", out var abs)) return null;
            var start = new long?[n];
            foreach (var a in abs.Where(a => a.X == 0 && a.Phys == 0)) start[a.Disk] = 0;
            if (start.Count(s => s == 0) != 1) return null;
            int first = Array.FindIndex(start, s => s == 0);
            // diğer diskler: göreli ailelerde ilk diskle ortak tabandan
            foreach (var f in fams.Where(f => f.Key != "abs"))
            {
                var byDisk = f.Value.GroupBy(a => a.Disk).ToDictionary(g => g.Key, g => g.Select(a => a.X - a.Phys).GroupBy(x => x).OrderByDescending(x => x.Count()).First().Key);
                if (!byDisk.TryGetValue(first, out long b0)) continue;
                foreach (var kv in byDisk) if (start[kv.Key] == null) start[kv.Key] = kv.Value - b0;
            }
            var order = Enumerable.Range(0, n).Where(i => start[i] != null).OrderBy(i => start[i]).ToList();
            if (order.Count < 2) return null;
            long acc = 0; int ok = 0;
            foreach (int i in order) { if (Math.Abs(start[i]!.Value - acc) < 1 << 20) ok++; acc += disks[i].Length; }
            score = ok / (double)n * 0.9;
            var rest = Enumerable.Range(0, n).Where(i => start[i] == null).ToList();
            return new RaidConfig
            {
                Kind = RaidKind.Jbod, DisksPerGroup = n, Members = order.Concat(rest).Select(i => (RaidMember?)disks[i]).ToArray(),
                DataStart = new long[n], Source = "Otomatik çözücü (üst verisiz)", Confidence = score
            };
        }
    }
}
