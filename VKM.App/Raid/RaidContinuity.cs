using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace VKM.App
{
    // ─────────────────────────────────────────────────────────────────────────
    //  DOSYA SİSTEMİNDEN BAĞIMSIZ ÇÖZÜCÜ (CCTV/NVR, tanınmayan FS, şifresiz ham veri)
    //  Fikir: bir dosyanın içeriği blok sınırında "kopmaz" ama yanlış sırada kopar.
    //  1) Blok boyutu: diskte her C baytta bir içerik sıçraması var mı (4K blok özellikleri)
    //  2) Sıra/yerleşim: bir bloğun sonu, sanal sırada bir sonraki bloğun başına en çok benzeyen dizilim
    // ─────────────────────────────────────────────────────────────────────────
    public static class RaidContinuity
    {
        const int B = 4096;
        const int F = 17;   // 16 nibble kutusu + sıfır bayrağı

        static float[] Feature(byte[] buf, int off)
        {
            var f = new float[F];
            int zero = 0;
            for (int i = 0; i < B; i++) { byte x = buf[off + i]; f[x >> 4] += 1; if (x == 0) zero++; }
            for (int k = 0; k < 16; k++) f[k] /= B;
            f[16] = zero == B ? 1 : 0;
            return f;
        }

        static double Dist(float[] a, float[] b)
        {
            double d = 0;
            for (int k = 0; k < 16; k++) d += Math.Abs(a[k] - b[k]);
            return d / 2 + (a[16] != b[16] ? 0.5 : 0);
        }

        public static (RaidConfig? cfg, double score, string report) Solve(
            List<(long off, byte[][] data)> regions, IList<RaidMember> disks,
            List<(List<int> disks, bool six)> groups, long ds, int disk0OfVirtual0, Func<bool> cancel)
        {
            var rep = new StringBuilder();
            int n = disks.Count;
            if (regions.Count == 0) return (null, 0, "örnek yok");
            int rsz = regions[0].data[0].Length;
            int nb = rsz / B;

            // özellikler: [bölge][disk][blok]
            var feat = new float[regions.Count][][][];
            for (int r = 0; r < regions.Count; r++)
            {
                feat[r] = new float[n][][];
                for (int d = 0; d < n; d++)
                {
                    feat[r][d] = new float[nb][];
                    for (int b = 0; b < nb; b++) feat[r][d][b] = Feature(regions[r].data[d], b * B);
                }
            }

            // 1) blok boyutu: sınırdaki kopukluk / sınır dışı kopukluk
            long bestC = 0; double bestRatio = 0;
            for (long C = 4096; C <= rsz / 4; C <<= 1)
            {
                double sb = 0, sn = 0; int cb = 0, cn = 0;
                for (int r = 0; r < regions.Count; r++)
                    for (int d = 0; d < n; d++)
                        for (int b = 1; b < nb; b++)
                        {
                            var a = feat[r][d][b - 1]; var c = feat[r][d][b];
                            if (a[16] == 1 && c[16] == 1) continue;
                            double x = Dist(a, c);
                            long pos = regions[r].off + (long)b * B - ds;
                            if (pos % C == 0) { sb += x; cb++; } else { sn += x; cn++; }
                        }
                if (cb < 8 || cn < 8) continue;
                double ratio = (sb / cb) / Math.Max(1e-6, sn / cn);
                rep.AppendLine($"  blok {Fmt.Size(C)}: sınır kopukluğu ×{ratio:0.00}");
                // daha büyük blokta da oran yüksek kalıyorsa büyüğü seç (küçük blok sınırları da büyük sınırlardır)
                if (ratio > bestRatio * 0.92 && ratio > 1.15) { bestRatio = ratio; bestC = C; }
            }
            if (bestC == 0) return (null, 0, rep + "Blok sınırı bulunamadı (veri çok düzgün ya da şifreli).");

            // 2) yapı
            RaidKind kind = RaidKind.Raid0; int N = n;
            if (groups.Count == 1) { kind = groups[0].six ? RaidKind.Raid6 : RaidKind.Raid5; N = groups[0].disks.Count; }
            else if (groups.Count > 1) return (null, 0, rep + "Çok gruplu (50/60) dizide içerik yöntemi henüz yok — elle kurun.");
            if (N != n) return (null, 0, rep + "Parite grubu tüm diskleri kapsamıyor.");

            var Ls = kind switch
            {
                RaidKind.Raid5 => new[] { RaidLayout.LeftSym, RaidLayout.LeftAsym, RaidLayout.RightSym, RaidLayout.RightAsym },
                RaidKind.Raid6 => new[] { RaidLayout.LeftSym, RaidLayout.LeftAsym, RaidLayout.RightSym, RaidLayout.RightAsym, RaidLayout.DdfNContinue },
                _ => new[] { RaidLayout.LeftSym }
            };
            int perC = (int)(bestC / B);

            // satırlar: (bölge, satırın bölge içi ilk bloğu, mutlak satır no)
            var rows = new List<(int r, int b0, long row)>();
            for (int r = 0; r < regions.Count; r++)
                for (int b = 0; b + perC <= nb; b++)
                {
                    long pos = regions[r].off + (long)b * B - ds;
                    if (pos >= 0 && pos % bestC == 0) rows.Add((r, b, pos / bestC));
                }
            // bitişik satır çiftleri (satır sonu → sonraki satır başı)
            var rowIndex = rows.Select((x, i) => (x, i)).ToDictionary(t => (t.x.r, t.x.row), t => t.i);

            double Sim(int ri, int dA, int dB, int nextRi)
            {
                var (r, b0, _) = rows[ri]; var (r2, b2, _) = rows[nextRi];
                var a = feat[r][dA][b0 + perC - 1]; var c = feat[r2][dB][b2];
                if (a[16] == 1 && c[16] == 1) return double.NaN;   // ikisi de boş: bilgi yok
                return 1 - Dist(a, c);
            }

            var cfg = new RaidConfig { Kind = kind, DisksPerGroup = N, Groups = 1, Chunk = bestC };
            int D = cfg.DataDisksPerRow;

            double Score(RaidLayout L, int[] perm)
            {
                cfg.Layout = L;
                double s = 0; int c = 0;
                for (int i = 0; i < rows.Count; i++)
                {
                    long row = rows[i].row;
                    for (int j = 0; j < D; j++)
                    {
                        int dA = SlotOf(cfg, row, j), dB; int nextRi;
                        if (j + 1 < D) { dB = SlotOf(cfg, row, j + 1); nextRi = i; }
                        else
                        {
                            if (!rowIndex.TryGetValue((rows[i].r, row + 1), out nextRi)) continue;
                            dB = SlotOf(cfg, row + 1, 0);
                        }
                        double x = Sim(i, perm[dA], perm[dB], nextRi);
                        if (double.IsNaN(x)) continue;
                        s += x; c++;
                    }
                }
                return c == 0 ? 0 : s / c;
            }

            // taban: rastgele disk çiftlerinin benzerliği
            double baseSum = 0; int baseCnt = 0;
            var rnd = new Random(11);
            for (int k = 0; k < 4000 && rows.Count > 0; k++)
            {
                int i = rnd.Next(rows.Count);
                double x = Sim(i, rnd.Next(n), rnd.Next(n), i);
                if (!double.IsNaN(x)) { baseSum += x; baseCnt++; }
            }
            double baseline = baseCnt > 0 ? baseSum / baseCnt : 0.5;

            double best = -1; RaidLayout bestL = Ls[0]; int[] bestP = Enumerable.Range(0, N).ToArray();
            foreach (var L in Ls)
            {
                for (int restart = 0; restart < 12 && !cancel(); restart++)
                {
                    var p = Enumerable.Range(0, N).ToArray();
                    if (restart > 0) p = p.OrderBy(_ => rnd.Next()).ToArray();
                    double cur = Score(L, p);
                    bool improved = true;
                    while (improved && !cancel())
                    {
                        improved = false;
                        for (int a = 0; a < N; a++)
                            for (int b = a + 1; b < N; b++)
                            {
                                (p[a], p[b]) = (p[b], p[a]);
                                double s = Score(L, p);
                                if (s > cur + 1e-9) { cur = s; improved = true; }
                                else (p[a], p[b]) = (p[b], p[a]);
                            }
                    }
                    if (cur > best) { best = cur; bestL = L; bestP = (int[])p.Clone(); }
                }
            }

            // döndürme belirsizliği: sanal 0'ı (MBR) tutan disk biliniyorsa onu 1. veri bloğuna oturt
            cfg.Layout = bestL;
            if (disk0OfVirtual0 >= 0)
            {
                long row0 = 0;
                for (int rot = 0; rot < N; rot++)
                {
                    var p = Enumerable.Range(0, N).Select(i => bestP[(i + rot) % N]).ToArray();
                    if (p[SlotOf(cfg, row0, 0)] == disk0OfVirtual0 && Math.Abs(Score(bestL, p) - best) < 0.02) { bestP = p; break; }
                }
            }

            double conf = Math.Clamp((best - baseline) / Math.Max(0.05, 1 - baseline) * 1.6, 0, 0.95);
            cfg.Members = bestP.Select(i => (RaidMember?)disks[i]).ToArray();
            cfg.DataStart = Enumerable.Repeat(ds, N).ToArray();
            cfg.Source = "İçerik devamlılığı çözücüsü (dosya sisteminden bağımsız)";
            cfg.Confidence = conf;
            rep.AppendLine($"Devamlılık: en iyi {best:0.000} · rastgele {baseline:0.000} · {RaidConfig.LayoutText(bestL)} · blok {Fmt.Size(bestC)} · güven %{conf * 100:0}");
            return (cfg, conf, rep.ToString());
        }

        static int SlotOf(RaidConfig c, long row, int j)
        {
            int n = c.DisksPerGroup;
            if (c.Kind == RaidKind.Raid5) { RaidMap.Row5(n, c.Layout, row, c.Delay, j, out int dd, out _); return dd; }
            if (c.Kind == RaidKind.Raid6) { RaidMap.Row6(n, c.Layout, row, c.Delay, j, out int dd, out _, out _); return dd; }
            return j;
        }
    }
}
