using System;
using System.IO;
using System.Runtime.InteropServices;

namespace VKM.App
{
    // Bir hedef yolun hangi fiziksel disk(ler) üzerinde olduğunu bulur.
    // Amaç: kurtarılan veriyi kaynak diske yazmayı engellemek.
    public static class DiskGuard
    {
        private const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x560000;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(string lpFileName, uint access, uint share,
            IntPtr sec, uint disp, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr h, uint code, IntPtr inBuf, uint inSize,
            byte[] outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr h);

        // path'in bulunduğu fiziksel disk numaraları (ör. [2]); bulunamazsa boş dizi
        public static int[] PhysicalDriveNumbers(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return Array.Empty<int>();
                string letter = root.TrimEnd('\\', '/');       // "D:"
                if (letter.Length < 2 || letter[1] != ':') return Array.Empty<int>();

                IntPtr h = CreateFileW(@"\\.\" + letter, 0, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (h == IntPtr.Zero || h.ToInt64() == -1) return Array.Empty<int>();

                try
                {
                    byte[] buf = new byte[512];
                    if (!DeviceIoControl(h, IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, IntPtr.Zero, 0,
                        buf, (uint)buf.Length, out _, IntPtr.Zero))
                        return Array.Empty<int>();

                    int n = BitConverter.ToInt32(buf, 0);
                    if (n <= 0 || n > 16) return Array.Empty<int>();
                    var list = new int[n];
                    for (int i = 0; i < n; i++)
                        list[i] = BitConverter.ToInt32(buf, 8 + i * 24);   // DISK_EXTENT.DiskNumber
                    return list;
                }
                finally { CloseHandle(h); }
            }
            catch { return Array.Empty<int>(); }
        }

        // Hedef sürücüde kullanılabilir boş alan (bayt); bulunamazsa -1
        public static long FreeSpace(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return -1;
                return new DriveInfo(root).AvailableFreeSpace;
            }
            catch { return -1; }
        }
    }
}
