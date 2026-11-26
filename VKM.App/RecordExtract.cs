using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace VKM.App
{
    // B3 — SATIR ÇIKARICI. Seçili nesnenin (m_objId) veri sayfalarındaki kayıtları çözer.
    // MSSQL veri kaydı yapısı:
    //   [0] statusA  (bit5=0x20 değişken kolon var, bit4=0x10 null bitmap)
    //   [2:4] fixedEnd = sabit alanın sonu (kolon sayısının olduğu yer)
    //   [4:fixedEnd] sabit kolon verisi
    //   [fixedEnd:+2] kolon sayısı N ; sonra null bitmap ceil(N/8) bayt
    //   (var ise) [+2] M ; M×2 offset dizisi (kayıt başına göre her değişken kolonun BİTİŞİ) ; veri
    // v1: değişken kolonları METNE çevirir (nvarchar→UTF-16, varchar→cp1254); sabit blok+nullbmp hex.
    //     Tip/kolon adı bir sonraki adımda syscolpars ile eklenecek.
    public static class RecordExtract
    {
        const int PAGE = 8192;
        static ushort U16(byte[] b, int o) => (ushort)(b[o] | (b[o + 1] << 8));
        static uint U32(byte[] b, int o) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        static bool ValidPage(byte[] b, int o) => b[o] == 0x01;

        public static int ExtractTable(string path, long objId, string csvPath,
            Action<string> log, Action<double>? prog, CancellationToken tok)
        {
            using var fs = File.OpenRead(path);
            long size = fs.Length;
            using var sw = new StreamWriter(csvPath, false, new UTF8Encoding(true));
            int rows = 0, pagesHit = 0, maxVar = 0;
            var pending = new StringBuilder();

            const int BLK = 8 << 20;
            byte[] buf = new byte[BLK];
            fs.Position = 0; long done = 0;
            log("obj " + objId + " için veri sayfaları taranıyor…");

            // başlık satırını sonradan maxVar'a göre yazacağız → önce satırları tut, en sonda birleştir
            var lines = new System.Collections.Generic.List<string[]>();

            while (true)
            {
                if (tok.IsCancellationRequested) break;
                int got = ReadFull(fs, buf, BLK);
                if (got < PAGE) break;
                int pages = got / PAGE;
                for (int pi = 0; pi < pages; pi++)
                {
                    int o = pi * PAGE;
                    if (!ValidPage(buf, o) || buf[o + 1] != 1) continue;   // sadece veri sayfası
                    if (U32(buf, o + 0x18) != objId) continue;
                    int slotCnt = U16(buf, o + 0x16);
                    if (slotCnt <= 0 || slotCnt > 1000) continue;
                    pagesHit++;
                    for (int s = 0; s < slotCnt; s++)
                    {
                        int rec = U16(buf, o + PAGE - 2 - s * 2);
                        if (rec < 96 || rec > PAGE - 4) continue;
                        var vals = ParseRecord(buf, o, rec, out string fixedHex, out string nullHex);
                        if (vals == null) continue;
                        if (vals.Length > maxVar) maxVar = vals.Length;
                        var row = new string[2 + vals.Length];
                        row[0] = fixedHex; row[1] = nullHex;
                        Array.Copy(vals, 0, row, 2, vals.Length);
                        lines.Add(row);
                        rows++;
                    }
                }
                done += got; prog?.Invoke((double)done / Math.Max(1, size));
            }

            // başlık
            var hdr = new StringBuilder("sabit_hex;null_bitmap");
            for (int i = 0; i < maxVar; i++) hdr.Append(";kolon").Append(i + 1);
            sw.WriteLine(hdr.ToString());
            foreach (var row in lines)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < 2 + maxVar; i++)
                {
                    if (i > 0) sb.Append(';');
                    string v = i < row.Length ? row[i] : "";
                    sb.Append(Csv(v));
                }
                sw.WriteLine(sb.ToString());
            }
            sw.Flush();
            log("✓ " + rows.ToString("N0") + " satır çıktı (" + pagesHit + " veri sayfası, en çok " + maxVar + " değişken kolon).");
            log("CSV: " + csvPath);
            return rows;
        }

        // Bir kaydı çöz → değişken kolon metinleri (+ sabit blok hex, null bitmap hex)
        static string[]? ParseRecord(byte[] p, int pageOff, int rec, out string fixedHex, out string nullHex)
        {
            fixedHex = ""; nullHex = "";
            try
            {
                int abs = pageOff + rec;
                byte statusA = p[abs];
                int recType = (statusA >> 1) & 0x07;
                if (recType != 0) return null;                       // sadece birincil kayıt
                int fixedEnd = U16(p, abs + 2);
                if (fixedEnd < 4 || rec + fixedEnd + 2 > PAGE) return null;
                int ncol = U16(p, abs + fixedEnd);
                if (ncol < 1 || ncol > 1024) return null;
                int nullBytes = (ncol + 7) / 8;
                int nbOff = abs + fixedEnd + 2;
                if (nbOff + nullBytes > pageOff + PAGE) return null;

                fixedHex = Hex(p, abs + 4, fixedEnd - 4);
                nullHex = Hex(p, nbOff, nullBytes);

                var outv = new System.Collections.Generic.List<string>();
                bool hasVar = (statusA & 0x20) != 0;
                if (hasVar)
                {
                    int varStart = nbOff + nullBytes;
                    if (varStart + 2 > pageOff + PAGE) return outv.ToArray();
                    int m = U16(p, varStart);
                    if (m < 1 || m > 1024) return outv.ToArray();
                    int offArr = varStart + 2;
                    if (offArr + m * 2 > pageOff + PAGE) return outv.ToArray();
                    int colStart = (offArr + m * 2) - abs;           // ilk değişken kolonun kayıt-içi başlangıcı
                    for (int k = 0; k < m; k++)
                    {
                        int entry = U16(p, offArr + k * 2);
                        bool offRow = (entry & 0x8000) != 0;         // satır-dışı/karmaşık kolon
                        int colEnd = entry & 0x7FFF;
                        int st = abs + colStart, en = abs + colEnd;
                        if (colEnd < colStart || en > pageOff + PAGE || st < pageOff) { outv.Add(offRow ? "[offrow]" : ""); colStart = colEnd; continue; }
                        outv.Add(offRow ? "[offrow]" : DecodeText(p, st, en - st));
                        colStart = colEnd;
                    }
                }
                return outv.ToArray();
            }
            catch { return null; }
        }

        // Değişken kolon baytlarını okunabilir metne çevir (nvarchar=UTF-16, varchar=cp1254), değilse hex
        static string DecodeText(byte[] b, int off, int len)
        {
            if (len <= 0) return "";
            if (len > 8000) len = 8000;
            // UTF-16LE dene (çift uzunluk + çoğu çift bayt yazılabilir/sıfır)
            if ((len & 1) == 0)
            {
                int ok = 0, tot = len / 2;
                for (int i = 0; i < len; i += 2)
                {
                    char c = (char)(b[off + i] | (b[off + i + 1] << 8));
                    if (c == 0 || (c >= 32 && c < 0xFFFD)) ok++;
                }
                if (tot > 0 && ok >= tot * 0.9)
                {
                    var s = Encoding.Unicode.GetString(b, off, len).Replace("\0", "").Trim();
                    if (s.Length > 0 && Printable(s)) return s;
                }
            }
            // tek-bayt (Latin1 — .NET 8 yerleşik, ek paket yok) dene
            int pr = 0;
            for (int i = 0; i < len; i++) { byte c = b[off + i]; if (c == 0 || (c >= 32 && c < 127) || c >= 160) pr++; }
            if (pr >= len * 0.85)
            {
                var s = Encoding.Latin1.GetString(b, off, len).Replace("\0", "").Trim();
                if (s.Length > 0 && Printable(s)) return s;
            }
            return "0x" + Hex(b, off, Math.Min(len, 64)).Replace(" ", "");
        }

        static bool Printable(string s)
        {
            int bad = 0; foreach (char c in s) if (c < 32 && c != '\t') bad++;
            return bad <= s.Length * 0.1;
        }

        static string Hex(byte[] b, int off, int len)
        {
            if (len <= 0) return "";
            var sb = new StringBuilder(len * 3);
            for (int i = 0; i < len; i++) { sb.Append(b[off + i].ToString("X2")); if (i + 1 < len) sb.Append(' '); }
            return sb.ToString();
        }

        static string Csv(string v)
        {
            if (v == null) return "";
            if (v.IndexOf(';') >= 0 || v.IndexOf('"') >= 0 || v.IndexOf('\n') >= 0 || v.IndexOf('\r') >= 0)
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }

        static int ReadFull(Stream s, byte[] buf, int want)
        { int n = 0; while (n < want) { int r = s.Read(buf, n, want - n); if (r <= 0) break; n += r; } return n; }
    }
}
