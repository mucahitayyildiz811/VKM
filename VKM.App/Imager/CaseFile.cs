using System;
using System.IO;
using System.Text;

namespace VKM.App
{
    // İş (CASE) durumu: CASE klasöründe "CASE.vkm" olarak durur.
    // İçinde iş no, isim, disk bilgisi, imaj yolu, harita geometrisi ve harita durumu (base64).
    // "Eski İş Aç" bunu okuyup haritayı ve root'u geri getirir.
    public class CaseFile
    {
        public string Number = "", Name = "", ImagePath = "", Model = "", Serial = "", Device = "";
        public long Total; public int Sector, Blocks, Heads, MapW, MapH;
        public byte[] Map = Array.Empty<byte>();

        public const string FileName = "CASE.vkm";

        public static void Save(string folder, CaseInfo c, DiskInfo src, ImageStore store, int mapW, int mapH)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var sb = new StringBuilder();
                sb.AppendLine("VKMCASE1");
                sb.AppendLine("num=" + c.Number);
                sb.AppendLine("name=" + c.Name);
                sb.AppendLine("image=" + c.ImagePath);
                sb.AppendLine("total=" + store.Total);
                sb.AppendLine("sector=" + store.Sector);
                sb.AppendLine("blocks=" + store.Blocks);
                sb.AppendLine("heads=" + store.Heads);
                sb.AppendLine("mapW=" + mapW);
                sb.AppendLine("mapH=" + mapH);
                sb.AppendLine("model=" + src.Model);
                sb.AppendLine("serial=" + src.Serial);
                sb.AppendLine("device=" + src.DeviceId);
                sb.AppendLine("map=" + Convert.ToBase64String(store.Status));
                File.WriteAllText(Path.Combine(folder, FileName), sb.ToString());
            }
            catch { }
        }

        public static CaseFile? Load(string datPath)
        {
            try
            {
                var f = new CaseFile();
                foreach (var raw in File.ReadAllLines(datPath))
                {
                    int e = raw.IndexOf('=');
                    if (e < 0) continue;
                    string k = raw[..e], v = raw[(e + 1)..];
                    switch (k)
                    {
                        case "num": f.Number = v; break;
                        case "name": f.Name = v; break;
                        case "image": f.ImagePath = v; break;
                        case "total": f.Total = long.Parse(v); break;
                        case "sector": f.Sector = int.Parse(v); break;
                        case "blocks": f.Blocks = int.Parse(v); break;
                        case "heads": f.Heads = int.Parse(v); break;
                        case "mapW": f.MapW = int.Parse(v); break;
                        case "mapH": f.MapH = int.Parse(v); break;
                        case "model": f.Model = v; break;
                        case "serial": f.Serial = v; break;
                        case "device": f.Device = v; break;
                        case "map": f.Map = Convert.FromBase64String(v); break;
                    }
                }
                return f.Total > 0 ? f : null;
            }
            catch { return null; }
        }
    }
}
