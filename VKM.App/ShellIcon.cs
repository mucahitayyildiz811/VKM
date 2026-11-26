using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VKM.App
{
    // Windows'un kayıtlı dosya-tipi ikonlarını çeker (Office/PDF vb. gerçek ikonlar).
    // Disk'e dokunmaz: uzantıya göre sistemden ikon alır. Uzantı başına önbellek.
    public static class ShellIcon
    {
        private static readonly object _lock = new();
        private static readonly Dictionary<string, ImageSource?> _cache = new();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

        public static ImageSource? Get(bool isDir, string name)
        {
            string key = isDir ? "<dir>" : Ext(name);
            lock (_lock)
            {
                if (_cache.TryGetValue(key, out ImageSource? cached)) return cached;

                ImageSource? img = null;
                try
                {
                    var shfi = new SHFILEINFO();
                    uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
                    string path = isDir ? "folder" : ("file" + key); // key uzantıyı (nokta dâhil) içerir
                    IntPtr r = SHGetFileInfo(path, attr, ref shfi, (uint)Marshal.SizeOf<SHFILEINFO>(),
                        SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

                    if (shfi.hIcon != IntPtr.Zero)
                    {
                        img = Imaging.CreateBitmapSourceFromHIcon(shfi.hIcon, Int32Rect.Empty,
                            BitmapSizeOptions.FromEmptyOptions());
                        img.Freeze();
                        DestroyIcon(shfi.hIcon);
                    }
                }
                catch { img = null; }

                _cache[key] = img;
                return img;
            }
        }

        private static string Ext(string name)
        {
            int dot = name.LastIndexOf('.');
            return dot >= 0 && dot < name.Length - 1 ? name.Substring(dot).ToLowerInvariant() : ".dat";
        }
    }
}
