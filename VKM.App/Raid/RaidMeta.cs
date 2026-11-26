using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VKM.App
{
    // ───────────── disk/bölüm üzerindeki RAID ve dosya sistemi imzalarını tanır ─────────────

    public class MdInfo
    {
        public string Uuid = "";
        public string Name = "";
        public int Version;            // 0 = 0.90, 1 = 1.x
        public int Level;              // 0,1,4,5,6,10,-1(linear)
        public int Layout;
        public long Chunk;             // bayt
        public int RaidDisks;
        public int Role = -1;          // -1: yedek / arızalı
        public long DataOffset;        // bayt
        public long DataSize;          // bayt (üye başına kullanılan)
        public ulong Events;
        public long SbOffset;          // süper bloğun yeri (bayt)
        public int DevNumber = -1;
        public string DevUuid = "";
        public DateTime? Created, Updated;
        public int[] Roles = Array.Empty<int>();     // v1: dev_roles (cihaz no → rol), v0.90: disk tablosu rolleri
        public string Variant => Version == 0 ? "0.90" : SbOffset == 0 ? "1.1" : SbOffset == 4096 ? "1.2" : "1.0";
        public string LayoutName => Level switch
        {
            5 or 6 => Layout switch { 0 => "sol-asimetrik", 1 => "sağ-asimetrik", 2 => "sol-simetrik", 3 => "sağ-simetrik", 4 => "parite ilk", 5 => "parite son", 8 => "DDF zero-restart", 9 => "DDF N-restart", 10 => "DDF N-continue", _ => "kod " + Layout },
            10 => ((Layout >> 8) & 0xFF) > 1 ? ((Layout & 0x10000) != 0 ? "offset" : "far") + " " + ((Layout >> 8) & 0xFF) + " kopya" : "near " + (Layout & 0xFF) + " kopya",
            _ => "—"
        };
    }

    public class DdfVd
    {
        public string Guid = "";
        public string Name = "";
        public int Prl, Rlq, Srl, SecCount = 1, SecSeq, PrimaryCount, RotateParity;
        public long StripBytes, BlockCount, VdSize;
        public uint[] PdSeq = Array.Empty<uint>();
        public long[] StartLba = Array.Empty<long>();
    }

    public class DdfInfo
    {
        public string HeaderGuid = "";
        public uint MyRef;
        public string Vendor = "";
        public List<DdfVd> Configs = new();
    }

    public class ImsmVol
    {
        public string Name = "";
        public int Level, Members, StripSectors;
        public long Pba, BlocksPerMember, Size;
        public int[] Ord = Array.Empty<int>();
    }

    public class ImsmInfo
    {
        public uint Family;
        public List<string> Serials = new();
        public List<ImsmVol> Vols = new();
    }

    public class LvmPv
    {
        public string PvUuid = "";
        public string Text = "";
        public long Seq;
        public string VgName = "";
        public string VgId = "";
    }

    public class Probe
    {
        public string Label = "";      // ekranda: "NTFS bölüm", "RAID 5 üyesi"…
        public string Fs = "";         // NTFS / ext / XFS / Btrfs / … (dosya sistemi varsa)
        public string VolName = "";
        public MdInfo? Md;
        public LvmPv? Lvm;
        public DdfInfo? Ddf;
        public ImsmInfo? Imsm;
        public bool HasPartTable;
        public bool IsContainer => Md != null || Lvm != null || Ddf != null || Imsm != null;
    }

    public static class RaidMeta
    {
        public const uint MdMagic = 0xa92b4efc;

        public static Probe Identify(Stream s, long len, bool wholeDisk)
        {
            var p = new Probe();
            try
            {
                // 1) Donanım/anakart RAID üst verisi (diskin sonunda) — sadece tüm disk
                if (wholeDisk)
                {
                    p.Ddf = ReadDdf(s, len);
                    if (p.Ddf != null) { p.Label = "DDF RAID üyesi (" + (p.Ddf.Vendor.Length > 0 ? p.Ddf.Vendor : "LSI/Dell/Adaptec") + ")"; return p; }
                    p.Imsm = ReadImsm(s, len);
                    if (p.Imsm != null) { p.Label = "Intel RST (anakart RAID) üyesi"; return p; }
                }
                // 2) Linux md (Synology, QNAP, WD, Netgear, Asustor, Buffalo…)
                p.Md = ReadMd(s, len);
                if (p.Md != null)
                {
                    p.Label = MdLevelName(p.Md) + (p.Md.Role < 0 ? " yedek/arızalı üye" : " üyesi") + " (md " + p.Md.Variant + ")";
                    return p;
                }
                // 3) LVM fiziksel birim
                p.Lvm = ReadLvm(s, len);
                if (p.Lvm != null) { p.Label = "LVM fiziksel birim" + (p.Lvm.VgName.Length > 0 ? " (" + p.Lvm.VgName + ")" : ""); return p; }
                // 4) Dosya sistemleri
                IdentifyFs(s, len, p);
            }
            catch { }
            if (p.Label.Length == 0) p.Label = "tanınmadı (RAID üyesi / şifreli / boş olabilir)";
            return p;
        }

        public static string MdLevelName(MdInfo m) => m.Level switch
        {
            0 => "RAID 0",
            1 => "Ayna (RAID 1)",
            4 => "RAID 4",
            5 => "RAID 5",
            6 => "RAID 6",
            10 => "RAID 10",
            -1 => "JBOD/Linear",
            _ => "RAID " + m.Level
        };

        public static void IdentifyFs(Stream s, long len, Probe p)
        {
            byte[] b0 = Fmt.ReadAt(s, 0, 4096);
            string oem = Encoding.ASCII.GetString(b0, 3, 8);
            bool sig55 = b0[510] == 0x55 && b0[511] == 0xAA;
            if (oem == "NTFS    ") { p.Fs = "NTFS"; p.Label = "NTFS bölüm"; return; }
            if (oem == "EXFAT   ") { p.Fs = "exFAT"; p.Label = "exFAT bölüm"; return; }
            if (oem.StartsWith("-FVE-FS-")) { p.Fs = "BitLocker"; p.Label = "BitLocker şifreli bölüm"; return; }
            if (b0[0] == 0 && Encoding.ASCII.GetString(b0, 3, 4) == "ReFS") { p.Fs = "ReFS"; p.Label = "ReFS bölüm"; return; }
            if (sig55 && Encoding.ASCII.GetString(b0, 0x52, 5) == "FAT32") { p.Fs = "FAT"; p.Label = "FAT32 bölüm"; return; }
            if (sig55 && Encoding.ASCII.GetString(b0, 0x36, 3) == "FAT") { p.Fs = "FAT"; p.Label = "FAT bölüm"; return; }
            if (Encoding.ASCII.GetString(b0, 0, 4) == "XFSB") { p.Fs = "XFS"; p.Label = "XFS bölüm"; p.VolName = Fmt.Ascii(b0, 0x6C, 12); return; }
            if (b0[0] == 'L' && b0[1] == 'U' && b0[2] == 'K' && b0[3] == 'S' && b0[4] == 0xBA) { p.Fs = "LUKS"; p.Label = "LUKS şifreli bölüm"; return; }
            if (Encoding.ASCII.GetString(b0, 32, 4) == "NXSB") { p.Fs = "APFS"; p.Label = "APFS kapsayıcı"; return; }
            if (Encoding.ASCII.GetString(b0, 4086, 10) is "SWAPSPACE2" or "SWAP-SPACE") { p.Fs = "swap"; p.Label = "SWAP (takas) bölüm"; return; }
            if (Fmt.U16(b0, 1024 + 0x38) == 0xEF53) { p.Fs = "ext"; p.Label = "Ext2/3/4 bölüm"; p.VolName = Fmt.Ascii(b0, 1024 + 0x78, 16); return; }
            if (b0[1024] == 'H' && (b0[1025] == '+' || b0[1025] == 'X')) { p.Fs = "HFS+"; p.Label = "HFS+ bölüm"; return; }
            if (Fmt.U32(b0, 1024) == 0xF2F52010) { p.Fs = "F2FS"; p.Label = "F2FS (Flash-Friendly) bölüm"; return; }
            if (Encoding.ASCII.GetString(b0, 0, 7) == "DHFS4.1") { p.Fs = "DHFS"; p.Label = "Dahua DHFS (CCTV/NVR)"; return; }
            if (Encoding.ASCII.GetString(b0, 0x210, 18) == "HIKVISION@HANGZHOU") { p.Fs = "HIKVISION"; p.Label = "Hikvision (CCTV/NVR)"; return; }
            if (len > 0x10000 + 0x600)
            {
                byte[] b64 = Fmt.ReadAt(s, 0x10000, 2048);
                if (Encoding.ASCII.GetString(b64, 0x34, 6) == "ReIsEr") { p.Fs = "ReiserFS"; p.Label = "ReiserFS bölüm"; return; }
                if (Fmt.U32(b64, 0x55C) == 0x19540119) { p.Fs = "UFS2"; p.Label = "UFS2 (FreeBSD) bölüm"; return; }
                byte[] b32 = Fmt.ReadAt(s, 0x8000, 2048);
                if (Encoding.ASCII.GetString(b32, 0, 4) == "JFS1") { p.Fs = "JFS"; p.Label = "JFS bölüm"; return; }
                byte[] b8 = Fmt.ReadAt(s, 0x2000, 2048);
                if (Fmt.U32(b8, 0x55C) == 0x011954) { p.Fs = "UFS"; p.Label = "UFS (BSD) bölüm"; return; }
            }
            if (len > 0x10000 + 0x48)
            {
                byte[] bt = Fmt.ReadAt(s, 0x10000, 4096);
                if (Encoding.ASCII.GetString(bt, 0x40, 8) == "_BHRfS_M") { p.Fs = "Btrfs"; p.Label = "Btrfs bölüm"; p.VolName = Fmt.Ascii(bt, 0x12B, 32); return; }
            }
            if (len > 0x200010)
            {
                byte[] bv = Fmt.ReadAt(s, 0x100000, 512);
                if (Fmt.U32(bv, 0) == 0xC001D00D) { p.Fs = "VMFS"; p.Label = "VMFS (VMware) bölüm"; return; }
                byte[] bz = Fmt.ReadAt(s, 0x20000, 1024);
                if (Fmt.U64(bz, 0) == 0x00bab10c || Fmt.B64(bz, 0) == 0x00bab10c) { p.Fs = "ZFS"; p.Label = "ZFS havuz üyesi"; return; }
            }
            // bölüm tablosu (sanal disk içinde) — NTFS/FAT değilse
            if (sig55 || Encoding.ASCII.GetString(Fmt.ReadAt(s, 512, 8)) == "EFI PART")
            {
                p.HasPartTable = true; p.Label = "bölüm tablosu (MBR/GPT)"; return;
            }
            if (b0.All(x => x == 0)) p.Label = "boş (başı sıfır)";
        }

        // ---------------- Linux md ----------------
        public static MdInfo? ReadMd(Stream s, long len)
        {
            // 1.1 (başta), 1.2 (4 KB), 1.0 (sonda), 0.90 (sonda, 64 KB hizalı)
            foreach (long off in new long[] { 0, 4096, ((len >> 9) - 16 & ~7L) << 9 })
            {
                if (off < 0 || off + 4096 > len) continue;
                byte[] b = Fmt.ReadAt(s, off, 4096);
                if (Fmt.U32(b, 0) == MdMagic && Fmt.U32(b, 4) == 1) { var r = ParseMd1(b); r.SbOffset = off; return r; }
            }
            long o90 = (len & ~65535L) - 65536;
            if (o90 > 0)
            {
                byte[] b = Fmt.ReadAt(s, o90, 4096);
                if (Fmt.U32(b, 0) == MdMagic) { var r = ParseMd090(b, false); r.SbOffset = o90; return r; }
                if (Fmt.B32(b, 0) == MdMagic) { var r = ParseMd090(b, true); r.SbOffset = o90; return r; }
            }
            return null;
        }

        private static MdInfo ParseMd1(byte[] b)
        {
            var m = new MdInfo { Version = 1 };
            m.Uuid = Fmt.Hex(b, 16, 16);
            m.Name = Fmt.Ascii(b, 32, 32);
            m.Level = (int)Fmt.U32(b, 72);
            m.Layout = (int)Fmt.U32(b, 76);
            long size = (long)Fmt.U64(b, 80);
            m.Chunk = Fmt.U32(b, 88) * 512L;
            m.RaidDisks = (int)Fmt.U32(b, 92);
            m.DataOffset = (long)Fmt.U64(b, 128) * 512;
            long dataSize = (long)Fmt.U64(b, 136);
            int devNum = (int)Fmt.U32(b, 160);
            m.Events = Fmt.U64(b, 200);
            int maxDev = (int)Fmt.U32(b, 220);
            m.DataSize = (size > 0 && m.Level is not (0 or -1) ? size : dataSize) * 512;
            if (devNum >= 0 && devNum < Math.Min(maxDev, 1900))
            {
                int role = Fmt.U16(b, 256 + devNum * 2);
                m.Role = role >= 0xFF00 ? -1 : role;
            }
            m.DevNumber = devNum;
            m.DevUuid = Fmt.Hex(b, 168, 16);
            m.Created = Unix((long)(Fmt.U64(b, 64) & 0xFFFFFFFFFF));
            m.Updated = Unix((long)(Fmt.U64(b, 192) & 0xFFFFFFFFFF));
            int md = Math.Min(maxDev, 384);
            m.Roles = new int[Math.Max(0, md)];
            for (int i = 0; i < md; i++) { int r = Fmt.U16(b, 256 + i * 2); m.Roles[i] = r >= 0xFF00 ? (r == 0xFFFE ? -2 : -1) : r; }
            return m;
        }

        private static MdInfo ParseMd090(byte[] b, bool be)
        {
            uint W(int i) => be ? Fmt.B32(b, i * 4) : Fmt.U32(b, i * 4);
            var m = new MdInfo { Version = 0 };
            m.Uuid = W(5).ToString("x8") + W(13).ToString("x8") + W(14).ToString("x8") + W(15).ToString("x8");
            m.Level = (int)W(7);
            m.DataSize = W(8) * 1024L;
            m.RaidDisks = (int)W(10);
            m.Events = (ulong)W(39) << 32 | W(40);
            m.Layout = (int)W(64);
            m.Chunk = W(65);
            uint state = W(996);
            m.Role = (state & 1) != 0 ? -1 : (int)W(995);
            m.DevNumber = (int)W(992);
            m.DataOffset = 0;
            m.Created = Unix(W(6));
            m.Updated = Unix(W(32));
            m.Name = "md (0.90)";
            m.Roles = new int[27];
            for (int i = 0; i < 27; i++) { uint st = W(128 + i * 32 + 4); m.Roles[i] = (st & 1) != 0 ? -2 : (int)W(128 + i * 32 + 3); if (W(128 + i * 32 + 1) == 0 && W(128 + i * 32 + 2) == 0 && W(128 + i * 32 + 3) == 0 && st == 0 && i > 0) m.Roles[i] = -1; }
            return m;
        }

        static DateTime? Unix(long sec) => sec > 946684800 && sec < 4102444800 ? DateTimeOffset.FromUnixTimeSeconds(sec).LocalDateTime : null;

        // ---------------- DDF (SNIA) — LSI MegaRAID, Dell PERC, IBM ServeRAID M, bazı Adaptec ----------------
        public static DdfInfo? ReadDdf(Stream s, long len)
        {
            if (len < 1 << 20) return null;
            byte[] a = Fmt.ReadAt(s, len - 512, 512);
            if (Fmt.B32(a, 0) != 0xDE11DE11) return null;
            long prim = (long)Fmt.B64(a, 0x60);
            byte[] h = prim > 0 && prim * 512 < len ? Fmt.ReadAt(s, prim * 512, 512) : a;
            if (Fmt.B32(h, 0) != 0xDE11DE11) { h = a; prim = len / 512 - 1; }
            var d = new DdfInfo { HeaderGuid = Fmt.Hex(h, 8, 24) };
            d.Vendor = Fmt.Ascii(h, 8, 8);
            long baseLba = prim;
            int recLen = Fmt.B16(h, 0x86);
            int maxPe = Fmt.B16(h, 0x88);
            long Sec(int o) => Fmt.B32(h, o);

            // bu diskin referansı
            byte[] pd = Fmt.ReadAt(s, (baseLba + Sec(0xE0)) * 512, 512);
            if (Fmt.B32(pd, 0) == 0x33333333) d.MyRef = Fmt.B32(pd, 0x20);

            // sanal disk adları
            var names = new Dictionary<string, string>();
            long vdSec = Sec(0xD0), vdLen = Sec(0xD4);
            if (vdLen > 0 && vdLen < 4096)
            {
                byte[] vd = Fmt.ReadAt(s, (baseLba + vdSec) * 512, (int)vdLen * 512);
                if (Fmt.B32(vd, 0) == 0xDDDDDDDD)
                {
                    int pop = Fmt.B16(vd, 8);
                    for (int i = 0; i < pop && 0x40 + i * 64 + 64 <= vd.Length; i++)
                    {
                        int e = 0x40 + i * 64;
                        names[Fmt.Hex(vd, e, 24)] = Fmt.Ascii(vd, e + 0x30, 16);
                    }
                }
            }

            long cfSec = Sec(0xD8), cfLen = Sec(0xDC);
            if (recLen <= 0 || cfLen <= 0 || cfLen > 1 << 16) return d;
            if (maxPe <= 0) maxPe = (recLen * 512 - 512) / 12;
            int nrec = (int)(cfLen / recLen);
            for (int r = 0; r < nrec && r < 512; r++)
            {
                byte[] c = Fmt.ReadAt(s, (baseLba + cfSec + (long)r * recLen) * 512, recLen * 512);
                if (Fmt.B32(c, 0) != 0xEEEEEEEE) continue;
                var v = new DdfVd
                {
                    Guid = Fmt.Hex(c, 8, 24),
                    PrimaryCount = Fmt.B16(c, 0x40),
                    StripBytes = (1L << c[0x42]) * Math.Max(512, (int)Fmt.B16(c, 0x58)),
                    Prl = c[0x43], Rlq = c[0x44], SecCount = Math.Max(1, (int)c[0x45]), SecSeq = c[0x46], Srl = c[0x47],
                    BlockCount = (long)Fmt.B64(c, 0x48), VdSize = (long)Fmt.B64(c, 0x50), RotateParity = c[0x5A]
                };
                if (Fmt.B16(c, 0x58) == 0) v.StripBytes = (1L << c[0x42]) * 512;
                int n = Math.Min(v.PrimaryCount, maxPe);
                v.PdSeq = new uint[n]; v.StartLba = new long[n];
                for (int i = 0; i < n; i++)
                {
                    int po = 0x200 + i * 4, so = 0x200 + maxPe * 4 + i * 8;
                    if (so + 8 > c.Length) break;
                    v.PdSeq[i] = Fmt.B32(c, po);
                    v.StartLba[i] = (long)Fmt.B64(c, so);
                }
                names.TryGetValue(v.Guid, out var nm); v.Name = nm ?? "";
                d.Configs.Add(v);
            }
            return d;
        }

        // ---------------- Intel Matrix / RST (IMSM) ----------------
        public static ImsmInfo? ReadImsm(Stream s, long len)
        {
            if (len < 1 << 20) return null;
            byte[] a = Fmt.ReadAt(s, len - 1024, 512);
            if (Encoding.ASCII.GetString(a, 0, 24) != "Intel Raid ISM Cfg Sig. ") return null;
            int mpbSize = (int)Fmt.U32(a, 36);
            int secs = Math.Max(1, (mpbSize + 511) / 512);
            byte[] b = new byte[secs * 512];
            Buffer.BlockCopy(a, 0, b, 0, 512);
            if (secs > 1)
            {
                byte[] rest = Fmt.ReadAt(s, len - 512L * (2 + secs - 1), (secs - 1) * 512);
                Buffer.BlockCopy(rest, 0, b, 512, rest.Length);
            }
            var r = new ImsmInfo { Family = Fmt.U32(b, 40) };
            int nd = b[56], nv = b[57];
            for (int i = 0; i < nd; i++) r.Serials.Add(Fmt.Ascii(b, 216 + i * 48, 16));
            int o = 216 + nd * 48;
            for (int v = 0; v < nv && o + 88 + 52 <= b.Length; v++)
            {
                var vol = new ImsmVol { Name = Fmt.Ascii(b, o, 16) };
                vol.Size = (long)(Fmt.U32(b, o + 16) | (ulong)Fmt.U32(b, o + 20) << 32) * 512;
                byte migr = b[o + 64];
                int m = o + 88;
                vol.Pba = (long)(Fmt.U32(b, m) | (ulong)Fmt.U32(b, m + 20) << 32);
                vol.BlocksPerMember = (long)(Fmt.U32(b, m + 4) | (ulong)Fmt.U32(b, m + 24) << 32);
                vol.StripSectors = Fmt.U16(b, m + 12);
                vol.Level = b[m + 15];
                vol.Members = b[m + 16];
                vol.Ord = new int[vol.Members];
                for (int i = 0; i < vol.Members && m + 48 + i * 4 + 4 <= b.Length; i++) vol.Ord[i] = (int)(Fmt.U32(b, m + 48 + i * 4) & 0xFFFFFF);
                r.Vols.Add(vol);
                int mapSize = 52 + 4 * Math.Max(0, vol.Members - 1);
                o = m + mapSize * (migr != 0 ? 2 : 1);
            }
            return r;
        }

        // ---------------- LVM2 ----------------
        public static LvmPv? ReadLvm(Stream s, long len)
        {
            byte[] h = Fmt.ReadAt(s, 0, 2048);
            for (int sec = 0; sec < 4; sec++)
            {
                int lo = sec * 512;
                if (Encoding.ASCII.GetString(h, lo, 8) != "LABELONE") continue;
                if (Encoding.ASCII.GetString(h, lo + 24, 8) != "LVM2 001") continue;
                int pvo = lo + (int)Fmt.U32(h, lo + 20);
                if (pvo + 40 > h.Length) return null;
                var pv = new LvmPv { PvUuid = Encoding.ASCII.GetString(h, pvo, 32) };
                // veri alanları (0 ile biter), sonra üst veri alanları
                int q = pvo + 40;
                while (q + 16 <= h.Length && Fmt.U64(h, q) != 0) q += 16;
                q += 16;
                string best = ""; long bestSeq = -1;
                while (q + 16 <= h.Length && Fmt.U64(h, q) != 0)
                {
                    long mdaOff = (long)Fmt.U64(h, q), mdaSize = (long)Fmt.U64(h, q + 8);
                    q += 16;
                    string t = ReadLvmText(s, mdaOff, mdaSize);
                    long seq = LvmText.SeqOf(t);
                    if (t.Length > 0 && seq > bestSeq) { best = t; bestSeq = seq; }
                }
                pv.Text = best; pv.Seq = bestSeq;
                if (best.Length > 0)
                {
                    var root = LvmText.Parse(best);
                    foreach (var kv in root)
                        if (kv.Value is Dictionary<string, object> vg && vg.ContainsKey("seqno"))
                        { pv.VgName = kv.Key; pv.VgId = LvmText.Str(vg, "id"); break; }
                }
                return pv;
            }
            return null;
        }

        private static string ReadLvmText(Stream s, long mdaOff, long mdaSize)
        {
            byte[] mh = Fmt.ReadAt(s, mdaOff, 512);
            if (Encoding.ASCII.GetString(mh, 4, 16) != " LVM2 x[5A%r0N*>") return "";
            long off = (long)Fmt.U64(mh, 40), size = (long)Fmt.U64(mh, 48);
            if (size <= 0 || size > 16 << 20) return "";
            byte[] t;
            if (off + size <= mdaSize) t = Fmt.ReadAt(s, mdaOff + off, (int)size);
            else
            {   // dairesel alan: sona taşan kısım 512. bayttan devam eder
                int first = (int)(mdaSize - off);
                byte[] a = Fmt.ReadAt(s, mdaOff + off, first);
                byte[] b = Fmt.ReadAt(s, mdaOff + 512, (int)size - first);
                t = a.Concat(b).ToArray();
            }
            int z = Array.IndexOf(t, (byte)0); if (z >= 0) t = t.Take(z).ToArray();
            return Encoding.UTF8.GetString(t);
        }
    }

    // LVM üst veri metni: name { key = value … } biçimi
    public static class LvmText
    {
        public static long SeqOf(string t)
        {
            int i = t.IndexOf("seqno", StringComparison.Ordinal);
            if (i < 0) return -1;
            int e = t.IndexOf('=', i); if (e < 0) return -1;
            int k = e + 1; while (k < t.Length && t[k] == ' ') k++;
            int st = k; while (k < t.Length && char.IsDigit(t[k])) k++;
            return long.TryParse(t[st..k], out long v) ? v : -1;
        }

        public static Dictionary<string, object> Parse(string t)
        {
            int i = 0;
            return ParseBlock(t, ref i);
        }

        private static Dictionary<string, object> ParseBlock(string t, ref int i)
        {
            var d = new Dictionary<string, object>(StringComparer.Ordinal);
            while (true)
            {
                Skip(t, ref i);
                if (i >= t.Length) return d;
                if (t[i] == '}') { i++; return d; }
                string key = Ident(t, ref i);
                if (key.Length == 0) { i++; continue; }
                Skip(t, ref i);
                if (i < t.Length && t[i] == '{') { i++; d[key] = ParseBlock(t, ref i); }
                else if (i < t.Length && t[i] == '=') { i++; Skip(t, ref i); d[key] = Value(t, ref i); }
            }
        }

        private static object Value(string t, ref int i)
        {
            if (i >= t.Length) return "";
            if (t[i] == '[')
            {
                i++; var l = new List<object>();
                while (true)
                {
                    Skip(t, ref i);
                    if (i >= t.Length) return l;
                    if (t[i] == ']') { i++; return l; }
                    if (t[i] == ',') { i++; continue; }
                    l.Add(Value(t, ref i));
                }
            }
            if (t[i] == '"')
            {
                int e = t.IndexOf('"', i + 1); if (e < 0) e = t.Length;
                string v = t.Substring(i + 1, e - i - 1); i = e + 1; return v;
            }
            int st = i;
            while (i < t.Length && !char.IsWhiteSpace(t[i]) && t[i] != ',' && t[i] != ']' && t[i] != '}') i++;
            string raw = t[st..i];
            return long.TryParse(raw, out long n) ? (object)n : raw;
        }

        private static string Ident(string t, ref int i)
        {
            int st = i;
            while (i < t.Length && (char.IsLetterOrDigit(t[i]) || t[i] is '_' or '-' or '.' or '+')) i++;
            return t[st..i];
        }

        private static void Skip(string t, ref int i)
        {
            while (i < t.Length)
            {
                if (char.IsWhiteSpace(t[i])) { i++; continue; }
                if (t[i] == '#') { while (i < t.Length && t[i] != '\n') i++; continue; }
                break;
            }
        }

        public static string Str(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v?.ToString() ?? "" : "";
        public static long Num(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is long l ? l : 0;
        public static Dictionary<string, object>? Sub(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
    }

    // LVM mantıksal birimi → sanal disk (linear ve striped parçalar)
    public sealed class LvmStream : Stream
    {
        public class Seg { public long Start, Count; public long StripeSize; public List<(int pv, long ext)> Stripes = new(); }
        private readonly List<Seg> _segs;
        private readonly long _ext;               // bayt
        private readonly long[] _peStart;          // bayt
        private readonly Stream?[] _pv;
        private readonly long _len;
        private long _pos;

        public LvmStream(List<Seg> segs, long extentBytes, long[] peStart, Func<Stream>?[] open)
        {
            _segs = segs; _ext = extentBytes; _peStart = peStart;
            _pv = open.Select(o => { try { return o?.Invoke(); } catch { return null; } }).ToArray();
            _len = segs.Count == 0 ? 0 : segs.Max(s => (s.Start + s.Count) * extentBytes);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin so) { _pos = so == SeekOrigin.Begin ? o : so == SeekOrigin.Current ? _pos + o : _len + o; return _pos; }
        protected override void Dispose(bool disposing) { if (disposing) foreach (var s in _pv) try { s?.Dispose(); } catch { } base.Dispose(disposing); }

        public override int Read(byte[] buf, int off, int count)
        {
            if (_pos >= _len) return 0;
            count = (int)Math.Min(count, _len - _pos);
            int done = 0;
            while (done < count)
            {
                long p = _pos + done;
                long ext = p / _ext;
                var seg = _segs.FirstOrDefault(s => ext >= s.Start && ext < s.Start + s.Count);
                int n;
                if (seg == null || seg.Stripes.Count == 0)
                {
                    n = (int)Math.Min(count - done, _ext - p % _ext);
                    Array.Clear(buf, off + done, n);
                }
                else
                {
                    long inSeg = p - seg.Start * _ext;
                    int pvi; long pvOff;
                    if (seg.Stripes.Count == 1)
                    {
                        pvi = seg.Stripes[0].pv; pvOff = seg.Stripes[0].ext * _ext + inSeg;
                        n = (int)Math.Min(count - done, seg.Count * _ext - inSeg);
                    }
                    else
                    {
                        long ss = Math.Max(512, seg.StripeSize);
                        long blk = inSeg / ss; int k = seg.Stripes.Count;
                        int which = (int)(blk % k); long row = blk / k;
                        pvi = seg.Stripes[which].pv; pvOff = seg.Stripes[which].ext * _ext + row * ss + inSeg % ss;
                        n = (int)Math.Min(count - done, ss - inSeg % ss);
                    }
                    n = Math.Min(n, 1 << 22);
                    var st = pvi >= 0 && pvi < _pv.Length ? _pv[pvi] : null;
                    if (st == null) Array.Clear(buf, off + done, n);
                    else
                    {
                        try
                        {
                            st.Position = _peStart[pvi] + pvOff;
                            int got = 0;
                            while (got < n) { int r = st.Read(buf, off + done + got, n - got); if (r <= 0) break; got += r; }
                            if (got < n) Array.Clear(buf, off + done + got, n - got);
                        }
                        catch { Array.Clear(buf, off + done, n); }
                    }
                }
                if (n <= 0) break;
                done += n;
            }
            _pos += done;
            return done;
        }
    }
}
