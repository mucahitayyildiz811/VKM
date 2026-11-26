using System;
using System.IO;
using System.Text;
using DiscUtils;
using DiscUtils.Btrfs;
using DiscUtils.Ext;
using DiscUtils.ExFat;
using DiscUtils.Fat;
using DiscUtils.HfsPlus;
using DiscUtils.Ntfs;
using DiscUtils.Xfs;

namespace VKM.App
{
    // Bir birimi sırayla tüm dosya sistemleriyle açmayı dener. Hem ana ekran
    // hem kurtarma penceresi bunu kullanır.
    public static class VolumeMount
    {
        public static DiscFileSystem? Open(Stream s, out string name, out bool ntfs, out string err)
        {
            name = ""; ntfs = false;
            var sb = new StringBuilder();
            DiscFileSystem? r;

            if ((r = Try(() => new NtfsFileSystem(s), s, "NTFS", sb)) != null) { name = "NTFS"; ntfs = true; err = ""; return r; }
            if ((r = Try(() => new ExFatFileSystem(s), s, "exFAT", sb)) != null) { name = "exFAT"; err = ""; return r; }
            if ((r = Try(() => new FatFileSystem(s), s, "FAT", sb)) != null) { name = "FAT"; err = ""; return r; }
            if ((r = Try(() => new ExtFileSystem(s), s, "ext", sb)) != null) { name = "ext"; err = ""; return r; }
            if ((r = Try(() => new HfsPlusFileSystem(s), s, "HFS+", sb)) != null) { name = "HFS+"; err = ""; return r; }
            if ((r = Try(() => new XfsFileSystem(s), s, "XFS", sb)) != null) { name = "XFS"; err = ""; return r; }
            if ((r = Try(() => new BtrfsFileSystem(s), s, "Btrfs", sb)) != null) { name = "Btrfs"; err = ""; return r; }

            err = sb.ToString();
            return null;
        }

        private static DiscFileSystem? Try(Func<DiscFileSystem> make, Stream s, string label, StringBuilder sb)
        {
            try { s.Position = 0; return make(); }
            catch (Exception e)
            {
                string m = e.Message.Replace("\r", " ").Replace("\n", " ");
                if (m.Length > 90) m = m.Substring(0, 90);
                sb.Append(label + ":" + m + "  ");
                return null;
            }
        }
    }
}
