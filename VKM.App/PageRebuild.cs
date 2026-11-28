using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace VKM.App
{
    // İÇERİKTEN KURTARMA — motorun açamadığı bozuk / ransomware'li MDF → .bak
    // Gerçek bir ransomware dosyasında (ORKA_0002_2020, aralıklı şifreleme) doğrulanan mantık:
    //  • Sayfa sınıflama: gerçek SQL sayfası / şifreli-çöp / boş. m_pageId (0x20) kendi numarasını taşır.
    //  • TORN-PAGE düzeltmesi: başlıkta 0x100 bayrağı varsa her 512B sektörün son baytının son 2 biti
    //    başlıktaki 0x3C alanından geri yüklenir (yapılmazsa her sayfada 16 bayt sessizce bozuk çıkar).
    //  • Katalog: sysschobjs(34) sysrowsets(5) sysallocunits(7) syscolpars(41) sysrscols(3) — sadece idx<256 sayfalar.
    //  • Tablo→sayfa: auid → (m_objId = auid>>16, m_indexId = auid>>48).
    //  • Kolon yerleşimi: sysrscols kesin kaydı; yoksa hipotezler (eksik kolon boyutu 0/1/2/4/8, nullbit=colid),
    //    tarih/metin/ondalık kolonlarla KANITLANAN hipotez seçilir; kanıt yoksa NULL (çöp yazılmaz).
    //  • Kayıp katalog satırları kardeş tablolardaki uzlaşmayla tamamlanır (ör. 1. kolon "ID int").
    public static class PageRebuild
    {
        const int PAGE = 8192;

        static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        static short I16(byte[] b, int o) => (short)U16(b, o);
        static int I32(byte[] b, int o) => b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        static uint U32(byte[] b, int o) => (uint)I32(b, o);
        static long I64(byte[] b, int o) => (long)U32(b, o) | ((long)U32(b, o + 4) << 32);

        static bool KnownType(byte t) =>
            t == 1 || t == 2 || t == 3 || t == 4 || t == 7 || t == 8 || t == 9 ||
            t == 10 || t == 11 || t == 13 || t == 15 || t == 16 || t == 17 || t == 18 || t == 19;

        static void FixTorn(byte[] b)
        {
            uint tb = U32(b, 0x3C);
            for (int s = 1; s < 16; s++) { int p = s * 512 + 511; b[p] = (byte)((b[p] & 0xFC) | ((tb >> (2 * s)) & 3)); }
        }

        static bool ReadPage(FileStream fs, long no, byte[] buf)
        {
            long off = no * PAGE; if (off + PAGE > fs.Length) return false;
            fs.Position = off;
            if (ReadFull(fs, buf, PAGE) != PAGE) return false;
            if (buf[0] == 1 && (U16(buf, 4) & 0x100) != 0) FixTorn(buf);
            return true;
        }

        // ───────────────────────── kayıt ─────────────────────────
        sealed class Rec
        {
            public byte[] P = Array.Empty<byte>();
            public int Abs, FixedEnd, NCol, NullOff, NullBytes, VarCount;
            public bool FromLog;      // işlem günlüğünden (LDF) gelen satır görüntüsü
            public int[] VS = Array.Empty<int>(), VE = Array.Empty<int>();
            public bool[] VC = Array.Empty<bool>();
            public bool IsNull(int nb1)
            {
                if (NullBytes == 0) return false;
                int i = nb1 - 1; if (i < 0 || i / 8 >= NullBytes) return false;
                return (P[NullOff + i / 8] & (1 << (i % 8))) != 0;
            }
        }

        static Rec? ParseRec(byte[] p, int rec, bool ghost = false)
        {
            if (rec < 96 || rec > PAGE - 4) return null;
            byte a = p[rec];
            int rt = (a >> 1) & 7;
            if (ghost ? rt != 6 : (rt != 0 && rt != 1)) return null;   // 6 = hayalet (silinmiş) veri kaydı
            int fe = U16(p, rec + 2);
            if (fe < 4 || rec + fe + 2 > PAGE) return null;
            int ncol = U16(p, rec + fe);
            if (ncol == 0 || ncol > 1024) return null;
            var r = new Rec { P = p, Abs = rec, FixedEnd = fe, NCol = ncol, NullOff = rec + fe + 2 };
            r.NullBytes = (a & 0x10) != 0 ? (ncol + 7) / 8 : 0;
            int pos = r.NullOff + r.NullBytes;
            if (pos > PAGE) return null;
            if ((a & 0x20) != 0)
            {
                if (pos + 2 > PAGE) return null;
                int vc = U16(p, pos);
                if (vc > 1024 || pos + 2 + vc * 2 > PAGE) return null;
                r.VarCount = vc; r.VS = new int[vc]; r.VE = new int[vc]; r.VC = new bool[vc];
                int prev = pos + 2 + vc * 2 - rec;
                for (int k = 0; k < vc; k++)
                {
                    int e = U16(p, pos + 2 + k * 2);
                    bool cx = (e & 0x8000) != 0; int end = e & 0x7FFF;
                    if (end < prev || rec + end > PAGE) { r.VS[k] = -1; r.VE[k] = -1; }
                    else { r.VS[k] = rec + prev; r.VE[k] = rec + end; prev = end; }
                    r.VC[k] = cx;
                }
            }
            return r;
        }

        static IEnumerable<Rec> PageRecs(byte[] page, bool ghost = false)
        {
            int slots = U16(page, 0x16);
            if (slots <= 0 || slots > 2000) yield break;
            for (int s = 0; s < slots; s++)
            {
                var r = ParseRec(page, U16(page, PAGE - 2 - s * 2), ghost);
                if (r != null) yield return r;
            }
        }

        // ── İŞLEM GÜNLÜĞÜ (LDF): MDF'de şifreli sayfada kalan EN SON kayıtlar günlükte satır görüntüsü olarak durur.
        //    Ekleme (LOP_INSERT_ROWS) kayıtlarındaki tam satır görüntüleri, tablonun kayıt imzasıyla (sabit alan boyu + kolon sayısı) bulunur.
        //    Aynı satırın birden çok görüntüsünde en yeni sürüm (tarihdamga = rowversion büyük olan) alınır; sonradan silinenler alınmaz.
        sealed class LdfImages
        {
            public byte[] L = Array.Empty<byte>();
            public Dictionary<(int fe, int nc), List<int>> Ins = new(), Del = new();
            // günlük kaydı satırın ait olduğu bölümün (partition/rowset) kimliğini görüntüden 24 bayt önce taşır → tablo kesin ayrılır
            public Dictionary<long, List<int>> InsByPid = new(), DelByPid = new();
            public int Count;
            public long Pid(int s) => s >= 24 ? BitConverter.ToInt64(L, s - 24) : 0;
            public static LdfImages? Load(string path)
            {
                var fi = new FileInfo(path);
                if (!fi.Exists || fi.Length < 65536 || fi.Length > 1_500_000_000L) return null;
                var x = new LdfImages { L = File.ReadAllBytes(path) };
                var L = x.L;
                // SEKTÖR PARİTESİ: günlük bloğunun her 512B sektörünün ilk baytı parite (0x40/0x80) ile değiştirilir; asılları bloğun
                // SONUNDA ters sırayla saklanır (sektör j → blok sonu - 1 - j). Geri konmazsa sektör sınırına denk gelen satırlar bozuk okunur.
                for (int pos = 8192; pos + 512 <= L.Length;)
                {
                    byte f = L[pos];
                    int size = L[pos + 6] | (L[pos + 7] << 8), nrec = L[pos + 2] | (L[pos + 3] << 8);
                    bool ok = (f & 0x10) != 0 && L[pos + 1] == 0 && size >= 512 && size % 512 == 0 && size <= 61440 && pos + size <= L.Length && nrec >= 1 && nrec <= 4000;
                    for (int j = 1; ok && j < Math.Min(4, size / 512); j++) { byte pj = L[pos + 512 * j]; if ((pj & 0xC0) == 0 || (pj & 0x07) != 0) ok = false; }
                    if (!ok) { pos += 512; continue; }
                    for (int j = 1; j < size / 512; j++) L[pos + 512 * j] = L[pos + size - 1 - j];
                    pos += size;
                }
                for (int s = 64; s + 8 < L.Length; s++)
                {
                    byte a = L[s];
                    if (a != 0x70 && a != 0x30 && a != 0x50 && a != 0x10) continue;
                    int fe = L[s + 2] | (L[s + 3] << 8);
                    if (fe < 8 || fe > 8060 || s + fe + 2 > L.Length) continue;
                    int nc = L[s + fe] | (L[s + fe + 1] << 8);
                    if (nc < 2 || nc > 1024) continue;
                    byte op = L[s - 50], ctx = L[s - 49];
                    if (ctx != 0x01 && ctx != 0x02) continue;                     // yığın / kümelenmiş tablo
                    var dict = op == 0x02 ? x.Ins : op == 0x03 ? x.Del : null;     // ekleme / silme
                    if (dict == null) continue;
                    if (!dict.TryGetValue((fe, nc), out var li)) dict[(fe, nc)] = li = new();
                    li.Add(s); x.Count++;
                    long pid = BitConverter.ToInt64(L, s - 24);
                    var pd = op == 0x02 ? x.InsByPid : x.DelByPid;
                    if (!pd.TryGetValue(pid, out var lp)) pd[pid] = lp = new();
                    lp.Add(s);
                }
                return x;
            }
            public Rec? At(int s)
            {
                var buf = new byte[PAGE];
                int n = Math.Min(8060, L.Length - s);
                Buffer.BlockCopy(L, s, buf, 96, n);
                return ParseRec(buf, 96);
            }
        }

        // şifreli MDF'nin yanındaki log: ORKA_0015_2026.mdf.xxx → ORKA_0015_2026_log.ldf.xxx
        static string? FindLdf(string src)
        {
            try
            {
                string dir = Path.GetDirectoryName(src) ?? ".", nm = Path.GetFileName(src);
                int i = nm.IndexOf(".mdf", StringComparison.OrdinalIgnoreCase);
                string bn = i > 0 ? nm[..i] : Path.GetFileNameWithoutExtension(nm);
                return Directory.EnumerateFiles(dir, bn + "*").FirstOrDefault(f =>
                {
                    string n = Path.GetFileName(f);
                    if (n.Equals(nm, StringComparison.OrdinalIgnoreCase)) return false;
                    string rest = n.Substring(bn.Length);
                    return rest.IndexOf("ldf", StringComparison.OrdinalIgnoreCase) >= 0 && (rest.StartsWith("_log", StringComparison.OrdinalIgnoreCase) || rest.StartsWith(".ldf", StringComparison.OrdinalIgnoreCase));
                });
            }
            catch { return null; }
        }

        static string VarUtf16(Rec r, int k1)
        {
            int k = k1 - 1; if (k < 0 || k >= r.VarCount || r.VS[k] < 0) return "";
            return Encoding.Unicode.GetString(r.P, r.VS[k], r.VE[k] - r.VS[k]).TrimEnd('\0');
        }

        // ───────────────────────── model ─────────────────────────
        internal sealed class Col
        {
            public int ColId; public string Name = ""; public byte X; public short Len; public byte Prec, Scale; public bool Guess;
            public bool Ident, NotNull;   // syscolpars.status: 0x4 = IDENTITY, 0x1 = NOT NULL
            public bool IsVar, IsBit, Lost; public int Off, VarIdx, NullBit, BitPos;
            public Col Copy() => new Col { ColId = ColId, Name = Name, X = X, Len = Len, Prec = Prec, Scale = Scale, Guess = Guess, Ident = Ident, NotNull = NotNull };
            public string Key() => IsVar + "|" + Off + "|" + VarIdx + "|" + NullBit + "|" + BitPos;
        }
        sealed class Tbl
        {
            public int Id; public string Name = ""; public List<Col> Cols = new(); public List<string> Guessed = new();
            public List<long> Pages = new(); public bool Compressed; public long RowsetId; public long SlotSum;
        }

        sealed class RefTbl
        {
            public string Schema = "dbo", Name = "";
            public List<Col> Cols = new();
            public Dictionary<string, (int off, int nb, int bp)> Lay = new(StringComparer.OrdinalIgnoreCase);
        }

        // Kurtarılan tablonun yeniden-kurulum DB'sindeki karşılığı (referansa aktarım için)
        // Aynı müşterinin Orka DB'lerinin uyumluluk seviyesi (referanstan / sağlam önyükleme sayfasından); referanssız DB'ler de buna çekilir
        static int KnownCompat = 0;
        // son Rebuild referansı gerçekten kullanabildi mi (panel "yapı referanstan" mesajı buna göre)
        public static bool LastRefUsed;
        // vergi/TC no → (yıl, firma veritabanı adı ORKA_xxxx_yyyy) — GNL_FIRMATANIM'dan; kurtarılan e-Faturalar firmasına bağlanır
        static readonly Dictionary<string, List<(int year, string db)>> FirmMap = new();
        // yeni çalıştırma (başka müşteri olabilir) → öğrenilenler sıfırlanır
        public static void ResetSession() { FirmMap.Clear(); KnownCompat = 0; PlanNames.Clear(); PlanNamesDbs.Clear(); PlanNamesDir = null; }
        // aynı müşterinin (aynı muhasebe bürosu) firmaları ortak hesap planı kullanır: "191 1 20" → "%20 İndirilecek KDV".
        //  Kurtarılan her firmanın planı öğrenilir; bir firmada adı kaybolan hesap, EN AZ 2 başka firmada aynı adla geçiyorsa o adı alır.
        static readonly Dictionary<(string kod, string ad), int> PlanNames = new();
        static readonly HashSet<string> PlanNamesDbs = new(StringComparer.OrdinalIgnoreCase);
        static string? PlanNamesDir;
        static void LoadPlanNames(string dir)
        {
            if (PlanNamesDir == dir) return;
            PlanNames.Clear(); PlanNamesDbs.Clear(); PlanNamesDir = dir;
            try
            {
                string f = Path.Combine(dir, "VKM_hesapadlari.tsv"); if (!File.Exists(f)) return;
                foreach (var l in File.ReadAllLines(f, Encoding.UTF8))
                {
                    var p = l.Split('\t');
                    if (p.Length == 2 && p[0] == "#db") PlanNamesDbs.Add(p[1]);
                    else if (p.Length == 3 && int.TryParse(p[2], out int n)) PlanNames[(p[0], p[1])] = n;
                }
            }
            catch { }
        }
        static void LearnPlanNames(string db, string dir)
        {
            LoadPlanNames(dir);
            if (!PlanNamesDbs.Add(db)) return;
            var cs = Conn(db);
            if (Query(cs, "SELECT OBJECT_ID('dbo.ORT_PLAN');", 60)[0][0] == DBNull.Value) return;
            foreach (var r in Query(cs, "SELECT DISTINCT LTRIM(RTRIM(plankod)), LTRIM(RTRIM(planadi)) FROM dbo.ORT_PLAN WHERE plankod IS NOT NULL AND LEN(planadi)>1 AND planadi NOT LIKE N'%(adı kurtarılamadı)%';", 300))
            {
                string k = Convert.ToString(r[0]) ?? "", a = Convert.ToString(r[1]) ?? "";
                if (k.Length == 0 || k.Length > 40 || a.IndexOf('\t') >= 0 || a.IndexOf('\n') >= 0 || k.Split(' ').Length > 4) continue;
                PlanNames[(k, a)] = PlanNames.TryGetValue((k, a), out int n) ? n + 1 : 1;
            }
            try
            {
                var sb = new StringBuilder();
                foreach (var d in PlanNamesDbs) sb.Append("#db\t").Append(d).Append('\n');
                foreach (var kv in PlanNames) sb.Append(kv.Key.kod).Append('\t').Append(kv.Key.ad).Append('\t').Append(kv.Value).Append('\n');
                File.WriteAllText(Path.Combine(dir, "VKM_hesapadlari.tsv"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
        static string? PlanNameConsensus(string kod)
        {
            var c = PlanNames.Where(kv => kv.Key.kod.Equals(kod, StringComparison.OrdinalIgnoreCase)).OrderByDescending(kv => kv.Value).Take(2).ToList();
            if (c.Count == 0 || c[0].Value < 2) return null;
            if (c.Count > 1 && c[1].Value * 2 > c[0].Value) return null;   // firmalar arasında tutarsız → uydurma
            return c[0].Key.ad;
        }

        sealed class RecTbl
        {
            public string Orig = "", Tn = ""; public long Rows;
            public Dictionary<string, string> ColMap = new(StringComparer.OrdinalIgnoreCase);   // orijinal ad → kurtarılan kolon adı
            public List<Col> Lay = new();
        }

        // Aynı müşterinin diğer (şifreli) dosyaları: her birinin sağlam katalog parçaları diğerlerinin eksiğini kapatır
        // Aynı programın diğer (şifreli) dosyalarından toplanan katalog bilgisi — SIKIŞTIRILMIŞ (yüzlerce dosyada bellek şişmez)
        public sealed class Pool
        {
            public int Count;
            // nesne ID → tablo adı (kaç dosyada)
            internal Dictionary<int, Dictionary<string, int>> IdNames = new();
            // tablo adı → farklı kolon listeleri (imzaya göre tekilleştirilmiş)
            internal Dictionary<string, Dictionary<string, PoolList>> Lists = new(StringComparer.OrdinalIgnoreCase);
            // tablo → kolon → kardeşlerin kesin yerleşimleri
            internal Dictionary<string, Dictionary<string, HashSet<(int off, int nb, int bp)>>> Lay = new(StringComparer.OrdinalIgnoreCase);
            // yapı: tablo → index adı → (status, tip, anahtar kolonlar) · "tablo|kolon" → varsayılan · modül adı → (tip, metin)
            internal Dictionary<string, Dictionary<string, (int status, byte type, string keys, int n)>> Indexes = new(StringComparer.OrdinalIgnoreCase);
            internal Dictionary<string, (string name, string text, int n)> Defaults = new(StringComparer.OrdinalIgnoreCase);
            internal Dictionary<string, (string type, string text, int n)> Modules = new(StringComparer.OrdinalIgnoreCase);
            // tablo adı → hangi dosya ailelerinde görüldü (ORKA_#_# firma DB'leri, ORKAGENELTANIM …) → başka tür DB'nin tablosu karışmasın
            internal Dictionary<string, HashSet<string>> Fam = new(StringComparer.OrdinalIgnoreCase);
            public bool HasPk(string table) => Indexes.TryGetValue(table, out var d) && d.Values.Any(v => (v.status & 0x20) != 0);
        }
        internal sealed class PoolList { public List<Col> Cols = new(); public int N; public HashSet<int> Ids = new(); }

        // Havuzu diske kaydet / yükle → yeniden başlatmada 1618 dosyayı tekrar okumaya gerek yok
        public static void SavePool(Pool p, string path, int fileCount)
        {
            using var w = new BinaryWriter(File.Create(path), Encoding.UTF8);
            w.Write("VKMPOOL3"); w.Write(fileCount); w.Write(p.Count);
            w.Write(p.IdNames.Count);
            foreach (var kv in p.IdNames) { w.Write(kv.Key); w.Write(kv.Value.Count); foreach (var n in kv.Value) { w.Write(n.Key); w.Write(n.Value); } }
            w.Write(p.Lists.Count);
            foreach (var kv in p.Lists)
            {
                w.Write(kv.Key); w.Write(kv.Value.Count);
                foreach (var l in kv.Value)
                {
                    w.Write(l.Key); w.Write(l.Value.N);
                    w.Write(l.Value.Ids.Count); foreach (var id in l.Value.Ids) w.Write(id);
                    w.Write(l.Value.Cols.Count);
                    foreach (var c in l.Value.Cols) { w.Write(c.ColId); w.Write(c.Name); w.Write(c.X); w.Write(c.Len); w.Write(c.Prec); w.Write(c.Scale); }
                }
            }
            w.Write(p.Lay.Count);
            foreach (var kv in p.Lay)
            {
                w.Write(kv.Key); w.Write(kv.Value.Count);
                foreach (var c in kv.Value) { w.Write(c.Key); w.Write(c.Value.Count); foreach (var e in c.Value) { w.Write(e.off); w.Write(e.nb); w.Write(e.bp); } }
            }
            w.Write(p.Indexes.Count);
            foreach (var kv in p.Indexes)
            {
                w.Write(kv.Key); w.Write(kv.Value.Count);
                foreach (var ix in kv.Value) { w.Write(ix.Key); w.Write(ix.Value.status); w.Write(ix.Value.type); w.Write(ix.Value.keys); w.Write(ix.Value.n); }
            }
            w.Write(p.Defaults.Count);
            foreach (var kv in p.Defaults) { w.Write(kv.Key); w.Write(kv.Value.name); w.Write(kv.Value.text); w.Write(kv.Value.n); }
            w.Write(p.Modules.Count);
            foreach (var kv in p.Modules) { w.Write(kv.Key); w.Write(kv.Value.type); w.Write(kv.Value.text); w.Write(kv.Value.n); }
            w.Write(p.Fam.Count);
            foreach (var kv in p.Fam) { w.Write(kv.Key); w.Write(kv.Value.Count); foreach (var f in kv.Value) w.Write(f); }
        }

        public static Pool? LoadPool(string path, int fileCount)
        {
            try
            {
                using var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
                if (r.ReadString() != "VKMPOOL3" || r.ReadInt32() != fileCount) return null;
                var p = new Pool { Count = r.ReadInt32() };
                int n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    int id = r.ReadInt32(); int m = r.ReadInt32(); var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    for (int j = 0; j < m; j++) d[r.ReadString()] = r.ReadInt32();
                    p.IdNames[id] = d;
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string tn = r.ReadString(); int m = r.ReadInt32(); var d = new Dictionary<string, PoolList>();
                    for (int j = 0; j < m; j++)
                    {
                        string sig = r.ReadString(); var pl = new PoolList { N = r.ReadInt32() };
                        int ni = r.ReadInt32(); for (int k = 0; k < ni; k++) pl.Ids.Add(r.ReadInt32());
                        int nc = r.ReadInt32();
                        for (int k = 0; k < nc; k++)
                            pl.Cols.Add(new Col { ColId = r.ReadInt32(), Name = r.ReadString(), X = r.ReadByte(), Len = r.ReadInt16(), Prec = r.ReadByte(), Scale = r.ReadByte() });
                        d[sig] = pl;
                    }
                    p.Lists[tn] = d;
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string tn = r.ReadString(); int m = r.ReadInt32(); var d = new Dictionary<string, HashSet<(int off, int nb, int bp)>>(StringComparer.OrdinalIgnoreCase);
                    for (int j = 0; j < m; j++)
                    {
                        string cn = r.ReadString(); int ne = r.ReadInt32(); var hs = new HashSet<(int off, int nb, int bp)>();
                        for (int k = 0; k < ne; k++) hs.Add((r.ReadInt32(), r.ReadInt32(), r.ReadInt32()));
                        d[cn] = hs;
                    }
                    p.Lay[tn] = d;
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++)
                {
                    string tn = r.ReadString(); int m = r.ReadInt32(); var d = new Dictionary<string, (int status, byte type, string keys, int n)>(StringComparer.OrdinalIgnoreCase);
                    for (int j = 0; j < m; j++) { string nm = r.ReadString(); d[nm] = (r.ReadInt32(), r.ReadByte(), r.ReadString(), r.ReadInt32()); }
                    p.Indexes[tn] = d;
                }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) { string k = r.ReadString(); p.Defaults[k] = (r.ReadString(), r.ReadString(), r.ReadInt32()); }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) { string k = r.ReadString(); p.Modules[k] = (r.ReadString(), r.ReadString(), r.ReadInt32()); }
                n = r.ReadInt32();
                for (int i = 0; i < n; i++) { string k = r.ReadString(); int m = r.ReadInt32(); var hs = new HashSet<string>(); for (int j = 0; j < m; j++) hs.Add(r.ReadString()); p.Fam[k] = hs; }
                return p;
            }
            catch { return null; }
        }

        public static Pool BuildPool(IList<string> files, Action<string> log, Func<bool>? cancel = null)
        {
            var pool = new Pool(); int i = 0;
            foreach (var f in files)
            {
                i++;
                if (cancel != null && cancel()) break;
                try
                {
                    using var fs = File.OpenRead(f);
                    var c = Ctx.Load(fs, null);
                    string fam = FamilyOf(f);
                    foreach (var o in c.Objs.Where(o => o.Value.type == "U "))
                    {
                        if (!pool.Fam.TryGetValue(o.Value.name, out var fs2)) pool.Fam[o.Value.name] = fs2 = new HashSet<string>();
                        fs2.Add(fam);
                        if (!pool.IdNames.TryGetValue(o.Key, out var nm)) pool.IdNames[o.Key] = nm = new(StringComparer.OrdinalIgnoreCase);
                        nm[o.Value.name] = nm.TryGetValue(o.Value.name, out var k) ? k + 1 : 1;
                        if (!c.Cols.TryGetValue(o.Key, out var cd) || cd.Count == 0) continue;
                        var cols = cd.Values.OrderBy(x => x.ColId).ToList();
                        string sig = string.Join("|", cols.Select(x => x.ColId + ":" + x.Name + ":" + x.X + ":" + x.Len + ":" + x.Prec + ":" + x.Scale));
                        if (!pool.Lists.TryGetValue(o.Value.name, out var lm)) pool.Lists[o.Value.name] = lm = new();
                        if (!lm.TryGetValue(sig, out var pl)) lm[sig] = pl = new PoolList { Cols = cols.Select(x => x.Copy()).ToList() };
                        pl.N++; pl.Ids.Add(o.Key);
                    }
                    foreach (var t in c.Tables)
                    {
                        if (!c.Objs.ContainsKey(t.Id) || !c.RsCols.TryGetValue(t.RowsetId, out var rm)) continue;
                        if (!pool.Lay.TryGetValue(t.Name, out var lm)) pool.Lay[t.Name] = lm = new(StringComparer.OrdinalIgnoreCase);
                        foreach (var col in t.Cols)
                            if (rm.TryGetValue(col.ColId, out var l))
                            {
                                if (!lm.TryGetValue(col.Name, out var hs)) lm[col.Name] = hs = new();
                                foreach (var x in l) if (hs.Count < 8) hs.Add((x.off, x.nb, x.bp));
                            }
                    }
                    // yapı bilgisi (index / varsayılan / modül) — en sık görülen sürüm kazanır
                    foreach (var ix in c.Idx)
                    {
                        if (!c.Objs.TryGetValue(ix.id, out var ow) || ow.type != "U " || !c.IdxCols.TryGetValue((ix.id, ix.indid), out var icl) || !c.Cols.TryGetValue(ix.id, out var tcols)) continue;
                        var keys = icl.Where(z => z.ord > 0).OrderBy(z => z.ord).Select(z => tcols.TryGetValue(z.colid, out var cc) ? cc.Name : null).ToList();
                        if (keys.Count == 0 || keys.Any(k => k == null)) continue;
                        if (!pool.Indexes.TryGetValue(ow.name, out var im)) pool.Indexes[ow.name] = im = new(StringComparer.OrdinalIgnoreCase);
                        string ks = string.Join(",", keys);
                        im[ix.name] = im.TryGetValue(ix.name, out var old) && old.keys == ks ? (old.status, old.type, ks, old.n + 1)
                                     : (!im.ContainsKey(ix.name) || old.n <= 1 ? (ix.status, ix.type, ks, 1) : old);
                    }
                    foreach (var o in c.Objs)
                    {
                        if (!c.ModText.TryGetValue(o.Key, out var txt)) continue;
                        string ty = o.Value.type;
                        if (ty == "D ")
                        {
                            if (!c.ObjParent.TryGetValue(o.Key, out var par) || !c.Objs.TryGetValue(par.pid, out var pt) || !c.Cols.TryGetValue(par.pid, out var pc) || !pc.TryGetValue(par.intprop, out var dc)) continue;
                            string key = pt.name + "|" + dc.Name;
                            pool.Defaults[key] = pool.Defaults.TryGetValue(key, out var od) ? (od.text == txt ? (od.name, od.text, od.n + 1) : od) : (o.Value.name, txt, 1);
                        }
                        else if (IsModuleType(ty))
                            pool.Modules[o.Value.name] = pool.Modules.TryGetValue(o.Value.name, out var om) ? (om.text == txt ? (om.type, om.text, om.n + 1) : om) : (ty, txt, 1);
                    }
                    pool.Count++;
                    if (files.Count <= 20 || i % 25 == 0 || i == files.Count)
                        log($"  havuz {i}/{files.Count}: {System.IO.Path.GetFileName(f)}  (toplam {pool.Lists.Count} tablo yapısı)");
                }
                catch (Exception ex) { log($"  havuz: {System.IO.Path.GetFileName(f)} okunamadı ({First(ex.Message)})"); }
            }
            return pool;
        }

        sealed class Ctx
        {
            public FileStream Fs = null!;
            public long PageCount, Valid, Garbage, Empty, DataCnt, IdMatch, TornPages;
            public bool EnforceId, UsePfs, Page0Ok, Page9Ok;
            public Dictionary<byte, long> TypeHist = new();
            public List<(long a, long b)> BadRuns = new();
            public List<long> GarbageSample = new();
            public Dictionary<(uint, ushort), List<long>> Data = new();
            public Dictionary<(uint, ushort), long> SlotSum = new();
            // veri sayfalarının LSN'i: aynı satırın eski kopyası (boşa çıkmış eski sayfa) varsa EN YENİSİ kazanır
            public Dictionary<long, ulong> Lsn = new();
            // ── yapı (katalogdan): nesne ebeveyni, index'ler, index kolonları, modül/varsayılan metinleri
            public Dictionary<int, (int pid, int intprop)> ObjParent = new();
            public List<(int id, int indid, string name, int status, byte type)> Idx = new();
            public Dictionary<(int id, int indid), List<(int subid, int status, int colid, int ord)>> IdxCols = new();
            public Dictionary<int, string> ModText = new();
            readonly Dictionary<long, byte[]> lobCache = new();

            byte[]? LobPage(long no)
            {
                if (lobCache.TryGetValue(no, out var cb)) return cb;
                var b = new byte[PAGE];
                if (!ReadPage(Fs, no, b) || b[0] != 1 || (b[1] != 3 && b[1] != 4)) return null;
                if (EnforceId && U32(b, 0x20) != (uint)no) return null;
                if (lobCache.Count > 256) lobCache.Clear();
                lobCache[no] = b; return b;
            }

            public bool LobNodeP(long page, int slot, uint ts, MemoryStream ms) => LobNode(page, slot, ts, ms, 0);
            public byte[]? LobPageP(long no) => LobPage(no);

            // LOB düğümü: 3 = veri parçası, 2 = iç düğüm (alt parçaların listesi). Blob kimliği kök zaman damgasıyla eşleşmeli.
            bool LobNode(long page, int slot, uint ts, MemoryStream ms, int depth)
            {
                if (depth > 12 || ms.Length > 512L * 1048576) return false;
                var b = LobPage(page); if (b == null) return false;
                int n = U16(b, 0x16); if (slot < 0 || slot >= n) return false;
                int r = U16(b, PAGE - 2 - slot * 2); if (r < 96 || r + 14 > PAGE) return false;
                int ln = U16(b, r + 2); if (ln < 14 || r + ln > PAGE) return false;
                if (U32(b, r + 6) != ts) return false;
                int bt = U16(b, r + 12);
                if (bt == 3) { ms.Write(b, r + 14, ln - 14); return true; }
                if (bt == 2)
                {
                    int cur = U16(b, r + 16);
                    if (cur <= 0 || r + 20 + cur * 16 > PAGE) return false;
                    for (int i = 0; i < cur; i++)
                    {
                        int e = r + 20 + 16 * i;
                        if (!LobNode(U32(b, e + 8), U16(b, e + 14), ts, ms, depth + 1)) return false;
                    }
                    return true;
                }
                return false;
            }

            // Satır içindeki LOB kökü (tip 4: max tipler) ya da satır taşması işaretçisi (tip 2)
            public byte[]? ReadLob(byte[] p, int off, int len)
            {
                try
                {
                    if (len < 24) return null;
                    byte t = p[off];
                    using var ms = new MemoryStream();
                    if (t == 4)
                    {
                        uint ts = U32(p, off + 8); int n = (len - 12) / 12; uint total = 0;
                        _lobUsed?.Add(ts);
                        for (int i = 0; i < n; i++)
                        {
                            int e = off + 12 + 12 * i; total = U32(p, e);
                            if (!LobNode(U32(p, e + 4), U16(p, e + 10), ts, ms, 0)) return null;
                        }
                        return ms.Length == total ? ms.ToArray() : null;
                    }
                    if (t == 2)
                    {
                        uint ts = U32(p, off + 8);
                        _lobUsed?.Add(ts);
                        if (!LobNode(U32(p, off + 16), U16(p, off + 22), ts, ms, 0)) return null;
                        return ms.ToArray();
                    }
                    return null;
                }
                catch { return null; }
            }
            public Dictionary<uint, List<long>> SysPages = new();
            public Dictionary<int, (string name, string type)> Objs = new();
            public Dictionary<int, Dictionary<int, Col>> Cols = new();
            public Dictionary<long, (int obj, int idx, int cmpr)> Rowsets = new();
            public List<(long auid, int type, long owner)> Aus = new();
            public Dictionary<long, Dictionary<int, List<(int off, int nb, int bp, int ok)>>> RsCols = new();
            public List<Tbl> Tables = new();
            readonly Dictionary<long, byte[]?> pfsCache = new();
            // referans DB'den gelen fiziksel yerleşim: tablo → kolon adı → (offset, nullbit, bitpos)
            public Dictionary<string, Dictionary<string, (int off, int nb, int bp)>> RefLay = new(StringComparer.OrdinalIgnoreCase);
            public string Family = "";
            // kardeş dosyaların kesin yerleşimleri (aday; bu dosyanın verisiyle puanlanır)
            public Dictionary<string, Dictionary<string, List<(int off, int nb, int bp)>>> PoolLay = new(StringComparer.OrdinalIgnoreCase);

            public void ApplyPool(Pool pool, string selfPath, Action<string> log)
            {
                if (pool.Count < 2) return;
                int byId = 0, byCols = 0, bySig = 0, colsFilled = 0;
                var present = new HashSet<string>(Tables.Where(t => Objs.ContainsKey(t.Id) && t.Pages.Count > 0).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                var lib = pool.Lists.SelectMany(kv => kv.Value.Values.Select(pl => (name: kv.Key, pl))).ToList();

                foreach (var t in Tables)
                {
                    bool named = Objs.ContainsKey(t.Id);
                    // 1) adı yok → aynı nesne ID'si başka dosyada adlı mı?
                    if (!named && pool.IdNames.TryGetValue(t.Id, out var nm) && nm.Count > 0)
                    {
                        string cand = nm.OrderByDescending(x => x.Value).First().Key;
                        bool okCols = true;
                        if (t.Cols.Count > 0 && pool.Lists.TryGetValue(cand, out var lists))
                        {
                            var a = new HashSet<string>(t.Cols.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
                            okCols = lists.Values.Any(l => l.Cols.Count(x => a.Contains(x.Name)) >= Math.Min(a.Count, l.Cols.Count) * 0.6);
                        }
                        if (okCols) { t.Name = cand; named = true; byId++; }
                    }
                    // 2) adı yok, kolonları var → kolon adları birebir tutan tablo
                    if (!named && t.Cols.Count >= 3)
                    {
                        var a = new HashSet<string>(t.Cols.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
                        var hits = lib.Where(l => { int ov = l.pl.Cols.Count(x => a.Contains(x.Name)); return ov >= a.Count * 0.9 && ov >= l.pl.Cols.Count * 0.9; })
                                      .Select(l => l.name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        var free = hits.Where(h => !present.Contains(h)).ToList();
                        var pick = free.Count == 1 ? free[0] : hits.Count == 1 ? hits[0] : null;
                        if (pick != null) { t.Name = pick; named = true; byCols++; }
                        else if (free.Count > 1) t.Name = "tablo_" + t.Id + "_olasi_" + free[0] + "_vb" + (free.Count - 1);
                    }
                    // 2b) kolonları KISMEN bilinen tablo: kaydın gerçek kolon sayısı (NCol) bildiğimizden fazlaysa eksikler
                    //     aynı adlı tablonun havuzdaki tam listesinden (aynı colid'de aynı ad → güvenli) tamamlanır.
                    //     (Sağlam örnekle karşılaştırma: GNL_FIRMATANIM 52, GNL_FIRMABEYAN 38 kolon eksik kalıyordu.)
                    if (named && t.Cols.Count > 0 && t.Pages.Count > 0)
                    {
                        var r0 = TableRecs(t).Take(50).ToList();
                        if (r0.Count > 0)
                        {
                            int nc = r0.GroupBy(r => r.NCol).OrderByDescending(g => g.Count()).First().Key;
                            int have = t.Cols.Max(x => x.ColId);
                            if (nc > have || t.Cols.Count < nc)
                            {
                                var mine = t.Cols.ToDictionary(x => x.ColId);
                                var best = lib.Where(l => l.name.Equals(t.Name, StringComparison.OrdinalIgnoreCase) && l.pl.Cols.Max(x => x.ColId) >= nc)
                                              .Select(l => (l, agree: l.pl.Cols.Count(x => mine.TryGetValue(x.ColId, out var mm) && mm.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase)),
                                                               clash: l.pl.Cols.Count(x => mine.TryGetValue(x.ColId, out var mm) && !mm.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase))))
                                              .Where(z => z.clash == 0 && z.agree >= Math.Max(1, mine.Count * 0.9))
                                              .OrderBy(z => Math.Abs(z.l.pl.Cols.Max(x => x.ColId) - nc)).ThenByDescending(z => z.l.pl.N).FirstOrDefault();
                                if (best.l.pl != null)
                                {
                                    int add = 0;
                                    foreach (var pc in best.l.pl.Cols)
                                        if (!mine.ContainsKey(pc.ColId)) { var k = pc.Copy(); k.Guess = true; t.Cols.Add(k); add++; }
                                    if (add > 0) { t.Cols = t.Cols.OrderBy(x => x.ColId).ToList(); t.Guessed.Add($"+{add} kolon(havuz)"); colsFilled++; }
                                }
                            }
                        }
                    }
                    // 3) verisi yok ama yapısı lazım → en sık kolon listesi
                    if (t.Cols.Count == 0 && t.Pages.Count == 0 && named && pool.Lists.TryGetValue(t.Name, out var el) && el.Count > 0)
                    {
                        t.Cols = el.Values.OrderByDescending(x => x.N).First().Cols.Select(x => { var k = x.Copy(); k.Guess = true; return k; }).ToList();
                        colsFilled++;
                    }
                    // 4) verisi var, kolonları yok → kayıt imzası (kolon sayısı + sabit uzunluk)
                    if (t.Cols.Count == 0 && t.Pages.Count > 0)
                    {
                        var r0 = TableRecs(t).Take(50).ToList();
                        if (r0.Count == 0) continue;
                        int nc = r0.GroupBy(r => r.NCol).OrderByDescending(g => g.Count()).First().Key;
                        int fe = r0.GroupBy(r => r.FixedEnd).OrderByDescending(g => g.Count()).First().Key;
                        var cand = named ? lib.Where(l => l.name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)).ToList() : lib;
                        var exact = cand.Where(l => l.pl.Cols.Max(x => x.ColId) == nc && FixedLen(l.pl.Cols) == fe).ToList();
                        if (!named) exact = exact.Where(l => !present.Contains(l.name)).ToList();
                        List<Col>? src = null; string? nm2 = null;
                        var sameId = exact.Where(l => l.pl.Ids.Contains(t.Id)).OrderByDescending(l => l.pl.N).FirstOrDefault();
                        if (sameId.pl != null) { src = sameId.pl.Cols; nm2 = sameId.name; }
                        else if (exact.Select(l => l.name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1)
                        { var e0 = exact.OrderByDescending(l => l.pl.N).First(); src = e0.pl.Cols; nm2 = e0.name; }
                        else if (named && cand.Count > 0)
                        { var e0 = cand.OrderByDescending(l => l.pl.N).First(); src = e0.pl.Cols; nm2 = t.Name; }
                        if (src != null && nm2 != null)
                        {
                            if (!named) { bySig++; named = true; }
                            t.Name = nm2;
                            t.Cols = src.Select(x => { var k = x.Copy(); k.Guess = true; return k; }).OrderBy(x => x.ColId).ToList();
                            t.Guessed.Add("tüm kolonlar(havuz)");
                            colsFilled++;
                        }
                    }
                    if (named && !Objs.ContainsKey(t.Id)) present.Add(t.Name);
                }
                foreach (var kv in pool.Lay)
                    PoolLay[kv.Key] = kv.Value.ToDictionary(x => x.Key, x => x.Value.ToList(), StringComparer.OrdinalIgnoreCase);
                log($"Havuz ({pool.Count} dosya): {byId + byCols + bySig} adsız tablo tanındı (ID {byId} · kolon {byCols} · imza {bySig}) · {colsFilled} kolonsuz tablo kuruldu");
            }

            // ── SAHİPSİZ VERİ SAYFALARI: tablonun sysrowsets kaydı şifreli sayfada kalınca veri sayfaları sağlam olsa bile
            //    hangi tabloya ait oldukları bilinmez (ORKA firma DB'lerinde ORT_FISSATIR / ORT_FISBASLIK böyle kayboluyordu → fişler boş).
            //    Kayıt imzası (kolon sayısı + sabit alan boyu) ile kolonları bilinen ama verisi olmayan tabloya bağlanır.
            public void AssignOrphans(Dictionary<string, RefTbl>? rf, Pool? pool, Action<string> log)
            {
                var owned = new HashSet<(uint, ushort)>();
                foreach (var au in Aus)
                    if (au.type == 1 && Rowsets.ContainsKey(au.owner))
                        owned.Add(((uint)((au.auid >> 16) & 0xFFFFFFFF), (ushort)((au.auid >> 48) & 0xFFFF)));
                var taken = new HashSet<long>(Tables.SelectMany(t => t.Pages));
                // aday: kolonları bilinen, verisi olmayan tablolar (+ referansta olup bu dosyanın kataloğunda hiç görünmeyenler)
                var cands = Tables.Where(t => t.Pages.Count == 0 && t.Cols.Count > 0).ToList();
                int nid = -1;
                if (rf != null)
                    foreach (var r in rf.Values)
                        if (!Tables.Any(t => t.Name.Equals(r.Name, StringComparison.OrdinalIgnoreCase)) && r.Cols.Count > 0)
                            cands.Add(new Tbl { Id = nid--, Name = r.Name, Cols = r.Cols.Select(x => { var k = x.Copy(); k.Guess = true; return k; }).OrderBy(x => x.ColId).ToList() });
                // kardeş dosyaların (aynı müşteri, aynı program sürümü) sağlam kataloglarındaki tablo yapıları:
                //  referans eski sürümse yeni kolonlu tablolar (ör. ORT_FISEXCELSATIR 153→191 kolon = Hızlı Fiş) ancak buradan tanınır
                var fromPool = new HashSet<Tbl>();
                if (pool != null)
                    foreach (var kv in pool.Lists)
                    {
                        if (Tables.Any(t => t.Pages.Count > 0 && t.Name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase))) continue;
                        bool known = Objs.Values.Any(o => o.name.Equals(kv.Key, StringComparison.OrdinalIgnoreCase)) || (rf != null && rf.ContainsKey(kv.Key))
                                     || !pool.Fam.TryGetValue(kv.Key, out var fm) || Family.Length == 0 || fm.Contains(Family);
                        if (!known) continue;
                        foreach (var pl in kv.Value.Values.Where(v => v.Cols.Count > 0).OrderByDescending(v => v.N))
                        {
                            var pt = new Tbl { Id = nid--, Name = kv.Key, Cols = pl.Cols.Select(x => { var k = x.Copy(); k.Guess = true; return k; }).OrderBy(x => x.ColId).ToList() };
                            cands.Add(pt); fromPool.Add(pt);
                        }
                    }
                if (cands.Count == 0) return;
                // aynı tablonun birden çok aday sürümü tutarsa tek tabloya say: önce bu dosyanın kendi kaydı, sonra havuz (en sık), sonra referans
                List<Tbl> One(List<Tbl> l)
                {
                    if (l.Count <= 1 || l.Select(t => t.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1) return l;
                    return new List<Tbl> { l.FirstOrDefault(t => t.Id > 0) ?? l.FirstOrDefault(t => fromPool.Contains(t)) ?? l[0] };
                }
                int poolHits = 0;
                int okN = 0, amb = 0; var names = new List<string>(); var unk = new List<string>();
                foreach (var kv in Data.OrderByDescending(kv => kv.Value.Count))
                {
                    if (kv.Key.Item1 < 100 || owned.Contains(kv.Key)) continue;
                    var pages = kv.Value.Where(p => !taken.Contains(p)).ToList();
                    if (pages.Count == 0) continue;
                    var recs = TableRecs(new Tbl { Pages = pages.Take(60).ToList() }).Take(400).ToList();
                    if (recs.Count == 0) continue;
                    // imza: tek tük bozuk kayıt (yarı şifreli sayfa) yanıltmasın → en az %5'inde görülen en geniş (kolon, sabit boy) çifti
                    var pairs = recs.GroupBy(r => (r.NCol, r.FixedEnd)).Select(g => (g.Key.NCol, g.Key.FixedEnd, n: g.Count())).ToList();
                    int minN = Math.Max(2, recs.Count / 20);
                    var okp = pairs.Where(z => z.n >= minN).ToList();
                    if (okp.Count == 0) okp = pairs.OrderByDescending(z => z.n).Take(1).ToList();
                    var top = okp.OrderByDescending(z => z.NCol).ThenByDescending(z => z.n).First();
                    int nc = top.NCol, fe = top.FixedEnd;
                    int vc = recs.Where(r => r.NCol == nc && r.FixedEnd == fe).Max(r => r.VarCount);
                    int Nc(Tbl t) => Math.Max(t.Cols.Count, t.Cols.Max(x => x.ColId));
                    int Vars(Tbl t) => t.Cols.Count(x => IsVarType(x.X, x.Len));
                    var live = cands.Where(t => t.Pages.Count == 0).ToList();
                    var exact = One(live.Where(t => Nc(t) == nc && FixedLen(t.Cols) == fe && Vars(t) >= vc).ToList());
                    // sürüm farkı: sonradan eklenmiş birkaç sabit kolon (referans eski sürümse)
                    var near = exact.Count > 0 ? exact
                             : One(live.Where(t => nc - Nc(t) >= 0 && nc - Nc(t) <= 3 && fe - FixedLen(t.Cols) >= 0 && fe - FixedLen(t.Cols) <= 8 * (nc - Nc(t)) && Vars(t) >= vc - 1).ToList());
                    // kolon sayısı yüksek ve bu sayıda kolonu olan tek tablo varsa (katalog kısmen + referans birleşimi tam tutmayabilir)
                    if (near.Count == 0 && nc >= 30)
                    {
                        var byNc = live.Where(t => Nc(t) == nc).ToList();
                        if (byNc.Count == 1 && fe >= FixedLen(byNc[0].Cols) - 32 && fe <= FixedLen(byNc[0].Cols) + 64) near = byNc;
                    }
                    // aynı kolon sayısında birden çok tablo varsa: sabit alan boyu açık ara en yakın olan (ör. ORT_PLAN 413 ↔ URE_URETIMBASLIK 588)
                    if (near.Count == 0)
                    {
                        var rank = live.Where(t => Nc(t) == nc && Vars(t) >= vc - 1)
                                       .Select(t => (t, d: Math.Abs(fe - FixedLen(t.Cols)))).OrderBy(z => z.d).ToList();
                        if (rank.Count > 0 && rank[0].d <= 16 && (rank.Count == 1 || rank[1].d >= rank[0].d + 40)) near = new List<Tbl> { rank[0].t };
                    }
                    if (near.Count != 1)
                    {
                        if (near.Count > 1) amb++;
                        else if (pages.Count >= 20)
                        {
                            var cl = live.Where(t => Nc(t) == nc).Select(t => $"{t.Name}(sabit {FixedLen(t.Cols)})").Take(4).ToList();
                            unk.Add($"{pages.Count} sayfa: {nc} kolon, sabit {fe}" + (cl.Count > 0 ? " ~ " + string.Join("/", cl) : ""));
                        }
                        continue;
                    }
                    var tt = near[0];
                    bool viaPool = fromPool.Contains(tt);
                    if (viaPool)
                    {
                        poolHits++;
                        // bu dosyanın kataloğunda aynı adlı (verisiz / kolonları eksik) kayıt varsa onu tamamla
                        var ex = Tables.FirstOrDefault(t => t.Name.Equals(tt.Name, StringComparison.OrdinalIgnoreCase) && t.Pages.Count == 0);
                        if (ex != null)
                        {
                            var mine = ex.Cols.Where(x => !x.Guess).GroupBy(x => x.ColId).ToDictionary(g => g.Key, g => g.First());
                            ex.Cols = tt.Cols.Select(x => mine.TryGetValue(x.ColId, out var m) && m.Name.Equals(x.Name, StringComparison.OrdinalIgnoreCase) ? m : x).ToList();
                            ex.Guessed.Add("kolonlar(kardeş dosya)");
                            tt = ex;
                        }
                        if (rf != null && rf.TryGetValue(tt.Name, out var r0) && (r0.Cols.Count != tt.Cols.Count || FixedLen(r0.Cols) != FixedLen(tt.Cols))) RefLay.Remove(tt.Name);
                    }
                    tt.Pages.AddRange(kv.Value);
                    // tahsis biriminin sahibi (rowset) biliniyorsa: o rowset'in sysrscols kayıtları (kolonların kesin yerleri) kullanılabilir
                    if (tt.RowsetId == 0)
                        foreach (var au in Aus)
                            if (au.type == 1 && (uint)((au.auid >> 16) & 0xFFFFFFFF) == kv.Key.Item1 && (ushort)((au.auid >> 48) & 0xFFFF) == kv.Key.Item2) { tt.RowsetId = au.owner; break; }
                    foreach (var p in kv.Value) taken.Add(p);
                    if (SlotSum.TryGetValue(kv.Key, out var ss)) tt.SlotSum += ss;
                    if (tt.Id < 0 && !Tables.Contains(tt))
                    {
                        Tables.Add(tt);
                        if (!viaPool && rf != null && rf.TryGetValue(tt.Name, out var rr)) RefLay[tt.Name] = rr.Lay;
                    }
                    var done1 = tt;
                    cands.RemoveAll(x => x != done1 && x.Name.Equals(done1.Name, StringComparison.OrdinalIgnoreCase));
                    okN++; names.Add(tt.Name + (viaPool ? "*" : ""));
                }
                if (okN > 0 || amb > 0)
                    log($"Kaydı şifreli kalan tablolar: {okN} veri grubu kayıt imzasıyla tablosuna bağlandı" +
                        (names.Count > 0 ? " (" + string.Join(", ", names.Take(8)) + (names.Count > 8 ? " …" : "") + ")" : "") +
                        (amb > 0 ? $" · {amb} grup birden çok tabloya uyduğu için bağlanmadı" : "") +
                        (poolHits > 0 ? $" · * = yapısı kardeş dosyalardan ({poolHits})" : ""));
                foreach (var u in unk.Take(6)) log("   eşleşmeyen büyük veri grubu → " + u);
            }

            internal static int FixedLen(List<Col> cols)
            {
                int off = 4, bits = 0;
                foreach (var c in cols.OrderBy(x => x.ColId))
                {
                    if (IsVarType(c.X, c.Len)) continue;
                    if (c.X == 104) { if (bits % 8 == 0) off += 1; bits++; }
                    else off += Math.Max(1, (int)c.Len);
                }
                return off;
            }

            int refDiffer;
            public void ApplyRef(Dictionary<string, RefTbl> rf, Action<string> log)
            {
                int named = 0, filled = 0, colsAdded = 0; refDiffer = 0;
                // adı kaybolmuş tablo → kolon adları en çok örtüşen referans tablosu. Aynı referans adına birden çok tablo
                // uyuyorsa (ör. firma başına açılan geçici tablolar) ad VERİLMEZ — yalnızca kolon yapısı alınır, veriler karışmaz.
                var guess = new Dictionary<int, RefTbl>();
                foreach (var t in Tables)
                {
                    if (rf.ContainsKey(t.Name) || t.Cols.Count < 3 || Objs.ContainsKey(t.Id)) continue;
                    var mine = new HashSet<string>(t.Cols.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
                    var best = rf.Values.Select(x => (x, ov: x.Cols.Count(cc => mine.Contains(cc.Name)) / (double)Math.Max(mine.Count, x.Cols.Count)))
                                        .OrderByDescending(z => z.ov).FirstOrDefault();
                    if (best.x != null && best.ov >= 0.7) guess[t.Id] = best.x;
                }
                var taken = new HashSet<string>(Tables.Where(t => !guess.ContainsKey(t.Id)).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                var perRef = guess.Values.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                foreach (var t in Tables)
                {
                    rf.TryGetValue(t.Name, out var r);
                    if (r == null && guess.TryGetValue(t.Id, out var gr))
                    {
                        r = gr;
                        if (perRef[gr.Name] == 1 && !taken.Contains(gr.Name)) { t.Name = gr.Name; named++; }
                        else if (!t.Name.Contains("_olasi_")) t.Name = $"tablo_{t.Id}_olasi_{gr.Name}";
                    }
                    if (r == null) continue;
                    bool wasEmpty = t.Cols.Count == 0;
                    var real = t.Cols.Where(x => !x.Guess).ToList();
                    var byName = new HashSet<string>(real.Select(x => x.Name), StringComparer.OrdinalIgnoreCase);
                    var refByName = r.Cols.GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    // müşterinin program sürümü referanstan farklı mı? (araya kolon eklenmiş → colid kayması, tip değişmiş, yeni kolon)
                    bool differs = false;
                    foreach (var rc0 in real)
                    {
                        if (!refByName.TryGetValue(rc0.Name, out var rr)) { differs = true; break; }
                        if (rr.ColId != rc0.ColId || rr.X != rc0.X || rr.Len != rc0.Len || rr.Prec != rc0.Prec || rr.Scale != rc0.Scale) { differs = true; break; }
                    }
                    // referans kolonlarını kendi katalogdaki ortak kolonlara göre hizala: araya kolon eklenmişse sonrakiler kayar
                    var anchors = real.Where(x => refByName.ContainsKey(x.Name)).Select(x => (refId: (int)refByName[x.Name].ColId, ownId: x.ColId)).OrderBy(z => z.refId).ToList();
                    foreach (var rc in r.Cols.OrderBy(x => x.ColId))
                    {
                        if (byName.Contains(rc.Name)) continue;
                        int shift = 0;
                        foreach (var a in anchors) { if (a.refId < rc.ColId) shift = a.ownId - a.refId; else break; }
                        int cid = rc.ColId + shift;
                        var at = t.Cols.FirstOrDefault(x => x.ColId == cid);
                        if (at != null && !at.Guess) continue;                // o colid'de katalogdan gelen gerçek kolon var
                        if (at != null) { t.Cols.Remove(at); t.Guessed.Remove(at.Name); }
                        var nc = rc.Copy(); nc.Guess = true; nc.ColId = cid;
                        t.Cols.Add(nc); t.Guessed.Add(nc.Name + "(ref)"); byName.Add(nc.Name); colsAdded++;
                    }
                    t.Cols = t.Cols.OrderBy(x => x.ColId).ToList();
                    if (wasEmpty && t.Cols.Count > 0) filled++;
                    // referansın fiziksel yerleşimi yalnızca yapı birebir aynıysa kullanılır; sürüm farkında yanlış kolona yanlış değer yazar
                    // (ör. ORT_FISBASLIK: 'fisbelgereferans' eklenmiş → açıklama kayboluyordu; ORT_PLAN: r_dvkuru tipi değişmiş → kaymış)
                    if (!differs) RefLay[t.Name] = r.Lay; else { RefLay.Remove(t.Name); refDiffer++; }
                }
                log($"Referans uygulandı: {colsAdded} eksik kolon tamamlandı · {filled} kolonsuz tablo kuruldu" + (named > 0 ? $" · {named} adsız tablo tanındı" : "") +
                    (refDiffer > 0 ? $" · {refDiffer} tabloda program sürümü referanstan farklı → o tabloların yerleşimi verinin kendisinden çözülüyor" : ""));
            }

            public int PfsBits(long no)
            {
                long pfsNo = no < 8088 ? 1 : (no / 8088) * 8088;
                long baseNo = pfsNo == 1 ? 0 : pfsNo;
                if (!pfsCache.TryGetValue(pfsNo, out var pb))
                {
                    var b = new byte[PAGE];
                    pb = ReadPage(Fs, pfsNo, b) && b[0] == 1 && b[1] == 11 ? b : null;
                    pfsCache[pfsNo] = pb;
                }
                if (pb == null) return -1;
                long i = no - baseNo; if (i < 0 || 100 + i >= PAGE) return -1;
                return pb[100 + i];
            }
            public bool Allocated(long no) { if (!UsePfs) return true; int b = PfsBits(no); return b < 0 || (b & 0x40) != 0; }

            public List<Rec> SysRecs(uint obj)
            {
                var res = new List<Rec>();
                if (!SysPages.TryGetValue(obj, out var list)) return res;
                foreach (var no in list)
                {
                    if (!Allocated(no)) continue;
                    var b = new byte[PAGE];
                    if (!ReadPage(Fs, no, b) || b[0] != 1 || b[1] != 1) continue;
                    res.AddRange(PageRecs(b));
                }
                return res;
            }

            // sayfa sayfa akış: büyük tablolarda bellek şişmez
            public IEnumerable<Rec> TableRecs(Tbl t, bool ghost = false)
            {
                // en yeni yazılmış sayfa önce → tekrar eden ID'de güncel satır tutulur, eski kopya elenir
                foreach (var no in t.Pages.Distinct().OrderByDescending(x => Lsn.TryGetValue(x, out var l) ? l : 0UL).ThenBy(x => x))
                {
                    if (!Allocated(no)) continue;
                    var b = new byte[PAGE];
                    if (!ReadPage(Fs, no, b) || b[0] != 1 || b[1] != 1) continue;
                    if (EnforceId && U32(b, 0x20) != (uint)no) continue;
                    foreach (var r in PageRecs(b, ghost)) yield return r;
                }
            }

            public static Ctx Load(FileStream fs, Action<string>? log)
            {
                var c = new Ctx { Fs = fs, PageCount = fs.Length / PAGE };
                const int BLK = 1024;
                byte[] blk = new byte[PAGE * BLK];
                fs.Position = 0;
                long runStart = -1;
                for (long baseNo = 0; baseNo < c.PageCount; baseNo += BLK)
                {
                    int got = ReadFull(fs, blk, blk.Length) / PAGE;
                    if (got <= 0) break;
                    for (int i = 0; i < got; i++)
                    {
                        int o = i * PAGE; long no = baseNo + i;
                        bool empty = blk[o] == 0 && blk[o + 1] == 0 && AllZero(blk, o, 96);
                        bool real = !empty && blk[o] == 0x01 && KnownType(blk[o + 1]);
                        bool bad = !empty && !real;
                        if (bad && runStart < 0) runStart = no;
                        if (!bad && runStart >= 0) { c.BadRuns.Add((runStart, no - 1)); runStart = -1; }
                        if (empty) { c.Empty++; continue; }
                        if (bad) { c.Garbage++; if (c.GarbageSample.Count < 24 && no % 7 == 0) c.GarbageSample.Add(no); continue; }
                        c.Valid++;
                        byte t = blk[o + 1];
                        c.TypeHist.TryGetValue(t, out long th); c.TypeHist[t] = th + 1;
                        if ((U16(blk, o + 4) & 0x100) != 0) c.TornPages++;
                        if (no == 0 && t == 15) c.Page0Ok = true;
                        if (no == 9 && t == 13) c.Page9Ok = true;
                        if (t != 1) continue;
                        c.DataCnt++;
                        if (U32(blk, o + 0x20) == (uint)no) c.IdMatch++;
                        c.Lsn[no] = ((ulong)U32(blk, o + 0x28) << 32) | U32(blk, o + 0x2C);   // m_lsn @0x28: (VLF sıra, blok)
                        uint obj = U32(blk, o + 0x18); ushort idx = U16(blk, o + 0x06);
                        var key = (obj, idx);
                        if (!c.Data.TryGetValue(key, out var l)) c.Data[key] = l = new List<long>();
                        l.Add(no);
                        c.SlotSum.TryGetValue(key, out long ss); c.SlotSum[key] = ss + U16(blk, o + 0x16);
                        if (obj < 100 && idx < 256)
                        {
                            if (!c.SysPages.TryGetValue(obj, out var sl)) c.SysPages[obj] = sl = new List<long>();
                            sl.Add(no);
                        }
                    }
                }
                if (runStart >= 0) c.BadRuns.Add((runStart, c.PageCount - 1));
                c.EnforceId = c.DataCnt > 0 && c.IdMatch >= c.DataCnt * 0.8;

                // PFS kalibrasyonu (sistem sayfaları neredeyse hep tahsislidir)
                int calTot = 0, calAlloc = 0;
                foreach (var kv in c.SysPages) foreach (var no in kv.Value)
                { int b = c.PfsBits(no); if (b < 0) continue; calTot++; if ((b & 0x40) != 0) calAlloc++; }
                c.UsePfs = calTot >= 10 && calAlloc >= calTot * 0.9;

                // ── katalog ──
                foreach (var r in c.SysRecs(34))
                {
                    if (r.FixedEnd < 19) continue;
                    int id = I32(r.P, r.Abs + 4);
                    string type = ((char)r.P[r.Abs + 17]).ToString() + (char)r.P[r.Abs + 18];
                    string name = VarUtf16(r, 1);
                    if (name.Length == 0 || c.Objs.ContainsKey(id)) continue;
                    c.Objs[id] = (name, type);
                    if (r.FixedEnd >= 28) c.ObjParent[id] = (I32(r.P, r.Abs + 19), I32(r.P, r.Abs + 24));
                }
                foreach (var r in c.SysRecs(41))
                {
                    if (r.FixedEnd < 23) continue;
                    int id = I32(r.P, r.Abs + 4);
                    if (I16(r.P, r.Abs + 8) != 0) continue;
                    int colid = I32(r.P, r.Abs + 10);
                    if (!c.Cols.TryGetValue(id, out var cd)) c.Cols[id] = cd = new Dictionary<int, Col>();
                    if (cd.ContainsKey(colid)) continue;
                    var col = new Col
                    {
                        ColId = colid, X = r.P[r.Abs + 14], Len = I16(r.P, r.Abs + 19),
                        Prec = r.P[r.Abs + 21], Scale = r.P[r.Abs + 22], Name = VarUtf16(r, 1)
                    };
                    if (r.FixedEnd >= 31) { int stt = I32(r.P, r.Abs + 27); col.Ident = (stt & 4) != 0; col.NotNull = (stt & 1) != 0; }
                    if (col.Name.Length == 0) col.Name = "kolon" + colid;
                    cd[colid] = col;
                }
                foreach (var r in c.SysRecs(5))
                {
                    if (r.FixedEnd < 21) continue;
                    long rs = I64(r.P, r.Abs + 4);
                    if (c.Rowsets.ContainsKey(rs)) continue;
                    c.Rowsets[rs] = (I32(r.P, r.Abs + 13), I32(r.P, r.Abs + 17), r.FixedEnd > 39 ? r.P[r.Abs + 39] : 0);
                }
                var seenAu = new HashSet<long>();
                foreach (var r in c.SysRecs(7))
                {
                    if (r.FixedEnd < 21) continue;
                    long au = I64(r.P, r.Abs + 4);
                    if (seenAu.Add(au)) c.Aus.Add((au, r.P[r.Abs + 12], I64(r.P, r.Abs + 13)));
                }
                foreach (var r in c.SysRecs(3))
                {
                    if (r.FixedEnd < 54) continue;
                    uint rid = U32(r.P, r.Abs + 12);
                    if ((rid & 0x04000000) != 0) continue;                 // silinmiş kolon
                    long rsid = I64(r.P, r.Abs + 4);
                    int cid = (int)(rid & 0xFFFFFF);
                    if (!c.RsCols.TryGetValue(rsid, out var m)) c.RsCols[rsid] = m = new();
                    if (!m.TryGetValue(cid, out var lst)) m[cid] = lst = new();
                    var e = (I32(r.P, r.Abs + 44), I32(r.P, r.Abs + 48) & 0xFFFF, (int)I16(r.P, r.Abs + 52), (int)I16(r.P, r.Abs + 36));   // null-bit alanının üst 2 baytı bayrak (anahtar kolonda 0x10001 gelir)
                    if (!lst.Contains(e)) lst.Add(e);
                }

                // ── yapı: index'ler (sysidxstats 54), index kolonları (sysiscols 55), modül/varsayılan metinleri (sysobjvalues 60)
                var seenIx = new HashSet<(int, int)>();
                foreach (var r in c.SysRecs(54))
                {
                    if (r.FixedEnd < 22) continue;
                    int id = I32(r.P, r.Abs + 4), indid = I32(r.P, r.Abs + 8);
                    byte ty = r.P[r.Abs + 21]; string nm = VarUtf16(r, 1);
                    if ((ty != 1 && ty != 2) || nm.Length == 0 || nm.StartsWith("_WA_Sys", StringComparison.OrdinalIgnoreCase)) continue;
                    if (seenIx.Add((id, indid))) c.Idx.Add((id, indid, nm, I32(r.P, r.Abs + 12), ty));
                }
                foreach (var r in c.SysRecs(55))
                {
                    if (r.FixedEnd < 25) continue;
                    var key = (I32(r.P, r.Abs + 4), I32(r.P, r.Abs + 8));
                    if (!c.IdxCols.TryGetValue(key, out var l)) c.IdxCols[key] = l = new();
                    var e = (I32(r.P, r.Abs + 12), I32(r.P, r.Abs + 16), I32(r.P, r.Abs + 20), (int)r.P[r.Abs + 24]);
                    if (!l.Contains(e)) l.Add(e);
                }
                foreach (var r in c.SysRecs(60))
                {
                    if (r.FixedEnd < 17 || r.P[r.Abs + 4] != 1 || I32(r.P, r.Abs + 9) != 0 || r.VarCount < 2) continue;
                    int oid = I32(r.P, r.Abs + 5);
                    if (c.ModText.ContainsKey(oid) || r.VS[1] < 0) continue;
                    byte[]? raw = r.VC[1] ? c.ReadLob(r.P, r.VS[1], r.VE[1] - r.VS[1]) : r.P.AsSpan(r.VS[1], r.VE[1] - r.VS[1]).ToArray();
                    if (raw == null || raw.Length == 0) continue;
                    c.ModText[oid] = DecodeModText(raw);
                }

                // ── kardeş tablolardan şema uzlaşması ──
                var userIds = c.Objs.Where(kv => kv.Value.type == "U ").Select(kv => kv.Key).ToList();
                // adı (sysschobjs kaydı) şifreli sayfada kalmış ama rowset'i duran tablolar da dahil
                var seenIds = new HashSet<int>(userIds);
                foreach (var rs in c.Rowsets.Values)
                    if (rs.obj > 100 && (rs.idx == 0 || rs.idx == 1) && !c.Objs.ContainsKey(rs.obj) && seenIds.Add(rs.obj)) userIds.Add(rs.obj);
                var cons = new Dictionary<int, Dictionary<string, (int n, Col col)>>();
                foreach (int id in userIds)
                {
                    if (!c.Cols.TryGetValue(id, out var cd)) continue;
                    foreach (var col in cd.Values)
                    {
                        string k = col.Name + "|" + col.X + "|" + col.Len + "|" + col.Prec + "|" + col.Scale;
                        if (!cons.TryGetValue(col.ColId, out var cm)) cons[col.ColId] = cm = new();
                        cm[k] = cm.TryGetValue(k, out var v) ? (v.n + 1, v.col) : (1, col);
                    }
                }

                foreach (int id in userIds)
                {
                    if (!c.Cols.TryGetValue(id, out var cd)) cd = new Dictionary<int, Col>();
                    string tnm = c.Objs.TryGetValue(id, out var ob) ? ob.name : "tablo_" + id;
                    var t = new Tbl { Id = id, Name = tnm };
                    var list = cd.Values.Select(x => x.Copy()).ToList();
                    int mx = list.Count == 0 ? 0 : list.Max(x => x.ColId);
                    for (int cid = 1; cid <= mx; cid++)
                    {
                        if (cd.ContainsKey(cid) || !cons.TryGetValue(cid, out var cm)) continue;
                        int tot = cm.Values.Sum(v => v.n);
                        var top = cm.Values.OrderByDescending(v => v.n).First();
                        if (tot >= 5 && top.n >= tot * 0.8)
                        {
                            var g = top.col.Copy(); g.ColId = cid; g.Guess = true;
                            list.Add(g); t.Guessed.Add(g.Name);
                        }
                    }
                    t.Cols = list.OrderBy(x => x.ColId).ToList();
                    foreach (var rs in c.Rowsets.Where(x => x.Value.obj == id && (x.Value.idx == 0 || x.Value.idx == 1)))
                    {
                        if (rs.Value.cmpr > 0) t.Compressed = true;
                        if (t.RowsetId == 0) t.RowsetId = rs.Key;
                        foreach (var au in c.Aus.Where(a => a.owner == rs.Key && a.type == 1))
                        {
                            var key = ((uint)((au.auid >> 16) & 0xFFFFFFFF), (ushort)((au.auid >> 48) & 0xFFFF));
                            if (c.Data.TryGetValue(key, out var pl)) t.Pages.AddRange(pl);
                            if (c.SlotSum.TryGetValue(key, out var ss)) t.SlotSum += ss;
                        }
                    }
                    if (!c.Objs.ContainsKey(id) && t.Pages.Count == 0) continue;   // adsız + verisiz → anlamsız
                    c.Tables.Add(t);
                }
                return c;
            }
        }

        // ───────────────────────── HIZLI ANALİZ (dosya eklenince) ─────────────────────────
        public static void QuickReport(string src, Action<string> log)
        {
            using var fs = File.OpenRead(src);
            var c = Ctx.Load(fs, null);
            long mb = fs.Length / 1048576;
            double pct(long x) => c.PageCount == 0 ? 0 : 100.0 * x / c.PageCount;

            log("══════ SAYFA ANALİZİ ══════");
            log($"Dosya     : {Path.GetFileName(src)}  ·  {mb:N0} MB  ·  {c.PageCount:N0} sayfa");
            string ext = Path.GetExtension(src).TrimStart('.');
            if (ext.Length > 10 && ext.IndexOfAny(new[] { '_', '-', '@' }) >= 0)
                log($"⚠ Uzantı değiştirilmiş: \".{(ext.Length > 40 ? ext.Substring(0, 40) + "…" : ext)}\" → RANSOMWARE izi (nomoreransom.org'da aileyi kontrol et).");
            log($"Sayfalar  : sağlam {c.Valid:N0} (%{pct(c.Valid):0.#})  ·  şifreli/çöp {c.Garbage:N0} (%{pct(c.Garbage):0.#})  ·  boş {c.Empty:N0}");
            if (c.GarbageSample.Count > 0)
            {
                double ent = 0; var b = new byte[PAGE]; int n = 0;
                foreach (var no in c.GarbageSample) { fs.Position = no * PAGE; if (ReadFull(fs, b, PAGE) == PAGE) { ent += Entropy(b); n++; } }
                if (n > 0) log($"Çöp sayfa entropisi: {ent / n:0.00}/8 → " + (ent / n > 7.8 ? "ŞİFRELİ (ransomware)" : "bozuk/üzerine yazılmış veri"));
            }
            if (c.BadRuns.Count > 0)
            {
                var parts = c.BadRuns.Take(10).Select(r => $"{r.a}-{r.b}");
                log($"Hasarlı bölgeler ({c.BadRuns.Count}): sayfa " + string.Join(", ", parts) + (c.BadRuns.Count > 10 ? " …" : ""));
                if (c.BadRuns.Count >= 4) log("   → aralıklı şifreleme deseni: dosyanın büyük kısmı sağlam, içerikten kurtarılabilir.");
            }
            log($"Sayfa 0 (dosya başlığı): {(c.Page0Ok ? "sağlam" : "HASARLI")}  ·  Sayfa 9 (boot): {(c.Page9Ok ? "sağlam" : "HASARLI")}" +
                (c.Page0Ok && c.Page9Ok ? "" : "  → SQL motoru bu dosyayı AÇAMAZ, içerikten kurtarma gerekir."));
            if (c.TornPages > 0) log($"Torn-page koruması: {c.TornPages:N0} sayfada aktif → otomatik düzeltilecek.");
            string th(byte t) => c.TypeHist.TryGetValue(t, out var v) ? v.ToString("N0") : "0";
            log($"Sayfa tipleri: veri {th(1)} · index {th(2)} · IAM {th(10)} · LOB {th(3)}/{th(4)} · GAM/SGAM/PFS {th(8)}/{th(9)}/{th(11)}");
            log("");
            int withData = c.Tables.Count(t => t.Pages.Count > 0);
            log("══════ TABLOLAR ══════");
            log($"Katalog: {c.Objs.Count:N0} nesne · {c.Tables.Count} kullanıcı tablosu · {withData} tanesinde veri var");
            foreach (var t in c.Tables.Where(t => t.Pages.Count > 0).OrderByDescending(t => t.SlotSum).Take(60))
            {
                string extra = (t.Compressed ? "  [sıkıştırılmış]" : "") + (t.Guessed.Count > 0 ? $"  [+{t.Guessed.Count} kolon kardeşlerden]" : "");
                log($"  {t.Name,-32} {t.Cols.Count,4} kolon  {t.Pages.Count,7:N0} sayfa  ~{t.SlotSum,9:N0} satır{extra}");
            }
            if (withData > 60) log($"  … +{withData - 60} tablo daha");
            int empty = c.Tables.Count - withData;
            if (empty > 0) log($"Verisi bulunamayan tablo: {empty} (boş tablo ya da sayfaları hasarlı bölgede)");
            int nMod = c.Objs.Count(o => IsModuleType(o.Value.type) && c.ModText.ContainsKey(o.Key));
            int nDef = c.Objs.Count(o => o.Value.type == "D " && c.ModText.ContainsKey(o.Key));
            log($"Yapı bilgisi okunabilen: {c.Idx.Count:N0} index/PK/UQ · {nDef:N0} varsayılan değer · {nMod:N0} fonksiyon/view/prosedür/trigger tanımı");
            log("");
            log(c.Page0Ok && c.Page9Ok
                ? "SONUÇ: Başlık sağlam → 'Otomatik Kurtar' önce SQL motoruyla (kayıpsız) dener."
                : "SONUÇ: Başlık hasarlı → 'Otomatik Kurtar' doğrudan İÇERİKTEN kurtarma yapacak (sağlam sayfalardan yeni DB → .bak).");
        }

        // Başlık (sayfa 0 ve 9) sağlam mı? Değilse motor denemesi boşuna.
        // ── MEVCUT .bak'ı ONAR: zaten kurtarılmış (müşteriye verilmiş) bir .bak'ı BAŞTAN TARAMADAN düzeltir.
        //    Geri yükle → Fiş Bütünleme + Fiş tipi + başlık tutarı + hızlı-fiş bütünleme → yeniden .bak.
        //    Kaynak .bak'a dokunmaz; yeni outBak üretir. (105 GB şifreli MDF'i tekrar taramaya gerek yok.)
        public const string BuildTag = "2026-11-28-Q";   // her değişiklikte güncellenir → logda hangi kodu çalıştırdığın belli olur

        // Referans klasöründe paylaşımlı ORKAGENELTANIM veritabanı dosyasını bulur (.mdf/.bak). Firma program nesneleri buna bağlı.
        static string? FindOrkaGenel(params string?[] hints)
        {
            var dirs = new List<string>();
            foreach (var h in hints)
            {
                if (string.IsNullOrEmpty(h)) continue;
                try { var dd = Path.GetDirectoryName(Path.GetFullPath(h)); if (dd != null && !dirs.Contains(dd)) dirs.Add(dd); } catch { }
            }
            foreach (var dir in dirs)
            {
                try
                {
                    foreach (var f in Directory.GetFiles(dir))
                    {
                        string nm = Path.GetFileName(f);
                        if (nm.IndexOf("ORKAGENELTANIM", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            (nm.EndsWith(".mdf", StringComparison.OrdinalIgnoreCase) || nm.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)))
                            return f;
                    }
                }
                catch { }
            }
            return null;
        }
        public static bool FixBak(string inBak, string outBak, string? refPath, Action<string> log, out string err)
        {
            err = "";
            log($"VKM Onarım • yapı {BuildTag} • referanssız bozuk-kolon onarımı AKTİF");
            if (!File.Exists(inBak)) { err = ".bak bulunamadı: " + inBak; return false; }
            if (!MssqlBak.BakCompatible(inBak))
            {
                log("Yedek bu SQL sürümünden yeni — daha yeni LocalDB deneniyor…");
                if (!MssqlBak.TryNewerLocalDb(log)) { err = "Bu .bak'ı açacak LocalDB sürümü yok (daha yeni SQL/LocalDB gerekir)."; return false; }
            }
            string db = "VKM_FixBak_" + Environment.ProcessId + "_" + DateTime.Now.ToString("HHmmss");
            string work = Path.Combine(Path.GetDirectoryName(outBak) ?? Path.GetTempPath(), "vkm_fixbak_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string cs0 = Conn("master");
            bool created = false;
            try
            {
                Directory.CreateDirectory(work);
                GrantAll(work);
                log("1) Yedek geri yükleniyor (çalışma kopyası, orijinal .bak'a dokunulmaz)…");
                var files = Query(cs0, $"RESTORE FILELISTONLY FROM DISK = N'{Esc(inBak)}';", 600);
                var moves = new List<string>(); int i = 0;
                foreach (var f in files)
                {
                    string logical = Convert.ToString(f[0]) ?? "", type = (Convert.ToString(f[2]) ?? "D").Trim();
                    string target = type switch
                    {
                        "L" => Path.Combine(work, $"{db}_log{i}.ldf"),
                        "D" => Path.Combine(work, $"{db}_dat{i}" + (i == 0 ? ".mdf" : ".ndf")),
                        _ => Path.Combine(work, $"{db}_x{i}")
                    };
                    moves.Add($"MOVE N'{Esc(logical)}' TO N'{Esc(target)}'"); i++;
                }
                try { ExecSql(cs0, $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", 120); } catch { }
                ExecSql(cs0, $"RESTORE DATABASE [{db}] FROM DISK = N'{Esc(inBak)}' WITH {string.Join(", ", moves)}, REPLACE, RECOVERY;", 14400);
                created = true;
                log("✓ Geri yüklendi. Onarım adımları çalışıyor…");

                // ÖNCE: Orka'nın adıyla aradığı kritik kolonların bozuk adları (hesapūodu→hesapkodu) — referans gelmese de düzelsin.
                try { FixKnownCorruptColumns(db, log); } catch (Exception ex) { log("Bilinen bozuk kolon onarımı atlandı: " + First(ex.Message)); }

                // 2024/sağlam referans verildiyse: hesap planı + cari + şablon/tanım tamamla (collation artık uyumlu)
                bool planDone = false;
                if (!string.IsNullOrEmpty(refPath) && File.Exists(refPath))
                {
                    string hDb = "VKM_Yrd_" + Environment.ProcessId + "_" + DateTime.Now.ToString("HHmmssfff");
                    string hWork = Path.Combine(work, "ref");
                    bool ogtOk = false;
                    try
                    {
                        log("── Referanstan tamamlama (hesap planı/cari/şablon): " + Path.GetFileName(refPath) + " ──");
                        Directory.CreateDirectory(hWork); GrantAll(hWork);
                        if (LoadRef(refPath, hDb, hWork, out string herr))
                        {
                            FillFromHelper(db, hDb, log);
                            // Paylaşımlı ORKAGENELTANIM referans/çıktı klasöründe varsa bağla → firma program nesneleri (ORKAGENELTANIM.dbo.*'a bağlı) temiz 2024'ten YENİDEN kurulabilsin (bozuk fonksiyonlar temizlenir)
                            string? ogtPath = FindOrkaGenel(refPath, inBak, outBak);
                            if (ogtPath != null)
                            {
                                try
                                {
                                    try { ExecSql(cs0, "IF DB_ID('ORKAGENELTANIM') IS NOT NULL BEGIN ALTER DATABASE [ORKAGENELTANIM] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [ORKAGENELTANIM]; END", 120); } catch { }
                                    string ogtWork = Path.Combine(work, "ogt"); Directory.CreateDirectory(ogtWork); GrantAll(ogtWork);
                                    if (LoadRef(ogtPath, "ORKAGENELTANIM", ogtWork, out string oerr)) { ogtOk = true; log("── Paylaşımlı veritabanı bağlandı: ORKAGENELTANIM (" + Path.GetFileName(ogtPath) + ") → program nesneleri temiz kurulacak ──"); }
                                    else log("⚠ ORKAGENELTANIM açılamadı: " + oerr);
                                }
                                catch (Exception ex) { log("ORKAGENELTANIM bağlama atlandı: " + First(ex.Message)); }
                            }
                            else log("ℹ ORKAGENELTANIM dosyası referans klasöründe bulunamadı — program nesneleri yalnızca eksik olanlar eklenecek (bozuk olanlar düzelmez). ORKAGENELTANIM.mdf'i referansın yanına koyarsan temiz kurulur.");
                            try { CopyModulesFromHelper(db, hDb, log, ogtOk); } catch (Exception ex) { log("Program nesnesi aktarımı atlandı: " + First(ex.Message)); }
                            // hesap planı bağlantı onarımı REFERANS AÇIKKEN: eksik hesap adları 2024'ten ("adı kurtarılamadı" yerine gerçek ad)
                            try { RepairPlanRefs(db, Path.GetDirectoryName(outBak) ?? ".", log, hDb); planDone = true; } catch (Exception ex) { log("Hesap planı bağlantı onarımı atlandı: " + First(ex.Message)); }
                        }
                        else log("⚠ Referans açılamadı: " + herr);
                    }
                    catch (Exception ex) { log("Referanstan tamamlama atlandı: " + First(ex.Message)); }
                    finally
                    {
                        try { ExecSql(cs0, $"IF DB_ID('{hDb}') IS NOT NULL BEGIN ALTER DATABASE [{hDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{hDb}]; END", 120); } catch { }
                        if (ogtOk) try { ExecSql(cs0, "IF DB_ID('ORKAGENELTANIM') IS NOT NULL BEGIN ALTER DATABASE [ORKAGENELTANIM] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [ORKAGENELTANIM]; END", 120); } catch { }
                    }
                }
                // referans yoksa/açılamadıysa: fişlerde kullanılıp planda olmayan hesapları referanssız aç (361 vb. "kod oluşturulamadı" hatasını önler)
                if (!planDone)
                    try { RepairPlanRefs(db, Path.GetDirectoryName(outBak) ?? ".", log); } catch (Exception ex) { log("Hesap planı bağlantı onarımı atlandı: " + First(ex.Message)); }

                // fiş satırı → plan köprüsü (IDHesapkodu) + gerçek kolon adı TANISI
                try { RepairLinePlanLinks(db, log); } catch (Exception ex) { log("Fiş-plan köprü onarımı atlandı: " + First(ex.Message)); }

                try { RebuildHeadersFromQuickEntry(db, log); } catch (Exception ex) { log("Hızlı fişten başlık üretimi atlandı: " + First(ex.Message)); }
                try { RebuildHeadersFromOrphanLines(db, log); } catch (Exception ex) { log("Yetim satırlardan başlık bütünleme atlandı: " + First(ex.Message)); }
                try { AssignMissingFisTipi(db, log); } catch (Exception ex) { log("Fiş tipi belirleme atlandı: " + First(ex.Message)); }
                try { RebuildLinesFromQuickEntry(db, log); } catch (Exception ex) { log("Hızlı fişten satır üretimi atlandı: " + First(ex.Message)); }
                try { FixHeaderTotals(db, log); } catch { }
                try { QualitySummary(db, Path.GetDirectoryName(outBak) ?? ".", Path.GetFileNameWithoutExtension(outBak), log); } catch (Exception ex) { log("Kalite özeti atlandı: " + First(ex.Message)); }
                try { ReportProblemVouchers(db, Path.GetDirectoryName(outBak) ?? ".", Path.GetFileNameWithoutExtension(outBak), log); } catch (Exception ex) { log("Eksik fiş raporu atlandı: " + First(ex.Message)); }

                log("2) Düzeltilmiş yedek yazılıyor…");
                ExecSql(cs0, $"BACKUP DATABASE [{db}] TO DISK = N'{Esc(outBak)}' WITH INIT, FORMAT;", 7200);
                log("✓✓ HAZIR — onarılmış .bak üretildi: " + outBak);
                return true;
            }
            catch (Exception ex) { err = First(ex.Message); log("✗ Onarım hatası: " + err); return false; }
            finally
            {
                if (created) { try { ExecSql(cs0, $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", 120); } catch { } }
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            }
        }

        // ── Kurtarılmış .bak'ı Excel'e aktar: Yevmiye Defteri + Mizan + Hesap Planı (3 sayfalı .xlsx). Orka UI açmasa da müşaviye teslim edilebilir.
        public static bool ExportToExcel(string inBak, string outXlsx, Action<string> log, out string err)
        {
            err = "";
            if (!File.Exists(inBak)) { err = ".bak bulunamadı: " + inBak; return false; }
            if (!MssqlBak.BakCompatible(inBak))
            {
                log("Yedek bu SQL sürümünden yeni — daha yeni LocalDB deneniyor…");
                if (!MssqlBak.TryNewerLocalDb(log)) { err = "Bu .bak'ı açacak LocalDB sürümü yok."; return false; }
            }
            string db = "VKM_Xls_" + Environment.ProcessId + "_" + DateTime.Now.ToString("HHmmss");
            string work = Path.Combine(Path.GetDirectoryName(outXlsx) ?? Path.GetTempPath(), "vkm_xls_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            string cs0 = Conn("master");
            bool created = false;
            try
            {
                Directory.CreateDirectory(work); GrantAll(work);
                log("1) Yedek geri yükleniyor (çalışma kopyası)…");
                var files = Query(cs0, $"RESTORE FILELISTONLY FROM DISK = N'{Esc(inBak)}';", 600);
                var moves = new List<string>(); int i = 0;
                foreach (var f in files)
                {
                    string logical = Convert.ToString(f[0]) ?? "", type = (Convert.ToString(f[2]) ?? "D").Trim();
                    string target = type switch { "L" => Path.Combine(work, $"{db}_log{i}.ldf"), "D" => Path.Combine(work, $"{db}_dat{i}" + (i == 0 ? ".mdf" : ".ndf")), _ => Path.Combine(work, $"{db}_x{i}") };
                    moves.Add($"MOVE N'{Esc(logical)}' TO N'{Esc(target)}'"); i++;
                }
                try { ExecSql(cs0, $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", 120); } catch { }
                ExecSql(cs0, $"RESTORE DATABASE [{db}] FROM DISK = N'{Esc(inBak)}' WITH {string.Join(", ", moves)}, REPLACE, RECOVERY;", 14400);
                created = true;
                string cs = Conn(db);
                log("2) Yevmiye / Mizan / Hesap Planı çekiliyor…");

                string yevmiyeSql = @"SELECT b.fisno, b.fistarihi, b.fisaciklama, s.kayitsirano, s.hesapkodu, p.planadi, s.fisaciklamasi, s.tlborctutar, s.tlalactutar
                    FROM dbo.ORT_FISBASLIK b JOIN dbo.ORT_FISSATIR s ON s.IDFisbaslik=b.ID
                    LEFT JOIN dbo.ORT_PLAN p ON p.plankod=s.hesapkodu
                    ORDER BY b.fistarihi, b.fisno, s.kayitsirano;";
                string mizanSql = @"SELECT s.hesapkodu, MAX(p.planadi), SUM(s.tlborctutar), SUM(s.tlalactutar), SUM(s.tlborctutar)-SUM(s.tlalactutar)
                    FROM dbo.ORT_FISSATIR s LEFT JOIN dbo.ORT_PLAN p ON p.plankod=s.hesapkodu
                    GROUP BY s.hesapkodu ORDER BY s.hesapkodu;";
                string planSql = @"SELECT plankod, planadi FROM dbo.ORT_PLAN ORDER BY plankod;";

                var yev = Query(cs, yevmiyeSql, 1800);
                var miz = Query(cs, mizanSql, 900);
                var pln = Query(cs, planSql, 300);

                var hYev = new[] { "Fiş No", "Tarih", "Fiş Açıklama", "Sıra", "Hesap Kodu", "Hesap Adı", "Satır Açıklama", "Borç", "Alacak" };
                var hMiz = new[] { "Hesap Kodu", "Hesap Adı", "Borç Toplam", "Alacak Toplam", "Bakiye" };
                var hPln = new[] { "Hesap Kodu", "Hesap Adı" };
                using (var wb = new ClosedXML.Excel.XLWorkbook())
                {
                    WriteSheet(wb, "Yevmiye Defteri", hYev, yev);
                    WriteSheet(wb, "Mizan", hMiz, miz);
                    WriteSheet(wb, "Hesap Planı", hPln, pln);
                    wb.SaveAs(outXlsx);
                }
                // CSV de üret (müşavir/başka muhasebe programları için): UTF-8 BOM + noktalı virgül ayraç (Türkçe Excel uyumlu)
                string xdir = Path.GetDirectoryName(outXlsx) ?? ".";
                string bn = Path.GetFileNameWithoutExtension(outXlsx);
                WriteCsv(Path.Combine(xdir, bn + "_Yevmiye.csv"), hYev, yev);
                WriteCsv(Path.Combine(xdir, bn + "_Mizan.csv"), hMiz, miz);
                WriteCsv(Path.Combine(xdir, bn + "_HesapPlani.csv"), hPln, pln);
                log($"✓✓ Excel + CSV yazıldı: {Path.GetFileName(outXlsx)} (+3 CSV)  ·  Yevmiye {yev.Count:N0} satır · Mizan {miz.Count:N0} hesap · Plan {pln.Count:N0} hesap");
                return true;
            }
            catch (Exception ex) { err = First(ex.Message); log("✗ Excel aktarım hatası: " + err); return false; }
            finally
            {
                if (created) { try { ExecSql(cs0, $"IF DB_ID('{db}') IS NOT NULL BEGIN ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}]; END", 120); } catch { } }
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
            }
        }

        static void WriteSheet(ClosedXML.Excel.XLWorkbook wb, string name, string[] headers, List<object[]> rows)
        {
            var ws = wb.Worksheets.Add(name.Length > 31 ? name.Substring(0, 31) : name);
            for (int c = 0; c < headers.Length; c++) { var h = ws.Cell(1, c + 1); h.Value = headers[c]; h.Style.Font.Bold = true; }
            for (int r = 0; r < rows.Count; r++)
            {
                var row = rows[r];
                for (int c = 0; c < headers.Length && c < row.Length; c++)
                {
                    object v = row[c];
                    if (v == null || v == DBNull.Value) continue;
                    var cell = ws.Cell(r + 2, c + 1);
                    if (v is decimal || v is double || v is float || v is int || v is long || v is short || v is byte)
                        cell.Value = Convert.ToDouble(v);
                    else if (v is DateTime dt) { cell.Value = dt; cell.Style.DateFormat.Format = "dd.MM.yyyy"; }
                    else if (v is bool bo) cell.Value = bo;
                    else cell.Value = Convert.ToString(v) ?? "";
                }
            }
            try { ws.SheetView.FreezeRows(1); ws.Columns().AdjustToContents(); } catch { }
        }

        static void WriteCsv(string path, string[] headers, List<object[]> rows)
        {
            var tr = new System.Globalization.CultureInfo("tr-TR");
            var sb = new System.Text.StringBuilder();
            string Cell(string s)
            {
                if (s == null) return "";
                bool need = s.IndexOf(';') >= 0 || s.IndexOf('"') >= 0 || s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;
                if (need) return "\"" + s.Replace("\"", "\"\"") + "\"";
                return s;
            }
            sb.Append(string.Join(";", headers.Select(Cell))).Append("\r\n");
            foreach (var row in rows)
            {
                var cells = new List<string>(headers.Length);
                for (int c = 0; c < headers.Length; c++)
                {
                    object v = c < row.Length ? row[c] : null;
                    if (v == null || v == DBNull.Value) { cells.Add(""); continue; }
                    if (v is decimal || v is double || v is float) cells.Add(Convert.ToDecimal(v).ToString("#,##0.00", tr));
                    else if (v is int || v is long || v is short || v is byte) cells.Add(Convert.ToInt64(v).ToString(tr));
                    else if (v is DateTime dt) cells.Add(dt.ToString("dd.MM.yyyy"));
                    else cells.Add(Cell(Convert.ToString(v) ?? ""));
                }
                sb.Append(string.Join(";", cells)).Append("\r\n");
            }
            File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(true));
        }

        public static bool HeaderLooksOk(string src)
        {
            try
            {
                using var fs = File.OpenRead(src);
                var b = new byte[PAGE];
                if (!ReadPage(fs, 0, b) || b[0] != 1 || b[1] != 15) return false;
                if (!ReadPage(fs, 9, b) || b[0] != 1 || b[1] != 13) return false;
                return true;
            }
            catch { return false; }
        }

        // ───────────────────────── İÇERİKTEN KURTAR → .bak ─────────────────────────
        public static bool Rebuild(string src, string outBak, Action<string> log, out string err, string? refPath = null, Pool? pool = null, bool scanDeleted = false, string? helperPath = null)
        {
            err = "";
            // aynı anda birden çok VKM penceresi çalışabilir → adlar işlem numarasıyla benzersiz
            string tag = Environment.ProcessId + "_" + DateTime.Now.ToString("HHmmssfff");
            string refDb = "VKM_Ref_" + tag;
            bool refCreated = false;
            string work = Path.Combine(Path.GetDirectoryName(outBak) ?? Path.GetTempPath(),
                                       "vkm_rebuild_" + tag);
            string db = "VKM_Rebuild_" + tag;
            bool dbCreated = false;
            string? renamedTo = null, refWork = null;
            LastRefUsed = false;
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
            Encoding enc;
            try { enc = Encoding.GetEncoding(1254); } catch { enc = Encoding.Latin1; }

            try
            {
                using var fs = File.OpenRead(src);
                log("İçerikten kurtarma: sayfalar taranıyor, katalog okunuyor…");
                var c = Ctx.Load(fs, log);
                c.Family = FamilyOf(src);
                _lobCtx = c; _lobOk = 0; _lobLost = 0; _lobUsed = new HashSet<uint>();
                log($"Sağlam sayfa {c.Valid:N0} · şifreli/çöp {c.Garbage:N0} · torn düzeltilecek {c.TornPages:N0}");
                log($"Katalog: {c.Objs.Count:N0} nesne · {c.Tables.Count} kullanıcı tablosu · {c.Tables.Count(t => t.Pages.Count > 0)} tanesinde veri");

                if (pool != null && pool.Count > 1) c.ApplyPool(pool, src, log);

                Directory.CreateDirectory(work);
                long estMb = c.DataCnt * PAGE / 1048576;
                MssqlBak.PickInstanceFor(estMb, log);

                // ── REFERANS (opsiyonel): sağlam aynı-program DB'si → şema, yerleşim, ID/PK/index/prosedür
                Dictionary<string, RefTbl>? rf = null;
                if (!string.IsNullOrWhiteSpace(refPath) && File.Exists(refPath))
                {
                    log("Referans yükleniyor: " + Path.GetFileName(refPath));
                    string rerr;
                    bool ok = LoadRef(refPath!, refDb, work, out rerr);
                    if (!ok && MssqlBak.UsingPreferredOld && (rerr.Contains("version") || rerr.Contains("sürüm")))
                    {
                        log("Referans daha yeni bir SQL sürümünden → en yeni LocalDB'ye geçiliyor.");
                        MssqlBak.UseDefault();
                        ok = LoadRef(refPath!, refDb, work, out rerr);
                    }
                    if (!ok && (rerr.Contains("version") || rerr.Contains("sürüm")) && MssqlBak.TryNewerLocalDb(log))
                        ok = LoadRef(refPath!, refDb, work, out rerr);
                    if (!ok)
                    {
                        // çıktı klasörü SQL motoru için yazılamaz olabilir (izin / salt-okunur sürücü) → Windows'un geçici klasöründe tekrar dene
                        refWork = Path.Combine(Path.GetTempPath(), "VKM_ref_" + DateTime.Now.ToString("HHmmssfff"));
                        log("Referans çıktı klasöründe açılamadı → geçici klasörde tekrar deneniyor…");
                        ok = LoadRef(refPath!, refDb, refWork, out string rerr2);
                        if (!ok) rerr = rerr2;
                    }
                    if (ok)
                    {
                        refCreated = true; LastRefUsed = true;
                        rf = ReadRefSchema(refDb);
                        log($"Referans açıldı: {rf.Count} tablo");
                        c.ApplyRef(rf, log);
                    }
                    else log("⚠ Referans açılamadı → referanssız devam (" + rerr + ")");
                }
                try { c.AssignOrphans(rf, pool, log); } catch (Exception ex) { log("Sahipsiz sayfa eşleştirme atlandı: " + First(ex.Message)); }
                LdfImages? ldf = null; long ldfAdded = 0; int ldfTables = 0;
                try
                {
                    string? lp = FindLdf(src);
                    if (lp != null)
                    {
                        ldf = LdfImages.Load(lp);
                        log(ldf != null ? $"İşlem günlüğü (LDF) okundu: {Path.GetFileName(lp)} · {ldf.Count:N0} satır görüntüsü → MDF'de şifreli kalan son kayıtlar buradan tamamlanacak."
                                        : $"İşlem günlüğü (LDF) bulundu ama okunamadı/çok büyük: {Path.GetFileName(lp)}");
                    }
                }
                catch (Exception ex) { log("LDF okunamadı: " + First(ex.Message)); ldf = null; }
                // bölümü (rowset) bilinen tablolar; imzası bilinmeyen-bölümlü tablolar arasında kaç tabloda ortak
                var knownPids = new HashSet<long>(c.Rowsets.Keys.Concat(c.Tables.Where(x => x.RowsetId != 0).Select(x => x.RowsetId)));
                var sigOwners = c.Tables.Where(x => x.RowsetId == 0 && x.Cols.Count > 0)
                                        .GroupBy(x => (Ctx.FixedLen(x.Cols), Math.Max(x.Cols.Count, x.Cols.Max(y => y.ColId))))
                                        .ToDictionary(g => g.Key, g => g.Count());
                if (c.Tables.Count(t => t.Cols.Count > 0) == 0) { err = "Sistem kataloğundan kullanıcı tablosu çıkarılamadı (katalog sayfaları hasarlı). Referans dosya vermeyi dene."; return false; }
                string cs0 = Conn("master");
                ExecSql(cs0, $"CREATE DATABASE [{db}] ON (NAME='{db}_dat', FILENAME=N'{Esc(Path.Combine(work, db + ".mdf"))}') " +
                              $"LOG ON (NAME='{db}_log', FILENAME=N'{Esc(Path.Combine(work, db + "_log.ldf"))}') COLLATE Turkish_CI_AS;", 300);
                dbCreated = true;
                ExecSql(cs0, $"ALTER DATABASE [{db}] SET RECOVERY SIMPLE;", 60);
                string cs = Conn(db);

                long totalRows = 0, totalBad = 0, totalLostCols = 0, totalCols = 0, totalDup = 0, totalGhosts = 0; int okTables = 0, identTables = 0, qcWarn = 0;
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var recovered = new Dictionary<string, RecTbl>(StringComparer.OrdinalIgnoreCase);
                string rnCol = rf != null ? ", [__vkm_rn] bigint IDENTITY(1,1) NOT NULL" : "";
                int noCol = 0;
                var created = new Dictionary<int, (string tn, string orig, Dictionary<int, string> cols, Dictionary<string, string> types)>();
                var pkMade = new HashSet<int>();
                foreach (var t in c.Tables)
                {
                    try
                    {
                    if (t.Cols.Count == 0) { if (t.Pages.Count > 0) noCol++; continue; }
                    string tname = Safe(t.Name), tn = tname; int dup = 2;
                    while (!usedNames.Add(tn)) tn = tname + "_" + dup++;

                    var colNames = new List<string>(); var defs = new List<string>(); var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var col in t.Cols)
                    {
                        string cn = Safe(col.Name), cn2 = cn; int d2 = 2;
                        while (!used.Add(cn2)) cn2 = cn + "_" + d2++;
                        colNames.Add(cn2); defs.Add($"[{cn2}] {SqlType(col)} NULL");
                    }
                    try { ExecSql(cs, $"CREATE TABLE [dbo].[{tn}] ({string.Join(", ", defs)}{rnCol});", 120); }
                    catch (Exception ex)
                    {
                        // isim çakışması (Türkçe harmanlama / bozuk isim) → kolon adlarına colid ekle, tekrar dene
                        for (int i = 0; i < colNames.Count; i++) colNames[i] = colNames[i] + "_" + t.Cols[i].ColId;
                        defs = t.Cols.Select((col, i) => $"[{colNames[i]}] {SqlType(col)} NULL").ToList();
                        try { ExecSql(cs, $"CREATE TABLE [dbo].[{tn}] ({string.Join(", ", defs)}{rnCol});", 120); log($"  ⚠ {tn}: kolon adları çakıştı → numaralı adlarla yaratıldı"); }
                        catch { log($"  ✗ {tn}: tablo yaratılamadı ({First(ex.Message)})"); continue; }
                    }

                    created[t.Id] = (tn, t.Name, t.Cols.Select((x, i) => (x.ColId, colNames[i])).GroupBy(z => z.Item1).ToDictionary(g => g.Key, g => g.First().Item2),
                                     t.Cols.Select((x, i) => (colNames[i], SqlType(x))).GroupBy(z => z.Item1, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Item2, StringComparer.OrdinalIgnoreCase));
                    var rt = new RecTbl { Orig = t.Name, Tn = tn, Lay = t.Cols };
                    for (int i = 0; i < t.Cols.Count; i++) rt.ColMap[t.Cols[i].Name] = colNames[i];
                    recovered[t.Name] = rt;

                    if (t.Compressed) { log($"  ⚠ {tn}: sıkıştırılmış tablo — bu sürüm çözemiyor, boş yaratıldı."); continue; }
                    // MDF'de hiç sayfası olmayan tablo: günlükte satırları varsa onlardan kurulur
                    List<Rec>? ldfOnly = null;
                    if (t.Pages.Count == 0 && ldf != null && t.Cols.Count > 0)
                    {
                        int nc0 = Math.Max(t.Cols.Count, t.Cols.Max(x => x.ColId)), fe0 = Ctx.FixedLen(t.Cols);
                        IEnumerable<int>? src0 = null;
                        if (t.RowsetId != 0) { if (ldf.InsByPid.TryGetValue(t.RowsetId, out var p0)) src0 = p0; }
                        else if (!(sigOwners.TryGetValue((fe0, nc0), out var so0) && so0 > 1) && ldf.Ins.TryGetValue((fe0, nc0), out var li0))
                            src0 = li0.Where(z => !knownPids.Contains(ldf.Pid(z)));
                        if (src0 != null) ldfOnly = src0.Select(ldf.At).Where(x => x != null).Select(x => x!).ToList();
                        if (ldfOnly != null && ldfOnly.Count == 0) ldfOnly = null;
                    }
                    if (t.Pages.Count == 0 && ldfOnly == null)
                    {
                        okTables++;
                        // boş tablo da program yeni kayıt girerken ID'yi kendisi üretilsin diye bekler → IDENTITY (referans yoksa)
                        if (rf == null)
                        {
                            int ix = t.Cols.FindIndex(x => x.Ident && (Ints.Contains(x.X) || x.X == 106 || x.X == 108));
                            if (ix < 0) ix = t.Cols.FindIndex(x => x.Name.Equals("ID", StringComparison.OrdinalIgnoreCase) && Ints.Contains(x.X) && x.ColId == t.Cols.Min(y => y.ColId));
                            if (ix >= 0)
                            {
                                bool catPk0 = c.Idx.Any(x => x.id == t.Id && (x.status & 0x20) != 0) || (pool != null && pool.HasPk(t.Name));
                                if (MakeIdentityPk(cs, tn, colNames, ix, SqlType(t.Cols[ix]), defs, addPk: !catPk0)) { identTables++; if (!catPk0) pkMade.Add(t.Id); }
                            }
                        }
                        continue;
                    }

                    totalCols += t.Cols.Count;
                    var sample = ldfOnly != null ? ldfOnly.Take(300).ToList() : c.TableRecs(t).Take(300).ToList();
                    var lay = ChooseLayout(t, c, sample, enc, out int lost);
                    totalLostCols += lost;

                    // günlükteki (LDF) satır görüntüleri: bu tablonun kayıt imzalarıyla; her ID'nin en yeni sürümü, silinenler hariç
                    var ldfRecs = new List<Rec>();
                    if (ldf != null)
                        try
                        {
                            var sigs = sample.GroupBy(r => (r.FixedEnd, r.NCol)).Where(g => g.Count() >= 2 || sample.Count < 10).Select(g => g.Key).ToHashSet();
                            if (sigs.Count == 0) sigs.Add((Ctx.FixedLen(t.Cols), Math.Max(t.Cols.Count, t.Cols.Max(x => x.ColId))));
                            int idI = lay.FindIndex(x => !x.Lost && x.Name.Equals("ID", StringComparison.OrdinalIgnoreCase) && Ints.Contains(x.X));
                            int tdI = lay.FindIndex(x => !x.Lost && x.X == 189 && !x.IsVar);
                            ulong Td(Rec r) => tdI < 0 || lay[tdI].Off + 8 > r.FixedEnd ? 0UL : System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(r.P.AsSpan(r.Abs + lay[tdI].Off, 8));
                            long? Id(Rec r) { if (idI < 0) return null; var v = DecodeEx(r, lay[idI], enc, out int st); return st == 1 && v != DBNull.Value ? Convert.ToInt64(v) : null; }
                            var best = new Dictionary<long, (Rec r, ulong td)>(); var noId = new List<Rec>();
                            var gone = new Dictionary<long, ulong>();
                            // bölüm kimliği biliniyorsa yalnız o tablonun kayıtları; bilinmiyorsa imza + sahibi bilinmeyen bölüm
                            IEnumerable<int> InsOf((int, int) sg) => t.RowsetId != 0
                                ? (ldf.InsByPid.TryGetValue(t.RowsetId, out var a1) ? a1 : Enumerable.Empty<int>())
                                : (ldf.Ins.TryGetValue(sg, out var a2) ? a2.Where(z => !knownPids.Contains(ldf.Pid(z))) : Enumerable.Empty<int>());
                            IEnumerable<int> DelOf((int, int) sg) => t.RowsetId != 0
                                ? (ldf.DelByPid.TryGetValue(t.RowsetId, out var d1) ? d1 : Enumerable.Empty<int>())
                                : (ldf.Del.TryGetValue(sg, out var d2) ? d2.Where(z => !knownPids.Contains(ldf.Pid(z))) : Enumerable.Empty<int>());
                            if (t.RowsetId == 0 && sigs.Any(sg => sigOwners.TryGetValue(sg, out var so) && so > 1)) sigs.Clear();   // aynı imzada başka tablo da var → karışmasın
                            foreach (var sg in (t.RowsetId != 0 ? sigs.Take(1) : sigs))
                            {
                                foreach (var s0 in InsOf(sg))
                                    {
                                        var r = ldf.At(s0); if (r == null) continue;
                                        var id = Id(r); ulong td = Td(r);
                                        if (id == null) { if (idI < 0) noId.Add(r); continue; }
                                        if (!best.TryGetValue(id.Value, out var cur) || td >= cur.td) best[id.Value] = (r, td);
                                    }
                                foreach (var s0 in DelOf(sg))
                                    {
                                        var r = ldf.At(s0); if (r == null) continue;
                                        var id = Id(r); if (id == null) continue;
                                        ulong td = Td(r); if (!gone.TryGetValue(id.Value, out var g0) || td > g0) gone[id.Value] = td;
                                    }
                            }
                            foreach (var kv in best.OrderByDescending(z => z.Value.td))
                                if (!gone.TryGetValue(kv.Key, out var gt) || gt < kv.Value.td) ldfRecs.Add(kv.Value.r);
                            if (idI < 0 && ldfOnly != null) ldfRecs.AddRange(noId);
                        }
                        catch { ldfRecs.Clear(); }

                    // anahtar kolon (ID / sysrscols ordkey) ile tekrar eden satırları ele
                    int keyIdx = lay.FindIndex(x => !x.Lost && x.Name.Equals("ID", StringComparison.OrdinalIgnoreCase) && (x.X == 56 || x.X == 127));
                    var seenKeys = new HashSet<long>();

                    var dt = new DataTable();
                    foreach (var cn in colNames) dt.Columns.Add(cn, typeof(object));
                    long rows = 0, bad = 0, dups = 0, batchBytes = 0, fromLdf = 0;
                    int live = lay.Count(x => !x.Lost);
                    foreach (var r in (ldfOnly != null ? Enumerable.Empty<Rec>() : c.TableRecs(t)).Concat(ldfRecs.Select(x => { x.FromLog = true; return x; })))
                    {
                        var vals = new object[lay.Count]; int errs = 0;
                        for (int i = 0; i < lay.Count; i++)
                        {
                            if (lay[i].Lost) { vals[i] = DBNull.Value; continue; }
                            vals[i] = DecodeEx(r, lay[i], enc, out int st);
                            if (st == 2) errs++;
                        }
                        if (live > 0 && errs > live / 2) { bad++; continue; }
                        if (keyIdx >= 0 && vals[keyIdx] != DBNull.Value)
                        {
                            long kv = Convert.ToInt64(vals[keyIdx]);
                            if (!seenKeys.Add(kv)) { if (!r.FromLog) dups++; continue; }   // MDF'deki sürüm varsa günlükteki atlanır
                        }
                        dt.Rows.Add(vals); rows++; if (r.FromLog) fromLdf++;
                        foreach (var v in vals) batchBytes += v is byte[] bb ? bb.Length : v is string ss ? ss.Length * 2 : 8;
                        if (dt.Rows.Count >= 5000 || batchBytes > 64L * 1048576) { bad += Flush(cs, tn, colNames, dt); batchBytes = 0; }
                    }
                    bad += Flush(cs, tn, colNames, dt);
                    totalRows += rows; totalBad += bad; totalDup += dups; okTables++;
                    if (fromLdf > 0) { ldfAdded += fromLdf; ldfTables++; }
                    rt.Rows = rows; rt.Lay = lay;

                    // ── kalite kontrolü: katalogda "boş olamaz" olup tamamen boş gelen kolon = yerleşim şüphesi
                    if (rows > 0 && colNames.Count <= 1000)
                        try
                        {
                            var qc = Query(cs, "SELECT " + string.Join(",", colNames.Select(n => $"COUNT([{n}])")) + $" FROM [dbo].[{tn}];", 600)[0];
                            var empt = new List<string>();
                            for (int i = 0; i < colNames.Count && i < qc.Length; i++)
                                if (Convert.ToInt64(qc[i]) == 0 && !lay[i].Lost && lay[i].NotNull) empt.Add(colNames[i]);
                            if (empt.Count > 0) { qcWarn += empt.Count; log($"     ⚠ kalite: {string.Join(", ", empt.Take(6))}{(empt.Count > 6 ? " …" : "")} tamamen boş geldi (katalogda boş olamaz)"); }
                        }
                        catch { }

                    // ── silinmiş (hayalet) kayıtlar → ayrı tablo, canlı veriye karışmaz
                    long ghosts = 0;
                    if (scanDeleted) try
                    {
                        DataTable? gdt = null; string gtn = Safe(tn + "__SILINEN");
                        foreach (var r in c.TableRecs(t, ghost: true))
                        {
                            var vals = new object[lay.Count]; int errs = 0;
                            for (int i = 0; i < lay.Count; i++)
                            {
                                if (lay[i].Lost) { vals[i] = DBNull.Value; continue; }
                                vals[i] = DecodeEx(r, lay[i], enc, out int st);
                                if (st == 2) errs++;
                            }
                            if (live > 0 && errs > live / 2) continue;
                            if (gdt == null)
                            {
                                ExecSql(cs, $"SELECT TOP 0 {string.Join(",", colNames.Select(n => "[" + n + "]"))} INTO [dbo].[{gtn}] FROM [dbo].[{tn}];", 120);
                                gdt = new DataTable(); foreach (var cn in colNames) gdt.Columns.Add(cn, typeof(object));
                            }
                            gdt.Rows.Add(vals); ghosts++;
                            if (gdt.Rows.Count >= 5000) Flush(cs, gtn, colNames, gdt);
                        }
                        if (gdt != null) Flush(cs, gtn, colNames, gdt);
                    }
                    catch { }
                    totalGhosts += ghosts;

                    // ── ID kolonu benzersiz ve dolu → otomatik artan + PK (referans yoksa; program yeni kayıt girebilsin)
                    bool identOk = false;
                    if (rf == null)
                    {
                        // katalog (ya da havuz) IDENTITY kolonunu biliyorsa o; bilmiyorsa ilk kolon 'ID' tahmini
                        bool catPk = c.Idx.Any(x => x.id == t.Id && (x.status & 0x20) != 0) || (pool != null && pool.HasPk(t.Name));
                        int idIdx = lay.FindIndex(x => !x.Lost && x.Ident && (Ints.Contains(x.X) || x.X == 106 || x.X == 108));
                        if (idIdx < 0 && keyIdx >= 0 && rows > 0 && t.Cols.Count > 0 && lay[keyIdx].ColId == t.Cols.Min(x => x.ColId)) idIdx = keyIdx;
                        if (idIdx >= 0) identOk = MakeIdentityPk(cs, tn, colNames, idIdx, SqlType(lay[idIdx]), defs, addPk: !catPk);
                        if (identOk && !catPk) pkMade.Add(t.Id);
                    }
                    if (identOk) identTables++;
                    if (ghosts > 0) log($"     + {ghosts:N0} silinmiş kayıt bulundu → {gtn_(tn)}");
                    string note = (fromLdf > 0 ? $" · +{fromLdf:N0} işlem günlüğünden (LDF)" : "") + (lost > 0 ? $" · {lost}/{t.Cols.Count} kolon belirsiz→NULL" : "") +
                                  (t.Guessed.Count > 0 ? $" · tahmini kolon: {string.Join(",", t.Guessed)}" : "") +
                                  (bad > 0 ? $" · {bad:N0} hasarlı satır atlandı" : "") +
                                  (dups > 0 ? $" · {dups:N0} tekrar satır elendi" : "");
                    log($"  ✓ {tn}: {rows:N0} satır{note}");
                    }
                    catch (Exception ex) { log($"  ✗ {t.Name}: bu tablo atlandı ({First(ex.Message)}) — diğer tablolar devam ediyor."); }
                }

                double colPct = totalCols == 0 ? 0 : 100.0 * (totalCols - totalLostCols) / totalCols;
                log("");
                log($"TOPLAM: {okTables}/{c.Tables.Count} tablo · {totalRows:N0} satır kurtarıldı · veri olan tablolarda kolonların %{colPct:0.0} çözüldü" +
                    (totalBad > 0 ? $" · {totalBad:N0} hasarlı satır" : ""));
                if (ldfAdded > 0) log($"İşlem günlüğünden (LDF) {ldfAdded:N0} satır {ldfTables} tabloya eklendi — MDF'de şifreli sayfada kalan son kayıtlar.");
                ldf = null;
                if (qcWarn > 0) log($"Kalite kontrolü: {qcWarn} kolon katalogda 'boş olamaz' iken boş geldi (yerleşim kesinleşmedi; referans/havuz ile düzelebilir).");
                else log("Kalite kontrolü: 'boş olamaz' kolonların hepsi dolu ✓");
                if (_lobOk + _lobLost > 0) log($"Satır dışı büyük değer (LOB: resim/ZIP/uzun metin): {_lobOk:N0} birleştirildi" + (_lobLost > 0 ? $" · {_lobLost:N0} tanesinin parçası şifreli sayfada → boş" : ""));
                if (rf == null) ApplyStructure(cs, c, created, pkMade, pool, log);
                if (totalGhosts > 0) log($"Silinmiş (hayalet) kayıt: {totalGhosts:N0} satır → '..__SILINEN' tablolarında, canlı veriden ayrı.");
                if (identTables > 0) log($"{identTables} tabloda ID kolonu otomatik artan + birincil anahtar (PK) yapıldı → program yeni kayıt girebilir.");
                if (noCol > 0) log($"Verisi olup kolon bilgisi hiçbir yerden bulunamayan {noCol} tablo atlandı" + (rf == null ? " (aynı programın başka dosyası/referans verilirse kurulabilir)." : "."));
                log("Not: 'belirsiz→NULL' kolonların katalog bilgisi şifreli sayfalarda kalmış; yanlış değer yazmamak için boş bırakıldı.");

                string finalDb = db;
                if (rf == null)
                {
                    try { ExportOrphanLobs(c, Path.GetDirectoryName(outBak) ?? ".", Path.GetFileNameWithoutExtension(outBak), pool, log, cs, created, recovered); }
                    catch (Exception ex) { log("Sahipsiz LOB çıkarımı atlandı: " + First(ex.Message)); }
                }
                else
                {
                    log("");
                    log("══════ REFERANS YAPISINA AKTARIM ══════");
                    TransferToRef(refDb, db, recovered, log);
                    finalDb = refDb;
                    // referans eski sürümse: müşterinin kendi fonksiyon/view/prosedürleri ve varsayılan değerleri
                    try { SyncModules(refDb, c, pool, log); } catch (Exception ex) { log("Program nesneleri eşitlenemedi: " + First(ex.Message)); }
                    // sahipsiz belgeler doğrudan referans yapısına (gerçek ID/varsayılanlarla) yazılır
                    try { ExportOrphanLobs(c, Path.GetDirectoryName(outBak) ?? ".", Path.GetFileNameWithoutExtension(outBak), pool, log, Conn(refDb), null, null); }
                    catch (Exception ex) { log("Sahipsiz LOB çıkarımı atlandı: " + First(ex.Message)); }
                }

                // aynı firmanın başka yıla ait SAĞLAM yedeği (ör. ORKA_0015_2024 → ORKA_0015_2026):
                //  hesap planı, cari kartlar, şablonlar, tanımlar gibi yıldan yıla devreden kayıtlardan eksik olanlar + boş/bozuk alanlar
                // aynı firmanın başka yıllara ait verileri (yakın yıldan uzağa; ör. 2025 kurtarılmış .bak, sonra sağlam 2024):
                //  hesap planı, cari kartlar, şablonlar, tanımlar gibi yıldan yıla devreden kayıtlardan eksik olanlar + boş/bozuk alanlar
                foreach (var hp in (helperPath ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).Where(File.Exists))
                {
                    string hDb = "VKM_Yrd_" + Environment.ProcessId + "_" + DateTime.Now.ToString("HHmmssfff"); int kc = KnownCompat; bool hOk = false; string? hTmp = null;
                    try
                    {
                        log(""); log("══════ AYNI FİRMANIN BAŞKA YIL VERİSİNDEN TAMAMLAMA ══════");
                        log("Yardımcı veri: " + Path.GetFileName(hp));
                        hOk = LoadRef(hp, hDb, refWork ?? work, out string herr);
                        if (!hOk && hTmp == null && !(herr.Contains("version") || herr.Contains("sürüm")))
                        {
                            hTmp = Path.Combine(Path.GetTempPath(), "VKM_yrd_" + DateTime.Now.ToString("HHmmssfff"));
                            hOk = LoadRef(hp, hDb, hTmp, out herr);
                        }
                        if (hOk)
                        {
                            FillFromHelper(finalDb, hDb, log);
                            try { CopyModulesFromHelper(finalDb, hDb, log); } catch (Exception ex) { log("Yardımcıdan program nesnesi aktarımı atlandı: " + First(ex.Message)); }
                        }
                        else log("⚠ Yardımcı veri açılamadı (" + herr + ")." + (herr.Contains("version") ? " Daha yeni SQL sürümünden; referans olarak da 2025 şablonu seçilirse program 2025 örneğine geçer ve bu da açılır." : ""));
                    }
                    catch (Exception ex) { log("Yardımcı veriden tamamlama atlandı: " + First(ex.Message)); }
                    finally
                    {
                        KnownCompat = kc;
                        try { ExecSql(Conn("master"), $"IF DB_ID('{hDb}') IS NOT NULL BEGIN ALTER DATABASE [{hDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{hDb}]; END", 120); } catch { }
                        try { if (hTmp != null && Directory.Exists(hTmp)) Directory.Delete(hTmp, true); } catch { }
                    }
                }
                try { RepairPlanRefs(finalDb, Path.GetDirectoryName(outBak) ?? ".", log); } catch (Exception ex) { log("Hesap planı bağlantı onarımı atlandı: " + First(ex.Message)); }
                try { RebuildHeadersFromQuickEntry(finalDb, log); } catch (Exception ex) { log("Hızlı fişten başlık üretimi atlandı: " + First(ex.Message)); }
                try { RebuildHeadersFromOrphanLines(finalDb, log); } catch (Exception ex) { log("Yetim satırlardan başlık bütünleme atlandı: " + First(ex.Message)); }
                try { AssignMissingFisTipi(finalDb, log); } catch (Exception ex) { log("Fiş tipi belirleme atlandı: " + First(ex.Message)); }
                try { RebuildLinesFromQuickEntry(finalDb, log); } catch (Exception ex) { log("Hızlı fişten satır üretimi atlandı: " + First(ex.Message)); }
                try { FillNullDefaults(finalDb, log); } catch (Exception ex) { log("Boş alanlara varsayılan değer atlandı: " + First(ex.Message)); }
                try { FixHeaderTotals(finalDb, log); } catch { }
                try { QualitySummary(finalDb, Path.GetDirectoryName(outBak) ?? ".", Path.GetFileNameWithoutExtension(outBak), log); } catch (Exception ex) { log("Kalite özeti çıkarılamadı: " + First(ex.Message)); }
                try { ReportProblemVouchers(finalDb, Path.GetDirectoryName(outBak) ?? ".", Path.GetFileNameWithoutExtension(outBak), log); } catch (Exception ex) { log("Eksik fiş raporu atlandı: " + First(ex.Message)); }

                // uyumluluk seviyesi: referanssız DB'ler aynı programın diğer DB'lerindeki seviyeye (Orka: 100 = SQL 2008 modu)
                if (rf == null)
                {
                    int cmp = SourceCompat(src); if (cmp == 0) cmp = KnownCompat;
                    if (cmp >= 100)
                        try { ExecSql(cs0, $"ALTER DATABASE [{finalDb}] SET COMPATIBILITY_LEVEL = {cmp};", 120); log($"Uyumluluk seviyesi {cmp} yapıldı (aynı programın diğer veritabanlarıyla aynı mod)."); }
                        catch { }
                }
                LearnFirms(finalDb, Path.GetDirectoryName(outBak) ?? ".", log);
                try { LearnPlanNames(finalDb, Path.GetDirectoryName(outBak) ?? "."); } catch { }
                CheckDb(finalDb, log);
                // .bak içindeki ad = orijinal ad → geri yüklerken ad kendiliğinden gelir
                string orig = OrigDbName(src);
                if (orig.Length > 0 && !orig.Equals(finalDb, StringComparison.OrdinalIgnoreCase))
                    try
                    {
                        ExecSql(cs0, $"IF DB_ID(N'{orig.Replace("'", "''")}') IS NULL BEGIN ALTER DATABASE [{finalDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; ALTER DATABASE [{finalDb}] MODIFY NAME = [{orig.Replace("]", "]]")}]; ALTER DATABASE [{orig.Replace("]", "]]")}] SET MULTI_USER; END", 300);
                        SqlConnection.ClearAllPools();
                        if (Query(cs0, $"SELECT DB_ID(N'{finalDb}');", 60)[0][0] == DBNull.Value)
                        {
                            if (finalDb == db) dbCreated = false; else refCreated = false;
                            renamedTo = finalDb = orig;
                            log($"Veritabanı adı .bak içinde: {orig} (geri yüklerken ad kendiliğinden gelir).");
                        }
                    }
                    catch { try { ExecSql(cs0, $"IF DB_ID(N'{finalDb}') IS NOT NULL ALTER DATABASE [{finalDb}] SET MULTI_USER;", 60); } catch { } }

                log("Yeni veritabanı yedekleniyor → " + outBak);
                ExecSql(cs0, $"BACKUP DATABASE [{finalDb}] TO DISK = N'{Esc(outBak)}' WITH INIT, FORMAT;", 7200);
                MssqlBak.LogCompat(log);
                return true;
            }
            catch (Exception ex)
            {
                err = First(ex.Message);
                if (err.Contains("10240") || err.Contains("licensed limit") || err.Contains("1827") || err.Contains("1105"))
                    err = "LocalDB 10 GB sınırına takıldı. Bu kadar büyük veritabanı için ücretsiz 'SQL Server Developer' kur; program otomatik onu kullanır. (" + err + ")";
                return false;
            }
            finally
            {
                if (dbCreated)
                    try { ExecSql(Conn("master"), $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{db}];", 120); } catch { }
                if (refCreated)
                    try { ExecSql(Conn("master"), $"ALTER DATABASE [{refDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{refDb}];", 120); } catch { }
                if (renamedTo != null)
                    try { ExecSql(Conn("master"), $"ALTER DATABASE [{renamedTo.Replace("]", "]]")}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{renamedTo.Replace("]", "]]")}];", 120); } catch { }
                try { if (Directory.Exists(work)) Directory.Delete(work, true); } catch { }
                try { if (refWork != null && Directory.Exists(refWork)) Directory.Delete(refWork, true); } catch { }
            }
        }

        static string gtn_(string tn) => Safe(tn + "__SILINEN");

        // Tabloyu ID IDENTITY + PK ile yeniden kur (veri korunur). Sadece ID boşsuz ve benzersizse.
        // ── SAHİPSİZ LOB: satırı şifreli sayfada kalmış büyük değerler (resim, ZIP, PDF…) parçalarından birleştirilip dosya olarak çıkarılır.
        //    İç düğümü (ağaç) olanlar kesin sırayla; tek seviyeliler sayfa/slot sırasıyla (test: 22/22 + 7/11 ZIP sağlam).
        static void ExportOrphanLobs(Ctx c, string outDir, string baseName, Pool? pool, Action<string> log,
            string? cs = null, Dictionary<int, (string tn, string orig, Dictionary<int, string> cols, Dictionary<string, string> types)>? created = null,
            Dictionary<string, RecTbl>? recovered = null)
        {
            // satırı kaybolan büyük değeri veritabanına geri koy: tablonun tek "max" kolonu (Orka: DOSYALAR.veritabani_dosya, RESIMLER.resim)
            // orijinal tablo adı → veritabanındaki fiziksel ad (kurtarma DB'sinde 'created', referans DB'sinde gerçek tablolar)
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (created != null) foreach (var v in created.Values) byName.TryAdd(v.orig, v.tn);
            else if (cs != null)
                try { foreach (var tr0 in Query(cs, "SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0 AND s.name='dbo';", 120)) { var tn0 = Convert.ToString(tr0[0]) ?? ""; byName.TryAdd(tn0, tn0); } }
                catch { }
            int inserted = 0, linked = 0;
            LoadFirms(outDir);
            string? insErr = null; int insFail = 0; var trigOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var colInfo = new Dictionary<string, List<(string name, string ty, bool nullable, bool ident, bool hasDef, int ml)>>(StringComparer.OrdinalIgnoreCase);
            List<(string name, string ty, bool nullable, bool ident, bool hasDef, int ml)> ColsOf(string tn)
            {
                if (colInfo.TryGetValue(tn, out var li)) return li;
                li = new();
                try
                {
                    foreach (var r in Query(cs!, $@"SELECT c.name, TYPE_NAME(c.system_type_id), c.is_nullable, c.is_identity, CASE WHEN c.default_object_id<>0 THEN 1 ELSE 0 END, c.max_length
                                                  FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'dbo.[{tn.Replace("'", "''")}]') AND c.is_computed = 0;", 120))
                        li.Add((Convert.ToString(r[0]) ?? "", (Convert.ToString(r[1]) ?? "").ToLowerInvariant(), Convert.ToBoolean(r[2]), Convert.ToBoolean(r[3]), Convert.ToInt32(r[4]) != 0, Convert.ToInt32(r[5])));
                }
                catch { }
                return colInfo[tn] = li;
            }
            bool TryInsert(string tgt, byte[] raw, string fileName, string? firm = null)
            {
                string ptn = byName[tgt];
                var info = ColsOf(ptn);
                bool Big((string name, string ty, bool nullable, bool ident, bool hasDef, int ml) z) => z.ml == -1 || z.ty is "image" or "text" or "ntext";
                var maxCols = info.Where(z => !z.ident && Big(z) && z.name != "__vkm_rn").ToList();
                var bin = maxCols.Where(z => z.ty is "varbinary" or "image").ToList();
                if (bin.Count == 1) maxCols = bin;
                if (maxCols.Count != 1) return false;
                var (mc, mt) = (maxCols[0].name, maxCols[0].ty);
                var colsSql = new List<string> { "[" + mc.Replace("]", "]]") + "]" };
                var vals = new List<string> { "@p0" };
                var pars = new List<object> { mt is "varbinary" or "image" ? raw : (object)Encoding.Unicode.GetString(raw, 0, raw.Length & ~1) };
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { mc };
                void Add(string cn, string expr) { if (!set.Add(cn)) return; colsSql.Add("[" + cn.Replace("]", "]]") + "]"); vals.Add(expr); }
                void AddP(string cn, object v) { if (set.Contains(cn)) return; pars.Add(v); Add(cn, "@p" + (pars.Count - 1)); }
                bool Has(string cn) => info.Any(z => z.name.Equals(cn, StringComparison.OrdinalIgnoreCase) && !z.ident);
                string nm100 = fileName.Length > 90 ? fileName[..90] : fileName;
                if (firm != null && Has("firmakod")) AddP("firmakod", firm);
                if (Has("dosyaadi")) AddP("dosyaadi", fileName.Length > 250 ? fileName[..250] : fileName);
                if (Has("kayitadi")) AddP("kayitadi", "VKM kurtarıldı: " + nm100);
                if (Has("aciklama")) AddP("aciklama", "VKM kurtarıldı: " + nm100);
                foreach (var g in new[] { "kayitdamga", "dosya_kayitdamga" }) if (Has(g)) Add(g, "NEWID()");
                foreach (var d in new[] { "firstdate", "editdate" }) if (Has(d)) Add(d, "GETDATE()");
                foreach (var z in info)   // boş olamayan, varsayılanı olmayan kolonlar → güvenli değer
                {
                    if (z.ident || z.nullable || z.hasDef || set.Contains(z.name) || z.ty == "timestamp") continue;
                    if (z.name.Equals("ID", StringComparison.OrdinalIgnoreCase) || z.name == "__vkm_rn")
                        Add(z.name, $"(SELECT ISNULL(MAX([{z.name}]),0)+1 FROM [dbo].[{ptn.Replace("]", "]]")}] WITH (TABLOCKX))");
                    else Add(z.name, DefLit(z.ty));
                }
                try
                {
                    using var cn0 = new SqlConnection(cs); cn0.Open();
                    if (trigOff.Add(ptn)) try { cn0.Execute($"ALTER TABLE [dbo].[{ptn.Replace("]", "]]")}] DISABLE TRIGGER ALL;"); } catch { }
                    using var cmd = new SqlCommand($"INSERT INTO [dbo].[{ptn.Replace("]", "]]")}] ({string.Join(",", colsSql)}) VALUES ({string.Join(",", vals)});", cn0) { CommandTimeout = 600 };
                    for (int i = 0; i < pars.Count; i++) cmd.Parameters.AddWithValue("@p" + i, pars[i]);
                    cmd.ExecuteNonQuery();
                    inserted++;
                    if (recovered != null && recovered.TryGetValue(tgt, out var rtt)) rtt.Rows++;
                    return true;
                }
                catch (Exception ex) { insFail++; insErr ??= $"{tgt}: {First(ex.Message)}"; return false; }
            }
            // kendi tablosuna: orijinal saklanan hali (raw); DOSYALAR/RESIMLER'e: açılmış dosya (data) — Orka eki dosya adı uzantısıyla açar
            bool InsertBack(string? table, string kind, byte[] raw, byte[] data, string fileName)
            {
                if (cs == null) return false;
                bool doc = kind is "e-Fatura" or "e-İrsaliye" or "xml" or "pdf" or "zip" or "metin" or "doc" or "rtf" or "tif";
                bool img = kind is "jpg" or "png" or "gif" or "bmp";
                // başka (iş) tablolarına satır uydurulmaz — boş fatura/kayıt gibi görünür; yalnızca Orka'nın ek-dosya tabloları
                if (table != null && (table.Equals("DOSYALAR", StringComparison.OrdinalIgnoreCase) || table.Equals("RESIMLER", StringComparison.OrdinalIgnoreCase))
                    && byName.ContainsKey(table) && TryInsert(table, raw, fileName)) return true;
                if (doc && byName.ContainsKey("DOSYALAR") && !"DOSYALAR".Equals(table, StringComparison.OrdinalIgnoreCase))
                {
                    string? firm = kind is "e-Fatura" or "e-İrsaliye" or "xml" ? FirmOf(data) : null;
                    bool okk = TryInsert("DOSYALAR", data, fileName, firm);
                    if (okk && firm != null) linked++;
                    return okk;
                }
                if (img && byName.ContainsKey("RESIMLER") && !"RESIMLER".Equals(table, StringComparison.OrdinalIgnoreCase)) return TryInsert("RESIMLER", data, fileName);
                return false;
            }
            // LOB tahsis birimi → tablo adı (kullanıcı tabloları); sistem tablolarınınkiler atlanır
            var auName = new Dictionary<uint, string>(); var auSys = new HashSet<uint>();
            foreach (var au in c.Aus.Where(a => a.type == 2 || a.type == 3))
            {
                uint ao = (uint)((au.auid >> 16) & 0xFFFFFFFF);
                if (!c.Rowsets.TryGetValue(au.owner, out var rs)) continue;
                if (rs.obj < 100) { auSys.Add(ao); continue; }
                string nm = c.Objs.TryGetValue(rs.obj, out var ob) ? ob.name
                          : pool != null && pool.IdNames.TryGetValue(rs.obj, out var pn) && pn.Count > 0 ? pn.OrderByDescending(x => x.Value).First().Key : "lob_" + rs.obj;
                auName[ao] = nm;
            }
            var used = _lobUsed ?? new HashSet<uint>();
            var meta = new Dictionary<(long pg, int sl), (uint ts, int bt, uint ao, long total, List<(long pg, int sl)>? ch)>();
            var buf = new byte[PAGE];
            for (long no = 0; no < c.PageCount; no++)
            {
                if (!ReadPage(c.Fs, no, buf) || buf[0] != 1 || (buf[1] != 3 && buf[1] != 4)) continue;
                if (c.EnforceId && U32(buf, 0x20) != (uint)no) continue;
                uint ao = U32(buf, 0x18);
                if (auSys.Contains(ao) || (!auName.ContainsKey(ao) && ao < 100)) continue;
                int n = U16(buf, 0x16); if (n <= 0 || n > 1000) continue;
                for (int sl = 0; sl < n; sl++)
                {
                    int r = U16(buf, PAGE - 2 - sl * 2); if (r < 96 || r + 14 > PAGE) continue;
                    int ln = U16(buf, r + 2); if (ln < 14 || r + ln > PAGE) continue;
                    uint ts = U32(buf, r + 6); int bt = U16(buf, r + 12);
                    if (used.Contains(ts)) continue;
                    if (bt == 3) meta[(no, sl)] = (ts, 3, ao, ln - 14, null);
                    else if (bt == 2)
                    {
                        int cur = U16(buf, r + 16); if (cur <= 0 || r + 20 + cur * 16 > PAGE) continue;
                        var ch = new List<(long, int)>(cur); long tot = 0;
                        for (int i = 0; i < cur; i++) { int e = r + 20 + 16 * i; tot = I64(buf, e); ch.Add((U32(buf, e + 8), U16(buf, e + 14))); }
                        meta[(no, sl)] = (ts, 2, ao, tot, ch);
                    }
                }
            }
            if (meta.Count == 0) return;
            var child = new HashSet<(long, int)>();
            foreach (var m in meta.Values) if (m.ch != null) foreach (var k in m.ch) child.Add(k);

            string root = Path.Combine(outDir, baseName + "_LOB");
            int ok = 0, part = 0; long bytes = 0; var kinds = new Dictionary<string, int>();
            var xsltCache = new Dictionary<string, System.Xml.Xsl.XslCompiledTransform?>();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int html = 0;
            void Save(uint ao, uint ts, byte[] raw, bool rawComplete)
            {
                // içerik çözümü: UTF-16/UTF-8 metin → Base64 ise aç → XML (e-Fatura ise numara_tarih adı + HTML görünüm) / metin / ikili
                var cl = Unwrap(raw, rawComplete, xsltCache);
                string dir = Path.Combine(root, Safe(auName.TryGetValue(ao, out var tn) ? tn : "lob_" + ao));
                Directory.CreateDirectory(dir);
                string nm = cl.name != null ? SafeFile(cl.name) : $"{ts:X8}";
                if (!usedNames.Add(Path.Combine(dir, nm))) nm += "_" + ts.ToString("X8");
                if (!cl.complete) nm += "_eksik";
                File.WriteAllBytes(Path.Combine(dir, nm + "." + cl.ext), cl.data);
                if (cl.complete) InsertBack(auName.TryGetValue(ao, out var tbn) ? tbn : null, cl.kind, raw, cl.data, nm + "." + cl.ext);
                if (cl.html != null) { File.WriteAllBytes(Path.Combine(dir, nm + ".html"), cl.html); html++; }
                if (cl.complete) ok++; else part++;
                bytes += cl.data.Length; kinds[cl.kind] = kinds.TryGetValue(cl.kind, out var kc) ? kc + 1 : 1;
            }
            // 1) ağaçlı (iç düğümlü) bloblar: kök = kimsenin çocuğu olmayan iç düğüm
            foreach (var kv in meta.Where(kv => kv.Value.bt == 2 && !child.Contains(kv.Key)))
            {
                using var ms = new MemoryStream();
                bool full = c.LobNodeP(kv.Key.pg, kv.Key.sl, kv.Value.ts, ms);
                if (ms.Length > 0) Save(kv.Value.ao, kv.Value.ts, ms.ToArray(), full && ms.Length == kv.Value.total);
            }
            // 2) tek seviyeli: kimsenin çocuğu olmayan veri parçaları, aynı blob kimliği → sayfa/slot sırasıyla
            foreach (var g in meta.Where(kv => kv.Value.bt == 3 && !child.Contains(kv.Key)).GroupBy(kv => (kv.Value.ao, kv.Value.ts)))
            {
                using var ms = new MemoryStream();
                foreach (var kv in g.OrderBy(x => x.Key.pg).ThenBy(x => x.Key.sl))
                {
                    var b = c.LobPageP(kv.Key.pg); if (b == null) continue;
                    int r = U16(b, PAGE - 2 - kv.Key.sl * 2); int ln = U16(b, r + 2);
                    ms.Write(b, r + 14, ln - 14);
                    if (ms.Length > 512L * 1048576) break;
                }
                if (ms.Length > 0) { var data = ms.ToArray(); Save(g.Key.ao, g.Key.ts, data, LooksComplete(data)); }
            }
            foreach (var t in trigOff)
                try { ExecSql(cs!, $"ALTER TABLE [dbo].[{t.Replace("]", "]]")}] ENABLE TRIGGER ALL; IF OBJECTPROPERTY(OBJECT_ID(N'dbo.[{t.Replace("'", "''").Replace("]", "]]")}]'),'TableHasIdentity')=1 DBCC CHECKIDENT(N'dbo.[{t.Replace("'", "''").Replace("]", "]]")}]') WITH NO_INFOMSGS;", 300); } catch { }
            if (ok + part == 0) return;
            log($"Sahipsiz büyük veri (satırı şifreli sayfada kalan resim/belge/ZIP): {ok + part:N0} dosya · {bytes / 1048576.0:N1} MB → {root}");
            log($"   türler: {string.Join(", ", kinds.OrderByDescending(k => k.Value).Select(k => k.Key + " " + k.Value))}" + (part > 0 ? $" · {part} tanesi eksik/şüpheli ('_eksik' adlı)" : ""));
            if (insFail > 0) log($"   ⚠ {insFail:N0} belge veritabanına yazılamadı (ilk hata: {insErr}) — dosya olarak klasörde duruyor.");
            if (inserted > 0) log($"   {inserted:N0} belge/resim veritabanına geri yazıldı (Orka: DOSYALAR / RESIMLER) — kayıt bağlantıları şifreli satırlarla gittiği için 'VKM kurtarıldı' adıyla.");
            if (linked > 0) log($"   {linked:N0} belge, faturadaki vergi/TC numarasından kendi firmasına bağlandı (DOSYALAR.firmakod = ORKA_xxxx_yyyy).");
            else if (inserted > 0 && FirmMap.Count == 0) log("   Belgeler firmaya bağlanamadı: firma listesi (ORKAGENELTANIM) bu çalıştırmada yok — ORKAGENELTANIM ile birlikte kurtarılınca bağlanır.");
            if (html > 0) log($"   {html:N0} e-Fatura/e-Arşiv belgesi kendi görüntüleme şablonuyla HTML'e çevrildi → tarayıcıda açılır (dosya adı: fatura no_tarih).");
        }

        static string SafeFile(string n)
        {
            foreach (var ch in Path.GetInvalidFileNameChars()) n = n.Replace(ch, '_');
            n = n.Trim(); return n.Length == 0 ? "x" : (n.Length > 120 ? n.Substring(0, 120) : n);
        }

        // Büyük değerin içeriğini çöz: Luca e-Fatura'yı nvarchar(max) içinde UTF-16 + Base64 saklıyor (ORKARESIMLER).
        static (byte[] data, string ext, string kind, string? name, bool complete, byte[]? html) Unwrap(byte[] v, bool rawComplete,
            Dictionary<string, System.Xml.Xsl.XslCompiledTransform?> xsltCache, int depth = 0)
        {
            string? text = null;
            int n = Math.Min(v.Length, 4096);
            if (v.Length >= 4)
            {
                bool bom16 = v[0] == 0xFF && v[1] == 0xFE;
                int zeros = 0; for (int i = 1; i < n; i += 2) if (v[i] <= 2) zeros++;   // UTF-16: Türkçe ı ğ ş = 0x01 üst bayt
                if (bom16 || zeros >= (n / 2) * 0.9) text = Encoding.Unicode.GetString(v, bom16 ? 2 : 0, (v.Length - (bom16 ? 2 : 0)) & ~1);
                else
                {
                    int pr = 0; for (int i = 0; i < n; i++) { byte b = v[i]; if (b == 9 || b == 10 || b == 13 || (b >= 32 && b < 127) || b >= 0xC2) pr++; }
                    if (pr >= n * 0.97) text = new UTF8Encoding(false).GetString(v).TrimStart('\uFEFF');
                }
            }
            if (text == null)
            {
                string e = Ext(v);
                return (v, e, e, null, rawComplete && LooksComplete(v), null);
            }
            // Base64 mi?
            if (depth < 2)
            {
                var sb = new StringBuilder(text.Length); int b64 = 0, tot = 0;
                foreach (char ch in text) { if (char.IsWhiteSpace(ch)) continue; tot++; if (char.IsLetterOrDigit(ch) && ch < 128 || ch == '+' || ch == '/' || ch == '=') { b64++; sb.Append(ch); } }
                if (tot >= 64 && b64 >= tot * 0.98)
                {
                    int len = sb.Length / 4 * 4;
                    try
                    {
                        var inner = Convert.FromBase64String(sb.ToString(0, len).TrimEnd('=').PadRight(len, '='));
                        var r = Unwrap(inner, rawComplete && len == sb.Length, xsltCache, depth + 1);
                        return r;
                    }
                    catch { }
                }
            }
            string t = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
            if (t.StartsWith("<"))
            {
                var bytes = new UTF8Encoding(true).GetBytes(t);
                try
                {
                    var doc = new System.Xml.XmlDocument { XmlResolver = null };
                    using (var xr = System.Xml.XmlReader.Create(new StringReader(t), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null }))
                        doc.Load(xr);
                    var root = doc.DocumentElement!;
                    string ln = root.LocalName;
                    if (ln == "Invoice" || ln == "CreditNote" || ln == "DespatchAdvice" || ln == "ApplicationResponse" || ln == "ReceiptAdvice")
                    {
                        string? id = root.ChildNodes.OfType<System.Xml.XmlElement>().FirstOrDefault(e => e.LocalName == "ID")?.InnerText;
                        string? dt = root.ChildNodes.OfType<System.Xml.XmlElement>().FirstOrDefault(e => e.LocalName == "IssueDate")?.InnerText;
                        byte[]? htmlOut = null;
                        var emb = doc.GetElementsByTagName("*").OfType<System.Xml.XmlElement>().FirstOrDefault(e => e.LocalName == "EmbeddedDocumentBinaryObject" && e.InnerText.Length > 100);
                        if (emb != null)
                            try
                            {
                                var xsl = Convert.FromBase64String(emb.InnerText.Trim());
                                string key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(xsl));
                                if (!xsltCache.TryGetValue(key, out var tr))
                                {
                                    tr = null;
                                    try
                                    {
                                        var x = new System.Xml.Xsl.XslCompiledTransform();
                                        using var sr = System.Xml.XmlReader.Create(new MemoryStream(xsl), new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
                                        x.Load(sr, new System.Xml.Xsl.XsltSettings(false, false), null);
                                        tr = x;
                                    }
                                    catch { }
                                    if (xsltCache.Count > 200) xsltCache.Clear();
                                    xsltCache[key] = tr;
                                }
                                if (tr != null)
                                {
                                    using var ms = new MemoryStream();
                                    tr.Transform(doc, null, ms);
                                    htmlOut = ms.ToArray();
                                }
                            }
                            catch { htmlOut = null; }
                        string kind = ln == "Invoice" ? "e-Fatura" : ln == "DespatchAdvice" ? "e-İrsaliye" : ln;
                        string? nm = id != null ? id + (dt != null ? "_" + dt : "") : null;
                        return (bytes, "xml", kind, nm, true, htmlOut);
                    }
                    return (bytes, "xml", "xml", null, true, null);
                }
                catch { return (bytes, "xml", "xml", null, false, null); }
            }
            return (new UTF8Encoding(true).GetBytes(text), "txt", "metin", null, rawComplete, null);
        }

        static string Ext(byte[] v)
        {
            bool S(params byte[] sig) => v.Length >= sig.Length && sig.Select((x, i) => v[i] == x).All(z => z);
            if (S(0xDF, 0xFF, 0x01)) return "sqlxml";   // SQL Server'ın ikili XML biçimi (xml kolonu)
            if (S(0xFF, 0xD8, 0xFF)) return "jpg";
            if (S(0x89, 0x50, 0x4E, 0x47)) return "png";
            if (S(0x25, 0x50, 0x44, 0x46)) return "pdf";
            if (S(0x50, 0x4B, 0x03, 0x04)) return "zip";
            if (S(0x47, 0x49, 0x46, 0x38)) return "gif";
            if (S(0x42, 0x4D)) return "bmp";
            if (S(0x49, 0x49, 0x2A, 0x00) || S(0x4D, 0x4D, 0x00, 0x2A)) return "tif";
            if (S(0xD0, 0xCF, 0x11, 0xE0)) return "doc";
            if (S(0x7B, 0x5C, 0x72, 0x74)) return "rtf";
            if (S(0x3C, 0x3F, 0x78, 0x6D) || S(0xEF, 0xBB, 0xBF, 0x3C) || S(0xFF, 0xFE, 0x3C, 0x00)) return "xml";
            return "bin";
        }

        static bool LooksComplete(byte[] v)
        {
            string e = Ext(v);
            int n = v.Length; while (n > 0 && v[n - 1] == 0) n--;
            if (e == "jpg") return n >= 2 && v[n - 2] == 0xFF && v[n - 1] == 0xD9;
            if (e == "png") return n >= 8 && Encoding.ASCII.GetString(v, Math.Max(0, n - 12), Math.Min(12, n)).Contains("IEND");
            if (e == "pdf") return Encoding.ASCII.GetString(v, Math.Max(0, n - 1024), Math.Min(1024, n)).Contains("%%EOF");
            if (e == "zip") { for (int i = Math.Max(0, n - 70000); i + 4 <= n; i++) if (v[i] == 0x50 && v[i + 1] == 0x4B && v[i + 2] == 5 && v[i + 3] == 6) return true; return false; }
            return e != "bin";
        }

        // ── Aynı firmanın sağlam yedeğinden tamamlama.
        //    Bir tablo "yıldan yıla devreden" sayılır: kurtarılan satırların ID'leri yedekte de var ve metin alanları aynı (aynı kayıt)
        //    ya da tablo kurtarmada boş kaldı ve adı tanım/kart/plan/şablon tablosu. Fiş/hareket tabloları ASLA doldurulmaz.
        static readonly Regex MasterRx = new(@"^(GRP_|ICE_CARI|ORT_PLAN$|ORT_FISEXCELSABLON$|ORT_KAYITSBL|ORT_MUHTABLO|ORT_MUHTSODE|ORT_OZELLIK|ORT_BELGE|ORT_(GRUP)?KEBIR|ORT_KDVTABLO$|ORT_\w*TANIM$|BIL_(BABSTANIM|KDVHESKOD|YANSITMA|MALIYET\w*|STOKKART)$|STK_(STOKKART|RENKLER|BOYUTLAR|STOKFIYATGRUP|INDIRIM\w*)$|PUB_(PERSONPROFIL|MESAITANIM|VARDIYA\w*|KNT\w*|PERSONEL\w*|ISEGIRISCIKIS|KIMLIKLER|ADRESLER|YAKINLARI|OGRENIMDURUMU|OZLUKDOSYASI|HESPLAN|CALISILANYERLER|\w*GELIRTURLERI|SENDIKA\w*|TESVIKTANIM|CALISMADUZENI\w*|CALISMAPLANLARI|CALISMAHAREKETTIPI|GRUP\w*|ZIMMETKART|MESAIHESAPLARI|PDKSGRUPLAR|EGTPLAN|ICRALAR|GELVERTABUCRET|SGKTABUCRET)$)", RegexOptions.IgnoreCase);
        static readonly Regex TranRx = new(@"(FISBASLIK|FISSATIR|EXCELSATIR|TRAN|HAREKET|BEYAN|EFATURA|EDEFTER|EYEVMIYE|DOSYA|LOG$|_LOG|MESAILER|IZINLER|BORDRO|PUANTAJ|SAYIM|ENVANTER|NOTLAR)", RegexOptions.IgnoreCase);
        // Kurtarmada katalog bozulunca kolon ADININ baytları garbellenebilir (ör. 'hesapkodu' → 'hesapūodu', 'imgidx' → 'imgifx').
        // Kolon ve VERİ yerinde ama adı yanlış → Orka "Field 'hesapkodu' not found" der, fişin içi boş görünür.
        // Düzeltme: sağlam referansla (2024) AYNI POZİSYON + AYNI TİP'teki kolonun bozuk adını doğru adıyla değiştir (sp_rename) — veri korunur.
        static void FixCorruptColumnNames(string R, string H, Action<string> log)
        {
            Dictionary<string, List<(int cid, string name, int type)>> Load(string db)
            {
                var res = new Dictionary<string, List<(int, string, int)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in Query(Conn(db), "SELECT t.name, c.column_id, c.name, c.system_type_id FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id WHERE t.is_ms_shipped=0 AND SCHEMA_NAME(t.schema_id)='dbo' ORDER BY t.name, c.column_id;", 300))
                {
                    string tn = Convert.ToString(r[0]) ?? "";
                    if (!res.TryGetValue(tn, out var l)) res[tn] = l = new();
                    l.Add((Convert.ToInt32(r[1]), Convert.ToString(r[2]) ?? "", Convert.ToInt32(r[3])));
                }
                return res;
            }
            bool Corrupt(string s) => s.Length == 0 || s.Any(ch => !((char.IsLetterOrDigit(ch) && ch < 128) || ch == '_'));
            string cs = Conn(R);
            var rT = Load(R); var hT = Load(H);
            int renamed = 0; var tset = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var ex0 = new List<string>();
            foreach (var kv in hT)
            {
                if (!rT.TryGetValue(kv.Key, out var rc)) continue;
                var hc = kv.Value;
                var hNames = new HashSet<string>(hc.Select(x => x.name), StringComparer.OrdinalIgnoreCase);
                var rNames = new HashSet<string>(rc.Select(x => x.name), StringComparer.OrdinalIgnoreCase);
                bool sameCount = rc.Count == hc.Count;
                int m = Math.Min(hc.Count, rc.Count);
                for (int i = 0; i < m; i++)
                {
                    string rn = rc[i].name, hn = hc[i].name;
                    if (rn.Equals(hn, StringComparison.OrdinalIgnoreCase) || rc[i].type != hc[i].type) continue;
                    if (hNames.Contains(rn) || rNames.Contains(hn)) continue;      // gerçek/farklı kolon → dokunma
                    bool similar = rn.Length == hn.Length && rn.Where((ch, k) => ch != hn[k]).Count() is >= 1 and <= 2;  // natrah_03≈matrah_03
                    if (!(sameCount || Corrupt(rn) || similar)) continue;           // güvenli hizalama / adı bozuk / referansa çok benzer
                    if (RobustRenameColumn(cs, kv.Key, rn, hn, log)) { renamed++; tset.Add(kv.Key); rNames.Remove(rn); rNames.Add(hn); if (ex0.Count < 4) ex0.Add($"{rn}→{hn}"); }
                }
            }
            if (renamed > 0) log($"Bozuk kolon ADI düzeltildi: {renamed} kolon ({tset.Count} tabloda) referanstaki doğru adıyla değiştirildi" + (ex0.Count > 0 ? " (ör. " + string.Join(", ", ex0) + ")" : "") + " — veri korundu, Orka 'Field not found' hataları biter.");
        }

        // Bir kolonu güvenle yeniden adlandırır. sp_rename DENER + SONUCU DOĞRULAR; olmadıysa ekle+kopyala+sil'e düşer.
        // sp_rename bazı bozuk adlarda sessizce başarısız oluyor; bu yüzden doğrulama + yedek yol şart.
        static bool RobustRenameColumn(string cs, string tbl, string from, string to, Action<string> log)
        {
            string Br(string s) => "[" + s.Replace("]", "]]") + "]";
            string Esc2(string s) => s.Replace("'", "''");
            int Exists(string col) { try { return Convert.ToInt32(Query(cs, $"SELECT COUNT(*) FROM sys.columns WHERE object_id=OBJECT_ID('dbo.{Esc2(tbl)}') AND name=N'{Esc2(col)}';", 60)[0][0]); } catch { return -1; } }
            if (Exists(to) > 0) return true;                       // hedef zaten var
            // 1) sp_rename dene + DOĞRULA
            try
            {
                ExecSql(cs, $"EXEC sp_rename N'[dbo].{Br(tbl)}.{Br(from)}', N'{Esc2(to)}', 'COLUMN';", 60);
                if (Exists(to) > 0) return true;                   // gerçekten değişti
            }
            catch (Exception e) { log($"  sp_rename {tbl}.{from}→{to} hata: {First(e.Message)}"); }
            // 2) ekle + kopyala + bozuğu düşür (bozuk karakterli adı DML'de köşeli parantez güvenle taşır)
            try
            {
                var tr = Query(cs, $"SELECT TYPE_NAME(system_type_id), max_length, precision, scale FROM sys.columns WHERE object_id=OBJECT_ID('dbo.{Esc2(tbl)}') AND name=N'{Esc2(from)}';", 60);
                if (tr.Count == 0) { log($"  {tbl}.{from}: kaynak kolon bulunamadı"); return false; }
                string ty = (Convert.ToString(tr[0][0]) ?? "").ToLowerInvariant(); int ml = Convert.ToInt32(tr[0][1]), pr = Convert.ToInt32(tr[0][2]), sc = Convert.ToInt32(tr[0][3]);
                string ts = ty switch
                {
                    "nvarchar" or "nchar" => ml == -1 ? ty + "(max)" : $"{ty}({ml / 2})",
                    "varchar" or "char" or "varbinary" or "binary" => ml == -1 ? ty + "(max)" : $"{ty}({ml})",
                    "decimal" or "numeric" => $"{ty}({pr},{sc})",
                    "datetime2" or "time" or "datetimeoffset" => $"{ty}({sc})",
                    _ => ty
                };
                ExecSql(cs, $"ALTER TABLE dbo.{Br(tbl)} ADD {Br(to)} {ts} NULL;", 120);
                ExecSql(cs, $"UPDATE dbo.{Br(tbl)} SET {Br(to)}={Br(from)};", 600);
                try { ExecSql(cs, $"ALTER TABLE dbo.{Br(tbl)} DROP COLUMN {Br(from)};", 120); } catch { }   // indeksli/kısıtlıysa kalsın, zararsız
                if (Exists(to) > 0) { log($"  ✓ ekle+kopyala: {tbl}.{from} → {to} ({ts})"); return true; }
                return false;
            }
            catch (Exception e) { log($"  {tbl}.{from}→{to} ekle+kopyala HATA: {First(e.Message)}"); return false; }
        }

        // Orka'nın ekran sorgularının adıyla aradığı KRİTİK kolonlar — bunların adı bozuksa (ör. hesapūodu) ekran boş gelir.
        // Referans OLMADAN da düzeltilmeli: doğru adları gömülü; bozuk eşleşen tek kolonu düzeltir. Tüm Orka firmalarında şema aynı.
        static readonly Dictionary<string, string[]> KnownCriticalCols = new(StringComparer.OrdinalIgnoreCase)
        {
            ["ORT_FISSATIR"] = new[] { "hesapkodu", "karsihesapkodu", "hesapkebirkodu", "fisaciklamasi", "fisbelgetarihi", "tlborctutar", "tlalactutar", "borcalacak", "kayitsirano", "defterbeyankodu", "kdvhesapkodu" },
            ["ORT_FISBASLIK"] = new[] { "fisno", "fistarihi", "fisaciklama", "fistipi", "fistutar", "fisyevmiyeno" },
            ["ORT_PLAN"] = new[] { "plankod", "planadi", "kebirkod" },
            ["ORT_FISEXCELSATIR"] = new[] { "matrah_01","matrah_02","matrah_03","matrah_04","matrah_05","matrah_06","matrah_07","kdvoran_01","kdvoran_02","kdvoran_03","kdvoran_04","kdvoran_05","kdvoran_06","kdvoran_07","kdvttr_01","kdvttr_02","kdvttr_03","kdvttr_04","kdvttr_05","kdvttr_06","kdvttr_07" },
        };
        static void FixKnownCorruptColumns(string R, Action<string> log)
        {
            string cs = Conn(R);
            bool CorruptCh(char c) => !(c < 128 && (char.IsLetterOrDigit(c) || c == '_'));
            int total = 0; var done = new List<string>();
            foreach (var kv in KnownCriticalCols)
            {
                string tbl = kv.Key;
                List<object[]> rows;
                try { rows = Query(cs, $"SELECT c.name FROM sys.columns c WHERE c.object_id=OBJECT_ID('dbo.{tbl}');", 60); }
                catch (Exception e) { log($"TANI · {tbl} kolonları okunamadı: {First(e.Message)}"); continue; }
                if (rows.Count == 0) continue;
                var actual = rows.Select(r => Convert.ToString(r[0]) ?? "").Where(s => s.Length > 0).ToList();
                var actualSet = new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase);
                foreach (string want in kv.Value)
                {
                    if (actualSet.Contains(want)) continue;                 // zaten doğru
                    // bozuk eşleşen: aynı uzunluk, her pozisyonda ya aynı ya bozuk-karakter, en az 1 bozuk karakter
                    var cand = actual.Where(a => a.Length == want.Length
                                 && Enumerable.Range(0, want.Length).All(i => a[i] == want[i] || CorruptCh(a[i]))
                                 && Enumerable.Range(0, want.Length).Any(i => a[i] != want[i])).Distinct().ToList();
                    if (cand.Count == 0) continue;
                    if (cand.Count > 1) { log($"TANI · {tbl}.{want}: birden çok bozuk aday ({string.Join("/", cand)}) — atlandı"); continue; }
                    string from = cand[0];
                    log($"Kritik kolon: {tbl}.{from} → {want} onarılıyor…");
                    if (RobustRenameColumn(cs, tbl, from, want, log)) { actualSet.Remove(from); actualSet.Add(want); total++; done.Add($"{from}→{want}"); }
                }
            }
            if (total > 0) log($"Kritik kolon onarımı: {total} kolon düzeltildi (" + string.Join(", ", done.Take(6)) + ") — Orka fiş düzeltme ekranı artık dolu gelmeli.");
            else log("Kritik kolon onarımı: bu veride düzeltilecek bozuk kritik kolon ADI bulunamadı.");
        }

        // Fiş satırı → hesap planı KÖPRÜSÜ. Orka'nın fiş DÜZELTME ekranı satırı IDHesapkodu ile plana bağlar;
        // IDHesapkodu boş/geçersizse satır ekranda GÖRÜNMEZ (fiş no dolu, içi boş). Ayrıca gerçek kolon adlarını loga yazar (TANI).
        static void RepairLinePlanLinks(string R, Action<string> log)
        {
            string cs = Conn(R);
            var h = Query(cs, "SELECT OBJECT_ID('dbo.ORT_FISSATIR'), OBJECT_ID('dbo.ORT_PLAN');", 60)[0];
            if (h[0] == DBNull.Value || h[1] == DBNull.Value) return;
            // TANI: gerçek SQL tarafında hesap kolon adları (hesapkodu mu, hesapūodu mu — kesin görelim)
            string nameCol = "hesapkodu";
            try
            {
                var hcols = Query(cs, "SELECT c.name FROM sys.columns c WHERE c.object_id=OBJECT_ID('dbo.ORT_FISSATIR') AND c.name LIKE N'%hesap%' ORDER BY c.column_id;", 60)
                            .Select(r => Convert.ToString(r[0]) ?? "").ToList();
                log("TANI · ORT_FISSATIR hesap kolonları: " + string.Join(", ", hcols));
                // 'hesapkodu' yoksa ama ona çok benzer bozuk ad varsa onu kullan (ör. hesapūodu) — en azından köprüyü kurabilelim
                if (!hcols.Any(c => c.Equals("hesapkodu", StringComparison.OrdinalIgnoreCase)))
                {
                    var alt = hcols.FirstOrDefault(c => c.Length == 9 && c.StartsWith("hesap", StringComparison.OrdinalIgnoreCase) && c.EndsWith("odu", StringComparison.OrdinalIgnoreCase));
                    if (alt != null) { nameCol = alt; log($"⚠ 'hesapkodu' kolonu yok, bozuk ad '{alt}' kullanılıyor — kolon adı onarımı neden çalışmadı, araştırılmalı."); }
                }
            }
            catch { }
            string NC = "[" + nameCol.Replace("]", "]]") + "]";
            int linked = 0, broken = 0;
            try
            {
                linked = Convert.ToInt32(Query(cs, $@"DISABLE TRIGGER ALL ON dbo.ORT_FISSATIR;
                    UPDATE s SET s.IDHesapkodu=p.ID
                    FROM dbo.ORT_FISSATIR s JOIN dbo.ORT_PLAN p ON LTRIM(RTRIM(p.plankod))=LTRIM(RTRIM(s.{NC}))
                    WHERE LTRIM(ISNULL(s.{NC},N''))<>N'' AND (s.IDHesapkodu IS NULL OR s.IDHesapkodu=0 OR NOT EXISTS(SELECT 1 FROM dbo.ORT_PLAN q WHERE q.ID=s.IDHesapkodu));
                    SELECT @@ROWCOUNT; ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;", 600)[0][0]);
            }
            catch (Exception ex) { try { ExecSql(cs, "ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;", 60); } catch { } log("Köprü onarımı hatası: " + First(ex.Message)); }
            try { broken = Convert.ToInt32(Query(cs, $"SELECT COUNT(*) FROM dbo.ORT_FISSATIR s WHERE LTRIM(ISNULL(s.{NC},N''))<>N'' AND (s.IDHesapkodu IS NULL OR s.IDHesapkodu=0 OR NOT EXISTS(SELECT 1 FROM dbo.ORT_PLAN q WHERE q.ID=s.IDHesapkodu));", 300)[0][0]); } catch { }
            log($"Fiş satırı → plan köprüsü: {linked} satırın hesap bağlantısı (IDHesapkodu) onarıldı" + (broken > 0 ? $" · {broken} satır hâlâ bağlanamadı (plan kodu eksik)" : " · tüm satırlar plana bağlı") + ".");
            // TANI: fiş DÜZELTME ekranı neden boş? Orka'nın sorgusundaki hangi join satırları düşürüyor — örnek fişlerle ölç.
            try
            {
                long Cnt(string sql) { try { return Convert.ToInt64(Query(cs, sql, 120)[0][0]); } catch (Exception e) { return -99; } }
                var ids = new List<long>();
                try { if (Convert.ToInt32(Query(cs, "SELECT COUNT(*) FROM dbo.ORT_FISBASLIK WHERE ID=7149;", 60)[0][0]) > 0) ids.Add(7149); } catch { }
                foreach (var r in Query(cs, "SELECT TOP 2 b.ID FROM dbo.ORT_FISBASLIK b WHERE EXISTS(SELECT 1 FROM dbo.ORT_FISSATIR s WHERE s.IDFisbaslik=b.ID) ORDER BY b.ID DESC;", 60))
                { long id = Convert.ToInt64(r[0]); if (!ids.Contains(id)) ids.Add(id); }
                foreach (long id in ids.Take(3))
                {
                    long c0 = Cnt($"SELECT COUNT_BIG(*) FROM dbo.ORT_FISSATIR WHERE IDFisbaslik={id};");
                    long c1 = Cnt($"SELECT COUNT_BIG(*) FROM dbo.ORT_FISSATIR s JOIN dbo.ORT_PLAN p ON p.ID=s.IDHesapkodu WHERE s.IDFisbaslik={id};");
                    long c2 = Cnt($"SELECT COUNT_BIG(*) FROM dbo.ORT_FISSATIR s JOIN dbo.ORT_PLAN p ON LTRIM(RTRIM(p.plankod))=LTRIM(RTRIM(s.{NC})) WHERE s.IDFisbaslik={id};");
                    long c3 = Cnt($"SELECT COUNT_BIG(*) FROM dbo.ORT_FISSATIR s JOIN dbo.ORT_FISBASLIK b ON b.ID=s.IDFisbaslik WHERE s.IDFisbaslik={id};");
                    log($"TANI fiş ID {id}: düz satır={c0} · plan(IDHesapkodu-join)={c1} · plan(hesapkodu-join)={c2} · başlık-join={c3}");
                }
            }
            catch (Exception ex) { log("Fiş düzeltme tanısı atlandı: " + First(ex.Message)); }
        }

        // Kurtarmada katalog sayfası şifreli kalınca bazı KOLONLAR hiç oluşturulamaz (ör. ORT_FISSATIR.hesapkodu).
        // Sağlam referansta (2024) olup kurtarılanda olmayan kolonları ekler → Orka'nın "Field 'x' not found" hatası biter.
        // Eklenen kolonlar NULL gelir; değerleri sonraki adımlar doldurur (hesapkodu → RepairPlanRefs plan bağlantısından).
        static void AddMissingColumnsFromHelper(string R, string H, Action<string> log)
        {
            string cs = Conn(R), hcs = Conn(H);
            string Q(string x) => "[" + x.Replace("]", "]]") + "]";
            var rCols = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Query(cs, "SELECT t.name, c.name FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id WHERE t.is_ms_shipped=0 AND SCHEMA_NAME(t.schema_id)='dbo';", 300))
            {
                string tn = Convert.ToString(r[0]) ?? "";
                if (!rCols.TryGetValue(tn, out var s)) rCols[tn] = s = new(StringComparer.OrdinalIgnoreCase);
                s.Add(Convert.ToString(r[1]) ?? "");
            }
            int added = 0; var tset = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Query(hcs, "SELECT t.name, c.name, TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, c.is_computed FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id WHERE t.is_ms_shipped=0 AND SCHEMA_NAME(t.schema_id)='dbo' ORDER BY t.name, c.column_id;", 300))
            {
                string tn = Convert.ToString(r[0]) ?? "", cn = Convert.ToString(r[1]) ?? "", ty = (Convert.ToString(r[2]) ?? "").ToLowerInvariant();
                int ml = Convert.ToInt32(r[3]), pr = Convert.ToInt32(r[4]), sc = Convert.ToInt32(r[5]); bool comp = Convert.ToBoolean(r[6]);
                if (comp || ty == "timestamp" || ty == "rowversion" || ty.Length == 0) continue;
                if (!rCols.TryGetValue(tn, out var have) || have.Contains(cn)) continue;
                string ts = ty switch
                {
                    "nvarchar" or "nchar" => ml == -1 ? ty + "(max)" : $"{ty}({ml / 2})",
                    "varchar" or "char" or "varbinary" or "binary" => ml == -1 ? ty + "(max)" : $"{ty}({ml})",
                    "decimal" or "numeric" => $"{ty}({pr},{sc})",
                    "datetime2" or "time" or "datetimeoffset" => $"{ty}({sc})",
                    _ => ty
                };
                try { ExecSql(cs, $"ALTER TABLE dbo.{Q(tn)} ADD {Q(cn)} {ts} NULL;", 120); added++; have.Add(cn); tset.Add(tn); }
                catch { }
            }
            if (added > 0) log($"Referanstan {added} eksik KOLON eklendi ({tset.Count} tabloya; şema tamamlandı → Orka 'Field not found' hataları giderilir).");
        }

        static void FillFromHelper(string R, string H, Action<string> log)
        {
            string cs = Conn(R);
            try { FixCorruptColumnNames(R, H, log); } catch (Exception ex) { log("Bozuk kolon adı düzeltme atlandı: " + First(ex.Message)); }
            try { AddMissingColumnsFromHelper(R, H, log); } catch (Exception ex) { log("Eksik kolon ekleme atlandı: " + First(ex.Message)); }
            string Q(string x) => "[" + x.Replace("]", "]]") + "]";
            string HQ = Q(H);
            bool IsText(int t) => t == 231 || t == 239 || t == 167 || t == 175;
            Dictionary<string, List<(string name, int type, bool ident, bool comp)>> Cols(string db)
            {
                var res = new Dictionary<string, List<(string, int, bool, bool)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in Query(Conn(db), "SELECT t.name, c.name, c.system_type_id, c.is_identity, c.is_computed FROM sys.tables t JOIN sys.columns c ON c.object_id=t.object_id WHERE t.is_ms_shipped=0 AND SCHEMA_NAME(t.schema_id)='dbo' ORDER BY t.name, c.column_id;", 300))
                {
                    string tn = Convert.ToString(r[0]) ?? "";
                    if (!res.TryGetValue(tn, out var l)) res[tn] = l = new();
                    l.Add((Convert.ToString(r[1]) ?? "", Convert.ToInt32(r[2]), Convert.ToBoolean(r[3]), Convert.ToBoolean(r[4])));
                }
                return res;
            }
            var rC = Cols(R); var hC = Cols(H);
            var uniq = new Dictionary<string, List<List<string>>>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Query(cs, @"SELECT t.name, i.index_id, c.name FROM sys.indexes i JOIN sys.tables t ON t.object_id=i.object_id
                    JOIN sys.index_columns ic ON ic.object_id=i.object_id AND ic.index_id=i.index_id AND ic.is_included_column=0
                    JOIN sys.columns c ON c.object_id=ic.object_id AND c.column_id=ic.column_id
                    WHERE i.is_unique=1 AND t.is_ms_shipped=0 ORDER BY t.name, i.index_id, ic.key_ordinal;", 300))
            {
                string tn = Convert.ToString(r[0]) ?? ""; int ix = Convert.ToInt32(r[1]);
                if (!uniq.TryGetValue(tn, out var l)) uniq[tn] = l = new();
                if (l.Count < ix) while (l.Count < ix) l.Add(new());
                l[ix - 1].Add(Convert.ToString(r[2]) ?? "");
            }
            var hTabs = Query(Conn(H), "SELECT t.name, SUM(p.rows) FROM sys.tables t JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1) WHERE t.is_ms_shipped=0 AND SCHEMA_NAME(t.schema_id)='dbo' GROUP BY t.name HAVING SUM(p.rows)>0;", 300)
                        .Select(r => Convert.ToString(r[0]) ?? "").ToList();
            int tabs = 0; long addAll = 0, fixAll = 0; var lines = new List<string>(); var skipped = new List<string>();
            foreach (var t in hTabs.OrderBy(x => x))
            {
                if (TranRx.IsMatch(t) && !t.EndsWith("SABLON", StringComparison.OrdinalIgnoreCase)) continue;
                if (!rC.TryGetValue(t, out var rc) || !hC.TryGetValue(t, out var hc)) continue;
                var hset = hc.Where(x => x.type != 189 && !x.comp).Select(x => x.name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var common = rc.Where(x => !x.comp && x.type != 189 && hset.Contains(x.name)).ToList();
                if (!common.Any(x => x.name.Equals("ID", StringComparison.OrdinalIgnoreCase))) continue;
                var txt = common.Where(x => IsText(x.type) && !Regex.IsMatch(x.name, "user|damga|date|tarih|aciklama|not", RegexOptions.IgnoreCase)).Select(x => x.name).Take(3).ToList();
                // İKİ DB farklı collation olabilir (kurtarılan Turkish_CI_AS, 2024 yedeği Turkish_CS_AS_WS) → her metin karşılaştırmasına COLLATE DATABASE_DEFAULT şart, yoksa "collation conflict" ve tamamlama hiç çalışmaz.
                string sameCond = txt.Count > 0 ? string.Join(" AND ", txt.Select(n => $"ISNULL(CAST(r.{Q(n)} AS nvarchar(4000)),N'')=ISNULL(CAST(h.{Q(n)} AS nvarchar(4000)) COLLATE DATABASE_DEFAULT,N'')")) : "1=0";
                // doldururken: anahtar metin kurtarmada boş/bozuk kaldıysa çelişki sayılmaz (tablo zaten aynı kayıt olduğu doğrulanmış)
                string fillCond = txt.Count > 0 ? string.Join(" AND ", txt.Select(n => $"(ISNULL(CAST(r.{Q(n)} AS nvarchar(4000)),N'')=ISNULL(CAST(h.{Q(n)} AS nvarchar(4000)) COLLATE DATABASE_DEFAULT,N'') OR LTRIM(ISNULL(CAST(r.{Q(n)} AS nvarchar(4000)),N''))=N'' OR CAST(r.{Q(n)} AS nvarchar(4000)) LIKE N'%'+NCHAR(65533)+N'%')")) : "1=0";
                string T = "dbo." + Q(t), HT = HQ + ".dbo." + Q(t);
                long rN = Convert.ToInt64(Query(cs, $"SELECT COUNT_BIG(*) FROM {T};", 300)[0][0]);
                bool master = MasterRx.IsMatch(t), ok;
                if (rN == 0) ok = master;
                else
                {
                    var q = Query(cs, $"SELECT COUNT_BIG(*), ISNULL(SUM(CASE WHEN {sameCond} THEN 1 ELSE 0 END),0) FROM {T} r JOIN {HT} h ON h.ID=r.ID;", 600)[0];
                    long n = Convert.ToInt64(q[0]), ag = Convert.ToInt64(q[1]);
                    ok = txt.Count > 0 && ((n >= 3 && ag * 10 >= n * 7) || (master && (n == 0 || ag * 2 >= n)));
                    if (!ok && master) skipped.Add($"{t}({ag}/{n})");
                }
                if (!ok) continue;
                // eklemede çift kayıt koruması: ID, benzersiz indeksler, "…kod" adlı ilk metin kolonu
                bool IsTextCol(string c) => common.Any(x => x.name.Equals(c, StringComparison.OrdinalIgnoreCase) && IsText(x.type));
                string Eq(string c) => IsTextCol(c) ? $"r.{Q(c)}=h.{Q(c)} COLLATE DATABASE_DEFAULT" : $"r.{Q(c)}=h.{Q(c)}";
                var guards = new List<string> { $"NOT EXISTS (SELECT 1 FROM {T} r WHERE r.[ID]=h.[ID])" };
                if (uniq.TryGetValue(t, out var ul))
                    foreach (var key in ul.Where(k => k.Count > 0 && k.All(c => common.Any(x => x.name.Equals(c, StringComparison.OrdinalIgnoreCase)))))
                        guards.Add("NOT EXISTS (SELECT 1 FROM " + T + " r WHERE " + string.Join(" AND ", key.Select(Eq)) + ")");
                var kod = common.FirstOrDefault(x => IsText(x.type) && Regex.IsMatch(x.name, "kod|kodu$", RegexOptions.IgnoreCase)).name;
                if (kod != null) guards.Add($"NOT EXISTS (SELECT 1 FROM {T} r WHERE r.{Q(kod)}=h.{Q(kod)} COLLATE DATABASE_DEFAULT)");
                string where = string.Join(" AND ", guards);
                bool ident = rc.Any(x => x.ident && hset.Contains(x.name));
                string cl = string.Join(",", common.Select(x => Q(x.name))), sel = string.Join(",", common.Select(x => "h." + Q(x.name)));
                string pre = $"ALTER TABLE {T} NOCHECK CONSTRAINT ALL; DISABLE TRIGGER ALL ON {T};" + (ident ? $" SET IDENTITY_INSERT {T} ON;" : "");
                string post = (ident ? $" SET IDENTITY_INSERT {T} OFF;" : "") + $" ENABLE TRIGGER ALL ON {T}; ALTER TABLE {T} CHECK CONSTRAINT ALL;";
                long added = 0, fixedN = 0;
                try { added = Convert.ToInt64(Query(cs, pre + $" INSERT INTO {T} ({cl}) SELECT {sel} FROM {HT} h WHERE {where}; SELECT @@ROWCOUNT;" + post, 1800)[0][0]); }
                catch
                {
                    // toplu ekleme bir satırda takıldı → satır satır
                    try
                    {
                        var ids = Query(cs, $"SELECT h.[ID] FROM {HT} h WHERE {where};", 600).Select(r => Convert.ToInt64(r[0])).ToList();
                        foreach (var id in ids)
                            try { added += Convert.ToInt64(Query(cs, pre + $" INSERT INTO {T} ({cl}) SELECT {sel} FROM {HT} h WHERE h.[ID]={id} AND {where}; SELECT @@ROWCOUNT;" + post, 300)[0][0]); }
                            catch { try { ExecSql(cs, post, 60); } catch { } }
                    }
                    catch { }
                }
                // aynı kayıtta (ID + metin anahtarları aynı) kurtarılan tarafta boş ya da bozuk (�) kalan metin alanları
                if (rN > 0 && txt.Count > 0)
                {
                    // tablo başına TEK güncelleme: her metin kolonunda "kurtarılan boş/bozuk ve yedekte dolu ise yedekteki"
                    var tc = common.Where(x => IsText(x.type) && x.type != 99 && x.type != 35).Select(x => Q(x.name)).ToList();
                    string Bad(string c) => $"(r.{c} IS NULL OR LTRIM(CAST(r.{c} AS nvarchar(4000)))=N'' OR CAST(r.{c} AS nvarchar(4000)) LIKE N'%'+NCHAR(65533)+N'%')";
                    string Has(string c) => $"LTRIM(ISNULL(CAST(h.{c} AS nvarchar(4000)),N''))<>N''";
                    if (tc.Count > 0)
                        try
                        {
                            fixedN += Convert.ToInt64(Query(cs, $"DISABLE TRIGGER ALL ON {T}; UPDATE r SET " +
                                string.Join(",", tc.Select(c => $"r.{c}=CASE WHEN {Bad(c)} AND {Has(c)} THEN h.{c} ELSE r.{c} END")) +
                                $" FROM {T} r JOIN {HT} h ON h.ID=r.ID WHERE {fillCond} AND (" + string.Join(" OR ", tc.Select(c => $"({Bad(c)} AND {Has(c)})")) + ")" +
                                $"; SELECT @@ROWCOUNT; ENABLE TRIGGER ALL ON {T};", 900)[0][0]);
                        }
                        catch { try { ExecSql(cs, $"ENABLE TRIGGER ALL ON {T};", 60); } catch { } }
                }
                // sayı/tarih kolonu kurtarmada TAMAMEN boş kaldıysa (yeri çözülemedi → NULL) aynı kaydın sağlam değeri
                if (rN > 0 && txt.Count > 0)
                {
                    var nc = common.Where(x => !IsText(x.type) && !x.ident && x.type != 34 && x.type != 241 && !x.name.Equals("ID", StringComparison.OrdinalIgnoreCase)
                                               && !Regex.IsMatch(x.name, "damga|firmyear|firmnmbr|transkayit", RegexOptions.IgnoreCase)).Select(x => Q(x.name)).ToList();
                    try
                    {
                        if (nc.Count > 0)
                        {
                            // tek sorguyla: hangi kolonlar kurtarılanda TAMAMEN boş
                            var cnt = Query(cs, "SELECT " + string.Join(",", nc.Select(c => $"COUNT({c})")) + $" FROM {T};", 300)[0];
                            var empty = nc.Where((c, i) => Convert.ToInt64(cnt[i]) == 0).ToList();
                            if (empty.Count > 0)
                                fixedN += Convert.ToInt64(Query(cs, $"DISABLE TRIGGER ALL ON {T}; UPDATE r SET " + string.Join(",", empty.Select(c => $"r.{c}=ISNULL(h.{c}, r.{c})")) +
                                    $" FROM {T} r JOIN {HT} h ON h.ID=r.ID WHERE {fillCond}; SELECT @@ROWCOUNT; ENABLE TRIGGER ALL ON {T};", 900)[0][0]);
                        }
                    }
                    catch { try { ExecSql(cs, $"ENABLE TRIGGER ALL ON {T};", 60); } catch { } }
                }
                if (added > 0 || fixedN > 0)
                {
                    tabs++; addAll += added; fixAll += fixedN;
                    lines.Add($"   {t}: " + (added > 0 ? $"+{added} kayıt" : "") + (added > 0 && fixedN > 0 ? " · " : "") + (fixedN > 0 ? $"{fixedN} boş/bozuk alan düzeltildi" : ""));
                }
            }
            log(tabs > 0 ? $"Sağlam yedekten tamamlandı: {tabs} tablo · +{addAll} kayıt · {fixAll} alan" : "Sağlam yedekten eklenecek eksik kayıt bulunmadı.");
            foreach (var l in lines) log(l);
            if (skipped.Count > 0) log("   (kayıtları bu yılla uyuşmadığı için dokunulmayan: " + string.Join(", ", skipped.Take(8)) + ")");
        }

        // ── Kurtarılan satırlarda NULL kalan, ama programın kendi eklediği kayıtlarda varsayılanla (0 / '') dolu olan alanlar.
        //    Orka (Delphi) NULL metni okuyunca "Could not convert variant of type (Null) into type (OleStr)" verir; varsayılan değer yazılır.
        //    Yalnız SABİT varsayılanlar ((0), (''), (1)…); getdate() gibi fonksiyonlu olanlara (ör. çıkış tarihi) dokunulmaz.
        static void FillNullDefaults(string R, Action<string> log)
        {
            string cs = Conn(R);
            var rows = Query(cs, @"SELECT t.name, c.name, dc.definition FROM sys.columns c
                    JOIN sys.tables t ON t.object_id=c.object_id
                    JOIN sys.default_constraints dc ON dc.object_id=c.default_object_id
                    WHERE c.is_nullable=1 AND t.is_ms_shipped=0 AND SCHEMA_NAME(t.schema_id)='dbo' AND c.is_computed=0
                    ORDER BY t.name;", 300);
            var konst = new Regex(@"^\(*\s*(N?'[^']*'|-?\d+(\.\d+)?)\s*\)*$");
            // yalnız satırı olan tablolar (şablonun boş tabloları atlanır) · tablo başına TEK güncelleme
            var dolu = new HashSet<string>(Query(cs, "SELECT t.name FROM sys.tables t JOIN sys.partitions p ON p.object_id=t.object_id AND p.index_id IN (0,1) GROUP BY t.name HAVING SUM(p.rows)>0;", 300)
                                           .Select(r => Convert.ToString(r[0]) ?? ""), StringComparer.OrdinalIgnoreCase);
            long total = 0; int tabs = 0;
            foreach (var g in rows.GroupBy(r => Convert.ToString(r[0]) ?? "").Where(g => dolu.Contains(g.Key)))
            {
                var sets = g.Select(r => (col: "[" + (Convert.ToString(r[1]) ?? "").Replace("]", "]]") + "]", def: (Convert.ToString(r[2]) ?? "").Trim()))
                            .Where(x => konst.IsMatch(x.def)).ToList();
                if (sets.Count == 0) continue;
                string T = "dbo.[" + g.Key.Replace("]", "]]") + "]";
                long n = 0;
                try
                {
                    n = Convert.ToInt64(Query(cs, $"DISABLE TRIGGER ALL ON {T}; UPDATE {T} SET " + string.Join(",", sets.Select(x => $"{x.col}=ISNULL({x.col},{x.def})")) +
                                                  " WHERE " + string.Join(" OR ", sets.Select(x => $"{x.col} IS NULL")) + $"; SELECT @@ROWCOUNT;", 900)[0][0]);
                }
                catch { }
                finally { try { ExecSql(cs, $"ENABLE TRIGGER ALL ON {T};", 60); } catch { } }
                if (n > 0) { total += n; tabs++; }
            }
            if (total > 0) log($"Boş (NULL) kalan alanlar: {total:N0} kayıtta, {tabs} tabloda programın varsayılan değeriyle dolduruldu (Orka 'Null → OleStr' hatası vermesin).");
        }

        // ── Başlığı şifreli sayfada kalmış ama Hızlı Fiş kaydı duran fişler: başlık hızlı fiş kaydından yeniden yazılır
        //    (tarih, belge no/seri, açıklama, tutar, fiş tipi/niteliği, belge tipi). Fiş no sona eklenir → Orka'da "Fiş Numaralarını Sırala".
        static void RebuildHeadersFromQuickEntry(string R, Action<string> log)
        {
            string cs = Conn(R);
            var has = Query(cs, "SELECT OBJECT_ID('dbo.ORT_FISEXCELSATIR'), OBJECT_ID('dbo.ORT_FISBASLIK');", 60)[0];
            if (has.Any(x => x == DBNull.Value)) return;
            var cols = Query(cs, "SELECT name, system_type_id, is_identity, is_computed FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ORT_FISBASLIK') ORDER BY column_id;", 60)
                       .Select(q => (name: Convert.ToString(q[0]) ?? "", type: Convert.ToInt32(q[1]), ident: Convert.ToBoolean(q[2]), comp: Convert.ToBoolean(q[3])))
                       .Where(k => !k.comp && k.type != 189).ToList();
            var ecn = new HashSet<string>(Query(cs, "SELECT name FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ORT_FISEXCELSATIR');", 60).Select(q => Convert.ToString(q[0]) ?? ""), StringComparer.OrdinalIgnoreCase);
            if (!ecn.Contains("IDFisbaslik") || !ecn.Contains("belgetarihi") || !cols.Any(k => k.name.Equals("ID", StringComparison.OrdinalIgnoreCase))) return;
            var tpl = Query(cs, "SELECT TOP 1 b.ID FROM dbo.ORT_FISBASLIK b JOIN dbo.ORT_FISEXCELSATIR e ON e.IDFisbaslik=b.ID ORDER BY b.ID DESC;", 60);
            if (tpl.Count == 0) return;
            long tid = Convert.ToInt64(tpl[0][0]);
            string E(string c) => ecn.Contains(c) ? "e." + c : "NULL";
            // başlık kolonu ← hızlı fiş kolonu
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = "e.IDFisbaslik",
                ["fistipi"] = $"ISNULL({E("fistipi")}, t.fistipi)",
                ["fisniteligi"] = $"ISNULL({E("nitelik")}, t.fisniteligi)",
                ["fisbelgetipi"] = $"ISNULL({E("belgetipi")}, t.fisbelgetipi)",
                ["kdvbelgetipi"] = $"ISNULL({E("kdvbelgetipi")}, t.kdvbelgetipi)",
                ["fistarihi"] = "e.belgetarihi",
                ["fisbelgetarihi"] = "e.belgetarihi",
                ["fisvadetarihi"] = $"ISNULL({E("belgevadesi")}, e.belgetarihi)",
                ["fisdonemi"] = $"ISNULL({E("belgedonemi")}, DATEADD(MONTH, DATEDIFF(MONTH, 0, e.belgetarihi), 0))",
                ["fiskayittarihi"] = "GETDATE()",
                ["fisbelgeserino"] = E("belgeserino"),
                ["fisbelgeno"] = E("belgeno"),
                ["fisaciklama"] = $"ISNULL({E("aciklama")}, t.fisaciklama)",
                ["fistutar"] = $"ISNULL({E("belgetutar")}, 0)",
                ["fisno"] = "(SELECT ISNULL(MAX(fisno),0) FROM dbo.ORT_FISBASLIK)+ROW_NUMBER() OVER (ORDER BY e.belgetarihi, e.IDFisbaslik)",
                ["fisyevmiyeno"] = "(SELECT ISNULL(MAX(fisyevmiyeno),0) FROM dbo.ORT_FISBASLIK)+ROW_NUMBER() OVER (ORDER BY e.belgetarihi, e.IDFisbaslik)",
            };
            string Q(string x) => "[" + x.Replace("]", "]]") + "]";
            var vals = cols.Select(k => map.TryGetValue(k.name, out var v) ? v : k.type == 36 ? "NEWID()" : "t." + Q(k.name)).ToList();
            bool ident = cols.Any(k => k.ident);
            string sql = (ident ? "SET IDENTITY_INSERT dbo.ORT_FISBASLIK ON; " : "") + "DISABLE TRIGGER ALL ON dbo.ORT_FISBASLIK; " +
                $@"INSERT INTO dbo.ORT_FISBASLIK ({string.Join(",", cols.Select(k => Q(k.name)))})
                   SELECT {string.Join(",", vals)} FROM (SELECT *, ROW_NUMBER() OVER (PARTITION BY IDFisbaslik ORDER BY ID) AS __n FROM dbo.ORT_FISEXCELSATIR) e
                   CROSS JOIN (SELECT * FROM dbo.ORT_FISBASLIK WHERE ID={tid}) t
                   WHERE e.__n=1 AND e.IDFisbaslik>0 AND e.belgetarihi IS NOT NULL AND NOT EXISTS (SELECT 1 FROM dbo.ORT_FISBASLIK b WHERE b.ID=e.IDFisbaslik);
                   SELECT @@ROWCOUNT; ENABLE TRIGGER ALL ON dbo.ORT_FISBASLIK;" + (ident ? " SET IDENTITY_INSERT dbo.ORT_FISBASLIK OFF;" : "");
            long n = 0;
            try { n = Convert.ToInt64(Query(cs, sql, 300)[0][0]); }
            catch (Exception ex) { try { ExecSql(cs, "ENABLE TRIGGER ALL ON dbo.ORT_FISBASLIK;" + (ident ? " SET IDENTITY_INSERT dbo.ORT_FISBASLIK OFF;" : ""), 60); } catch { } log("Hızlı fişten başlık üretilemedi: " + First(ex.Message)); return; }
            if (n > 0) log($"Hızlı Fiş kayıtlarından {n} kayıp fiş başlığı yeniden yazıldı (fiş no sona eklendi → Orka'da Fiş/Evrak Kayıt › Servis › 'Fiş Numaralarını Sırala').");
        }

        // ── FİŞ TİPİ: hesap deseninden türet (Tahsil/Tediye/Mahsup/Virman/Açılış). Kodları SABİT yazmayız;
        //    dosyanın kendi doğru-tipli fişlerinden ÖĞRENİRİZ (Orka/Luca sürümüne göre kod değişebilir).
        const string CASHSET = "(100,101,102,103,108)";   // kasa, alınan çek, banka, verilen çek, diğer hazır değerler
        static string KebirExpr(string cs) =>
            Query(cs, "SELECT COL_LENGTH('dbo.ORT_FISSATIR','hesapkebirkodu');", 60)[0][0] != DBNull.Value
                ? "s.hesapkebirkodu" : "TRY_CONVERT(int, LEFT(LTRIM(ISNULL(s.hesapkodu,N'')),3))";
        // fişin desenini çıkaran alt sorgu: cashB/cashA = kasa-banka borç/alacak, ncash/nline = kasa satır/toplam satır
        static string PatSub(string keb) =>
            $@"SELECT s.IDFisbaslik fid,
                 SUM(CASE WHEN {keb} IN {CASHSET} THEN ISNULL(s.tlborctutar,0) ELSE 0 END) cashB,
                 SUM(CASE WHEN {keb} IN {CASHSET} THEN ISNULL(s.tlalactutar,0) ELSE 0 END) cashA,
                 COUNT(*) nline, SUM(CASE WHEN {keb} IN {CASHSET} THEN 1 ELSE 0 END) ncash
               FROM dbo.ORT_FISSATIR s GROUP BY s.IDFisbaslik";
        // desen → tip ETİKETİ (SQL). p = desen alanları, acik = açıklama ifadesi
        static string TypeLabel(string p, string acik) =>
            $@"CASE WHEN {acik} LIKE N'%Açılış%' OR {acik} LIKE N'%AÇILIŞ%' OR {acik} LIKE N'%Devir%' THEN N'Açılış'
                    WHEN {p}.cashB+{p}.cashA=0 THEN N'Mahsup'
                    WHEN {p}.ncash={p}.nline THEN N'Virman'
                    WHEN {p}.cashB>{p}.cashA THEN N'Tahsil'
                    WHEN {p}.cashA>{p}.cashB THEN N'Tediye'
                    ELSE N'Mahsup' END";
        // mevcut doğru-tipli fişlerden kod↔tip eşlemesini öğren (her tip için en çok kullanılan kod)
        static Dictionary<string, int> LearnFisTipi(string cs)
        {
            var m = new Dictionary<string, int>(StringComparer.Ordinal);
            try
            {
                string keb = KebirExpr(cs);
                var rows = Query(cs, $@"SELECT b.fistipi AS code, {TypeLabel("p", "b.fisaciklama")} AS ty
                    FROM dbo.ORT_FISBASLIK b JOIN ({PatSub(keb)}) p ON p.fid=b.ID WHERE b.fistipi IS NOT NULL;", 300);
                var tally = new Dictionary<string, Dictionary<int, int>>();
                foreach (var r in rows)
                {
                    if (r[0] == DBNull.Value || r[1] == DBNull.Value) continue;
                    int code = Convert.ToInt32(r[0]); string ty = Convert.ToString(r[1]) ?? "";
                    if (!tally.TryGetValue(ty, out var dd)) tally[ty] = dd = new();
                    dd[code] = dd.TryGetValue(code, out var c) ? c + 1 : 1;
                }
                foreach (var kv in tally)
                {
                    var best = kv.Value.OrderByDescending(x => x.Value).First();
                    if (best.Value >= 2) m[kv.Key] = best.Key;   // en az 2 kanıtlı eşleme
                }
            }
            catch { }
            return m;
        }
        // öğrenilen eşlemeyle: desen → KOD (SQL CASE). Bilinmeyen tip için fallback (dokunma).
        static string FisTipiExpr(Dictionary<string, int> m, string p, string acik, string fallback)
        {
            var sb = new StringBuilder("CASE");
            int k = 0;
            void Br(string ty, string cond) { if (m.TryGetValue(ty, out var code)) { sb.Append($" WHEN {cond} THEN {code}"); k++; } }
            Br("Açılış", $"({acik} LIKE N'%Açılış%' OR {acik} LIKE N'%AÇILIŞ%' OR {acik} LIKE N'%Devir%')");
            Br("Mahsup", $"{p}.cashB+{p}.cashA=0");
            Br("Virman", $"{p}.ncash={p}.nline");
            Br("Tahsil", $"{p}.cashB>{p}.cashA");
            Br("Tediye", $"{p}.cashA>{p}.cashB");
            if (k == 0) return fallback;   // hiç öğrenilemedi → mevcut değere dokunma
            sb.Append($" ELSE {fallback} END");
            return sb.ToString();
        }

        // ── FİŞ BÜTÜNLEME: ORT_FISSATIR'da başlığı (ORT_FISBASLIK) eksik kalan "yetim" satırlar
        //    (aralıklı şifrelemede başlık sayfası şifreli kalmış; satır sayfası sağlam). Satırlar fiş
        //    anahtarına (IDFisbaslik) göre gruplanıp eksik başlık SENTEZLENİR: tarih + açıklama satırlardan,
        //    tutar = borç toplamı. Satırın kendi IDFisbaslik'i başlık ID'si yapılır → bağlantı birebir korunur.
        //    Yalnız başlığı GERÇEKTEN olmayan anahtarlar için üretir; mevcut başlıklara dokunmaz. (Hızlı-fiş
        //    bütünlemesinden farkı: kaynağı ORT_FISSATIR, yani normal yevmiye satırları.)
        static void RebuildHeadersFromOrphanLines(string R, Action<string> log)
        {
            string cs = Conn(R);
            var has = Query(cs, "SELECT OBJECT_ID('dbo.ORT_FISBASLIK'), OBJECT_ID('dbo.ORT_FISSATIR'), COL_LENGTH('dbo.ORT_FISSATIR','IDFisbaslik'), COL_LENGTH('dbo.ORT_FISSATIR','fisbelgetarihi');", 60)[0];
            if (has.Any(x => x == DBNull.Value)) return;
            var cols = Query(cs, "SELECT name, system_type_id, is_identity, is_computed FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ORT_FISBASLIK') ORDER BY column_id;", 60)
                       .Select(q => (name: Convert.ToString(q[0]) ?? "", type: Convert.ToInt32(q[1]), ident: Convert.ToBoolean(q[2]), comp: Convert.ToBoolean(q[3])))
                       .Where(k => !k.comp && k.type != 189).ToList();
            if (!cols.Any(k => k.name.Equals("ID", StringComparison.OrdinalIgnoreCase))) return;
            // diğer başlık kolonları için şablon (mevcut bir başlık)
            var tpl = Query(cs, "SELECT TOP 1 ID FROM dbo.ORT_FISBASLIK ORDER BY ID DESC;", 60);
            if (tpl.Count == 0) return;
            long tid = Convert.ToInt64(tpl[0][0]);
            var scn = new HashSet<string>(Query(cs, "SELECT name FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ORT_FISSATIR');", 60).Select(q => Convert.ToString(q[0]) ?? ""), StringComparer.OrdinalIgnoreCase);
            string acikCol = scn.Contains("fisaciklamasi") ? "fisaciklamasi" : (scn.Contains("aciklama") ? "aciklama" : "");
            string belgenoCol = scn.Contains("fisbelgeno") ? "fisbelgeno" : "";
            string tutarExpr = scn.Contains("tlborctutar") ? "SUM(ISNULL(s.tlborctutar,0))" : "0";
            string gAcik = acikCol.Length > 0 ? $"MAX(NULLIF(LTRIM(RTRIM(s.{acikCol})),N''))" : "NULL";
            string gBelge = belgenoCol.Length > 0 ? $"MAX(s.{belgenoCol})" : "NULL";
            string keb = KebirExpr(cs);
            var ftm = LearnFisTipi(cs);   // fiş tipi kodlarını dosyadan öğren
            // başlığı GERÇEKTEN olmayan fiş anahtarlarını, satırlarından grupla (+ fiş tipi için kasa deseni)
            string grp = $@"SELECT s.IDFisbaslik AS fid, MAX(s.fisbelgetarihi) AS tarih, {tutarExpr} AS tutar, {gAcik} AS acik, {gBelge} AS belgeno,
                            SUM(CASE WHEN {keb} IN {CASHSET} THEN ISNULL(s.tlborctutar,0) ELSE 0 END) AS cashB,
                            SUM(CASE WHEN {keb} IN {CASHSET} THEN ISNULL(s.tlalactutar,0) ELSE 0 END) AS cashA,
                            COUNT(*) AS nline, SUM(CASE WHEN {keb} IN {CASHSET} THEN 1 ELSE 0 END) AS ncash
                            FROM dbo.ORT_FISSATIR s
                            WHERE s.IDFisbaslik > 0 AND s.fisbelgetarihi IS NOT NULL
                              AND NOT EXISTS (SELECT 1 FROM dbo.ORT_FISBASLIK b WHERE b.ID=s.IDFisbaslik)
                            GROUP BY s.IDFisbaslik";
            string Q(string x) => "[" + x.Replace("]", "]]") + "]";
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["ID"] = "g.fid",
                ["fistarihi"] = "g.tarih",
                ["fisbelgetarihi"] = "g.tarih",
                ["fisvadetarihi"] = "g.tarih",
                ["fisdonemi"] = "DATEADD(MONTH, DATEDIFF(MONTH, 0, g.tarih), 0)",
                ["fiskayittarihi"] = "GETDATE()",
                ["fisaciklama"] = "ISNULL(g.acik, t.fisaciklama)",
                ["fistutar"] = "ISNULL(g.tutar, 0)",
                ["fistipi"] = FisTipiExpr(ftm, "g", "g.acik", "t." + Q("fistipi")),   // tip hesap deseninden
                ["fisbelgeno"] = belgenoCol.Length > 0 ? "g.belgeno" : "NULL",
                ["fisno"] = "(SELECT ISNULL(MAX(fisno),0) FROM dbo.ORT_FISBASLIK)+ROW_NUMBER() OVER (ORDER BY g.tarih, g.fid)",
                ["fisyevmiyeno"] = "(SELECT ISNULL(MAX(fisyevmiyeno),0) FROM dbo.ORT_FISBASLIK)+ROW_NUMBER() OVER (ORDER BY g.tarih, g.fid)",
            };
            var vals = cols.Select(k => map.TryGetValue(k.name, out var v) ? v : k.type == 36 ? "NEWID()" : "t." + Q(k.name)).ToList();
            bool ident = cols.Any(k => k.ident);
            string sql = (ident ? "SET IDENTITY_INSERT dbo.ORT_FISBASLIK ON; " : "") + "DISABLE TRIGGER ALL ON dbo.ORT_FISBASLIK; " +
                $@"INSERT INTO dbo.ORT_FISBASLIK ({string.Join(",", cols.Select(k => Q(k.name)))})
                   SELECT {string.Join(",", vals)} FROM ({grp}) g
                   CROSS JOIN (SELECT * FROM dbo.ORT_FISBASLIK WHERE ID={tid}) t;
                   SELECT @@ROWCOUNT; ENABLE TRIGGER ALL ON dbo.ORT_FISBASLIK;" + (ident ? " SET IDENTITY_INSERT dbo.ORT_FISBASLIK OFF;" : "");
            long n = 0;
            try { n = Convert.ToInt64(Query(cs, sql, 300)[0][0]); }
            catch (Exception ex) { try { ExecSql(cs, "ENABLE TRIGGER ALL ON dbo.ORT_FISBASLIK;" + (ident ? " SET IDENTITY_INSERT dbo.ORT_FISBASLIK OFF;" : ""), 60); } catch { } log("Yetim satırlardan başlık üretilemedi: " + First(ex.Message)); return; }
            if (n > 0) log($"Fiş Bütünleme: başlıksız yevmiye satırlarından {n} kayıp fiş başlığı bütünlendi (tarih+açıklama satırlardan, tutar=borç toplamı; fiş no sona eklendi → Orka'da Fiş/Evrak Kayıt › Servis › 'Fiş Numaralarını Sırala').");
        }

        // ── Tipi BOŞ (NULL/0) kalan fişlerin tipini hesap deseninden belirle. Mevcut doğru tipe DOKUNMAZ.
        //    Kodları dosyanın kendi fişlerinden öğrenir; öğrenemezse hiç dokunmaz.
        static void AssignMissingFisTipi(string R, Action<string> log)
        {
            string cs = Conn(R);
            var has = Query(cs, "SELECT OBJECT_ID('dbo.ORT_FISBASLIK'), OBJECT_ID('dbo.ORT_FISSATIR'), COL_LENGTH('dbo.ORT_FISBASLIK','fistipi'), COL_LENGTH('dbo.ORT_FISBASLIK','fisaciklama');", 60)[0];
            if (has.Any(x => x == DBNull.Value)) return;
            var ftm = LearnFisTipi(cs);
            if (ftm.Count == 0) { log("Fiş tipi: öğrenilecek doğru-tipli fiş yok → tip belirleme atlandı."); return; }
            string keb = KebirExpr(cs);
            string expr = FisTipiExpr(ftm, "p", "b.fisaciklama", "b.fistipi");
            string sql = $@"DISABLE TRIGGER ALL ON dbo.ORT_FISBASLIK;
                UPDATE b SET b.fistipi = {expr}
                FROM dbo.ORT_FISBASLIK b JOIN ({PatSub(keb)}) p ON p.fid=b.ID
                WHERE b.fistipi IS NULL OR b.fistipi=0;
                SELECT @@ROWCOUNT; ENABLE TRIGGER ALL ON dbo.ORT_FISBASLIK;";
            long n = 0;
            try { n = Convert.ToInt64(Query(cs, sql, 300)[0][0]); }
            catch (Exception ex) { try { ExecSql(cs, "ENABLE TRIGGER ALL ON dbo.ORT_FISBASLIK;", 60); } catch { } log("Fiş tipi belirleme atlandı: " + First(ex.Message)); return; }
            if (n > 0) log($"Fiş tipi: boş kalan {n} fişin tipi hesap deseninden belirlendi (Tahsil/Tediye/Mahsup/Virman/Açılış) — kodlar dosyadan öğrenildi.");
        }

        // fiş başlığında tutar boş/0 ama satırları duruyor → başlık tutarı = satırların borç toplamı
        static void FixHeaderTotals(string R, Action<string> log)
        {
            string cs = Conn(R);
            var has = Query(cs, "SELECT OBJECT_ID('dbo.ORT_FISBASLIK'), OBJECT_ID('dbo.ORT_FISSATIR'), COL_LENGTH('dbo.ORT_FISBASLIK','fistutar'), COL_LENGTH('dbo.ORT_FISSATIR','tlborctutar');", 60)[0];
            if (has.Any(x => x == DBNull.Value)) return;
            long n = Convert.ToInt64(Query(cs, @"DISABLE TRIGGER ALL ON dbo.ORT_FISBASLIK;
                UPDATE b SET b.fistutar=s.t FROM dbo.ORT_FISBASLIK b JOIN (SELECT IDFisbaslik, SUM(ISNULL(tlborctutar,0)) t FROM dbo.ORT_FISSATIR GROUP BY IDFisbaslik) s ON s.IDFisbaslik=b.ID
                WHERE ISNULL(b.fistutar,0)=0 AND s.t<>0; SELECT @@ROWCOUNT; ENABLE TRIGGER ALL ON dbo.ORT_FISBASLIK;", 300)[0][0]);
            if (n > 0) log($"Fiş başlıkları: tutarı boş kalan {n} fişe satırlarının toplamı yazıldı.");
        }

        // ── Kalite özeti: her dosya için tek satır (log + klasörde VKM_ozet.csv) → yüzlerce dosyada zayıf çıkanı hemen görmek için
        static void QualitySummary(string R, string dir, string name, Action<string> log)
        {
            string cs = Conn(R);
            long Sc(string sql) { try { var q = Query(cs, sql, 300); return q.Count == 0 || q[0][0] == DBNull.Value ? -1 : Convert.ToInt64(q[0][0]); } catch { return -1; } }
            bool T(string t) => Sc($"SELECT CASE WHEN OBJECT_ID('dbo.{t}') IS NULL THEN 0 ELSE 1 END;") == 1;
            long fb = -1, fs = -1, bos = -1, yetim = -1, dengesiz = -1, plan = -1, adsiz = -1, pers = -1, cari = -1; string aylar = "", eksikAy = "";
            if (T("ORT_FISBASLIK") && T("ORT_FISSATIR"))
            {
                fb = Sc("SELECT COUNT(*) FROM dbo.ORT_FISBASLIK;");
                fs = Sc("SELECT COUNT(*) FROM dbo.ORT_FISSATIR;");
                bos = Sc("SELECT COUNT(*) FROM dbo.ORT_FISBASLIK b WHERE NOT EXISTS (SELECT 1 FROM dbo.ORT_FISSATIR s WHERE s.IDFisbaslik=b.ID);");
                yetim = Sc("SELECT COUNT(*) FROM dbo.ORT_FISSATIR s WHERE NOT EXISTS (SELECT 1 FROM dbo.ORT_FISBASLIK b WHERE b.ID=s.IDFisbaslik);");
                dengesiz = Sc("SELECT COUNT(*) FROM (SELECT IDFisbaslik FROM dbo.ORT_FISSATIR GROUP BY IDFisbaslik HAVING ABS(SUM(ISNULL(tlborctutar,0))-SUM(ISNULL(tlalactutar,0)))>0.01) x;");
                try
                {
                    var ms = Query(cs, "SELECT DISTINCT YEAR(fistarihi)*100+MONTH(fistarihi) FROM dbo.ORT_FISBASLIK WHERE fistarihi IS NOT NULL ORDER BY 1;", 300).Select(q => Convert.ToInt32(q[0])).ToList();
                    if (ms.Count > 0)
                    {
                        aylar = $"{ms[0] % 100:00}/{ms[0] / 100}–{ms[^1] % 100:00}/{ms[^1] / 100}";
                        var gaps = new List<string>();
                        for (int ym = ms[0]; ym <= ms[^1]; ym = ym % 100 == 12 ? (ym / 100 + 1) * 100 + 1 : ym + 1)
                            if (!ms.Contains(ym)) gaps.Add($"{ym % 100:00}/{ym / 100}");
                        eksikAy = string.Join(" ", gaps);
                    }
                }
                catch { }
            }
            if (T("ORT_PLAN")) { plan = Sc("SELECT COUNT(*) FROM dbo.ORT_PLAN;"); adsiz = Sc("SELECT COUNT(*) FROM dbo.ORT_PLAN WHERE LTRIM(ISNULL(planadi,N''))=N'' OR planadi LIKE N'%(adı kurtarılamadı)%';"); }
            if (T("PUB_ISEGIRISCIKIS")) pers = Sc("SELECT COUNT(*) FROM dbo.PUB_ISEGIRISCIKIS;");
            if (T("ICE_CARIKART")) cari = Sc("SELECT COUNT(*) FROM dbo.ICE_CARIKART;");
            if (fb < 0 && plan < 0) return;   // muhasebe firması değil (GENELTANIM, RESİMLER …)
            string S(long v) => v < 0 ? "-" : v.ToString("N0");
            // az fişli firmada boş ay doğaldır → eksik ay yalnız ayda ortalama 5+ fiş olan firmada uyarı sayılır
            int ayAdedi = 0;
            try { if (aylar.Length > 0) { var p1 = aylar.Split('–'); var a1 = p1[0].Split('/'); var a2 = p1[1].Split('/'); ayAdedi = (int.Parse(a2[1]) - int.Parse(a1[1])) * 12 + int.Parse(a2[0]) - int.Parse(a1[0]) + 1; } } catch { }
            bool ayUyari = eksikAy.Length > 0 && fb >= 5L * Math.Max(ayAdedi, 1);
            if (!ayUyari && eksikAy.Length > 0) eksikAy = "(az fişli — boş ay doğal olabilir: " + eksikAy + ")";
            bool zayif = (fb >= 20 && bos * 10 > fb) || (fs >= 0 && yetim > Math.Max(50, fs / 20)) || (plan >= 0 && adsiz * 10 > Math.Max(plan, 1))
                         || ayUyari || (fb >= 20 && dengesiz * 5 > fb);
            log("");
            log($"KALİTE ÖZETİ {(zayif ? "⚠ kontrol et" : "✓")}: fiş {S(fb)} (satırsız {S(bos)}, dengesiz {S(dengesiz)}) · satır {S(fs)} (başlıksız {S(yetim)}) · aylar {(aylar.Length > 0 ? aylar : "-")}" +
                (eksikAy.Length > 0 ? (ayUyari ? $" · EKSİK AY: {eksikAy}" : $" · {eksikAy}") : "") + $" · plan {S(plan)} (adsız {S(adsiz)}) · personel {S(pers)} · cari {S(cari)}");
            try
            {
                string f = Path.Combine(dir, "VKM_ozet.csv");
                if (!File.Exists(f)) File.WriteAllText(f, "dosya;durum;fis;satirsiz_fis;dengesiz_fis;satir;basliksiz_satir;aylar;eksik_ay;plan;adsiz_hesap;personel;cari;tarih\n", new UTF8Encoding(true));
                File.AppendAllText(f, string.Join(";", name, zayif ? "KONTROL" : "IYI", fb, bos, dengesiz, fs, yetim, aylar, eksikAy, plan, adsiz, pers, cari, DateTime.Now.ToString("yyyy-MM-dd HH:mm")) + "\n", Encoding.UTF8);
            }
            catch { }
        }

        static string FmtN(object o) => o == DBNull.Value ? "" : Convert.ToDecimal(o).ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("tr-TR"));

        // ── EKSİK FİŞ RAPORU: içeriden tamamlanamayan fişleri tek tek listeler (müşavir kaynak belgeden/e-defterden kapatır).
        //    DENGESIZ = bazı satırları şifreli sayfada kaybolmuş (Borç≠Alacak). SATIRSIZ = başlık var, satırları hiç yok.
        //    Eksik satırın HESABI kayıp olduğu için UYDURULMAZ (defteri bozar) — sadece net rapor: hangi fiş, ne kadar eksik.
        static void ReportProblemVouchers(string R, string dir, string name, Action<string> log)
        {
            string cs = Conn(R);
            var has = Query(cs, "SELECT OBJECT_ID('dbo.ORT_FISBASLIK'), OBJECT_ID('dbo.ORT_FISSATIR'), COL_LENGTH('dbo.ORT_FISSATIR','tlborctutar');", 60)[0];
            if (has.Any(x => x == DBNull.Value)) return;
            string fno = Query(cs, "SELECT COL_LENGTH('dbo.ORT_FISBASLIK','fisno');", 60)[0][0] != DBNull.Value ? "fisno" : "ID";
            List<object[]> deng, bos;
            try
            {
                deng = Query(cs, $@"SELECT b.{fno} AS fno, CONVERT(varchar(10), b.fistarihi, 104) AS tarih, LEFT(ISNULL(b.fisaciklama,N''),60) AS acik,
                        s.borc, s.alac, s.borc - s.alac AS fark
                     FROM dbo.ORT_FISBASLIK b
                     JOIN (SELECT IDFisbaslik, SUM(ISNULL(tlborctutar,0)) borc, SUM(ISNULL(tlalactutar,0)) alac
                           FROM dbo.ORT_FISSATIR GROUP BY IDFisbaslik
                           HAVING ABS(SUM(ISNULL(tlborctutar,0))-SUM(ISNULL(tlalactutar,0)))>0.01) s ON s.IDFisbaslik=b.ID
                     ORDER BY b.fistarihi, b.{fno};", 600);
                bos = Query(cs, $@"SELECT b.{fno} AS fno, CONVERT(varchar(10), b.fistarihi, 104) AS tarih, LEFT(ISNULL(b.fisaciklama,N''),60) AS acik, ISNULL(b.fistutar,0) AS tutar
                     FROM dbo.ORT_FISBASLIK b
                     WHERE NOT EXISTS (SELECT 1 FROM dbo.ORT_FISSATIR s WHERE s.IDFisbaslik=b.ID)
                     ORDER BY b.fistarihi, b.{fno};", 600);
            }
            catch (Exception ex) { log("Eksik fiş raporu çıkarılamadı: " + First(ex.Message)); return; }
            if (deng.Count == 0 && bos.Count == 0) return;
            try
            {
                string f = Path.Combine(dir, "VKM_eksik_fisler.csv");
                bool neu = !File.Exists(f);
                var sb = new StringBuilder();
                if (neu) sb.Append("firma;durum;fisno;tarih;aciklama;borc;alacak;eksik_tutar\n");
                string C(object o) => (Convert.ToString(o) ?? "").Replace(";", " ").Replace("\n", " ").Replace("\r", " ");
                foreach (var r in deng) sb.Append(string.Join(";", name, "DENGESIZ", C(r[0]), C(r[1]), C(r[2]), FmtN(r[3]), FmtN(r[4]), FmtN(r[5])) + "\n");
                foreach (var r in bos) sb.Append(string.Join(";", name, "SATIRSIZ", C(r[0]), C(r[1]), C(r[2]), "", "", FmtN(r[3])) + "\n");
                File.AppendAllText(f, sb.ToString(), new UTF8Encoding(neu));
            }
            catch { }
            log($"⚠ Eksik fiş raporu: {deng.Count} dengesiz + {bos.Count} satırsız fiş → VKM_eksik_fisler.csv (eksik satırların hesabı şifreli sayfada kaybolmuş; müşavir kaynak belgeden/e-defterden tamamlar — uydurulmadı).");
        }

        // ── Hızlı Fiş (Excel Fiş Girişi / yazarkasa Z raporu) kaydından üretilmiş fişlerin şifreli sayfada kalan SATIRLARI:
        //    ORT_FISEXCELSATIR satırı fişin tam reçetesini taşır (ödeme hesabı+tutar, matrah hesabı+tutar+KDV oranı, KDV hesabı+tutar).
        //    Orka'nın kendi ürettiği satır düzeniyle (ödeme borç / matrah alacak / KDV alacak) eksik satırlar yeniden yazılır;
        //    kalan alanlar aynı rolde başka bir fiş satırından kopyalanır. Mevcut satırlara dokunulmaz.
        static void RebuildLinesFromQuickEntry(string R, Action<string> log)
        {
            string cs = Conn(R);
            var has = Query(cs, "SELECT OBJECT_ID('dbo.ORT_FISEXCELSATIR'), OBJECT_ID('dbo.ORT_FISSATIR'), OBJECT_ID('dbo.ORT_FISBASLIK'), OBJECT_ID('dbo.ORT_PLAN');", 60)[0];
            if (has.Any(x => x == DBNull.Value)) return;
            var cols = Query(cs, "SELECT name, system_type_id, is_identity, is_computed FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ORT_FISSATIR') ORDER BY column_id;", 60)
                       .Select(q => (name: Convert.ToString(q[0]) ?? "", type: Convert.ToInt32(q[1]), ident: Convert.ToBoolean(q[2]), comp: Convert.ToBoolean(q[3])))
                       .Where(k => !k.comp && k.type != 189).ToList();
            var cn = new HashSet<string>(cols.Select(k => k.name), StringComparer.OrdinalIgnoreCase);
            foreach (var need in new[] { "ID", "IDFisbaslik", "hesapkodu", "tlborctutar", "tlalactutar", "borcalacak" })
                if (!cn.Contains(need)) return;
            var ecn = new HashSet<string>(Query(cs, "SELECT name FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ORT_FISEXCELSATIR');", 60).Select(q => Convert.ToString(q[0]) ?? ""), StringComparer.OrdinalIgnoreCase);
            if (!ecn.Contains("IDFisbaslik") || !ecn.Contains("odeme_01") || !ecn.Contains("matrah_01")) return;
            bool ident = cols.Any(k => k.ident);
            string Q(string x) => "[" + x.Replace("]", "]]") + "]";
            string S(string x) => "N'" + x.Replace("'", "''") + "'";
            string D(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            decimal Dec(object o) => o == DBNull.Value ? 0m : Convert.ToDecimal(o);
            string Str(object o) => o == DBNull.Value ? "" : (Convert.ToString(o) ?? "").Trim();

            // fişi mevcut olan hızlı fiş kayıtları
            var sel = new List<string> { "e.IDFisbaslik", "e.belgetarihi", "e.belgeno", "e.aciklama" };
            for (int k = 1; k <= 7; k++)
                foreach (var f in new[] { "odeme", "odepln", "matrah", "mthpln", "kdvttr", "kdvpln", "kdvoran" })
                    sel.Add(ecn.Contains($"{f}_{k:00}") ? $"e.{f}_{k:00}" : "NULL");
            var rows = Query(cs, $"SELECT {string.Join(",", sel)} FROM dbo.ORT_FISEXCELSATIR e WHERE e.IDFisbaslik > 0 AND EXISTS (SELECT 1 FROM dbo.ORT_FISBASLIK b WHERE b.ID=e.IDFisbaslik);", 600);
            if (rows.Count == 0) return;
            // fiş başına mevcut satırlar (hesap+taraf+tutar)
            var have = new Dictionary<long, List<(string kod, int ba, decimal tut)>>();
            foreach (var q in Query(cs, "SELECT IDFisbaslik, hesapkodu, borcalacak, ISNULL(tlborctutar,0), ISNULL(tlalactutar,0) FROM dbo.ORT_FISSATIR;", 600))
            {
                if (q[0] == DBNull.Value) continue;
                long id = Convert.ToInt64(q[0]);
                if (!have.TryGetValue(id, out var l)) have[id] = l = new();
                decimal b = Dec(q[3]), a = Dec(q[4]);
                l.Add((Str(q[1]), b != 0 ? -1 : 1, b != 0 ? b : a));
            }
            var planId = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var q in Query(cs, "SELECT plankod, ID FROM dbo.ORT_PLAN WHERE plankod IS NOT NULL;", 300)) planId[Str(q[0])] = Convert.ToInt64(q[1]);
            // aynı hesap + aynı tarafta örnek satır (yoksa aynı rolde herhangi biri): rol = 100 ödeme, 200 matrah, 300 KDV
            long? Tpl(string kod, int ba, int role)
            {
                var t = Query(cs, $"SELECT TOP 1 s.ID FROM dbo.ORT_FISSATIR s JOIN dbo.ORT_FISEXCELSATIR e ON e.IDFisbaslik=s.IDFisbaslik WHERE s.hesapkodu={S(kod)} AND s.borcalacak={ba} ORDER BY s.ID DESC;", 60);
                if (t.Count == 0 && cn.Contains("kayitsirano"))
                    t = Query(cs, $"SELECT TOP 1 s.ID FROM dbo.ORT_FISSATIR s JOIN dbo.ORT_FISEXCELSATIR e ON e.IDFisbaslik=s.IDFisbaslik WHERE s.borcalacak={ba} AND s.kayitsirano={role} ORDER BY s.ID DESC;", 60);
                if (t.Count == 0) t = Query(cs, $"SELECT TOP 1 s.ID FROM dbo.ORT_FISSATIR s JOIN dbo.ORT_FISEXCELSATIR e ON e.IDFisbaslik=s.IDFisbaslik WHERE s.borcalacak={ba} ORDER BY s.ID DESC;", 60);
                return t.Count == 0 ? null : Convert.ToInt64(t[0][0]);
            }
            var tplCache = new Dictionary<(string, int, int), long?>();
            int made = 0, fises = 0, noTpl = 0;
            foreach (var r in rows)
            {
                long fid = Convert.ToInt64(r[0]);
                DateTime? tarih = r[1] == DBNull.Value ? null : Convert.ToDateTime(r[1]);
                string belgeno = r[2] == DBNull.Value ? "NULL" : Convert.ToString(r[2], System.Globalization.CultureInfo.InvariantCulture)!;
                string acik = Str(r[3]);
                var want = new List<(string kod, int ba, decimal tut, int role, decimal oran, decimal kdv)>();
                for (int k = 0; k < 7; k++)
                {
                    int o = 4 + k * 7;
                    decimal ode = Dec(r[o]), mat = Dec(r[o + 2]), kdv = Dec(r[o + 4]), oran = Dec(r[o + 6]);
                    string odeK = Str(r[o + 1]), matK = Str(r[o + 3]), kdvK = Str(r[o + 5]);
                    if (ode != 0 && odeK.Length > 0) want.Add((odeK, -1, ode, 100, 0, 0));
                    if (mat != 0 && matK.Length > 0) want.Add((matK, 1, mat, 200, oran, kdv));
                    if (kdv != 0 && kdvK.Length > 0) want.Add((kdvK, 1, kdv, 300, oran, kdv));
                }
                // hızlı fişin kendisi dengeli değilse (kesinti/eklenti vb. bilinmeyen kalem) dokunma
                if (want.Count == 0 || Math.Abs(want.Where(w => w.ba < 0).Sum(w => w.tut) - want.Where(w => w.ba > 0).Sum(w => w.tut)) > 0.02m) continue;
                var cur = have.TryGetValue(fid, out var cl) ? new List<(string kod, int ba, decimal tut)>(cl) : new();
                bool any = false;
                foreach (var w in want)
                {
                    int ix = cur.FindIndex(c => c.kod.Equals(w.kod, StringComparison.OrdinalIgnoreCase) && c.ba == w.ba && Math.Abs(c.tut - w.tut) < 0.011m);
                    if (ix >= 0) { cur.RemoveAt(ix); continue; }
                    if (!tplCache.TryGetValue((w.kod, w.ba, w.role), out var tid)) tplCache[(w.kod, w.ba, w.role)] = tid = Tpl(w.kod, w.ba, w.role);
                    if (tid == null) { noTpl++; continue; }
                    long newId = Convert.ToInt64(Query(cs, "SELECT ISNULL(MAX(ID),0)+1 FROM dbo.ORT_FISSATIR;", 60)[0][0]);
                    var vals = new List<string>();
                    foreach (var c in cols)
                    {
                        string n = c.name, v;
                        switch (n.ToLowerInvariant())
                        {
                            case "id": v = newId.ToString(); break;
                            case "idfisbaslik": v = fid.ToString(); break;
                            case "hesapkodu": v = S(w.kod); break;
                            case "idhesapkodu": v = planId.TryGetValue(w.kod, out var pid) ? pid.ToString() : "NULL"; break;
                            case "hesapkebirkodu": v = w.kod.Length >= 3 && int.TryParse(w.kod.Substring(0, 3), out var kb) ? kb.ToString() : "t." + Q(n); break;
                            case "tlborctutar": v = w.ba < 0 ? D(w.tut) : "0"; break;
                            case "tlalactutar": v = w.ba > 0 ? D(w.tut) : "0"; break;
                            case "borcalacak": v = w.ba.ToString(); break;
                            case "kdvoran": v = w.role == 100 ? "t." + Q(n) : D(w.oran); break;
                            case "kdvtutari": v = w.role == 100 ? "t." + Q(n) : D(w.kdv); break;
                            case "fisbelgetarihi": v = tarih is DateTime d1 ? $"'{d1:yyyyMMdd}'" : "t." + Q(n); break;
                            case "fisbelgedonemi": case "fisbelgevadesi": v = tarih is DateTime d2 ? $"'{d2:yyyyMM}01'" : "t." + Q(n); break;
                            case "fisbelgeno": v = belgeno; break;
                            case "fisaciklamasi": v = acik.Length > 0 ? S(acik) : "t." + Q(n); break;
                            case "kayitsirano": v = w.role.ToString(); break;
                            default: v = c.type == 36 ? "NEWID()" : "t." + Q(n); break;
                        }
                        vals.Add(v);
                    }
                    string sql = (ident ? "SET IDENTITY_INSERT dbo.ORT_FISSATIR ON; " : "") + "DISABLE TRIGGER ALL ON dbo.ORT_FISSATIR; " +
                                 $"INSERT INTO dbo.ORT_FISSATIR ({string.Join(",", cols.Select(k => Q(k.name)))}) SELECT {string.Join(",", vals)} FROM dbo.ORT_FISSATIR t WHERE t.ID={tid}; " +
                                 "ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;" + (ident ? " SET IDENTITY_INSERT dbo.ORT_FISSATIR OFF;" : "");
                    try { ExecSql(cs, sql, 120); made++; any = true; }
                    catch { try { ExecSql(cs, "ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;" + (ident ? " SET IDENTITY_INSERT dbo.ORT_FISSATIR OFF;" : ""), 60); } catch { } }
                }
                if (any) fises++;
            }
            if (made > 0 || noTpl > 0)
                log($"Hızlı Fiş kayıtlarından fiş satırı tamamlandı: {fises} fişe {made} satır (ödeme / matrah / KDV) yeniden yazıldı" + (noTpl > 0 ? $" · {noTpl} satır için örnek satır bulunamadı" : "") + ".");
        }

        // ── Aynı firmanın başka yıl verisinde olup çıktıda OLMAYAN firmaya özel view/fonksiyon/prosedürler
        //    (ör. ORT_FISSATIR_ACIKLAMA view'ı: Orka her firmaya ayrıca kurar, şablonda yok, 2026'nın kataloğunda şifreli)
        // rebuild=false: sadece R'de olmayan nesneleri ekler. rebuild=true (ORKAGENELTANIM bağlıyken): TÜM program nesnelerini
        // temiz 2024'ten CREATE OR ALTER ile yeniden kurar → bozuk fonksiyon/prosedürler (ör. SELFCT) temizlenir, Orka fiş ekranı açılır.
        static void CopyModulesFromHelper(string R, string H, Action<string> log, bool rebuild = false)
        {
            string cs = Conn(R);
            var have = new HashSet<string>(Query(cs, "SELECT name FROM sys.objects WHERE is_ms_shipped=0;", 300).Select(r => Convert.ToString(r[0]) ?? ""), StringComparer.OrdinalIgnoreCase);
            var mods = Query(Conn(H), "SELECT o.name, o.type, m.definition FROM sys.objects o JOIN sys.sql_modules m ON m.object_id=o.object_id WHERE o.is_ms_shipped=0 AND o.type IN ('FN','IF','TF','V','P','TR') AND m.definition IS NOT NULL;", 300)
                       .Select(r => (name: Convert.ToString(r[0]) ?? "", type: (Convert.ToString(r[1]) ?? "").Trim(), def: Convert.ToString(r[2]) ?? ""))
                       .Where(m => m.name.Length > 0 && (rebuild || !have.Contains(m.name)))
                       .Select(m => (m.name, m.type, def: rebuild ? Regex.Replace(m.def, @"\bCREATE\s+(PROC|PROCEDURE|FUNCTION|VIEW|TRIGGER)\b", "CREATE OR ALTER $1", RegexOptions.IgnoreCase) : m.def))
                       .OrderBy(m => m.type is "FN" or "IF" or "TF" ? 0 : m.type == "V" ? 1 : m.type == "P" ? 2 : 3).ToList();
            int ok = 0; var lastErr = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int pass = 0; pass < 6 && mods.Count > 0; pass++)
            {
                var next = mods.Where(m => { try { ExecSql(cs, m.def, 300); ok++; return false; } catch (Exception ex) { lastErr[m.name] = First(ex.Message); return true; } }).ToList();
                if (next.Count == mods.Count) { mods = next; break; }
                mods = next;
            }
            if (ok > 0 || mods.Count > 0)
                log($"Program nesneleri (view/fonksiyon/prosedür) {(rebuild ? "temiz 2024'ten YENİDEN kuruldu" : "yardımcı veriden eklendi")}: {ok} {(rebuild ? "güncellendi" : "eklendi")}" + (mods.Count > 0 ? $" · {mods.Count} kurulamadı [" + string.Join("; ", mods.Take(4).Select(m => $"{m.name}({m.type}): {(lastErr.TryGetValue(m.name, out var e) ? e : "?")}")) + "]" : "") + ".");
        }

        // ── Fiş satırlarında kullanılıp hesap planında OLMAYAN hesaplar (planın o sayfası şifreli kalmış, önceki yılda da yok):
        //    adı cari karttan (ICE_CARIKART plankod/planadi/IDplan) alınır, yoksa "adı kurtarılamadı" ile açılır.
        //    Diğer alanlar (kebir, tür, nitelik bayrakları) aynı üst hesabın kardeş hesabından kopyalanır → Orka mizanda/fişte tanır.
        static void RepairPlanRefs(string R, string dir, Action<string> log, string? hDb = null)
        {
            LoadPlanNames(dir);
            string cs = Conn(R);
            string HQ = string.IsNullOrEmpty(hDb) ? "" : "[" + hDb!.Replace("]", "]]") + "]";
            var has = Query(cs, "SELECT OBJECT_ID('dbo.ORT_PLAN'), OBJECT_ID('dbo.ORT_FISSATIR'), OBJECT_ID('dbo.ICE_CARIKART');", 60)[0];
            if (has[0] == DBNull.Value || has[1] == DBNull.Value) return;
            bool cari = has[2] != DBNull.Value;
            // EN GÜVENİLİR KAYNAK: 2024 referansındaki aynı kodun adı → planda adı boş/placeholder kalanları doldur
            if (HQ.Length > 0)
                try
                {
                    int fromRef = Convert.ToInt32(Query(cs, $@"UPDATE p SET p.planadi = LTRIM(RTRIM(h.planadi))
                        FROM dbo.ORT_PLAN p JOIN {HQ}.dbo.ORT_PLAN h
                          ON LTRIM(RTRIM(h.plankod)) COLLATE DATABASE_DEFAULT = LTRIM(RTRIM(p.plankod)) COLLATE DATABASE_DEFAULT
                        WHERE (LTRIM(ISNULL(p.planadi,N''))=N'' OR p.planadi LIKE N'%(adı kurtarıl%)%')
                          AND LEN(LTRIM(ISNULL(h.planadi,N'')))>1 AND h.planadi NOT LIKE N'%(adı kurtarıl%)%';
                        SELECT @@ROWCOUNT;", 300)[0][0]);
                    if (fromRef > 0) log($"Hesap planı: {fromRef} hesabın eksik/placeholder adı 2024 referansından dolduruldu.");
                }
                catch (Exception ex) { log("2024 referansından ad doldurma atlandı: " + First(ex.Message)); }
            // planda adı boş kalan hesaplar → cari karttaki ad
            int named = 0;
            if (cari)
                try { named = Convert.ToInt32(Query(cs, "UPDATE p SET p.planadi=c.planadi FROM dbo.ORT_PLAN p JOIN dbo.ICE_CARIKART c ON c.plankod=p.plankod WHERE LTRIM(ISNULL(p.planadi,N''))=N'' AND LTRIM(ISNULL(c.planadi,N''))<>N''; SELECT @@ROWCOUNT;", 300)[0][0]); } catch { }
            // planda adı boş kalan hesaplar → diğer firmaların ortak planındaki ad (en az 2 firmada aynı)
            try
            {
                foreach (var r in Query(cs, "SELECT ID, LTRIM(RTRIM(plankod)) FROM dbo.ORT_PLAN WHERE LTRIM(ISNULL(planadi,N''))=N'' AND plankod IS NOT NULL;", 300))
                {
                    string? cn2 = PlanNameConsensus(Convert.ToString(r[1]) ?? "");
                    if (cn2 == null) continue;
                    ExecSql(cs, $"UPDATE dbo.ORT_PLAN SET planadi=N'{cn2.Replace("'", "''")}' WHERE ID={Convert.ToInt64(r[0])};", 60); named++;
                }
            }
            catch { }
            // satırdaki hesap kodu BOŞ (kolon şema-tamamlamayla sonradan eklendi, NULL geldi) ya da bozuk (torn: "10@ 1 1 00")
            // → hesap ID'si (IDHesapkodu) plandaki hesabı gösteriyorsa plandaki kodla DOLDUR.
            // ÖNEMLİ: Orka fiş DÜZELTME ekranı hesapkodu'nu doğrudan okur; boşsa satırları HİÇ göstermez (fiş no dolu, içi boş görünür).
            int fixedCodes = 0;
            try
            {
                fixedCodes = Convert.ToInt32(Query(cs, @"DISABLE TRIGGER ALL ON dbo.ORT_FISSATIR;
                    UPDATE s SET s.hesapkodu=p.plankod FROM dbo.ORT_FISSATIR s JOIN dbo.ORT_PLAN p ON p.ID=s.IDHesapkodu
                    WHERE p.plankod IS NOT NULL AND (s.hesapkodu IS NULL OR LTRIM(s.hesapkodu)=N''
                          OR (NOT EXISTS (SELECT 1 FROM dbo.ORT_PLAN q WHERE q.plankod=s.hesapkodu) AND LEN(ISNULL(s.hesapkodu,N''))=LEN(p.plankod)));
                    SELECT @@ROWCOUNT; ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;", 300)[0][0]);
                // hesapkebirkodu (ilk 3 hane) ve karşı hesap/kebir boş kaldıysa koddan türet
                try { ExecSql(cs, @"IF COL_LENGTH('dbo.ORT_FISSATIR','hesapkebirkodu') IS NOT NULL EXEC('DISABLE TRIGGER ALL ON dbo.ORT_FISSATIR; UPDATE dbo.ORT_FISSATIR SET hesapkebirkodu=TRY_CONVERT(int,LEFT(LTRIM(hesapkodu),3)) WHERE (hesapkebirkodu IS NULL OR hesapkebirkodu=0) AND hesapkodu IS NOT NULL; ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;');", 120); } catch { }
            }
            catch { try { ExecSql(cs, "ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;", 60); } catch { } }
            // hâlâ bozuk karakterli kodlar ("1@0 1 1 00"): bozuk karakter joker sayılır; planda ya da diğer satırlarda TEK bir sağlam karşılık varsa o
            try
            {
                var okRx = new Regex(@"^[\p{L}\p{N} .\-_/]+$");
                var all = Query(cs, "SELECT DISTINCT LTRIM(RTRIM(plankod)) FROM dbo.ORT_PLAN WHERE plankod IS NOT NULL UNION SELECT DISTINCT LTRIM(RTRIM(hesapkodu)) FROM dbo.ORT_FISSATIR WHERE hesapkodu IS NOT NULL;", 300)
                              .Select(q => Convert.ToString(q[0]) ?? "").Where(k => k.Length > 0).ToList();
                var good = all.Where(k => okRx.IsMatch(k)).ToList();
                foreach (var bad in all.Where(k => !okRx.IsMatch(k)))
                {
                    var cand = good.Where(g => g.Length == bad.Length && Enumerable.Range(0, g.Length).All(i => g[i] == bad[i] || !(char.IsLetterOrDigit(bad[i]) || " .-_/".IndexOf(bad[i]) >= 0))).Distinct().ToList();
                    if (cand.Count != 1) continue;
                    int n = Convert.ToInt32(Query(cs, $"DISABLE TRIGGER ALL ON dbo.ORT_FISSATIR; UPDATE dbo.ORT_FISSATIR SET hesapkodu=N'{cand[0].Replace("'", "''")}' WHERE LTRIM(RTRIM(hesapkodu))=N'{bad.Replace("'", "''")}'; SELECT @@ROWCOUNT; ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;", 120)[0][0]);
                    fixedCodes += n;
                }
            }
            catch { try { ExecSql(cs, "ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;", 60); } catch { } }
            if (fixedCodes > 0) log($"Fiş satırları: {fixedCodes} satırda bozuk hesap kodu, satırın bağlı olduğu hesabın kodu ile düzeltildi.");
            var need = Query(cs, @"SELECT k, MAX(pid) FROM (
                    SELECT s.hesapkodu AS k, s.IDHesapkodu AS pid FROM dbo.ORT_FISSATIR s
                    UNION ALL SELECT s.karsihesapkodu, NULL FROM dbo.ORT_FISSATIR s) x
                WHERE LTRIM(ISNULL(k,N''))<>N'' AND NOT EXISTS (SELECT 1 FROM dbo.ORT_PLAN p WHERE p.plankod=x.k) GROUP BY k;", 300);
            if (need.Count == 0) { if (named > 0) log($"Hesap planı: {named} hesabın boş adı cari karttan dolduruldu."); return; }
            var cols = Query(cs, "SELECT name, system_type_id, is_nullable, is_identity, is_computed FROM sys.columns WHERE object_id=OBJECT_ID('dbo.ORT_PLAN') ORDER BY column_id;", 60)
                       .Select(q => (name: Convert.ToString(q[0]) ?? "", type: Convert.ToInt32(q[1]), nul: Convert.ToBoolean(q[2]), ident: Convert.ToBoolean(q[3]), comp: Convert.ToBoolean(q[4])))
                       .Where(k => !k.comp && k.type != 189).ToList();
            bool ident = cols.Any(k => k.ident);
            string Q(string x) => "[" + x.Replace("]", "]]") + "]";
            string S(string x) => "N'" + x.Replace("'", "''") + "'";
            int made = 0, fromCari = 0, fromCommon = 0, fromRef2 = 0; var noName = new List<string>();
            foreach (var r in need)
            {
                string kod = Convert.ToString(r[0])!.Trim();
                if (kod.Length > 60 || !Regex.IsMatch(kod, @"^[\p{L}\p{N} .\-_/]+$")) { noName.Add(kod + "(bozuk kod, açılmadı)"); continue; }
                long? pid = r[1] == DBNull.Value ? null : Convert.ToInt64(r[1]);
                string? ad = null; long? cid = null; bool refName = false;
                // en güvenilir: 2024 referansındaki aynı kodun adı
                if (HQ.Length > 0)
                    try
                    {
                        var hr = Query(cs, $"SELECT TOP 1 LTRIM(RTRIM(planadi)) FROM {HQ}.dbo.ORT_PLAN WHERE LTRIM(RTRIM(plankod)) COLLATE DATABASE_DEFAULT={S(kod)} COLLATE DATABASE_DEFAULT AND LEN(LTRIM(ISNULL(planadi,N'')))>1 AND planadi NOT LIKE N'%(adı kurtarıl%)%;", 60);
                        if (hr.Count > 0 && hr[0][0] != DBNull.Value) { ad = Convert.ToString(hr[0][0]); refName = true; }
                    }
                    catch { }
                if (cari)
                    try
                    {
                        var cr = Query(cs, $"SELECT TOP 1 planadi, IDplan FROM dbo.ICE_CARIKART WHERE plankod={S(kod)} ORDER BY ID DESC;", 60);
                        if (cr.Count > 0) { if (string.IsNullOrWhiteSpace(ad) && cr[0][0] != DBNull.Value) ad = Convert.ToString(cr[0][0]); cid = cr[0][1] == DBNull.Value ? null : Convert.ToInt64(cr[0][1]); }
                    }
                    catch { }
                // kardeş hesap: aynı üst koddan (320 1 00035 → "320 1 …"), yoksa aynı kebirden
                string parent = kod.Contains(' ') ? kod.Substring(0, kod.LastIndexOf(' ')) : kod;
                string kebir = kod.Length >= 3 ? kod.Substring(0, 3) : kod;
                int lvl = kod.Count(ch => ch == ' ');
                var tpl = Query(cs, $@"SELECT TOP 1 ID FROM dbo.ORT_PLAN WHERE plankod LIKE {S(parent + " %")} ORDER BY plankod DESC;", 60);
                if (tpl.Count == 0) tpl = Query(cs, $@"SELECT TOP 1 ID FROM dbo.ORT_PLAN WHERE plankod LIKE {S(kebir + "%")} ORDER BY ABS(LEN(plankod)-LEN(REPLACE(plankod,' ',''))-{lvl}), plankod DESC;", 60);
                if (tpl.Count == 0) tpl = Query(cs, "SELECT TOP 1 ID FROM dbo.ORT_PLAN ORDER BY ID;", 60);
                if (tpl.Count == 0) break;
                long tid = Convert.ToInt64(tpl[0][0]);
                long FreeId(long? want) =>
                    want is long w && w > 0 && Convert.ToInt64(Query(cs, $"SELECT COUNT(*) FROM dbo.ORT_PLAN WHERE ID={w};", 60)[0][0]) == 0 ? w
                    : Convert.ToInt64(Query(cs, "SELECT ISNULL(MAX(ID),0)+1 FROM dbo.ORT_PLAN;", 60)[0][0]);
                long id = FreeId(pid ?? cid);
                if (pid == null && cid != null && id != cid) id = FreeId(null);
                bool common = false;
                if (string.IsNullOrWhiteSpace(ad)) { ad = PlanNameConsensus(kod); common = ad != null; }
                string name = string.IsNullOrWhiteSpace(ad) ? kod + " (adı kurtarılamadı)" : ad!.Trim();
                if (name.Length > 100) name = name.Substring(0, 100);
                var sel = new List<string>();
                foreach (var col in cols)
                {
                    string n = col.name;
                    if (n.Equals("ID", StringComparison.OrdinalIgnoreCase)) sel.Add(id.ToString());
                    else if (n.Equals("plankod", StringComparison.OrdinalIgnoreCase)) sel.Add(S(kod));
                    else if (n.Equals("planadi", StringComparison.OrdinalIgnoreCase)) sel.Add(S(name));
                    else if (col.type == 36) sel.Add("NEWID()");
                    else if (col.type == 231 || col.type == 167 || col.type == 239 || col.type == 175 || col.type == 99 || col.type == 35) sel.Add(col.nul ? "NULL" : "N''");
                    else if ((col.type == 61 || col.type == 42) && Regex.IsMatch(n, "firstdate|editdate", RegexOptions.IgnoreCase)) sel.Add("GETDATE()");
                    else sel.Add("t." + Q(n));
                }
                string sql = (ident ? "SET IDENTITY_INSERT dbo.ORT_PLAN ON; " : "") + "DISABLE TRIGGER ALL ON dbo.ORT_PLAN; " +
                             $"INSERT INTO dbo.ORT_PLAN ({string.Join(",", cols.Select(k => Q(k.name)))}) SELECT {string.Join(",", sel)} FROM dbo.ORT_PLAN t WHERE t.ID={tid}; " +
                             "ENABLE TRIGGER ALL ON dbo.ORT_PLAN;" + (ident ? " SET IDENTITY_INSERT dbo.ORT_PLAN OFF;" : "");
                try
                {
                    ExecSql(cs, sql, 120); made++;
                    if (ad != null) { if (refName) fromRef2++; else if (common) fromCommon++; else fromCari++; } else noName.Add(kod);
                    // bu kodu kullanan fiş satırlarının hesap ID'si plana bağlansın
                    try { ExecSql(cs, $"DISABLE TRIGGER ALL ON dbo.ORT_FISSATIR; UPDATE dbo.ORT_FISSATIR SET IDHesapkodu={id} WHERE hesapkodu={S(kod)} AND (IDHesapkodu IS NULL OR NOT EXISTS (SELECT 1 FROM dbo.ORT_PLAN p WHERE p.ID=ORT_FISSATIR.IDHesapkodu)); ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;", 120); }
                    catch { try { ExecSql(cs, "ENABLE TRIGGER ALL ON dbo.ORT_FISSATIR;", 60); } catch { } }
                }
                catch { try { ExecSql(cs, "ENABLE TRIGGER ALL ON dbo.ORT_PLAN;" + (ident ? " SET IDENTITY_INSERT dbo.ORT_PLAN OFF;" : ""), 60); } catch { } }
            }
            log($"Hesap planı: fişlerde kullanılıp planda olmayan {need.Count} hesap → {made} açıldı ({fromRef2} adı 2024 referansından, {fromCari} adı cari karttan" + (fromCommon > 0 ? $", {fromCommon} adı diğer firmaların ortak planından" : "") +
                (noName.Count > 0 ? $", {noName.Count} adı kurtarılamadı: " + string.Join(", ", noName.Take(8)) + (noName.Count > 8 ? " …" : "") : "") + ")" +
                (named > 0 ? $" · {named} hesabın boş adı cari karttan dolduruldu" : "") + ".");
        }

        // ── Referans DB'yi müşterinin program sürümüne getir: eksik/eski fonksiyon-view-prosedür-trigger + eksik varsayılan değerler.
        //    (Orka 'Eksik Alan Kontrolleri' → "Cannot find ... dbo.fnc_pub_saatcevrimleri": referansta olmayan yeni sürüm fonksiyonu)
        static void SyncModules(string R, Ctx c, Pool? pool, Action<string> log)
        {
            string cs = Conn(R);
            bool Try(string sql) { try { ExecSql(cs, sql, 600); return true; } catch { return false; } }
            static string Norm(string x) => Regex.Replace(x ?? "", @"\s+", " ").Trim().ToUpperInvariant();
            static bool Plausible(string t) => t.Length > 20 && t.IndexOf('\0') < 0 && Regex.IsMatch(t, @"\bCREATE\s+(OR\s+ALTER\s+)?(FUNCTION|PROC|PROCEDURE|VIEW|TRIGGER)\b", RegexOptions.IgnoreCase);
            static string FirstOnly(string t)
            {
                var m = Regex.Match(t, @"\bCREATE\s+(OR\s+ALTER\s+)?(?=(FUNCTION|PROC|PROCEDURE|VIEW|TRIGGER)\b)", RegexOptions.IgnoreCase);
                return m.Success ? t.Substring(0, m.Index) + "ALTER " + t.Substring(m.Index + m.Length) : t;
            }

            var have = new Dictionary<string, (string type, string def)>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Query(cs, "SELECT o.name, o.type, ISNULL(m.definition,'') FROM sys.objects o JOIN sys.sql_modules m ON m.object_id=o.object_id WHERE o.is_ms_shipped=0;", 300))
                have[Convert.ToString(r[0]) ?? ""] = (Convert.ToString(r[1]) ?? "", Convert.ToString(r[2]) ?? "");

            var mods = new Dictionary<string, (string type, string text)>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in c.Objs.Where(o => IsModuleType(o.Value.type)))
                if (c.ModText.TryGetValue(o.Key, out var t) && Plausible(t)) mods[o.Value.name] = (o.Value.type, t);
            int fromPool = 0;
            if (pool != null)
            {
                // bu dosyanın kataloğunda adı geçen ama metni şifreli kalanlar
                foreach (var o in c.Objs.Where(o => IsModuleType(o.Value.type)))
                    if (!mods.ContainsKey(o.Value.name) && pool.Modules.TryGetValue(o.Value.name, out var pm) && pm.type == o.Value.type && Plausible(pm.text))
                    { mods[o.Value.name] = (pm.type, pm.text); fromPool++; }
                // adı da şifreli kalmış ama bu dosyanın kendi fonksiyon/view metinlerinde ADI GEÇEN nesneler (ör. ORT_FISSATIR_ACIKLAMA)
                var allText = string.Join("\n", c.ModText.Values);
                foreach (var kv in pool.Modules)
                    if (!mods.ContainsKey(kv.Key) && !have.ContainsKey(kv.Key) && Plausible(kv.Value.text)
                        && allText.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    { mods[kv.Key] = (kv.Value.type, kv.Value.text); fromPool++; }
            }
            int Rank(string t) => t == "FN" || t == "IF" || t == "TF" ? 0 : t == "V " ? 1 : t == "P " ? 2 : 3;
            var todo = mods.Where(m => !have.TryGetValue(m.Key, out var h) || Norm(h.def) != Norm(m.Value.text)).OrderBy(m => Rank(m.Value.type)).ToList();
            int same = mods.Count - todo.Count, added = 0, updated = 0;
            for (int pass = 0; pass < 5 && todo.Count > 0; pass++)
            {
                var next = new List<KeyValuePair<string, (string type, string text)>>();
                foreach (var m in todo)
                {
                    bool exists = have.ContainsKey(m.Key);
                    if (exists ? Try(FirstOnly(m.Value.text)) : Try(m.Value.text)) { if (exists) updated++; else { added++; have[m.Key] = (m.Value.type, m.Value.text); } }
                    else next.Add(m);
                }
                if (next.Count == todo.Count) { todo = next; break; }
                todo = next;
            }
            var missingStill = todo.Where(m => !have.ContainsKey(m.Key)).Select(m => m.Key).ToList();
            log($"Program nesneleri (fonksiyon/view/prosedür/trigger): {added} eklendi · {updated} müşteri sürümüne güncellendi · {same} zaten aynı" +
                (fromPool > 0 ? $" · {fromPool} metni kardeş dosyalardan" : "") +
                (todo.Count > 0 ? $" · {todo.Count} kurulamadı" + (missingStill.Count > 0 ? " (eksik: " + string.Join(", ", missingStill.Take(6)) + (missingStill.Count > 6 ? " …" : "") + ")" : " (referanstaki hali kaldı)") : ""));

            // varsayılan değerler: referansta varsayılanı olmayan kolonlar (özellikle sonradan eklenen yeni sürüm kolonları)
            var cols = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var hasDef = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Query(cs, "SELECT t.name, c.name, c.default_object_id FROM sys.columns c JOIN sys.tables t ON t.object_id=c.object_id WHERE t.is_ms_shipped=0;", 300))
            {
                string k = (Convert.ToString(r[0]) ?? "") + "|" + (Convert.ToString(r[1]) ?? "");
                cols.Add(k); if (Convert.ToInt32(r[2]) != 0) hasDef.Add(k);
            }
            var defs = new Dictionary<string, (string name, string text)>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in c.Objs.Where(o => o.Value.type == "D "))
                if (c.ObjParent.TryGetValue(o.Key, out var par) && c.Objs.TryGetValue(par.pid, out var pt) && c.Cols.TryGetValue(par.pid, out var pc) && pc.TryGetValue(par.intprop, out var dc) && c.ModText.TryGetValue(o.Key, out var txt) && txt.Length > 0 && txt.Length < 400)
                    defs[pt.name + "|" + dc.Name] = (o.Value.name, txt);
            if (pool != null) foreach (var kv in pool.Defaults) if (!defs.ContainsKey(kv.Key) && kv.Value.text.Length < 400) defs[kv.Key] = (kv.Value.name, kv.Value.text);
            int dOk = 0;
            string Q(string x) => "[" + x.Replace("]", "]]") + "]";
            foreach (var kv in defs)
            {
                if (!cols.Contains(kv.Key) || hasDef.Contains(kv.Key)) continue;
                var p = kv.Key.Split('|'); string tx = kv.Value.text.Trim();
                if (tx.IndexOf('\0') >= 0 || !tx.StartsWith("(")) continue;
                if (Try($"ALTER TABLE [dbo].{Q(p[0])} ADD CONSTRAINT {Q(kv.Value.name)} DEFAULT {tx} FOR {Q(p[1])};") ||
                    Try($"ALTER TABLE [dbo].{Q(p[0])} ADD CONSTRAINT {Q("DF_" + p[0] + "_" + p[1])} DEFAULT {tx} FOR {Q(p[1])};")) { dOk++; hasDef.Add(kv.Key); }
            }
            if (dOk > 0) log($"Eksik varsayılan değer: {dOk} kolona eklendi (yeni kayıt girerken boş alan hatası olmasın).");
        }

        // modül metni: yalnız ASCII/Latin1 karakter varsa 1 bayt, 'İ ş ğ ı' gibi bir karakter varsa UTF-16 saklanır
        static string DecodeModText(byte[] raw)
        {
            int n = Math.Min(raw.Length, 800) & ~1, z = 0;
            for (int i = 1; i < n; i += 2) if (raw[i] == 0) z++;
            if (raw.Length % 2 == 0 && n >= 8 && z * 10 >= (n / 2) * 6) return Encoding.Unicode.GetString(raw).TrimEnd('\0');
            return Encoding.Latin1.GetString(raw).TrimEnd('\0');
        }
        // dosya ailesi: rakamlar # → ORKA_0015_2026.mdf.xxx = ORKA_#_#
        internal static string FamilyOf(string path)
        {
            string n = Path.GetFileName(path);
            int k = n.IndexOf(".mdf", StringComparison.OrdinalIgnoreCase); if (k > 0) n = n.Substring(0, k);
            return Regex.Replace(n, @"\d+", "#").ToUpperInvariant();
        }
        static bool IsModuleType(string ty) => ty == "FN" || ty == "IF" || ty == "TF" || ty == "V " || ty == "P " || ty == "TR";

        // ── Yapıyı geri kur: varsayılan değerler, PK/UQ/index'ler, fonksiyon/view/prosedür/trigger'lar.
        //    Önce dosyanın kendi kataloğu; şifreli kalan kısım havuzdan (aynı programın diğer dosyalarından).
        static void ApplyStructure(string cs, Ctx c, Dictionary<int, (string tn, string orig, Dictionary<int, string> cols, Dictionary<string, string> types)> created,
                                   HashSet<int> pkMade, Pool? pool, Action<string> log)
        {
            int dOk = 0, dFail = 0, iOk = 0, iFail = 0, iWeak = 0, mOk = 0, mFail = 0;
            string Q(string x) => "[" + x.Replace("]", "]]") + "]";
            bool Try(string sql) { try { ExecSql(cs, sql, 3600); return true; } catch { return false; } }

            // 1) fonksiyon / view / prosedür / trigger — bağımlılık sırası bilinmediği için birkaç tur
            var mods = new Dictionary<string, (string type, string text)>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in c.Objs.Where(o => IsModuleType(o.Value.type)))
                if (c.ModText.TryGetValue(o.Key, out var txt)) mods[o.Value.name] = (o.Value.type, txt);
            int fromPool = 0;
            // havuzdan yalnız BU veritabanında var olduğu bilinen (kataloğunda adı geçen) ama metni şifreli kalan modüller
            //  → başka tür veritabanının prosedürleri buraya taşınmaz
            if (pool != null)
                foreach (var o in c.Objs.Where(o => IsModuleType(o.Value.type)))
                    if (!mods.ContainsKey(o.Value.name) && pool.Modules.TryGetValue(o.Value.name, out var pm) && pm.type == o.Value.type)
                    { mods[o.Value.name] = (pm.type, pm.text); fromPool++; }
            int Rank(string t) => t == "FN" || t == "IF" || t == "TF" ? 0 : t == "V " ? 1 : t == "P " ? 2 : 3;
            var left = mods.OrderBy(m => Rank(m.Value.type)).ToList();
            for (int pass = 0; pass < 4 && left.Count > 0; pass++)
            {
                var next = new List<KeyValuePair<string, (string type, string text)>>();
                foreach (var m in left) if (Try(m.Value.text)) mOk++; else next.Add(m);
                if (next.Count == left.Count) { left = next; break; }
                left = next;
            }
            mFail = left.Count;
            // 2) varsayılan değerler (DEFAULT)
            var hasDef = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in c.Objs.Where(o => o.Value.type == "D "))
            {
                if (!c.ObjParent.TryGetValue(o.Key, out var par) || !created.TryGetValue(par.pid, out var ct) || !ct.cols.TryGetValue(par.intprop, out var col) || !c.ModText.TryGetValue(o.Key, out var txt)) continue;
                if (!hasDef.Add(ct.tn + "|" + col)) continue;
                if (Try($"ALTER TABLE [dbo].{Q(ct.tn)} ADD CONSTRAINT {Q(o.Value.name)} DEFAULT {txt} FOR {Q(col)};")) dOk++; else dFail++;
            }
            if (pool != null)
                foreach (var ct in created.Values)
                    foreach (var col in ct.cols.Values)
                    {
                        if (hasDef.Contains(ct.tn + "|" + col) || !pool.Defaults.TryGetValue(ct.orig + "|" + col, out var pd)) continue;
                        hasDef.Add(ct.tn + "|" + col);
                        if (Try($"ALTER TABLE [dbo].{Q(ct.tn)} ADD CONSTRAINT {Q(pd.name)} DEFAULT {pd.text} FOR {Q(col)};") ||
                            Try($"ALTER TABLE [dbo].{Q(ct.tn)} ADD CONSTRAINT {Q("DF_" + ct.tn + "_" + col)} DEFAULT {pd.text} FOR {Q(col)};")) dOk++; else dFail++;
                    }

            // 3) index'ler: önce kümelenmiş (clustered), sonra diğerleri
            var idxList = new List<(int id, string name, int status, byte type, List<string> keys)>();
            var ownIdxTables = new HashSet<int>();
            foreach (var ix in c.Idx)
            {
                if (!created.TryGetValue(ix.id, out var ct) || !c.IdxCols.TryGetValue((ix.id, ix.indid), out var icl)) continue;
                var keys = icl.Where(z => z.ord > 0).OrderBy(z => z.ord).Select(z => ct.cols.TryGetValue(z.colid, out var nm) ? nm : null).ToList();
                if (keys.Count == 0 || keys.Any(k => k == null)) continue;
                idxList.Add((ix.id, ix.name, ix.status, ix.type, keys!));
                ownIdxTables.Add(ix.id);
            }
            if (pool != null)
                foreach (var kv in created.Where(kv => !ownIdxTables.Contains(kv.Key)))
                {
                    if (!pool.Indexes.TryGetValue(kv.Value.orig, out var pim)) continue;
                    var names = new HashSet<string>(kv.Value.cols.Values, StringComparer.OrdinalIgnoreCase);
                    foreach (var pi in pim)
                    {
                        var keys = pi.Value.keys.Split(',').ToList();
                        if (keys.All(k => names.Contains(k))) idxList.Add((kv.Key, pi.Key, pi.Value.status, pi.Value.type, keys));
                    }
                }
            foreach (var ix in idxList.OrderBy(x => x.type).ThenByDescending(x => (x.status & 0x20) != 0))
            {
                var ct = created[ix.id];
                bool pk = (ix.status & 0x20) != 0, uq = (ix.status & 0x40) != 0, unique = (ix.status & 0x8) != 0;
                if (pk && pkMade.Contains(ix.id)) continue;
                if (pk)   // PK boş olabilen kolona kurulamaz → anahtar kolonları NOT NULL yap (veride boş yoksa)
                    foreach (var k in ix.keys)
                        if (ct.types.TryGetValue(k, out var kt)) Try($"ALTER TABLE [dbo].{Q(ct.tn)} ALTER COLUMN {Q(k)} {kt} NOT NULL;");
                string cl = ix.type == 1 ? "CLUSTERED" : "NONCLUSTERED";
                string cols = string.Join(", ", ix.keys.Select(Q));
                string sql = pk ? $"ALTER TABLE [dbo].{Q(ct.tn)} ADD CONSTRAINT {Q(ix.name)} PRIMARY KEY {cl} ({cols});"
                           : uq ? $"ALTER TABLE [dbo].{Q(ct.tn)} ADD CONSTRAINT {Q(ix.name)} UNIQUE {cl} ({cols});"
                           : $"CREATE {(unique ? "UNIQUE " : "")}{cl} INDEX {Q(ix.name)} ON [dbo].{Q(ct.tn)} ({cols});";
                if (Try(sql)) { iOk++; continue; }
                // tekrar eden anahtar (şifreli sayfalardan kalan eski kopya vb.) → benzersizlik olmadan index
                if ((pk || uq || unique) && Try($"CREATE {cl} INDEX {Q(ix.name)} ON [dbo].{Q(ct.tn)} ({cols});")) { iWeak++; continue; }
                iFail++;
            }

            log($"Yapı: {dOk:N0} varsayılan değer · {iOk:N0} index/PK/UQ" + (iWeak > 0 ? $" (+{iWeak} tekrar eden anahtar yüzünden benzersizliksiz)" : "") +
                $" · {mOk:N0} fonksiyon/view/prosedür/trigger" + (fromPool > 0 ? $" ({fromPool} tanesi havuzdan)" : "") +
                (dFail + iFail + mFail > 0 ? $" · kurulamayan: {dFail} varsayılan, {iFail} index, {mFail} modül" : ""));
        }

        static bool MakeIdentityPk(string cs, string tn, List<string> colNames, int keyIdx, string keyType, List<string> defs, bool addPk = true)
        {
            try
            {
                string k = colNames[keyIdx];
                var q = Query(cs, $"SELECT COUNT(*), COUNT([{k}]), COUNT(DISTINCT [{k}]), ISNULL(MIN([{k}]),0) FROM [dbo].[{tn}];", 600)[0];
                long n = Convert.ToInt64(q[0]), nn = Convert.ToInt64(q[1]), nd = Convert.ToInt64(q[2]), mn = Convert.ToInt64(q[3]);
                if (nn != n || mn < 0) return false;                 // IDENTITY kolonunda boş değer olamaz
                if (addPk && nd != n) addPk = false;                 // PK için benzersiz olmalı (yoksa sadece IDENTITY); boş tabloda PK olur
                string tmp = Safe(tn + "__vkm_tmp");
                var d2 = defs.Select((d, i) => i == keyIdx ? $"[{k}] {keyType} IDENTITY(1,1) NOT NULL" : d).ToList();
                string cols = string.Join(",", colNames.Select(c => "[" + c + "]"));
                string pk = addPk ? $", CONSTRAINT [PK_{Safe(tn)}] PRIMARY KEY CLUSTERED ([{k}])" : "";
                ExecSql(cs, $@"CREATE TABLE [dbo].[{tmp}] ({string.Join(", ", d2)}{pk});
SET IDENTITY_INSERT [dbo].[{tmp}] ON;
INSERT INTO [dbo].[{tmp}] ({cols}) SELECT {cols} FROM [dbo].[{tn}];
SET IDENTITY_INSERT [dbo].[{tmp}] OFF;
DROP TABLE [dbo].[{tn}];
EXEC sp_rename N'dbo.{tmp.Replace("'", "''")}', N'{tn.Replace("'", "''")}';
DBCC CHECKIDENT(N'dbo.{tn.Replace("'", "''")}', RESEED) WITH NO_INFOMSGS;", 3600);
                return true;
            }
            catch { return false; }
        }

        // ───────────────────────── REFERANS DB ─────────────────────────
        // kaynağın önyükleme sayfası (9) sağlamsa uyumluluk seviyesi (dbi_cmptlevel, sayfa içi 410)
        static int SourceCompat(string src)
        {
            try
            {
                using var fs = File.OpenRead(src); var b = new byte[PAGE];
                if (ReadPage(fs, 9, b) && b[0] == 1 && b[1] == 13) { int v = U16(b, 410); if (v >= 100 && v <= 170 && v % 10 == 0) { KnownCompat = v; return v; } }
            }
            catch { }
            return 0;
        }

        // "ORKAGENELTANIM.mdf.pegasus_…" → "ORKAGENELTANIM"
        static string OrigDbName(string src)
        {
            string n = Path.GetFileName(src);
            int i = n.IndexOf(".mdf", StringComparison.OrdinalIgnoreCase);
            if (i < 0) i = n.IndexOf(".ndf", StringComparison.OrdinalIgnoreCase);
            n = i > 0 ? n[..i] : Path.GetFileNameWithoutExtension(n);
            n = new string(n.Where(ch => char.IsLetterOrDigit(ch) || ch == '_' || ch == '-').ToArray());
            return n.Length is > 0 and <= 120 && !n.StartsWith("VKM_", StringComparison.OrdinalIgnoreCase) ? n : "";
        }

        // Tam tutarlılık kontrolü; hata varsa yalnızca veri KAYBETMEYEN onarım (REPAIR_REBUILD)
        static void CheckDb(string db, Action<string> log)
        {
            string m = Conn("master");
            List<string> Run(string sql)
            {
                var errs = new List<string>();
                try
                {
                    using var cn = new SqlConnection(m); cn.Open();
                    cn.FireInfoMessageEventOnUserErrors = true;
                    cn.InfoMessage += (_, a) => { foreach (SqlError er in a.Errors) if (er.Class > 10 && er.Number != 8989) errs.Add(er.Message); };
                    using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 7200 }; cmd.ExecuteNonQuery();
                }
                catch (SqlException ex) { foreach (SqlError er in ex.Errors) if (er.Number != 8989) errs.Add(er.Message); }
                catch (Exception ex) { errs.Add(ex.Message); }
                return errs;
            }
            string q = db.Replace("]", "]]");
            log("Tutarlılık kontrolü (DBCC CHECKDB) yapılıyor…");
            var e1 = Run($"DBCC CHECKDB([{q}]) WITH NO_INFOMSGS;");
            if (e1.Count == 0) { log("Tutarlılık kontrolü: temiz ✓ (sayfa, index, bağlantı hatası yok)"); return; }
            log($"Tutarlılık kontrolü: {e1.Count} uyarı (ilk: {First(e1[0])}) → veri kaybetmeyen onarım (REPAIR_REBUILD) deneniyor…");
            Run($"ALTER DATABASE [{q}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DBCC CHECKDB([{q}], REPAIR_REBUILD) WITH NO_INFOMSGS; ALTER DATABASE [{q}] SET MULTI_USER;");
            SqlConnection.ClearAllPools();
            try { ExecSql(m, $"ALTER DATABASE [{q}] SET MULTI_USER;", 60); } catch { }
            var e2 = Run($"DBCC CHECKDB([{q}]) WITH NO_INFOMSGS;");
            log(e2.Count == 0 ? "Onarıldı → tutarlılık kontrolü temiz ✓"
                              : $"⚠ {e2.Count} uyarı kaldı (ilk: {First(e2[0])}). Veri silen onarım yapılmadı; .bak yine de açılır.");
        }

        // GNL_FIRMATANIM → vergi/TC no ile firma veritabanı adı eşlemesi (dosyaya da yazılır: sonraki çalıştırmalar için)
        static void LearnFirms(string db, string dir, Action<string> log)
        {
            try
            {
                string cs = Conn(db);
                var h = Query(cs, "SELECT COL_LENGTH(N'dbo.GNL_FIRMATANIM',N'veritabaniadi'), COL_LENGTH(N'dbo.GNL_FIRMATANIM',N'vergino'), COL_LENGTH(N'dbo.GNL_FIRMATANIM',N'tckimliknr');", 60)[0];
                if (h[0] == DBNull.Value || (h[1] == DBNull.Value && h[2] == DBNull.Value)) return;
                string sel = "veritabaniadi, " + (h[1] != DBNull.Value ? "vergino" : "NULL") + ", " + (h[2] != DBNull.Value ? "tckimliknr" : "NULL");
                int before = FirmMap.Count;
                foreach (var r in Query(cs, $"SELECT DISTINCT {sel} FROM dbo.GNL_FIRMATANIM WHERE veritabaniadi IS NOT NULL;", 300))
                {
                    string dbn = (Convert.ToString(r[0]) ?? "").Trim(); if (dbn.Length == 0) continue;
                    var ym = Regex.Match(dbn, @"_(\d{4})$"); int y = ym.Success ? int.Parse(ym.Groups[1].Value) : 0;
                    for (int k = 1; k <= 2; k++) AddFirm(new string((Convert.ToString(r[k]) ?? "").Where(char.IsDigit).ToArray()), y, dbn);
                }
                if (FirmMap.Count == 0) return;
                try { File.WriteAllLines(Path.Combine(dir, "VKM_firmalar.tsv"), FirmMap.SelectMany(kv => kv.Value.Select(v => kv.Key + "\t" + v.year + "\t" + v.db))); } catch { }
                if (FirmMap.Count > before) log($"Firma listesi öğrenildi: {FirmMap.Count:N0} vergi/TC no → kurtarılan e-Faturalar firmalarına bağlanacak.");
            }
            catch { }
        }

        static void AddFirm(string no, int y, string dbn)
        {
            if (no.Length is not (10 or 11) || no.All(ch => ch == '0')) return;
            if (!FirmMap.TryGetValue(no, out var li)) FirmMap[no] = li = new();
            if (!li.Any(z => z.year == y && z.db.Equals(dbn, StringComparison.OrdinalIgnoreCase))) li.Add((y, dbn));
        }

        static void LoadFirms(string dir)
        {
            if (FirmMap.Count > 0) return;
            try
            {
                string f = Path.Combine(dir, "VKM_firmalar.tsv"); if (!File.Exists(f)) return;
                foreach (var l in File.ReadAllLines(f)) { var p = l.Split('\t'); if (p.Length == 3 && int.TryParse(p[1], out int y)) AddFirm(p[0], y, p[2]); }
            }
            catch { }
        }

        // e-Fatura/e-İrsaliye XML → satıcı (yoksa alıcı) VKN/TCKN + düzenleme yılı → firma veritabanı adı
        static string? FirmOf(byte[] data)
        {
            if (FirmMap.Count == 0 || data.Length < 100) return null;
            string s;
            try { s = data[0] == 0xFF && data[1] == 0xFE ? Encoding.Unicode.GetString(data) : Encoding.UTF8.GetString(data); } catch { return null; }
            int year = 0; var dm = Regex.Match(s, @"IssueDate>\s*(\d{4})-"); if (dm.Success) year = int.Parse(dm.Groups[1].Value);
            foreach (var part in new[] { "SupplierParty", "CustomerParty" })
            {
                var pm = Regex.Match(s, part + @">(.*?)" + part + ">", RegexOptions.Singleline);
                if (!pm.Success) continue;
                foreach (Match im in Regex.Matches(pm.Groups[1].Value, @"schemeID\s*=\s*""(?:VKN|TCKN)""[^>]*>\s*(\d{10,11})\s*<"))
                    if (FirmMap.TryGetValue(im.Groups[1].Value, out var li) && li.Count > 0)
                        return li.Where(z => z.year == year).Select(z => z.db).FirstOrDefault()
                            ?? li.Where(z => z.year > 0 && z.year <= year).OrderByDescending(z => z.year).Select(z => z.db).FirstOrDefault()
                            ?? li.OrderByDescending(z => z.year).First().db;
            }
            return null;
        }

        // SQL motoru (LocalDB / servis hesabı) kopyalanan dosyaları yazabilsin: klasöre herkes için tam yetki (Everyone SID)
        static void GrantAll(string dir)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("icacls", $"\"{dir.TrimEnd('\\')}\" /grant *S-1-1-0:(OI)(CI)F /T /Q")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
                using var p = System.Diagnostics.Process.Start(psi); p?.WaitForExit(60000);
            }
            catch { }
        }

        static bool LoadRef(string refPath, string refDb, string work, out string err)
        {
            err = "";
            string cs0 = Conn("master");
            try
            {
                try { ExecSql(cs0, $"IF DB_ID('{refDb}') IS NOT NULL BEGIN ALTER DATABASE [{refDb}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{refDb}]; END", 120); } catch { }
                if (refPath.EndsWith(".bak", StringComparison.OrdinalIgnoreCase) || IsBackupFile(refPath))
                {
                    var files = Query(cs0, $"RESTORE FILELISTONLY FROM DISK = N'{Esc(refPath)}';", 600);
                    var moves = new List<string>(); int i = 0;
                    foreach (var f in files)
                    {
                        string logical = Convert.ToString(f[0]) ?? "", type = (Convert.ToString(f[2]) ?? "D").Trim();
                        string target = type switch
                        {
                            "L" => Path.Combine(work, $"{refDb}_log{i}.ldf"),
                            "D" => Path.Combine(work, $"{refDb}_dat{i}" + (i == 0 ? ".mdf" : ".ndf")),
                            _ => Path.Combine(work, $"{refDb}_x{i}")
                        };
                        moves.Add($"MOVE N'{Esc(logical)}' TO N'{Esc(target)}'"); i++;
                    }
                    ExecSql(cs0, $"RESTORE DATABASE [{refDb}] FROM DISK = N'{Esc(refPath)}' WITH {string.Join(", ", moves)}, REPLACE, RECOVERY;", 7200);
                }
                else
                {
                    Directory.CreateDirectory(work);
                    string wm = Path.Combine(work, refDb + ".mdf");
                    File.Copy(refPath, wm, true);
                    File.SetAttributes(wm, FileAttributes.Normal);
                    // yanındaki log dosyası (ORKARESIMLER_log.LDF / ORKARESIMLER.ldf) varsa birlikte bağla — kapanmadan kopyalanmış MDF de açılır
                    string dirR = Path.GetDirectoryName(refPath) ?? ".", bn = Path.GetFileNameWithoutExtension(refPath);
                    string? ldf = Directory.EnumerateFiles(dirR).FirstOrDefault(f =>
                        Path.GetExtension(f).Equals(".ldf", StringComparison.OrdinalIgnoreCase) &&
                        (Path.GetFileNameWithoutExtension(f).Equals(bn + "_log", StringComparison.OrdinalIgnoreCase) || Path.GetFileNameWithoutExtension(f).Equals(bn, StringComparison.OrdinalIgnoreCase)));
                    bool attached = false;
                    if (ldf != null)
                        try
                        {
                            string wl = Path.Combine(work, refDb + "_log.ldf");
                            File.Copy(ldf, wl, true); File.SetAttributes(wl, FileAttributes.Normal);
                            GrantAll(work);
                            ExecSql(cs0, $"CREATE DATABASE [{refDb}] ON (FILENAME = N'{Esc(wm)}'), (FILENAME = N'{Esc(wl)}') FOR ATTACH;", 1800);
                            attached = true;
                        }
                        catch { try { ExecSql(cs0, $"IF DB_ID('{refDb}') IS NOT NULL DROP DATABASE [{refDb}];", 120); } catch { } }
                    if (!attached)
                    {
                        GrantAll(work);
                        ExecSql(cs0, $"CREATE DATABASE [{refDb}] ON (FILENAME = N'{Esc(wm)}') FOR ATTACH_REBUILD_LOG;", 1800);
                    }
                }
                ExecSql(cs0, $"ALTER DATABASE [{refDb}] SET RECOVERY SIMPLE;", 60);
                try { var q = Query(cs0, $"SELECT compatibility_level FROM sys.databases WHERE name = N'{refDb}';", 60); if (q.Count > 0) KnownCompat = Convert.ToInt32(q[0][0]); } catch { }
                return true;
            }
            catch (Exception ex) { err = First(ex.Message); return false; }
        }

        static bool IsBackupFile(string path)
        {
            try { using var f = File.OpenRead(path); var b = new byte[4]; return f.Read(b, 0, 4) == 4 && b[0] == (byte)'T' && b[1] == (byte)'A' && b[2] == (byte)'P' && b[3] == (byte)'E'; }
            catch { return false; }
        }

        static Dictionary<string, RefTbl> ReadRefSchema(string refDb)
        {
            var res = new Dictionary<string, RefTbl>(StringComparer.OrdinalIgnoreCase);
            var rows = Query(Conn(refDb), @"
SELECT s.name, t.name, c.column_id, c.name, c.system_type_id, c.max_length, c.precision, c.scale,
       pc.leaf_offset, pc.leaf_null_bit, pc.leaf_bit_position
FROM sys.tables t
JOIN sys.schemas s ON s.schema_id = t.schema_id
JOIN sys.columns c ON c.object_id = t.object_id
OUTER APPLY (SELECT TOP 1 p.partition_id FROM sys.partitions p WHERE p.object_id = t.object_id AND p.index_id IN (0,1) ORDER BY p.partition_number) pp
LEFT JOIN sys.system_internals_partition_columns pc
       ON pc.partition_id = pp.partition_id AND pc.partition_column_id = c.column_id AND pc.is_dropped = 0
WHERE t.is_ms_shipped = 0 AND c.is_computed = 0
ORDER BY t.name, c.column_id;", 300);
            foreach (var r in rows)
            {
                string sch = Convert.ToString(r[0]) ?? "dbo", tn = Convert.ToString(r[1]) ?? "";
                if (!res.TryGetValue(tn, out var t)) res[tn] = t = new RefTbl { Schema = sch, Name = tn };
                var col = new Col
                {
                    ColId = Convert.ToInt32(r[2]), Name = Convert.ToString(r[3]) ?? "",
                    X = Convert.ToByte(r[4]), Len = Convert.ToInt16(r[5]), Prec = Convert.ToByte(r[6]), Scale = Convert.ToByte(r[7])
                };
                t.Cols.Add(col);
                if (r[8] != DBNull.Value && r[9] != DBNull.Value)
                    t.Lay[col.Name] = (Convert.ToInt32(r[8]), Convert.ToInt32(r[9]), r[10] == DBNull.Value ? 0 : Convert.ToInt32(r[10]));
            }
            return res;
        }

        // Kurtarılan veriyi referansın TAM yapısına (ID, PK, index, prosedür, view) yerleştir.
        static void TransferToRef(string T, string R, Dictionary<string, RecTbl> recovered, Action<string> log)
        {
            string cs = Conn(T);
            var tables = Query(cs, "SELECT s.name, t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id=t.schema_id WHERE t.is_ms_shipped=0;", 120)
                         .Select(r => (sch: Convert.ToString(r[0]) ?? "dbo", name: Convert.ToString(r[1]) ?? "")).ToList();
            string Full((string sch, string name) x) => $"[{x.sch.Replace("]", "]]")}].[{x.name.Replace("]", "]]")}]";

            foreach (var x in tables)
            {
                try { ExecSql(cs, $"ALTER TABLE {Full(x)} NOCHECK CONSTRAINT ALL; ALTER TABLE {Full(x)} DISABLE TRIGGER ALL;", 120); } catch { }
            }

            // Referans aynı müşterinin (eski) yedeği mi? → ID'si aynı satırlarda metin kolonları da tutuyor mu
            long tot = 0, same = 0;
            foreach (var rt in recovered.Values.Where(v => v.Rows > 0).OrderByDescending(v => v.Rows).Take(12))
            {
                var tx = tables.FirstOrDefault(z => z.name.Equals(rt.Orig, StringComparison.OrdinalIgnoreCase));
                if (tx.name == null || !rt.ColMap.TryGetValue("ID", out var idc)) continue;
                var txt = rt.Lay.FirstOrDefault(l => !l.Lost && (l.X == 231 || l.X == 167) && !l.Name.Equals("ID", StringComparison.OrdinalIgnoreCase));
                if (txt == null || !rt.ColMap.TryGetValue(txt.Name, out var tc)) continue;
                try
                {
                    var q = Query(cs, $@"SELECT COUNT(*), SUM(CASE WHEN CAST(x.[{txt.Name.Replace("]", "]]")}] AS nvarchar(400)) = CAST(r.[{tc}] AS nvarchar(400)) COLLATE DATABASE_DEFAULT THEN 1 ELSE 0 END)
                                         FROM [{R}].[dbo].[{rt.Tn}] r JOIN {Full(tx)} x ON x.[ID] = r.[{idc}]
                                         WHERE r.[{tc}] IS NOT NULL AND LEN(CAST(r.[{tc}] AS nvarchar(400))) > 2;", 300);
                    if (q.Count > 0) { tot += Convert.ToInt64(q[0][0]); same += q[0][1] == DBNull.Value ? 0 : Convert.ToInt64(q[0][1]); }
                }
                catch { }
            }
            bool sameCompany = tot >= 20 && same >= tot * 0.5;
            bool freshRef = tot < 20;   // referansta bizim ID'lerle örtüşen kayıt yok → boş/yeni kurulum (Orka'nın kendi oluşturduğu DB): sistem/ayar satırları korunur
            if (freshRef) log("Referans boş/yeni kurulum görünüyor → yapısı + programın kendi ayar satırları kullanılacak, üstüne kurtarılan veri yazılacak.");
            else log(sameCompany
                ? $"Referans AYNI MÜŞTERİNİN yedeği görünüyor ({same:N0}/{tot:N0} satır eşleşti) → kurtarılamayan tablolar referanstaki (eski) veriyle dolu kalacak."
                : $"Referans BAŞKA bir veri seti ({same:N0}/{tot:N0} eşleşme) → yalnızca YAPISI kullanılacak, içindeki veri temizlenecek.");

            long moved = 0, failed = 0; int done = 0, cleaned = 0, kept = 0, added = 0, errT = 0, newCols = 0;
            foreach (var x in tables)
            {
                recovered.TryGetValue(x.name, out var rt);
                // müşterinin programı referanstan YENİ sürümse: kurtarılan katalogda olup referansta olmayan kolonlar eklenir
                // (ör. GNL_KULLANICITANIMLAR.IDplasiyer) — yoksa program "Invalid column name" der
                if (rt != null) newCols += AddMissingCols(cs, Full(x), rt);
                if (rt == null || rt.Rows == 0)
                {
                    if (sameCompany || freshRef) { kept++; continue; }
                    try { ClearTable(cs, Full(x)); cleaned++; } catch { }
                    continue;
                }
                long ok = 0, bad = 0, kept0 = 0;
                try { CopyInto(cs, T, R, rt, Full(x), sameCompany || freshRef, ref ok, ref bad, ref kept0); done++; }
                catch (Exception ex)
                {
                    errT++;
                    log($"  ✗ {x.name}: yapıya aktarılamadı ({First(ex.Message)}) → kurtarılan satırlar 'VKM_{rt.Tn}' tablosunda ayrıca duruyor.");
                    CopyRaw(cs, R, rt, "VKM_" + rt.Tn);
                    continue;
                }
                moved += ok; failed += bad;
                if (bad > 0 || ok < rt.Rows || kept0 > 0)
                    log($"  ✓ {x.name}: {ok:N0}/{rt.Rows:N0} satır yerleşti" + (bad > 0 ? $" · {bad:N0} satır kısıta takıldı" : "") +
                        (kept0 > 0 ? $" · referansın kendi {kept0:N0} satırı korundu" : ""));
            }
            // referansta olmayan ama kurtarılan tablolar → olduğu gibi ekle (boş olsa da adı gerçekse yapı olarak: program tabloyu arar)
            foreach (var rt in recovered.Values.Where(v => v.Rows > 0 || (!v.Orig.StartsWith("tablo_", StringComparison.OrdinalIgnoreCase) && !v.Orig.EndsWith("__SILINEN", StringComparison.OrdinalIgnoreCase))))
            {
                if (tables.Any(z => z.name.Equals(rt.Orig, StringComparison.OrdinalIgnoreCase))) continue;
                if (CopyRaw(cs, R, rt, rt.Tn)) added++;
            }
            foreach (var x in tables)
            {
                try { ExecSql(cs, $"ALTER TABLE {Full(x)} WITH NOCHECK CHECK CONSTRAINT ALL; ALTER TABLE {Full(x)} ENABLE TRIGGER ALL;", 120); } catch { }
                try { ExecSql(cs, $"IF OBJECTPROPERTY(OBJECT_ID(N'{Full(x).Replace("'", "''")}'),'TableHasIdentity')=1 DBCC CHECKIDENT(N'{Full(x).Replace("'", "''")}') WITH NO_INFOMSGS;", 300); } catch { }
            }
            if (newCols > 0) log($"Müşterinin program sürümüne göre {newCols} kolon eklendi / tipi güncellendi (referans eski sürüm).");
            if (errT > 0) log($"⚠ {errT} tablo referans yapısına aktarılamadı (yukarıdaki ✗ satırları) — bu tablolarda program referanstaki satırları görür.");
            log($"Aktarım: {done} tablo · {moved:N0} satır yerleşti" + (failed > 0 ? $" · {failed:N0} satır kısıt nedeniyle girmedi" : "") +
                (cleaned > 0 ? $" · referans verisi temizlenen {cleaned} tablo" : "") + (kept > 0 ? $" · referanstan korunan {kept} tablo" : "") +
                (added > 0 ? $" · referansta olmayan {added} tablo eklendi" : ""));
            log("Sonuç: ID/PK/index/prosedür/view referanstan → program veritabanını kendi DB'si gibi tanır.");
        }

        static int AddMissingCols(string cs, string full, RecTbl rt)
        {
            int n = 0;
            try
            {
                var tcols = Query(cs, $@"SELECT c.name, TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed
                                         FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'{full.Replace("'", "''")}');", 120);
                var have = new Dictionary<string, object[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in tcols) have[Convert.ToString(r[0]) ?? ""] = r;
                if (have.Count == 0) return 0;
                foreach (var c in rt.Lay)
                {
                    if (c.Guess || c.Name == "__vkm_rn") continue;                          // tahmini (ref/havuz) kolonlar zaten referanstan
                    string q = $"[{c.Name.Replace("]", "]]")}]";
                    if (!have.TryGetValue(c.Name, out var tc))
                    {
                        try { ExecSql(cs, $"ALTER TABLE {full} ADD {q} {SqlType(c)} NULL;", 300); n++; } catch { }
                        continue;
                    }
                    // aynı kolon, müşterinin sürümünde farklı tipte (uzatılmış metin, değişmiş ondalık…) → referanstakini ona çevir; yoksa veri kırpılır
                    string cur = TypeDef((Convert.ToString(tc[1]) ?? "").ToLowerInvariant(), Convert.ToInt32(tc[2]), Convert.ToInt32(tc[3]), Convert.ToInt32(tc[4]));
                    string want = SqlType(c);
                    string tyn0 = (Convert.ToString(tc[1]) ?? "").ToLowerInvariant();
                    if (tyn0 is "timestamp" or "image" or "text" or "ntext" or "xml" or "sql_variant" or "hierarchyid" or "geography" or "geometry") continue;
                    if (Convert.ToBoolean(tc[6]) || Convert.ToBoolean(tc[7]) || cur.Equals(want, StringComparison.OrdinalIgnoreCase)) continue;
                    if (want.StartsWith("varbinary", StringComparison.OrdinalIgnoreCase) && !cur.Contains("binary", StringComparison.OrdinalIgnoreCase)) continue;   // emin olunamayan tip
                    try { ExecSql(cs, $"ALTER TABLE {full} ALTER COLUMN {q} {want} {(Convert.ToBoolean(tc[5]) ? "NULL" : "NOT NULL")};", 600); n++; } catch { }
                }
            }
            catch { }
            return n;
        }

        static void ClearTable(string cs, string full)
        {
            try { ExecSql(cs, $"TRUNCATE TABLE {full};", 600); }
            catch { ExecSql(cs, $"DELETE FROM {full};", 3600); }
        }

        // INSERT kurtarma DB'sinin (R, yeni uyumluluk seviyesi) içinden çalışır: Orka DB'leri uyumluluk 100'de (SQL 2008) → TRY_CONVERT orada yok.
        // merge: referans satırlarından yalnızca kurtarılan ID'lerle çakışanlar silinir, kalanı (şifreli sayfada kalmış eski kayıtlar) korunur.
        static void CopyInto(string cs, string T, string R, RecTbl rt, string full, bool merge, ref long okRows, ref long badRows, ref long keptRows)
        {
            string fullT = $"[{T.Replace("]", "]]")}].{full}";
            var cols = Query(cs, $@"SELECT c.name, TYPE_NAME(c.system_type_id), c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.default_object_id, c.collation_name
                                    FROM sys.columns c WHERE c.object_id = OBJECT_ID(N'{full.Replace("'", "''")}') AND c.is_computed = 0 ORDER BY c.column_id;", 120);
            var lost = new HashSet<string>(rt.Lay.Where(l => l.Lost).Select(l => l.Name), StringComparer.OrdinalIgnoreCase);
            var tgt = new List<string>(); var exprs = new List<string>(); string? idSrc = null; bool hasIdent = false;
            string? idTgt = null, idType = null, idS = null;
            var colExpr = new Dictionary<string, (string e, string? coll)>(StringComparer.OrdinalIgnoreCase);   // kolon → eklenecek değerin ifadesi
            foreach (var c in cols)
            {
                string cn = Convert.ToString(c[0]) ?? "", tyn = (Convert.ToString(c[1]) ?? "").ToLowerInvariant();
                int ml = Convert.ToInt32(c[2]), pr = Convert.ToInt32(c[3]), sc = Convert.ToInt32(c[4]);
                bool nullable = Convert.ToBoolean(c[5]), ident = Convert.ToBoolean(c[6]), hasDef = Convert.ToInt32(c[7]) != 0;
                if (tyn == "timestamp" || tyn == "geography" || tyn == "geometry" || tyn == "hierarchyid") continue;
                bool have = rt.ColMap.TryGetValue(cn, out var src) && !lost.Contains(cn);
                if (ident) { if (!have) continue; hasIdent = true; idSrc = src; }
                if (!have && (nullable || hasDef)) continue;                 // NULL ya da kendi varsayılanı
                string ty = TypeDef(tyn, ml, pr, sc);
                string e = have ? $"TRY_CONVERT({ty}, r.[{src}])" : "NULL";
                if (!nullable) e = $"ISNULL({e}, {DefLit(tyn)})";
                tgt.Add($"[{cn.Replace("]", "]]")}]"); exprs.Add(e);
                if (have) colExpr[cn] = (e, c[8] == DBNull.Value ? null : Convert.ToString(c[8]));
                if (cn.Equals("ID", StringComparison.OrdinalIgnoreCase) && have) { idTgt = cn; idType = ty; idS = src; }
            }
            if (tgt.Count == 0) return;
            string where = idSrc != null ? $" AND r.[{idSrc}] IS NOT NULL" : "";
            if (merge && idTgt != null)
            {
                ExecSql(Conn(R), $"DELETE t FROM {fullT} t WHERE t.[{idTgt}] IN (SELECT TRY_CONVERT({idType}, r.[{idS}]) FROM [dbo].[{rt.Tn}] r WHERE r.[{idS}] IS NOT NULL);", 3600);
                // aynı kayıt referansta BAŞKA ID ile duruyorsa (benzersiz anahtar: dönem+kod vb.) → kurtarılan (daha yeni) hali geçerli, eskisi silinir
                try
                {
                    var uk = Query(cs, $@"SELECT i.index_id, c.name FROM sys.indexes i
                                          JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
                                          JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                                          WHERE i.object_id = OBJECT_ID(N'{full.Replace("'", "''")}') AND i.is_unique = 1 AND i.has_filter = 0
                                          ORDER BY i.index_id, ic.key_ordinal;", 120);
                    foreach (var g in uk.GroupBy(z => Convert.ToInt32(z[0])))
                    {
                        var keys = g.Select(z => Convert.ToString(z[1]) ?? "").ToList();
                        if (keys.Count == 1 && keys[0].Equals(idTgt, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!keys.All(k => colExpr.ContainsKey(k))) continue;
                        var conds = keys.Select(k =>
                        {
                            var (e, coll) = colExpr[k]; string ex = coll != null ? $"({e}) COLLATE {coll}" : $"({e})";
                            string tk = $"t.[{k.Replace("]", "]]")}]";
                            return $"({tk} = {ex} OR ({tk} IS NULL AND ({e}) IS NULL))";
                        });
                        try { ExecSql(Conn(R), $"DELETE t FROM {fullT} t WHERE EXISTS (SELECT 1 FROM [dbo].[{rt.Tn}] r WHERE {string.Join(" AND ", conds)});", 3600); } catch { }
                    }
                }
                catch { }
                try { keptRows = Convert.ToInt64(Query(cs, $"SELECT COUNT_BIG(*) FROM {full};", 600)[0][0]); } catch { }
            }
            else ClearTable(cs, full);
            string ins = $"INSERT INTO {fullT} ({string.Join(",", tgt)}) SELECT {string.Join(",", exprs)} FROM [dbo].[{rt.Tn}] r WHERE r.[__vkm_rn] BETWEEN @a AND @b{where};";
            if (hasIdent) ins = $"SET IDENTITY_INSERT {fullT} ON; {ins} SET IDENTITY_INSERT {fullT} OFF;";

            using var cn0 = new SqlConnection(Conn(R)); cn0.Open();
            // derleme/tip hatası var mı? (tek satırla değil, boş aralıkla dene)
            RunRange(cn0, ins, 0, -1);
            long max = Convert.ToInt64(Query(cs, $"SELECT ISNULL(MAX([__vkm_rn]),0) FROM [{R}].[dbo].[{rt.Tn}];", 120)[0][0]);
            long a = 1; int singleFails = 0; long ok0 = 0, bad0 = 0;
            void Go(long lo, long hi)
            {
                if (lo > hi) return;
                try { long n = RunRange(cn0, ins, lo, hi); ok0 += n; if (hi == lo) singleFails = 0; }
                catch
                {
                    try { cn0.Execute($"IF @@TRANCOUNT>0 ROLLBACK;"); } catch { }
                    if (hasIdent) try { cn0.Execute($"SET IDENTITY_INSERT {fullT} OFF;"); } catch { }
                    if (lo == hi) { bad0++; if (++singleFails > 200) throw new Exception("çok fazla satır kısıta takılıyor"); return; }
                    long mid = (lo + hi) / 2; Go(lo, mid); Go(mid + 1, hi);
                }
            }
            while (a <= max) { long b = Math.Min(max, a + 49999); Go(a, b); a = b + 1; }
            okRows = ok0; badRows = bad0;
        }

        static long RunRange(SqlConnection cn, string sql, long a, long b)
        {
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 3600 };
            cmd.Parameters.AddWithValue("@a", a); cmd.Parameters.AddWithValue("@b", b);
            return Math.Max(0, cmd.ExecuteNonQuery());
        }

        static void Execute(this SqlConnection cn, string sql)
        {
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = 120 }; cmd.ExecuteNonQuery();
        }

        static bool CopyRaw(string cs, string R, RecTbl rt, string newName)
        {
            try
            {
                string nn = Safe(newName);
                ExecSql(cs, $"IF OBJECT_ID(N'dbo.[{nn}]') IS NULL BEGIN SELECT * INTO [dbo].[{nn}] FROM [{R}].[dbo].[{rt.Tn}]; ALTER TABLE [dbo].[{nn}] DROP COLUMN [__vkm_rn]; END", 3600);
                return true;
            }
            catch { return false; }
        }

        static string TypeDef(string t, int ml, int pr, int sc) => t switch
        {
            "varchar" or "char" or "varbinary" or "binary" => ml == -1 ? $"{t}(max)" : $"{t}({Math.Max(1, ml)})",
            "nvarchar" or "nchar" => ml == -1 ? $"{t}(max)" : $"{t}({Math.Max(1, ml / 2)})",
            "decimal" or "numeric" => $"{t}({pr},{sc})",
            "datetime2" or "time" or "datetimeoffset" => $"{t}({sc})",
            _ => t
        };

        static string DefLit(string t) => t switch
        {
            "tinyint" or "smallint" or "int" or "bigint" or "bit" or "decimal" or "numeric" or "money" or "smallmoney" or "float" or "real" => "0",
            "char" or "varchar" or "text" => "''",
            "nchar" or "nvarchar" or "ntext" or "xml" or "sysname" => "N''",
            "date" or "datetime" or "smalldatetime" or "datetime2" or "datetimeoffset" => "'19000101'",
            "time" => "'00:00'",
            "uniqueidentifier" => "NEWID()",
            "binary" or "varbinary" or "image" => "0x",
            _ => "0"
        };

        static List<object[]> Query(string cs, string sql, int timeout)
        {
            var res = new List<object[]>();
            using var cn = new SqlConnection(cs); cn.Open();
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = timeout };
            using var rd = cmd.ExecuteReader();
            while (rd.Read()) { var a = new object[rd.FieldCount]; rd.GetValues(a); res.Add(a); }
            return res;
        }

        // ───────────────────────── kolon yerleşimi (kanıt oylaması) ─────────────────────────
        static readonly HashSet<byte> Strong = new() { 61, 58, 40, 42, 43, 62, 59, 106, 108, 60, 122, 231, 167, 175, 239 };
        static readonly HashSet<byte> Ints = new() { 48, 52, 56, 127 };
        static readonly int[] Gaps = { 0, 4, 8, 1, 2 };

        static bool IsVarType(byte x, short len) =>
            x == 167 || x == 231 || x == 165 || x == 35 || x == 99 || x == 34 || x == 241 || x == 98 || len == -1;

        static Dictionary<int, Col> Variant(Tbl t, int gap)
        {
            var byId = t.Cols.ToDictionary(x => x.ColId);
            int mx = t.Cols.Max(x => x.ColId);
            var outd = new Dictionary<int, Col>();
            int off = 4, vi = 0, bits = 0, bb = 0;
            for (int cid = 1; cid <= mx; cid++)
            {
                if (!byId.TryGetValue(cid, out var m)) { off += gap; continue; }
                var c = m.Copy(); c.NullBit = cid; c.IsBit = c.X == 104;
                if (IsVarType(c.X, c.Len)) { c.IsVar = true; c.VarIdx = ++vi; }
                else if (c.IsBit) { if (bits % 8 == 0) { bb = off; off += 1; } c.Off = bb; c.BitPos = bits % 8; bits++; }
                else { c.Off = off; off += Math.Max(1, (int)c.Len); }
                outd[cid] = c;
            }
            return outd;
        }

        static double Plaus(Col c, List<object> vals, int errCount)
        {
            var nn = vals.Where(v => v != DBNull.Value).ToList();
            double s;
            try
            {
            if (nn.Count == 0) s = 0.7;
            else if (c.X == 61 || c.X == 58 || c.X == 40 || c.X == 42 || c.X == 43)
                s = nn.Count(v => { var d = v is DateTimeOffset o ? o.DateTime : (DateTime)v; return (d.Year >= 1990 && d.Year <= 2035) || (d.Year == 1900 && d.Month == 1 && d.Day == 1) || (d.Year == 1899 && d.Month == 12 && d.Day == 30); }) / (double)nn.Count;   // 01.01.1900 / 30.12.1899 (Delphi sıfır tarihi) = yalnız saat tutulan alan
            else if (c.X == 231 || c.X == 167 || c.X == 175 || c.X == 239)
                s = nn.Count(v => v is string tx && GoodText(tx)) / (double)nn.Count;
            else if (c.X == 106 || c.X == 108)
                s = nn.Count(v => { var d = (decimal)v; return Math.Abs(d) < 1e13m && !(d < 0 && Math.Abs(d) < 0.01m); }) / (double)nn.Count;
            else if (c.X == 60 || c.X == 122)
                s = nn.Count(v => Math.Abs((decimal)v) < 1e13m) / (double)nn.Count;
            else if (c.X == 62 || c.X == 59)
                s = nn.Count(v => Math.Abs(Convert.ToDouble(v)) < 1e13) / (double)nn.Count;
            else if (Ints.Contains(c.X))
                s = nn.Any(v => Math.Abs(Convert.ToDouble(v)) > (c.X == 127 ? 1e17 : 2e9)) ? 0.5 : 0.8;   // bigint: 2021000518948 gibi belge no'lar normal
            else s = 0.7;
            }
            catch { return -1; }      // beklenmeyen tip → bu yerleşim yanlış
            if (errCount > 0) s -= 0.5;
            return s;
        }

        static double Score(Col meta, Col L, List<Rec> sample, Encoding enc, bool keyCheck) => Score(meta, L, sample, enc, keyCheck, out _);

        // nonNull: örnekte gerçekten değer çıkan satır sayısı. Hepsi NULL çıkan yerleşim KANIT DEĞİLDİR
        // (yanlış null-bit hep "boş" der ve 'makul' görünür — 135'te ID gibi kolonların boş gelmesinin sebebi buydu).
        static double Score(Col meta, Col L, List<Rec> sample, Encoding enc, bool keyCheck, out int nonNull)
        {
            var vals = new List<object>(); int errs = 0, nullHits = 0; nonNull = 0;
            foreach (var r in sample)
            {
                var v = DecodeEx(r, L, enc, out int st); if (st == 2) errs++; vals.Add(v);
                if (v != DBNull.Value) nonNull++;
                else if (L.NullBit > 0 && L.NullBit <= r.NCol && r.IsNull(L.NullBit)) nullHits++;
            }
            double s = Plaus(meta, vals, errs);
            if (meta.NotNull && nullHits > 0) s -= 1;          // katalog "boş olamaz" diyor ama bu yerleşim boş okuyor → yanlış null-bit
            if (keyCheck && Ints.Contains(meta.X))
            {
                var nn = vals.Where(v => v != DBNull.Value).Select(v => Convert.ToInt64(v)).ToList();
                if (nn.Count > 0 && nn.Distinct().Count() < nn.Count * 0.9) s -= 1;
            }
            return s;
        }

        [ThreadStatic] static Ctx? _lobCtx;
        [ThreadStatic] static bool _noLob;
        [ThreadStatic] static long _lobOk, _lobLost;
        [ThreadStatic] static HashSet<uint>? _lobUsed;

        static List<Col> ChooseLayout(Tbl t, Ctx ctx, List<Rec> sample, Encoding enc, out int lost)
        {
            _noLob = true;
            try { return ChooseLayoutCore(t, ctx, sample, enc, out lost); }
            finally { _noLob = false; }
        }

        static List<Col> ChooseLayoutCore(Tbl t, Ctx ctx, List<Rec> sample, Encoding enc, out int lost)
        {
            var variants = Gaps.ToDictionary(g => g, g => Variant(t, g));

            // varyant güveni: sabit kolon güveni ilk güçlü sabit hatada, değişken kolon güveni ilk güçlü metin hatasında biter
            var trust = new Dictionary<int, Dictionary<int, (bool ok, double s)>>();
            var ev = new Dictionary<int, int>();
            foreach (var g in Gaps)
            {
                bool ft = true, vt = true; var tr = new Dictionary<int, (bool, double)>(); int e = 0;
                foreach (var m in t.Cols)
                {
                    var L = variants[g][m.ColId];
                    double s = Score(m, L, sample, enc, false, out int nnz);
                    bool ok = L.IsVar ? vt : ft;
                    if (nnz == 0 && s >= 0.6) { tr[m.ColId] = (ok, s); continue; }   // hep boş: ne kanıt ne ret
                    if (Strong.Contains(m.X) && s < 0.6) { if (L.IsVar) vt = false; else ft = false; ok = false; }
                    if (ok && Strong.Contains(m.X) && s >= 0.6) e++;
                    tr[m.ColId] = (ok, s);
                }
                trust[g] = tr; ev[g] = e;
            }

            ctx.RsCols.TryGetValue(t.RowsetId, out var rsm);
            // 0) Dosyanın KENDİ kataloğu (sysrscols) bu tablo için eksiksiz ve tutarlıysa → kesin yerleşim budur, başka adayla yarıştırılmaz.
            //    (Yarışta "daha çok değer okuyan" aday kazanıyordu: sonradan eklenen/çoğu boş kolonlara başka kolonun değeri yazılıyordu —
            //     ör. GNL_KULLANICITANIMLAR.parola'ya 'unvan' geldi → Orka'da "şifre hatalı".)
            if (rsm != null && OwnLayout(t, rsm, sample, enc, out var own)) { lost = 0; return own; }
            var outl = new List<Col>(); lost = 0;
            foreach (var m in t.Cols)
            {
                Col? chosen = null; double best = -9;
                bool keyLike = m.Name.Equals("ID", StringComparison.OrdinalIgnoreCase);
                // 1) sysrscols kesin kaydı + referans DB'nin yerleşimi
                var cands = new List<(int off, int nb, int bp, int ok)>();
                if (rsm != null && rsm.TryGetValue(m.ColId, out var rl0)) cands.AddRange(rl0);
                if (ctx.RefLay.TryGetValue(t.Name, out var rlay) && rlay.TryGetValue(m.Name, out var rp) && rp.nb > 0)
                    cands.Add(((rp.off & 0xFFFF), rp.nb, rp.bp, 0));
                if (ctx.PoolLay.TryGetValue(t.Name, out var play) && play.TryGetValue(m.Name, out var pl2))
                    foreach (var e in pl2) if (e.nb > 0) cands.Add((e.off, e.nb, e.bp, 0));
                foreach (var (off, nb, bp, ok) in cands)
                    {
                        short leaf = (short)(off & 0xFFFF);
                        var L = m.Copy(); L.NullBit = nb; L.BitPos = bp; L.IsBit = m.X == 104;
                        if ((leaf < 0) != IsVarType(m.X, m.Len) || leaf == 0) continue;   // tip ile çelişen aday (başka sürümün yerleşimi) → at
                        if (leaf < 0) { L.IsVar = true; L.VarIdx = -leaf; } else { L.IsVar = false; L.Off = leaf; }
                        double s = Score(m, L, sample, enc, ok > 0 || keyLike, out int nnz);
                        if (nnz == 0 && sample.Count > 0) continue;              // hep boş okuyan aday kesin sayılmaz
                        s += 0.01 * nnz / Math.Max(1, sample.Count);             // eşitlikte daha çok değer okuyan kazanır
                        if (s >= 0.6 && s > best) { best = s; chosen = L; }
                    }
                // 2) hipotezler: kanıtı en güçlü yerleşim; eşit kanıt + farklı yerleşim → belirsiz
                if (chosen == null)
                {
                    var groups = new Dictionary<string, (int ev, Col L)>();
                    foreach (var g in Gaps)
                    {
                        var (ok, s) = trust[g][m.ColId];
                        if (!ok || s < 0.6) continue;
                        var L = variants[g][m.ColId];
                        if (keyLike && Score(m, L, sample, enc, true) < 0.6) continue;
                        string k = L.Key();
                        if (!groups.TryGetValue(k, out var cur) || ev[g] > cur.ev) groups[k] = (ev[g], L);
                    }
                    var ranked = groups.Values.OrderByDescending(x => x.ev).ToList();
                    if (ranked.Count == 1 || (ranked.Count > 1 && ranked[0].ev > ranked[1].ev)) chosen = ranked[0].L;
                }
                if (chosen == null) { var l = m.Copy(); l.Lost = true; outl.Add(l); lost++; }
                else outl.Add(chosen);
            }
            return outl;
        }

        static bool OwnLayout(Tbl t, Dictionary<int, List<(int off, int nb, int bp, int ok)>> rsm, List<Rec> sample, Encoding enc, out List<Col> outl)
        {
            outl = new List<Col>();
            var varIdx = new HashSet<int>(); var nbs = new HashSet<int>(); var spans = new List<(int a, int b)>();
            foreach (var m in t.Cols)
            {
                if (m.Guess) return false;                                       // kolonu tahmin/referanstan gelmiş tablo → katalog eksik
                if (!rsm.TryGetValue(m.ColId, out var li) || li.Count == 0) return false;
                var e = li[0];
                if (li.Any(x => (x.off & 0xFFFF) != (e.off & 0xFFFF) || x.nb != e.nb || x.bp != e.bp)) return false;   // çelişen kayıtlar
                short leaf = (short)(e.off & 0xFFFF);
                if (leaf == 0 || (leaf < 0) != IsVarType(m.X, m.Len) || e.nb <= 0 || !nbs.Add(e.nb)) return false;
                var L = m.Copy(); L.NullBit = e.nb; L.BitPos = e.bp; L.IsBit = m.X == 104;
                if (leaf < 0) { if (!varIdx.Add(-leaf)) return false; L.IsVar = true; L.VarIdx = -leaf; }
                else
                {
                    if (leaf < 4) return false;
                    L.IsVar = false; L.Off = leaf;
                    if (m.X != 104) spans.Add((leaf, leaf + Math.Max(1, (int)m.Len)));
                }
                outl.Add(L);
            }
            if (outl.Count == 0) return false;
            var fs = spans.OrderBy(x => x.a).ToList();
            for (int i = 1; i < fs.Count; i++) if (fs[i].a < fs[i - 1].b) return false;   // sabit alanlar çakışıyor → kayıt bozuk
            // örnek satırlarla son kontrol: çözülemeyen değer oranı düşük olmalı; 'boş olamaz' / ID kolonu hep boş okunuyorsa yerleşim yanlış
            int tot = 0, err = 0;
            foreach (var L in outl)
            {
                int nn = 0;
                foreach (var r in sample) { var v = DecodeEx(r, L, enc, out int st); tot++; if (st == 2) err++; if (v != DBNull.Value) nn++; }
                if (sample.Count > 0 && nn == 0 && (L.NotNull || L.Ident || L.Name.Equals("ID", StringComparison.OrdinalIgnoreCase))) return false;
            }
            return tot == 0 || err <= tot * 0.02;
        }

        // ───────────────────────── değer çözümü ─────────────────────────
        // st: 0 = NULL, 1 = değer, 2 = hata (çözülemedi)
        static object DecodeEx(Rec r, Col c, Encoding enc, out int st)
        {
            st = 0;
            try
            {
                if (c.NullBit > r.NCol) return DBNull.Value;
                if (r.IsNull(c.NullBit)) return DBNull.Value;
                byte[] p = r.P;
                if (c.IsVar != IsVarType(c.X, c.Len)) { st = 2; return DBNull.Value; }
                if (c.IsVar)
                {
                    if (c.X == 35 || c.X == 99 || c.X == 34 || c.X == 241 || c.X == 98) return DBNull.Value;
                    int k = c.VarIdx - 1;
                    if (k >= r.VarCount) { st = 1; return c.X == 165 ? Array.Empty<byte>() : (object)""; }
                    if (r.VS[k] < 0) { st = 2; return DBNull.Value; }
                    if (r.VC[k])
                    {
                        // satır dışı (LOB / taşma) değer: puanlamada okunmaz; kurtarmada parçalar birleştirilir
                        if (_noLob || _lobCtx == null) { st = 1; return c.X == 165 ? Array.Empty<byte>() : (object)""; }
                        var lob = _lobCtx.ReadLob(p, r.VS[k], r.VE[k] - r.VS[k]);
                        if (lob == null) { _lobLost++; return DBNull.Value; }
                        _lobOk++; st = 1;
                        if (c.X == 231) return Trunc(Encoding.Unicode.GetString(lob, 0, lob.Length & ~1), c.Len == -1 ? int.MaxValue : c.Len / 2);
                        if (c.X == 167) return Trunc(enc.GetString(lob), c.Len == -1 ? int.MaxValue : c.Len);
                        return lob;
                    }
                    int s0 = r.VS[k], ln = r.VE[k] - s0;
                    if (c.X == 231)
                    {
                        if ((ln & 1) != 0) { st = 2; return DBNull.Value; }
                        st = 1; return Trunc(Encoding.Unicode.GetString(p, s0, ln), c.Len == -1 ? int.MaxValue : c.Len / 2);
                    }
                    if (c.X == 167) { st = 1; return Trunc(enc.GetString(p, s0, ln), c.Len == -1 ? int.MaxValue : c.Len); }
                    var bytes = new byte[ln]; Buffer.BlockCopy(p, s0, bytes, 0, ln); st = 1; return bytes;
                }

                int len = c.IsBit ? 1 : Math.Max(1, (int)c.Len);
                if (c.Off < 4 || c.Off + len > r.FixedEnd) { st = 2; return DBNull.Value; }
                int o = r.Abs + c.Off;
                st = 1;
                switch (c.X)
                {
                    case 48: return p[o];
                    case 52: return I16(p, o);
                    case 56: return I32(p, o);
                    case 127: return I64(p, o);
                    case 104: return ((p[o] >> c.BitPos) & 1) == 1;
                    case 59: { float f = BitConverter.ToSingle(p, o); if (float.IsNaN(f) || float.IsInfinity(f)) { st = 2; return DBNull.Value; } return f; }
                    case 62: { double d = BitConverter.ToDouble(p, o); if (double.IsNaN(d) || double.IsInfinity(d)) { st = 2; return DBNull.Value; } return d; }
                    case 60: return I64(p, o) / 10000m;
                    case 122: return I32(p, o) / 10000m;
                    case 61:
                        {
                            int time = I32(p, o), days = I32(p, o + 4);
                            if (days < -53690 || days > 2958463 || time < 0 || time > 25920000) { st = 2; return DBNull.Value; }
                            return new DateTime(1900, 1, 1).AddDays(days).AddMilliseconds(time * 10.0 / 3.0);
                        }
                    case 58:
                        {
                            int mins = U16(p, o), days = U16(p, o + 2);
                            if (mins >= 1440) { st = 2; return DBNull.Value; }
                            return new DateTime(1900, 1, 1).AddDays(days).AddMinutes(mins);
                        }
                    case 40: { int days = p[o] | (p[o + 1] << 8) | (p[o + 2] << 16); if (days > 3652058) { st = 2; return DBNull.Value; } return new DateTime(1, 1, 1).AddDays(days); }
                    case 41: return TimePart(p, o, c.Scale, out _);
                    case 42:
                        {
                            var tp = TimePart(p, o, c.Scale, out int tb);
                            int days = p[o + tb] | (p[o + tb + 1] << 8) | (p[o + tb + 2] << 16);
                            if (days > 3652058) { st = 2; return DBNull.Value; }
                            return new DateTime(1, 1, 1).AddDays(days).Add(tp);
                        }
                    case 43:
                        {
                            var tp = TimePart(p, o, c.Scale, out int tb);
                            int days = p[o + tb] | (p[o + tb + 1] << 8) | (p[o + tb + 2] << 16);
                            short ofs = I16(p, o + tb + 3);
                            if (days > 3652058 || Math.Abs((int)ofs) > 840) { st = 2; return DBNull.Value; }
                            var utc = new DateTime(1, 1, 1).AddDays(days).Add(tp);
                            return new DateTimeOffset(utc.AddMinutes(ofs), TimeSpan.FromMinutes(ofs));
                        }
                    case 36: { var g = new byte[16]; Buffer.BlockCopy(p, o, g, 0, 16); return new Guid(g); }
                    case 106:
                    case 108:
                        {
                            byte sign = p[o];
                            if (sign > 1) { st = 2; return DBNull.Value; }
                            var mag = new BigInteger(new ReadOnlySpan<byte>(p, o + 1, len - 1), isUnsigned: true, isBigEndian: false);
                            if (sign == 0 && mag.IsZero) { st = 2; return DBNull.Value; }    // "negatif sıfır" = hizalama hatası
                            decimal v = (decimal)mag;
                            for (int i = 0; i < c.Scale; i++) v /= 10m;
                            return sign == 1 ? v : -v;
                        }
                    case 175: return Trunc(enc.GetString(p, o, len), len);
                    case 239: return Trunc(Encoding.Unicode.GetString(p, o, len), len / 2);
                    default: { var b = new byte[len]; Buffer.BlockCopy(p, o, b, 0, len); return b; }
                }
            }
            catch { st = 2; return DBNull.Value; }
        }

        static bool GoodText(string s)
        {
            foreach (char ch in s)
            {
                if (ch < 32 && ch != '\t' && ch != '\r' && ch != '\n') return false;
                if (ch >= 0x250 && "İıŞşĞğÜüÖöÇç€".IndexOf(ch) < 0) return false;
            }
            return true;
        }

        static TimeSpan TimePart(byte[] p, int o, int scale, out int tb)
        {
            tb = scale <= 2 ? 3 : scale <= 4 ? 4 : 5;
            long v = 0; for (int i = tb - 1; i >= 0; i--) v = (v << 8) | p[o + i];
            long ticks = v; for (int i = scale; i < 7; i++) ticks *= 10;
            if (ticks < 0 || ticks >= TimeSpan.TicksPerDay) ticks = 0;
            return TimeSpan.FromTicks(ticks);
        }

        static string SqlType(Col c)
        {
            int L(int n) => Math.Max(1, Math.Min(n, 8000));
            return c.X switch
            {
                48 => "tinyint", 52 => "smallint", 56 => "int", 127 => "bigint", 59 => "real", 62 => "float",
                60 => "money", 122 => "smallmoney", 104 => "bit", 61 => "datetime", 58 => "smalldatetime",
                40 => "date", 41 => $"time({c.Scale})", 42 => $"datetime2({c.Scale})", 43 => $"datetimeoffset({c.Scale})",
                36 => "uniqueidentifier",
                106 => $"decimal({Math.Max((int)c.Prec, 1)},{c.Scale})", 108 => $"numeric({Math.Max((int)c.Prec, 1)},{c.Scale})",
                175 => $"char({L(c.Len)})", 239 => $"nchar({L(c.Len / 2)})", 173 => $"binary({L(c.Len)})",
                167 => c.Len == -1 || c.Len > 8000 ? "varchar(max)" : $"varchar({L(c.Len)})",
                231 => c.Len == -1 || c.Len > 8000 ? "nvarchar(max)" : $"nvarchar({L(c.Len / 2)})",
                165 => c.Len == -1 || c.Len > 8000 ? "varbinary(max)" : $"varbinary({L(c.Len)})",
                35 => "varchar(max)", 99 => "nvarchar(max)", 34 => "varbinary(max)",
                189 => "binary(8)", 241 => "xml", 98 => "sql_variant",
                _ => "varbinary(max)"
            };
        }

        // ───────────────────────── yazma ─────────────────────────
        static long Flush(string cs, string table, List<string> colNames, DataTable dt)
        {
            if (dt.Rows.Count == 0) return 0;
            long skipped = 0;
            try
            {
                using var bc = new SqlBulkCopy(cs) { DestinationTableName = "[dbo].[" + table + "]", BulkCopyTimeout = 600 };
                foreach (var cn in colNames) bc.ColumnMappings.Add(cn, cn);
                bc.WriteToServer(dt);
            }
            catch
            {
                using var cn0 = new SqlConnection(cs); cn0.Open();
                string sql = $"INSERT INTO [dbo].[{table}] ({string.Join(",", colNames.Select(n => "[" + n + "]"))}) VALUES ({string.Join(",", colNames.Select((_, i) => "@p" + i))})";
                foreach (DataRow row in dt.Rows)
                {
                    try
                    {
                        using var cmd = new SqlCommand(sql, cn0) { CommandTimeout = 60 };
                        for (int i = 0; i < colNames.Count; i++) cmd.Parameters.AddWithValue("@p" + i, row[i] ?? DBNull.Value);
                        cmd.ExecuteNonQuery();
                    }
                    catch { skipped++; }
                }
            }
            dt.Rows.Clear();
            return skipped;
        }

        // ───────────────────────── yardımcılar ─────────────────────────
        static double Entropy(byte[] b)
        {
            var h = new int[256]; foreach (var x in b) h[x]++;
            double e = 0; foreach (var n in h) { if (n == 0) continue; double q = (double)n / b.Length; e -= q * Math.Log2(q); }
            return e;
        }
        static string Conn(string db) =>
            $"Server={MssqlBak.Instance};Database={db};Integrated Security=true;Connect Timeout=60;Encrypt=False;Pooling=false;";
        static void ExecSql(string cs, string sql, int timeout)
        {
            using var cn = new SqlConnection(cs); cn.Open();
            using var cmd = new SqlCommand(sql, cn) { CommandTimeout = timeout }; cmd.ExecuteNonQuery();
        }
        static int ReadFull(Stream s, byte[] buf, int want)
        { int n = 0; while (n < want) { int r = s.Read(buf, n, want - n); if (r <= 0) break; n += r; } return n; }
        static bool AllZero(byte[] b, int o, int n) { for (int i = 0; i < n; i++) if (b[o + i] != 0) return false; return true; }
        static string Trunc(string s, int max) => s.Length > max ? s.Substring(0, max) : s;
        static string Safe(string n) { n = (n ?? "").Replace("]", "").Replace("[", "").Trim(); return n.Length == 0 ? "x" : (n.Length > 120 ? n.Substring(0, 120) : n); }
        static string Esc(string p) => p.Replace("'", "''");
        static string First(string s) { s = (s ?? "").Replace("\r", " "); int i = s.IndexOf('\n'); return (i > 0 ? s.Substring(0, i) : s).Trim(); }
    }
}
