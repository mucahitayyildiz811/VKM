using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace VKM.App
{
    // Excel (.xlsx) ve Word (.docx) içeriğini dış kütüphane olmadan (ZIP+XML) okur.
    public static class OfficePreview
    {
        // Excel: ilk sayfayı hizalı metin tablosu olarak döndürür
        public static string? Xlsx(byte[] data, out string err)
        {
            err = "";
            try
            {
                using var ms = new MemoryStream(data);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);

                // Paylaşılan metinler
                var shared = new List<string>();
                var ssEntry = zip.GetEntry("xl/sharedStrings.xml");
                if (ssEntry != null)
                {
                    using var s = ssEntry.Open();
                    var sdoc = XDocument.Load(s);
                    foreach (var si in sdoc.Root!.Elements().Where(e => e.Name.LocalName == "si"))
                    {
                        var sb = new StringBuilder();
                        foreach (var t in si.Descendants().Where(e => e.Name.LocalName == "t")) sb.Append(t.Value);
                        shared.Add(sb.ToString());
                    }
                }

                // İlk çalışma sayfası
                var wsEntry = zip.Entries.FirstOrDefault(e =>
                    e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml"));
                if (wsEntry == null) { err = "çalışma sayfası bulunamadı"; return null; }

                var cells = new Dictionary<(int r, int c), string>();
                int maxR = 0, maxC = 0;
                using (var ws = wsEntry.Open())
                {
                    var wdoc = XDocument.Load(ws);
                    foreach (var c in wdoc.Descendants().Where(e => e.Name.LocalName == "c"))
                    {
                        (int row, int col) = ParseRef(c.Attribute("r")?.Value ?? "");
                        if (row <= 0 || col <= 0) continue;
                        string t = c.Attribute("t")?.Value ?? "";
                        string val;
                        if (t == "inlineStr")
                            val = string.Concat(c.Descendants().Where(e => e.Name.LocalName == "t").Select(x => x.Value));
                        else
                        {
                            val = c.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                            if (t == "s" && int.TryParse(val, out int idx) && idx >= 0 && idx < shared.Count)
                                val = shared[idx];
                        }
                        cells[(row, col)] = val;
                        if (row > maxR) maxR = row;
                        if (col > maxC) maxC = col;
                    }
                }

                if (maxR == 0) { err = "boş sayfa"; return "(boş sayfa)"; }
                maxR = Math.Min(maxR, 300);
                maxC = Math.Min(maxC, 40);

                // Sütun genişliklerini hesapla (hizalı görünüm için, en çok 22)
                var w = new int[maxC + 1];
                for (int r = 1; r <= maxR; r++)
                    for (int col = 1; col <= maxC; col++)
                        if (cells.TryGetValue((r, col), out string? v) && v != null)
                            w[col] = Math.Max(w[col], Math.Min(v.Length, 22));

                var sb2 = new StringBuilder();
                for (int r = 1; r <= maxR; r++)
                {
                    for (int col = 1; col <= maxC; col++)
                    {
                        cells.TryGetValue((r, col), out string? v);
                        v ??= "";
                        if (v.Length > 22) v = v.Substring(0, 21) + "…";
                        sb2.Append(v.PadRight(Math.Max(w[col], 3) + 2));
                    }
                    sb2.Append('\n');
                }
                return sb2.ToString();
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        // Word: gövde metnini paragraf paragraf döndürür
        public static string? Docx(byte[] data, out string err)
        {
            err = "";
            try
            {
                using var ms = new MemoryStream(data);
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
                var entry = zip.GetEntry("word/document.xml");
                if (entry == null) { err = "belge bulunamadı"; return null; }

                using var s = entry.Open();
                var doc = XDocument.Load(s);
                var sb = new StringBuilder();
                foreach (var p in doc.Descendants().Where(e => e.Name.LocalName == "p"))
                {
                    foreach (var t in p.Descendants().Where(e => e.Name.LocalName == "t")) sb.Append(t.Value);
                    sb.Append('\n');
                }
                return sb.ToString();
            }
            catch (Exception ex) { err = ex.Message; return null; }
        }

        // "B5" -> (5, 2)
        private static (int row, int col) ParseRef(string r)
        {
            int i = 0, col = 0;
            while (i < r.Length && char.IsLetter(r[i])) { col = col * 26 + (char.ToUpperInvariant(r[i]) - 'A' + 1); i++; }
            int.TryParse(r.Substring(i), out int row);
            return (row, col);
        }
    }
}
