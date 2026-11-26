using System;
using System.Collections.Generic;
using System.Management;

namespace VKM.App
{
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

            // 2) Ham SMART öznitelikleri
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
            for (int offset = 2; offset + 12 <= raw.Length; offset += 12)
            {
                byte id = raw[offset];
                if (id == 0) continue;

                // Ham değer: 6 bayt, little-endian
                ulong val = 0;
                for (int i = 0; i < 6; i++)
                    val |= (ulong)raw[offset + 5 + i] << (8 * i);

                switch (id)
                {
                    case 5:   d.Reallocated = val; break;
                    case 197: d.Pending = val; break;
                    case 198: d.Uncorrectable = val; break;
                    case 199: d.Crc = val & 0xFFFFFFFF; break;
                    case 9:   d.PowerOnHours = val & 0xFFFFFFFF; break;
                }
            }
        }

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
