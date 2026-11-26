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

        // ATA IDENTIFY DEVICE (0xEC) → model, firmware, seri, dönüş hızı (1 = SSD, yoksa RPM)
        public static (string model, string fw, string serial, int rotation)? Identify(string deviceId)
        {
            var d = DataIn(deviceId, 0xEC, 0, 0, 0);
            if (d == null) return null;
            return (AtaStr(d, 27, 20), AtaStr(d, 23, 4), AtaStr(d, 10, 10), d[434] | (d[435] << 8));
        }

        // ATA identify string: her 16-bit sözcükte baytlar takas (yüksek bayt önce)
        static string AtaStr(byte[] buf, int wordStart, int wordCount)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < wordCount; i++)
            {
                int off = (wordStart + i) * 2;
                if (off + 1 >= buf.Length) break;
                sb.Append((char)buf[off + 1]);
                sb.Append((char)buf[off]);
            }
            return sb.ToString().Trim().Trim('\0');
        }

        // SMART 512 baytlık veri bloğunu oku. feature: 0xD0 (DATA) ya da 0xD1 (THRESHOLDS). Olmazsa null.
        public static byte[]? ReadBlock(string deviceId, byte feature)
            => DataIn(deviceId, 0xB0, feature, 0x4F, 0xC2);

        // 512 baytlık veri-giriş ATA komutu (SMART / IDENTIFY ortak yolu)
        static byte[]? DataIn(string deviceId, byte command, byte feature, byte lbaMid, byte lbaHigh)
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
                    p.CurrentTaskFile[0] = feature;
                    p.CurrentTaskFile[1] = 1;
                    p.CurrentTaskFile[3] = lbaMid;
                    p.CurrentTaskFile[4] = lbaHigh;
                    p.CurrentTaskFile[6] = command;

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
