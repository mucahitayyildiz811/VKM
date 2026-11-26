using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace VKM.App
{
    // MSSQL 8KB SAYFA okuyucu (B3 çekirdeği). Motoru baypas eder — dosyayı doğrudan sayfa sayfa okur.
    // Sayfa başlığı 96 bayt; kayıtlar sayfa sonundaki slot dizisinden bulunur.
    // NOT: Çözülen alanlar DBCC PAGE ile doğrulanabilir; hex her zaman yanında gösterilir.
    public static class MssqlPage
    {
        public const int PAGE = 8192;

        public static string TypeName(byte t) => t switch
        {
            1 => "VERİ (data)",
            2 => "index",
            3 => "LOB (text mix)",
            4 => "LOB (text tree)",
            7 => "sort",
            8 => "GAM",
            9 => "SGAM",
            10 => "IAM",
            11 => "PFS",
            13 => "boot",
            15 => "dosya başlığı",
            16 => "diff map",
            17 => "ML map",
            18 => "DCM",
            19 => "BCM",
            _ => "tip " + t
        };

        public sealed class Hdr
        {
            public int PageNo;
            public byte Type;
            public string TypeName = "";
            public int Level, SlotCnt, FreeCnt, FreeData, Pminlen, IndexId;
            public long ObjId;
            public int[] Slots = Array.Empty<int>();
        }

        private static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        private static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        public static Hdr Decode(byte[] p, int pageNo)
        {
            var h = new Hdr { PageNo = pageNo };
            h.Type = p[1];
            h.TypeName = TypeName(p[1]);
            h.Level = p[3];
            h.IndexId = U16(p, 0x06);
            h.Pminlen = U16(p, 0x0E);
            h.SlotCnt = U16(p, 0x16);
            h.ObjId = U32(p, 0x18);
            h.FreeCnt = U16(p, 0x1C);
            h.FreeData = U16(p, 0x1E);
            // slot dizisi: sayfa sonundan geriye, her slot 2 bayt = kayıt offseti
            int n = Math.Max(0, Math.Min(h.SlotCnt, 1000));
            h.Slots = new int[n];
            for (int i = 0; i < n; i++) h.Slots[i] = U16(p, PAGE - 2 - i * 2);
            return h;
        }

        public sealed class ScanResult
        {
            public long PageCount;
            public Dictionary<byte, long> ByType = new();
            public List<(long objId, long pages)> TopObjects = new();
            public string DbNameGuess = "";
            public long DataPages, EmptyPages, GarbagePages;
        }

        private static bool KnownType(byte t) =>
            t == 1 || t == 2 || t == 3 || t == 4 || t == 7 || t == 8 || t == 9 ||
            t == 10 || t == 11 || t == 13 || t == 15 || t == 16 || t == 17 || t == 18 || t == 19;

        // Gerçek SQL sayfası mı? (header sürümü 0x01 + bilinen tip)
        private static bool ValidPage(byte[] b, int o) => b[o] == 0x01 && KnownType(b[o + 1]);

        public static ScanResult Scan(string path, Action<double>? prog, Action<string>? log, CancellationToken tok)
        {
            var r = new ScanResult();
            using var fs = File.OpenRead(path);
            long size = fs.Length;
            r.PageCount = size / PAGE;

            // boot page (sayfa 9) → tahmini DB adı
            try
            {
                if (size >= 10L * PAGE)
                {
                    byte[] bp = new byte[PAGE];
                    fs.Position = 9L * PAGE; ReadFull(fs, bp, PAGE);
                    r.DbNameGuess = LongestUtf16(bp);
                }
            }
            catch { }

            log?.Invoke("Sayfalar taranıyor (" + r.PageCount.ToString("N0") + " sayfa)…");
            var objPages = new Dictionary<long, long>();
            const int BLK = 8 << 20;                 // 8 MB = 1024 sayfa
            byte[] buf = new byte[BLK];
            fs.Position = 0;
            long pageNo = 0;
            while (true)
            {
                if (tok.IsCancellationRequested) break;
                int got = ReadFull(fs, buf, BLK);
                if (got < PAGE) break;
                int pages = got / PAGE;
                for (int i = 0; i < pages; i++)
                {
                    int o = i * PAGE;
                    byte t = buf[o + 1];
                    if (buf[o] == 0 && t == 0 && IsZero(buf, o, 96)) { r.EmptyPages++; }
                    else if (ValidPage(buf, o))
                    {
                        r.ByType.TryGetValue(t, out long c); r.ByType[t] = c + 1;
                        if (t == 1)   // gerçek veri sayfası → obj say
                        {
                            r.DataPages++;
                            long obj = U32(buf, o + 0x18);
                            objPages.TryGetValue(obj, out long pc); objPages[obj] = pc + 1;
                        }
                    }
                    else r.GarbagePages++;   // çöp/şifreli/üzerine yazılmış
                }
                pageNo += pages;
                prog?.Invoke((double)(pageNo * PAGE) / Math.Max(1, size));
            }

            // en çok veri sayfası olan nesneler
            var list = new List<(long, long)>();
            foreach (var kv in objPages) list.Add((kv.Key, kv.Value));
            list.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            int top = Math.Min(20, list.Count);
            for (int i = 0; i < top; i++) r.TopObjects.Add(list[i]);
            return r;
        }

        public static void Report(ScanResult r, Action<string> log)
        {
            log("");
            log("══════ SAYFA ANALİZİ ══════");
            if (!string.IsNullOrEmpty(r.DbNameGuess)) log("Tahmini DB adı : " + r.DbNameGuess);
            log("Toplam sayfa   : " + r.PageCount.ToString("N0") + "  (boş: " + r.EmptyPages.ToString("N0") + ")");
            log("Gerçek SQL     : " + (r.PageCount - r.EmptyPages - r.GarbagePages).ToString("N0") +
                "   ·   ÇÖP/şifreli: " + r.GarbagePages.ToString("N0") +
                (r.GarbagePages > 0 ? "  ⚠ (ransomware/hasar işareti)" : ""));
            log("Veri sayfası   : " + r.DataPages.ToString("N0"));
            log("Sayfa tipleri  :");
            foreach (var kv in SortByVal(r.ByType))
                log(string.Format("   {0,-16} {1,10:N0}", TypeName(kv.Key), kv.Value));
            if (r.TopObjects.Count > 0)
            {
                log("En büyük nesneler (obj id · veri sayfası · ~MB):");
                foreach (var (obj, pages) in r.TopObjects)
                    log(string.Format("   obj {0,-12} {1,8:N0} sayfa  ~{2:N0} MB", obj, pages, pages * 8192L / 1048576));
            }
            log("═══════════════════════════");
            log("Sonraki adım: seçili nesnenin veri sayfalarından kayıtları çöz (şema + satır) → export.");
        }

        // sysschobjs (obj 34) kayıtlarından nesne adlarını çıkar → (id, ad). Tablo adları burada.
        // Ön sürüm: id = kaydın ilk fixed int'i (+4); ad = kayıt içindeki en uzun UTF-16 metin.
        public static List<(long id, string name)> ExtractObjects(string path, Action<string>? log, Action<double>? prog, CancellationToken tok)
        {
            var outp = new List<(long id, string name)>();
            var seen = new HashSet<long>();
            using var fs = File.OpenRead(path);
            long size = fs.Length;
            const int BLK = 8 << 20;
            byte[] buf = new byte[BLK];
            fs.Position = 0; long done = 0;
            log?.Invoke("sysschobjs (obj 34) taranıyor — tablo adları çıkarılıyor…");
            while (true)
            {
                if (tok.IsCancellationRequested) break;
                int got = ReadFull(fs, buf, BLK);
                if (got < PAGE) break;
                int pages = got / PAGE;
                for (int pi = 0; pi < pages; pi++)
                {
                    int o = pi * PAGE;
                    if (!ValidPage(buf, o) || buf[o + 1] != 1) continue;
                    if (U32(buf, o + 0x18) != 34) continue;            // sadece sysschobjs sayfaları
                    int slotCnt = U16(buf, o + 0x16);
                    if (slotCnt <= 0 || slotCnt > 1000) continue;
                    for (int s = 0; s < slotCnt; s++)
                    {
                        int rec = U16(buf, o + PAGE - 2 - s * 2);
                        if (rec < 4 || rec > PAGE - 8) continue;
                        int abs = o + rec;
                        long id = U32(buf, abs + 4);
                        int end = Math.Min(o + PAGE, abs + 320);
                        string name = Utf16Run(buf, abs + 8, end);
                        if (name.Length >= 1 && !seen.Contains(id) && id > 0 && id < 0x7fffffff)
                        { seen.Add(id); outp.Add((id, name)); }
                    }
                }
                done += got; prog?.Invoke((double)done / Math.Max(1, size));
            }
            outp.Sort((a, b) => a.id.CompareTo(b.id));
            return outp;
        }

        // Bir aralıkta en uzun UTF-16LE "ad" dizisini bul (harf/rakam/_/Türkçe)
        private static string Utf16Run(byte[] b, int start, int end)
        {
            string best = ""; var sb = new StringBuilder();
            for (int i = start; i + 1 < end; i += 2)
            {
                char c = (char)(b[i] | (b[i + 1] << 8));
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ||
                          c == '_' || c == ' ' || "ÇĞİÖŞÜçğıöşü".IndexOf(c) >= 0;
                if (ok) sb.Append(c);
                else { if (sb.Length > best.Length) best = sb.ToString(); sb.Clear(); }
            }
            if (sb.Length > best.Length) best = sb.ToString();
            return best.Trim();
        }

        private static IEnumerable<KeyValuePair<byte, long>> SortByVal(Dictionary<byte, long> d)
        {
            var l = new List<KeyValuePair<byte, long>>(d);
            l.Sort((a, b) => b.Value.CompareTo(a.Value));
            return l;
        }

        private static string LongestUtf16(byte[] b)
        {
            string best = ""; var sb = new StringBuilder();
            for (int i = 0; i + 1 < b.Length; i += 2)
            {
                char c = (char)(b[i] | (b[i + 1] << 8));
                if (c >= 32 && c < 0x7f || (c >= 0xC0 && c < 0x250)) sb.Append(c);
                else { if (sb.Length > best.Length) best = sb.ToString(); sb.Clear(); }
            }
            if (sb.Length > best.Length) best = sb.ToString();
            return best.Length >= 3 ? best.Trim() : "";
        }

        private static bool IsZero(byte[] b, int off, int len)
        { for (int i = 0; i < len; i++) if (b[off + i] != 0) return false; return true; }

        private static int ReadFull(Stream s, byte[] buf, int want)
        { int n = 0; while (n < want) { int r = s.Read(buf, n, want - n); if (r <= 0) break; n += r; } return n; }
    }
}
