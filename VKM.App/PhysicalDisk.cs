using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VKM.App
{
    // Fiziksel diski SALT OKUNUR açar. Diske asla yazılmaz.
    internal static class PhysicalDisk
    {
        const uint GENERIC_READ = 0x80000000;
        const uint GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 0x1;
        const uint FILE_SHARE_WRITE = 0x2;
        const uint OPEN_EXISTING = 3;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern SafeFileHandle CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        public static Stream OpenReadOnly(string deviceId, long length, int sector, bool strict = false)
        {
            var handle = CreateFileW(
                deviceId, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    "Disk açılamadı (büyük ihtimalle yönetici yetkisi gerekiyor). Hata kodu: " + err);
            }

            var baseStream = new FileStream(handle, FileAccess.Read);
            if (strict) return new AligningStream(baseStream, length, sector);   // RAID: hata istisna atsın, pariteden tamamlansın
            // Zayıf diskte bad-sektörü atla+say: önce retry, üstünde hizalama
            var retry = new RetryStream(baseStream, sector <= 0 ? 512 : sector, 3);
            return new AligningStream(retry, length, sector);
        }

        // DİKKAT: diske YAZMA için açar (Refresh/HPA gibi onaylı işlemler için). Ham FileStream döner.
        public static FileStream OpenReadWrite(string deviceId)
        {
            var handle = CreateFileW(
                deviceId, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE,
                IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException("Disk yazma için açılamadı (yönetici gerekir, disk kullanımda olabilir). Hata kodu: " + err);
            }
            return new FileStream(handle, FileAccess.ReadWrite);
        }
    }
}
