using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VKM.App
{
    // HPA (Host Protected Area) ile diskin azami LBA'sını değiştirir.
    // "Kes" = azami LBA'yı düşür (sondaki bad bölge kullanıcı alanı dışına çıkar).
    // "Geri al" = azami LBA'yı native (gerçek) değere döndür → tam kapasite.
    // ATA pass-through gerektirir → USB köprülerinde çalışmaz, SATA/doğrudan ister.
    public static class HpaControl
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
        const uint IOCTL_ATA_PASS_THROUGH_DIRECT = 0x0004D030;
        const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01;
        const ushort ATA_FLAGS_48BIT = 0x08;

        [StructLayout(LayoutKind.Sequential)]
        struct APTD
        {
            public ushort Length;
            public ushort AtaFlags;
            public byte PathId, TargetId, Lun, ReservedAsUchar;
            public uint DataTransferLength;
            public uint TimeOutValue;
            public uint ReservedAsUlong;
            public IntPtr DataBuffer;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] PreviousTaskFile;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public byte[] CurrentTaskFile;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, ref APTD inBuf, int inSize, ref APTD outBuf, int outSize, out uint ret, IntPtr ov);

        static SafeFileHandle Open(string deviceId)
        {
            var h = CreateFileW(deviceId, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) { int e = Marshal.GetLastWin32Error(); h.Dispose(); throw new IOException("Disk açılamadı (yönetici gerekir). Hata: " + e); }
            return h;
        }

        // 48-bit, veri taşımayan ATA komutu gönder; LBA registerlarını yükle, sonuç task file döndür
        static (byte[] cur, byte[] prev) Cmd48(SafeFileHandle h, byte cmd, long lba, byte sectorCountLow)
        {
            var p = new APTD
            {
                Length = (ushort)Marshal.SizeOf<APTD>(),
                AtaFlags = (ushort)(ATA_FLAGS_DRDY_REQUIRED | ATA_FLAGS_48BIT),
                TimeOutValue = 15,
                DataTransferLength = 0,
                DataBuffer = IntPtr.Zero,
                CurrentTaskFile = new byte[8],
                PreviousTaskFile = new byte[8]
            };
            // task file: [0]Features [1]SectorCount [2]LbaLow [3]LbaMid [4]LbaHigh [5]Device [6]Command
            p.CurrentTaskFile[1] = sectorCountLow;
            p.CurrentTaskFile[2] = (byte)(lba & 0xFF);
            p.CurrentTaskFile[3] = (byte)((lba >> 8) & 0xFF);
            p.CurrentTaskFile[4] = (byte)((lba >> 16) & 0xFF);
            p.PreviousTaskFile[2] = (byte)((lba >> 24) & 0xFF);
            p.PreviousTaskFile[3] = (byte)((lba >> 32) & 0xFF);
            p.PreviousTaskFile[4] = (byte)((lba >> 40) & 0xFF);
            p.CurrentTaskFile[5] = 0x40;   // LBA modu
            p.CurrentTaskFile[6] = cmd;

            bool ok = DeviceIoControl(h, IOCTL_ATA_PASS_THROUGH_DIRECT, ref p, p.Length, ref p, p.Length, out _, IntPtr.Zero);
            if (!ok) throw new IOException("ATA komutu geçmedi (büyük ihtimalle USB köprüsü — SATA/doğrudan bağlantı gerekir). Hata: " + Marshal.GetLastWin32Error());
            return (p.CurrentTaskFile, p.PreviousTaskFile);
        }

        // Gerçek (native) azami LBA — son adreslenebilir sektör. Kapasite = (LBA+1)*sektör
        public static long ReadNativeMax(string deviceId)
        {
            using var h = Open(deviceId);
            var (cur, prev) = Cmd48(h, 0x27, 0, 0);   // READ NATIVE MAX ADDRESS EXT
            long lba = cur[2] | ((long)cur[3] << 8) | ((long)cur[4] << 16)
                     | ((long)prev[2] << 24) | ((long)prev[3] << 32) | ((long)prev[4] << 40);
            return lba;
        }

        // Azami LBA'yı ayarla. SET MAX ADDRESS EXT (0x37). sectorCountLow bit0=0 → kalıcı.
        public static void SetMax(string deviceId, long newMaxLba)
        {
            using var h = Open(deviceId);
            Cmd48(h, 0x37, newMaxLba, 0);   // non-volatile (kalıcı)
        }

        // Tam kapasiteye dön: azami LBA'yı native'e ayarla
        public static long RestoreNative(string deviceId)
        {
            long nat = ReadNativeMax(deviceId);
            SetMax(deviceId, nat);
            return nat;
        }
    }
}
