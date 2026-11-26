using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VKM.App
{
    // ATA pass-through ile DOĞRUDAN SMART okuma (WMI'nin kaçırdığı diskler için).
    // Standart ATA SMART komutları (0xB0): READ DATA (0xD0), READ THRESHOLDS (0xD1).
    // Vendor komutları da aynı pass-through kanalından gidecek — bu, o altyapıyı kurar.
    public static class AtaSmart
    {
        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_RW = 0x3, OPEN_EXISTING = 3;
        const uint IOCTL_ATA_PASS_THROUGH_DIRECT = 0x0004D030;
        const ushort ATA_FLAGS_DRDY_REQUIRED = 0x01;
        const ushort ATA_FLAGS_DATA_IN = 0x02;

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

        // SMART 512 baytlık veri bloğunu oku. feature: 0xD0 (DATA) ya da 0xD1 (THRESHOLDS). Olmazsa null.
        public static byte[]? ReadBlock(string deviceId, byte feature)
        {
            SafeFileHandle? h = null;
            try
            {
                h = CreateFileW(deviceId, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h.IsInvalid) { h = CreateFileW(deviceId, GENERIC_READ, FILE_SHARE_RW, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero); }
                if (h.IsInvalid) return null;

                var data = new byte[512];
                var gch = GCHandle.Alloc(data, GCHandleType.Pinned);
                try
                {
                    var p = new APTD
                    {
                        Length = (ushort)Marshal.SizeOf<APTD>(),
                        AtaFlags = (ushort)(ATA_FLAGS_DRDY_REQUIRED | ATA_FLAGS_DATA_IN),   // 28-bit SMART
                        DataTransferLength = 512,
                        TimeOutValue = 15,
                        DataBuffer = gch.AddrOfPinnedObject(),
                        CurrentTaskFile = new byte[8],
                        PreviousTaskFile = new byte[8]
                    };
                    // SMART: Features=feature, SectorCount=1, LbaMid=0x4F, LbaHigh=0xC2, Command=0xB0
                    p.CurrentTaskFile[0] = feature;
                    p.CurrentTaskFile[1] = 1;
                    p.CurrentTaskFile[3] = 0x4F;
                    p.CurrentTaskFile[4] = 0xC2;
                    p.CurrentTaskFile[6] = 0xB0;

                    bool ok = DeviceIoControl(h, IOCTL_ATA_PASS_THROUGH_DIRECT, ref p, p.Length, ref p, p.Length, out _, IntPtr.Zero);
                    return ok ? data : null;
                }
                finally { gch.Free(); }
            }
            catch { return null; }
            finally { h?.Dispose(); }
        }
    }
}
