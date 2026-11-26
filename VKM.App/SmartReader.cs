using System;
using System.Collections.Generic;
using System.Management;

namespace VKM.App
{
    // Tek bir SMART özniteliği (satır)
    public class SmartAttr
    {
        public int Id;
        public string Name = "";
        public int Current;      // normalize değer (0-255)
        public int Worst;
        public int Threshold = -1;
        public ulong Raw;        // ham değer (6 bayt)
        public bool Fail => Threshold > 0 && Current > 0 && Current <= Threshold;
        public bool Warn => (Id == 5 || Id == 197 || Id == 198 || Id == 187 || Id == 196) && Raw > 0;
        public string IdHex => "0x" + Id.ToString("X2") + " (" + Id + ")";
        public string ThreshText => Threshold >= 0 ? Threshold.ToString() : "-";
        public string Durum => Fail ? "ARIZA" : Warn ? "DİKKAT" : "OK";
    }

    // Bir diskin SMART değerleri
    public class SmartData
    {
        public bool HasData;
        public bool PredictFailure;
        public ulong? Reallocated;    // 5
        public ulong? Pending;        // 197
        public ulong? Uncorrectable;  // 198
        public ulong? Crc;            // 199
        public ulong? PowerOnHours;   // 9
        public int? TempC;            // 194 / 190
        public readonly List<SmartAttr> Attrs = new();
        public readonly Dictionary<int, int> Thresh = new();   // id -> eşik (ara depo)
    }

    // Windows WMI (root\wmi) üzerinden SMART okuma.
    // Çoğu SMART sorgusu YÖNETİCİ yetkisi ister; yoksa boş döner (hata vermez).
    public static class SmartReader
    {
        public static Dictionary<string, SmartData> ReadAll()
        {
            var map = new Dictionary<string, SmartData>(StringComparer.OrdinalIgnoreCase);

            // 1) Arıza tahmini (PredictFailure)
            TryEach("SELECT * FROM MSStorageDriver_FailurePredictStatus", mo =>
            {
                string inst = Norm(mo["InstanceName"] as string);
                var d = GetOrAdd(map, inst);
                try { d.PredictFailure = Convert.ToBoolean(mo["PredictFailure"]); } catch { }
                d.HasData = true;
            });

            // 2) Eşik değerleri (öznitelikten ÖNCE oku ki satırlara işlensin)
            TryEach("SELECT * FROM MSStorageDriver_FailurePredictThresholds", mo =>
            {
                string inst = Norm(mo["InstanceName"] as string);
                if (mo["VendorSpecific"] is not byte[] raw) return;
                var d = GetOrAdd(map, inst);
                for (int off = 2; off + 12 <= raw.Length; off += 12)
                {
                    byte id = raw[off];
                    if (id == 0) continue;
                    d.Thresh[id] = raw[off + 1];
                }
            });

            // 3) Ham SMART öznitelikleri
            TryEach("SELECT * FROM MSStorageDriver_ATAPISmartData", mo =>
            {
                string inst = Norm(mo["InstanceName"] as string);
                if (mo["VendorSpecific"] is not byte[] raw) return;
                var d = GetOrAdd(map, inst);
                ParseAttributes(raw, d);
                d.HasData = true;
            });

            return map;
        }

        private static void TryEach(string query, Action<ManagementObject> action)
        {
            try
            {
                var scope = new ManagementScope(@"\\.\root\wmi");
                scope.Connect();
                using var searcher = new ManagementObjectSearcher(scope, new ObjectQuery(query));
                foreach (ManagementObject mo in searcher.Get())
                {
                    try { action(mo); } catch { /* bu diski atla */ }
                }
            }
            catch { /* SMART okunamadı (yönetici gerekebilir) */ }
        }

        // 512 baytlık SMART verisini öznitelik öznitelik çöz
        private static void ParseAttributes(byte[] raw, SmartData d)
        {
            // Yapı: 2 bayt başlık, sonra 30 öznitelik x 12 bayt
            // [id(1)][flags(2)][current(1)][worst(1)][raw(6)][resv(1)]
            for (int offset = 2; offset + 12 <= raw.Length; offset += 12)
            {
                byte id = raw[offset];
                if (id == 0) continue;

                int cur = raw[offset + 3];
                int worst = raw[offset + 4];
                ulong val = 0;
                for (int i = 0; i < 6; i++)
                    val |= (ulong)raw[offset + 5 + i] << (8 * i);

                var a = new SmartAttr { Id = id, Name = AttrName(id), Current = cur, Worst = worst, Raw = val };
                if (d.Thresh.TryGetValue(id, out int th)) a.Threshold = th;
                d.Attrs.Add(a);

                switch (id)
                {
                    case 5: d.Reallocated = val; break;
                    case 197: d.Pending = val; break;
                    case 198: d.Uncorrectable = val; break;
                    case 199: d.Crc = val & 0xFFFFFFFF; break;
                    case 9: d.PowerOnHours = val & 0xFFFFFFFF; break;
                    case 194: d.TempC = (int)(val & 0xFF); break;
                    case 190: if (d.TempC == null) d.TempC = (int)(val & 0xFF); break;
                }
            }
        }

        // Yaygın SMART öznitelik adları
        public static string AttrName(int id) => id switch
        {
            1 => "Okuma Hata Oranı",
            2 => "Verim (Throughput)",
            3 => "Spin-Up Süresi",
            4 => "Başlat/Durdur Sayısı",
            5 => "Yeniden Atanan Sektör",
            7 => "Arama Hata Oranı",
            8 => "Arama Zaman Verimi",
            9 => "Çalışma Saati (Power-On)",
            10 => "Spin Yeniden Deneme",
            11 => "Kalibrasyon Yeniden Deneme",
            12 => "Güç Döngüsü Sayısı",
            177 => "Wear Leveling (SSD)",
            179 => "Kullanılan Yedek Blok (SSD)",
            181 => "Program Hatası (SSD)",
            182 => "Silme Hatası (SSD)",
            184 => "Uçtan Uca Hata",
            187 => "Bildirilen Düzeltilemez Hata",
            188 => "Komut Zaman Aşımı",
            190 => "Hava Akışı Sıcaklığı",
            191 => "G-Sense Hata",
            192 => "Kapanışta Kafa Çekme",
            193 => "Yükle/Boşalt Döngüsü",
            194 => "Sıcaklık",
            195 => "Donanım ECC Düzeltme",
            196 => "Yeniden Atama Olayı",
            197 => "Bekleyen Sektör (Pending)",
            198 => "Çevrimdışı Düzeltilemez",
            199 => "UDMA CRC Hatası",
            200 => "Çok Bölgeli Yazma Hatası",
            201 => "Yumuşak Okuma Hatası",
            231 => "SSD Ömür / Boş",
            233 => "Medya Aşınması (SSD)",
            240 => "Kafa Uçuş Saati",
            241 => "Toplam Yazılan (LBA)",
            242 => "Toplam Okunan (LBA)",
            _ => "Öznitelik " + id
        };

        private static SmartData GetOrAdd(Dictionary<string, SmartData> map, string key)
        {
            if (!map.TryGetValue(key, out var d))
            {
                d = new SmartData();
                map[key] = d;
            }
            return d;
        }

        // InstanceName / PNPDeviceID eşleşmesi için normalize et
        public static string Norm(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim().ToUpperInvariant();
            if (s.EndsWith("_0")) s = s.Substring(0, s.Length - 2);
            return s;
        }
    }
}
