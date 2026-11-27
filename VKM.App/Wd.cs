using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace VKM.App
{
    // WD ROYL (Marvell) Servis Alanı modül ERİŞİMİ — ŞİMDİLİK SALT OKUMA.
    // Belgelenmiş VSC protokolü (çok sayıda bağımsız kaynak + WD TREX):
    //   1) VSC-enable (Super On): Feat 0x45, Sec 0x0B, LBA 0x00/0x44/0x57("DW"), Dev 0xA0, Cmd 0x80
    //   2) Komut anahtarı → SMART WRITE LOG 0xBE  (Feat 0xD6, Cmd 0xB0), 512B payload
    //      payload: 08 00 01 00 + modül ID(2B, LE)   (action 8 = SA modül, func 1 = oku)
    //   3) 0xBE sonrası dönen LBA-Mid/High = modülün SEKTÖR SAYISI
    //   4) Veri → SMART READ LOG 0xBF (Feat 0xD5), o kadar sektör
    // Modül OKUMAK firmware'i DEĞİŞTİRMEZ. Yazma (reset) ayrı, sonra, onaylı yapılacak.
    // Eski ROYL disklerde (WD..BEVT/BEVS vb.) ATA pass-through ile çalışır; yeni SMR/SED
    // mimarileri (Palmer/Spyglass) VSC'yi kilitler → orada bu yol reddedilir (donanım gerekir).
    public static class Wd
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
        const uint IOCTL = 0x0004D030;   // IOCTL_ATA_PASS_THROUGH_DIRECT
        const ushort F_DRDY = 0x01, F_DATA_IN = 0x02, F_DATA_OUT = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        struct APTD
        {
            public ushort Length, AtaFlags;
            public byte PathId, TargetId, Lun, Rsv;
            public uint DataTransferLength, TimeOutValue, RsvUlong;
            public IntPtr DataBuffer;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Prev;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] Cur;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string n, uint a, uint s, IntPtr sa, uint d, uint f, IntPtr t);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref APTD i, int isz, ref APTD o, int osz, out uint r, IntPtr ov);

        static SafeFileHandle Open(string dev)
        {
            var h = CreateFileW(dev, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { int e = Marshal.GetLastWin32Error(); h.Dispose(); throw new System.IO.IOException("Disk açılamadı (yönetici gerekir). Hata " + e); }
            return h;
        }

        // Tek ATA komutu. dir: 0 veri yok, 1 veri-giriş, 2 veri-çıkış. task: dönen register'lar.
        static byte[]? Cmd(SafeFileHandle h, byte feat, byte sec, byte lbaLow, byte lbaMid, byte lbaHigh, byte cmd,
                           int dir, byte[]? outData, out byte[] task, out bool ok)
        {
            int xfer = dir == 0 ? 0 : (sec == 0 ? 256 : sec) * 512;
            byte[] buf = dir == 2 ? ResizeTo(outData, xfer) : new byte[Math.Max(xfer, 1)];
            var gch = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try
            {
                var p = new APTD
                {
                    Length = (ushort)Marshal.SizeOf<APTD>(),
                    AtaFlags = (ushort)(F_DRDY | (dir == 1 ? F_DATA_IN : dir == 2 ? F_DATA_OUT : 0)),
                    DataTransferLength = (uint)(dir == 0 ? 0 : xfer),
                    TimeOutValue = 20,
                    DataBuffer = dir == 0 ? IntPtr.Zero : gch.AddrOfPinnedObject(),
                    Cur = new byte[8],
                    Prev = new byte[8]
                };
                p.Cur[0] = feat; p.Cur[1] = sec; p.Cur[2] = lbaLow; p.Cur[3] = lbaMid; p.Cur[4] = lbaHigh; p.Cur[5] = 0xA0; p.Cur[6] = cmd;
                ok = DeviceIoControl(h, IOCTL, ref p, p.Length, ref p, p.Length, out _, IntPtr.Zero);
                task = p.Cur;
                return dir == 1 ? buf : null;
            }
            finally { gch.Free(); }
        }

        static byte[] ResizeTo(byte[]? src, int len)
        {
            var b = new byte[len];
            if (src != null) Array.Copy(src, b, Math.Min(src.Length, len));
            return b;
        }

        static bool VscEnable(SafeFileHandle h)
        {
            Cmd(h, 0x45, 0x0B, 0x00, 0x44, 0x57, 0x80, 0, null, out _, out bool ok);
            return ok;
        }

        // Açık ve VSC-enable yapılmış handle üzerinde modülü oku. (ok, veri, sektör, mesaj)
        static (bool ok, byte[]? data, int sectors, string log) ReadModuleOn(SafeFileHandle h, int moduleId)
        {
            var key = new byte[512];
            key[0] = 0x08; key[1] = 0x00; key[2] = 0x01; key[3] = 0x00;
            key[4] = (byte)(moduleId & 0xFF); key[5] = (byte)((moduleId >> 8) & 0xFF);
            Cmd(h, 0xD6, 0x01, 0xBE, 0x4F, 0xC2, 0xB0, 2, key, out byte[] task, out bool okKey);
            if (!okKey) return (false, null, 0, "0x" + moduleId.ToString("X2") + ": anahtar (0xBE) reddedildi");
            int count = task[3] | (task[4] << 8);   // LBA-Mid/High = sektör sayısı
            if (count <= 0 || count > 65535) return (false, null, 0, "0x" + moduleId.ToString("X2") + ": sektör sayısı " + count + " (modül yok / farklı mimari)");
            int rd = Math.Min(count, 255);
            var data = Cmd(h, 0xD5, (byte)(rd & 0xFF), 0xBF, 0x4F, 0xC2, 0xB0, 1, null, out _, out bool okData);
            if (!okData || data == null) return (false, null, 0, "0x" + moduleId.ToString("X2") + ": veri (0xBF) reddedildi");
            return (true, data, rd, "0x" + moduleId.ToString("X2") + ": " + rd + "/" + count + " sektör okundu");
        }

        // SALT-OKUMA PROBE: VSC açılıyor mu, modül dizini + SMART modülleri okunuyor mu?
        // Firmware'e yazmaz. Dönen: rapor metni + dizin ve SMART ham baytları (varsa).
        public static (string report, byte[]? dir, byte[]? smart) Probe(string dev)
        {
            var sb = new StringBuilder();
            byte[]? dir = null, smart = null;
            SafeFileHandle? h = null;
            try { h = Open(dev); }
            catch (Exception ex) { return ("Açılamadı: " + ex.Message, null, null); }

            try
            {
                if (!VscEnable(h))
                {
                    sb.AppendLine("VSC-enable REDDEDİLDİ.");
                    sb.AppendLine("→ Bu disk vendor komutunu geçirmiyor. Olası sebepler:");
                    sb.AppendLine("  • USB köprüsü ardında (doğrudan SATA'ya tak)");
                    sb.AppendLine("  • Yeni SMR/SED mimarisi (Palmer/Spyglass) VSC'yi kilitliyor");
                    sb.AppendLine("  • ROYL olmayan/farklı üretici");
                    return (sb.ToString(), null, null);
                }
                sb.AppendLine("✓ VSC-enable BAŞARILI — disk firmware moduna girdi.");

                var d = ReadModuleOn(h, 0x01);   // modül dizini (DIR)
                sb.AppendLine("DIR  " + d.log);
                if (d.ok) dir = d.data;

                var s = ReadModuleOn(h, 0x24);   // SMART ana modül
                sb.AppendLine("SMART " + s.log);
                if (s.ok) smart = s.data;

                if (dir != null)
                    sb.AppendLine("\nDIR ilk 16 bayt: " + Hex(dir, 16));
                if (smart != null)
                    sb.AppendLine("SMART ilk 16 bayt: " + Hex(smart, 16));

                sb.AppendLine("\nSonraki adım: dizini çözüp SMART/G-list modüllerinin yerini bulmak,");
                sb.AppendLine("sonra (onaylı) yazma ile reset. Ham dumpları bana gönder.");
            }
            catch (Exception ex) { sb.AppendLine("Hata: " + ex.Message); }
            finally { h?.Dispose(); }

            return (sb.ToString(), dir, smart);
        }

        static string Hex(byte[] b, int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(n, b.Length); i++) sb.Append(b[i].ToString("X2")).Append(' ');
            return sb.ToString().Trim();
        }
    }
}
