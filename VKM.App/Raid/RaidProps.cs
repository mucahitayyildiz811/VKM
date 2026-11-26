using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace VKM.App
{
    // Sağ tık → Özellikler / Eski RAID yapısı / Eski diski bul
    public static class RaidProps
    {
        // ---------------- disk özellikleri ----------------
        public static string Disk(RaidMember m, DiskInfo? d)
        {
            var sb = new StringBuilder();
            sb.AppendLine("DİSK ÖZELLİKLERİ");
            sb.AppendLine("────────────────");
            sb.AppendLine("Ad      : " + m.Name);
            if (m.Model.Length > 0) sb.AppendLine("Model   : " + m.Model);
            if (m.Serial.Length > 0) sb.AppendLine("Seri    : " + m.Serial);
            if (d?.Interface.Length > 0) sb.AppendLine("Arayüz  : " + d.Interface);
            sb.AppendLine($"Boyut   : {Fmt.Size(m.Length)}  ({m.Length:N0} bayt · {m.Length / 512:N0} sektör)");
            sb.AppendLine("Sektör  : " + m.Sector + " B");
            if (m.DeviceId.Length > 0) sb.AppendLine("Aygıt   : " + m.DeviceId);
            if (m.ImagePath.Length > 0) sb.AppendLine("İmaj    : " + m.ImagePath);
            if (d != null)
            {
                sb.AppendLine();
                sb.AppendLine("SAĞLIK (SMART)");
                sb.AppendLine("Durum   : " + (d.Health.Length > 0 ? d.Health : "bilinmiyor") + (d.HealthPercent >= 0 ? "  · %" + d.HealthPercent : ""));
                if (d.SmartLine.Length > 0) sb.AppendLine("Değerler: " + d.SmartLine);
                if (d.IsRisky) sb.AppendLine("⚠ RİSKLİ: " + d.RiskDetail + " → önce imaj alın.");
            }
            sb.AppendLine();
            try { using var s = m.Open(); sb.Append(PartTable(s, m.Length, m.Sector)); }
            catch (Exception ex) { sb.AppendLine("Okunamadı: " + ex.Message); }
            return sb.ToString();
        }

        public static string PartTable(Stream s, long len, int ss)
        {
            var sb = new StringBuilder();
            byte[] mbr = Fmt.ReadAt(s, 0, 512);
            byte[] gh = Fmt.ReadAt(s, ss, 512);
            bool gpt = Encoding.ASCII.GetString(gh, 0, 8) == "EFI PART";
            sb.AppendLine("BÖLÜM TABLOSU: " + (gpt ? "GPT" : mbr[510] == 0x55 && mbr[511] == 0xAA ? "MBR" : "yok / tanınmadı"));
            if (gpt)
            {
                long ent = (long)Fmt.U64(gh, 0x48); int cnt = (int)Math.Min(Fmt.U32(gh, 0x50), 256u); int esz = (int)Fmt.U32(gh, 0x54);
                sb.AppendLine($"Disk GUID: {new Guid(gh.AsSpan(0x38, 16))} · yedek başlık LBA {Fmt.U64(gh, 0x20):N0}");
                if (esz >= 128 && esz <= 1024)
                {
                    byte[] e = Fmt.ReadAt(s, ent * ss, cnt * esz);
                    int k = 0;
                    for (int i = 0; i < cnt; i++)
                    {
                        int o = i * esz;
                        if (e.Skip(o).Take(16).All(x => x == 0)) continue;
                        long first = (long)Fmt.U64(e, o + 0x20), last = (long)Fmt.U64(e, o + 0x28);
                        string name = Encoding.Unicode.GetString(e, o + 0x38, 72).TrimEnd('\0');
                        var type = new Guid(e.AsSpan(o, 16));
                        sb.AppendLine($"  {++k,2}. {GptName(type),-26} başl. {first,14:N0}  son {last,14:N0}  {Fmt.Size((last - first + 1) * ss),9}  {name}");
                    }
                }
            }
            else if (mbr[510] == 0x55 && mbr[511] == 0xAA)
            {
                sb.AppendLine($"Disk imzası: {Fmt.U32(mbr, 0x1B8):X8}");
                for (int i = 0; i < 4; i++)
                {
                    int o = 0x1BE + i * 16; byte t = mbr[o + 4];
                    if (t == 0) continue;
                    long st = Fmt.U32(mbr, o + 8), c = Fmt.U32(mbr, o + 12);
                    bool over = (st + c) * ss > len;
                    sb.AppendLine($"  {i + 1}. 0x{t:X2} {MbrName(t),-22} {(mbr[o] == 0x80 ? "aktif" : "     ")} başl. {st,12:N0}  {Fmt.Size(c * ss),9}" + (over ? "  ← DİSKTEN BÜYÜK (RAID'in 1. diski)" : ""));
                }
            }
            return sb.ToString();
        }

        static string GptName(Guid g) => g.ToString().ToUpperInvariant() switch
        {
            "EBD0A0A2-B9E5-4433-87C0-68B6B72699C7" => "Temel veri (NTFS/FAT)",
            "E3C9E316-0B5C-4DB8-817D-F92DF00215AE" => "Microsoft ayrılmış (MSR)",
            "C12A7328-F81F-11D2-BA4B-00A0C93EC93B" => "EFI sistem",
            "DE94BBA4-06D1-4D40-A16A-BFD50179D6AC" => "Windows kurtarma",
            "0FC63DAF-8483-4772-8E79-3D69D8477DE4" => "Linux dosya sistemi",
            "A19D880F-05FC-4D3B-A006-743F0F84911E" => "Linux RAID (md)",
            "E6D6D379-F507-44C2-A23C-238F2A3DF928" => "Linux LVM",
            "0657FD6D-A4AB-43C4-84E5-0933C84B4F4F" => "Linux swap",
            "5808C8AA-7E8F-42E0-85D2-E1E90434CFB3" => "Windows dinamik (LDM üst veri)",
            "AF9B60A0-1431-4F62-BC68-3311714A69AD" => "Windows dinamik (LDM veri)",
            "E75CAF8F-F680-4CEE-AFA3-B001E56EFC2D" => "Storage Spaces",
            "AA31E02A-400F-11DB-9590-000C2911D1B8" => "VMware VMFS",
            "6A898CC3-1DD2-11B2-99A6-080020736631" => "ZFS",
            "516E7CBA-6ECF-11D6-8FF8-00022D09712B" => "FreeBSD ZFS",
            "48465300-0000-11AA-AA11-00306543ECAC" => "Apple HFS+",
            "7C3457EF-0000-11AA-AA11-00306543ECAC" => "Apple APFS",
            _ => g.ToString()[..8] + "…"
        };

        static string MbrName(byte t) => t switch
        {
            0x07 => "NTFS / exFAT", 0x0B or 0x0C => "FAT32", 0x06 or 0x0E or 0x04 => "FAT16", 0x01 => "FAT12",
            0x05 or 0x0F or 0x85 => "Genişletilmiş", 0x82 => "Linux swap", 0x83 => "Linux", 0x8E => "Linux LVM",
            0xFD => "Linux RAID (md)", 0x42 => "Windows dinamik (LDM)", 0xEE => "GPT koruyucu", 0xA5 => "FreeBSD",
            0xAF => "Apple HFS", 0xFB => "VMware VMFS", 0x27 => "Kurtarma", 0x17 => "Gizli NTFS", 0x1C => "Gizli FAT32",
            0xDE => "Dell yardımcı", 0x12 => "OEM/kurtarma", _ => "?"
        };

        // ---------------- bölüm / birim özellikleri ----------------
        public static string Volume(RaidNode n)
        {
            var sb = new StringBuilder();
            sb.AppendLine("BÖLÜM / BİRİM ÖZELLİKLERİ");
            sb.AppendLine("─────────────────────────");
            sb.AppendLine("Ad      : " + n.Name);
            sb.AppendLine($"Başlangıç: sektör {n.Start / 512:N0}  ({Fmt.Size(n.Start)})");
            sb.AppendLine($"Boyut   : {Fmt.Size(n.Length)}  ({n.Length / 512:N0} sektör)");
            if (n.Open == null) return sb.ToString();
            try
            {
                using var s = n.Open();
                byte[] b = Fmt.ReadAt(s, 0, 4096);
                string oem = Encoding.ASCII.GetString(b, 3, 8);
                if (oem == "NTFS    ")
                {
                    int bps = Fmt.U16(b, 0x0B); int spc = b[0x0D]; if (spc > 0x80) spc = 1 << (256 - spc);
                    sbyte cpr = (sbyte)b[0x40]; long rec = cpr < 0 ? 1L << -cpr : (long)cpr * spc * bps;
                    sb.AppendLine("Dosya sistemi: NTFS");
                    sb.AppendLine($"Küme (cluster): {Fmt.Size((long)bps * spc)} · sektör {bps} B");
                    sb.AppendLine($"Toplam sektör: {Fmt.U64(b, 0x28):N0}");
                    sb.AppendLine($"$MFT: küme {Fmt.U64(b, 0x30):N0} · $MFTMirr: küme {Fmt.U64(b, 0x38):N0} · kayıt {rec} B");
                    sb.AppendLine($"Birim seri no: {Fmt.U64(b, 0x48):X16}");
                    sb.AppendLine($"Gizli sektör (bölüm başı): {Fmt.U32(b, 0x1C):N0}");
                }
                else if (oem == "EXFAT   ")
                {
                    sb.AppendLine("Dosya sistemi: exFAT");
                    sb.AppendLine($"Küme: {Fmt.Size(1L << (b[0x6C] + b[0x6D]))} · seri {Fmt.U32(b, 0x64):X8}");
                }
                else if (Encoding.ASCII.GetString(b, 0x52, 5) == "FAT32")
                    sb.AppendLine($"Dosya sistemi: FAT32 · etiket '{Fmt.Ascii(b, 0x47, 11)}' · küme {Fmt.Size(Fmt.U16(b, 0x0B) * (long)b[0x0D])}");
                else if (Fmt.U16(b, 1024 + 0x38) == 0xEF53)
                {
                    int o = 1024; long bs = 1024L << (int)Fmt.U32(b, o + 0x18);
                    ulong blocks = Fmt.U32(b, o + 4) | ((Fmt.U32(b, o + 0x60) & 0x80) != 0 ? (ulong)Fmt.U32(b, o + 0x150) << 32 : 0);
                    uint inc = Fmt.U32(b, o + 0x60);
                    string ver = (inc & 0x40) != 0 || (inc & 0x80) != 0 ? "ext4" : (Fmt.U32(b, o + 0x5C) & 4) != 0 ? "ext3" : "ext2";
                    sb.AppendLine("Dosya sistemi: " + ver);
                    sb.AppendLine($"Etiket: '{Fmt.Ascii(b, o + 0x78, 16)}' · UUID {Fmt.Hex(b, o + 0x68, 16)}");
                    sb.AppendLine($"Blok: {bs} B · blok sayısı {blocks:N0} ({Fmt.Size((long)blocks * bs)}) · inode {Fmt.U32(b, o):N0}");
                    sb.AppendLine($"Son bağlandığı yer: {Fmt.Ascii(b, o + 0x88, 64)}");
                    sb.AppendLine($"Son bağlanma: {Unix(Fmt.U32(b, o + 0x2C))} · son yazma: {Unix(Fmt.U32(b, o + 0x30))}");
                    sb.AppendLine("Durum: " + ((Fmt.U16(b, o + 0x3A) & 1) != 0 ? "temiz kapatılmış" : "temiz kapatılmamış (journal kontrolü önerilir)"));
                }
                else if (Encoding.ASCII.GetString(b, 0, 4) == "XFSB")
                {
                    sb.AppendLine("Dosya sistemi: XFS");
                    sb.AppendLine($"Etiket: '{Fmt.Ascii(b, 0x6C, 12)}' · UUID {Fmt.Hex(b, 0x20, 16)}");
                    sb.AppendLine($"Blok: {Fmt.B32(b, 4)} B · blok sayısı {Fmt.B64(b, 8):N0} · AG {Fmt.B32(b, 0x58)} × {Fmt.B32(b, 0x54):N0} blok");
                }
                else
                {
                    byte[] bt = Fmt.ReadAt(s, 0x10000, 4096);
                    if (Encoding.ASCII.GetString(bt, 0x40, 8) == "_BHRfS_M")
                    {
                        sb.AppendLine("Dosya sistemi: Btrfs");
                        sb.AppendLine($"Etiket: '{Fmt.Ascii(bt, 0x12B, 64)}' · fsid {Fmt.Hex(bt, 0x20, 16)}");
                        sb.AppendLine($"Toplam: {Fmt.Size((long)Fmt.U64(bt, 0x70))} · kullanılan {Fmt.Size((long)Fmt.U64(bt, 0x78))} · aygıt sayısı {Fmt.U64(bt, 0x88)}");
                        sb.AppendLine($"Sektör {Fmt.U32(bt, 0x90)} · düğüm {Fmt.U32(bt, 0x94)} · nesil {Fmt.U64(bt, 0x48):N0}");
                    }
                    else
                    {
                        var p = new Probe(); RaidMeta.IdentifyFs(s, n.Length, p);
                        sb.AppendLine("İçerik: " + p.Label);
                        if (p.HasPartTable) { sb.AppendLine(); sb.Append(PartTable(s, n.Length, 512)); }
                    }
                }
            }
            catch (Exception ex) { sb.AppendLine("Okunamadı: " + ex.Message); }
            return sb.ToString();
        }

        static string Unix(uint t) => t == 0 ? "—" : DateTimeOffset.FromUnixTimeSeconds(t).LocalDateTime.ToString("dd.MM.yyyy HH:mm");

        // ---------------- ESKİ RAID YAPISI (bu disk/bölüm hangi dizinin neresiydi) ----------------
        public static string OldStructure(RaidMember m, bool wholeDisk, RaidScanner? sc)
        {
            var sb = new StringBuilder();
            sb.AppendLine("ESKİ RAID YAPISI — " + m.Name);
            sb.AppendLine("──────────────────────────────────────────");
            Probe pr;
            try { using var s = m.Open(); pr = RaidMeta.Identify(s, m.Length, wholeDisk); }
            catch (Exception ex) { return sb + "Okunamadı: " + ex.Message; }

            if (pr.Md != null)
            {
                var md = pr.Md;
                sb.AppendLine($"Tür          : Linux md {md.Variant} süper blok (Synology / QNAP / WD / Netgear / Linux)");
                sb.AppendLine($"Dizi adı     : {md.Name}");
                sb.AppendLine($"Dizi UUID    : {md.Uuid}");
                sb.AppendLine($"RAID seviyesi: {RaidMeta.MdLevelName(md)}");
                sb.AppendLine($"Yerleşim     : {md.LayoutName}");
                if (md.Level is not (1 or -1)) sb.AppendLine($"Blok boyutu  : {Fmt.Size(md.Chunk)}");
                sb.AppendLine($"Disk sayısı  : {md.RaidDisks}");
                sb.AppendLine($"BU DİSKİN SIRASI: {(md.Role < 0 ? "yedek / arızalı işaretli" : (md.Role + 1) + ". disk")}");
                sb.AppendLine($"Olay sayacı  : {md.Events}   (diğer üyelerden küçükse bu disk diziden ÖNCE düşmüş = eski veri)");
                if (md.Created != null) sb.AppendLine($"Oluşturulma  : {md.Created:dd.MM.yyyy HH:mm}");
                if (md.Updated != null) sb.AppendLine($"Son güncelleme: {md.Updated:dd.MM.yyyy HH:mm}");
                sb.AppendLine($"Veri başı    : sektör {md.DataOffset / 512:N0}  · üye başına veri {Fmt.Size(md.DataSize)}");
                sb.AppendLine($"Süper blok   : bayt {md.SbOffset:N0}");
                if (sc != null)
                {
                    var all = sc.MdPool.Where(x => x.md.Uuid == md.Uuid).ToList();
                    ulong max = all.Count > 0 ? all.Max(x => x.md.Events) : md.Events;
                    sb.AppendLine();
                    sb.AppendLine("DİZİNİN TÜM ÜYELERİ (takılı disklerde bulunan):");
                    for (int r = 0; r < md.RaidDisks; r++)
                    {
                        var hits = all.Where(x => x.md.Role == r).ToList();
                        if (hits.Count == 0) { sb.AppendLine($"  {r + 1,2}. — BULUNAMADI (disk takılı değil ya da okunamıyor)"); continue; }
                        foreach (var h in hits)
                            sb.AppendLine($"  {r + 1,2}. {h.m.Name} · olay {h.md.Events}" + (h.md.Events + 1 < max ? "  ← ESKİ (düşmüş)" : "") + (h.m.HealthPercent >= 0 ? $" · sağlık %{h.m.HealthPercent}" : ""));
                    }
                    foreach (var h in all.Where(x => x.md.Role < 0)) sb.AppendLine($"   yedek/arızalı: {h.m.Name}");
                }
                return sb.ToString();
            }
            if (pr.Ddf != null)
            {
                var d = pr.Ddf;
                sb.AppendLine($"Tür          : DDF (SNIA) — {d.Vendor} kontrolcü (LSI MegaRAID / Dell PERC / IBM ServeRAID M)");
                sb.AppendLine($"Başlık GUID  : {d.HeaderGuid}");
                sb.AppendLine($"Bu diskin referansı: {d.MyRef:X8}");
                foreach (var v in d.Configs)
                {
                    int pos = Array.IndexOf(v.PdSeq, d.MyRef);
                    sb.AppendLine();
                    sb.AppendLine($"Sanal disk '{v.Name}'  ({v.Guid[..16]}…)");
                    sb.AppendLine($"  Seviye: {DdfLevel(v.Prl, v.Rlq)}" + (v.SecCount > 1 ? $" · üst katman {v.SecCount} grup (RAID {(v.Prl == 5 ? "50" : v.Prl == 6 ? "60" : "10")}), bu grup {v.SecSeq + 1}." : ""));
                    sb.AppendLine($"  Blok: {Fmt.Size(v.StripBytes)} · üye başına {Fmt.Size(v.BlockCount * 512)} · boyut {Fmt.Size(v.VdSize * 512)}" + (v.RotateParity > 0 ? $" · parite gecikmesi {1 << v.RotateParity}" : ""));
                    sb.AppendLine($"  BU DİSKİN SIRASI: {(pos >= 0 ? (pos + 1) + ". disk" : "bu sanal diskte yok")}");
                    sb.AppendLine("  Üye sırası:");
                    for (int i = 0; i < v.PdSeq.Length; i++)
                    {
                        var who = sc?.DdfPool.FirstOrDefault(x => x.d.MyRef == v.PdSeq[i] && x.d.HeaderGuid == d.HeaderGuid).m;
                        sb.AppendLine($"   {i + 1,2}. ref {v.PdSeq[i]:X8} · başl. LBA {v.StartLba[i]:N0} · " + (who != null ? who.Name : "takılı değil"));
                    }
                }
                return sb.ToString();
            }
            if (pr.Imsm != null)
            {
                var im = pr.Imsm;
                sb.AppendLine("Tür          : Intel Matrix / RST (anakart RAID)");
                sb.AppendLine($"Aile no      : {im.Family:X8}");
                sb.AppendLine("Kayıtlı diskler (seri no): " + string.Join(", ", im.Serials));
                foreach (var v in im.Vols)
                {
                    sb.AppendLine();
                    sb.AppendLine($"Birim '{v.Name}': RAID {(v.Level == 1 && v.Members >= 4 ? 10 : v.Level)} · {v.Members} disk · blok {Fmt.Size(v.StripSectors * 512L)} · başl. LBA {v.Pba:N0} · boyut {Fmt.Size(v.Size)}");
                    for (int i = 0; i < v.Ord.Length; i++)
                        sb.AppendLine($"   {i + 1}. seri {(v.Ord[i] < im.Serials.Count ? im.Serials[v.Ord[i]] : "?")}" + (m.Serial.Length > 0 && v.Ord[i] < im.Serials.Count && Norm(im.Serials[v.Ord[i]]).EndsWith(Norm(m.Serial)) ? "  ← BU DİSK" : ""));
                }
                return sb.ToString();
            }
            if (pr.Lvm != null)
            {
                sb.AppendLine($"Tür          : LVM fiziksel birim");
                sb.AppendLine($"Birim grubu  : {pr.Lvm.VgName}  ({pr.Lvm.VgId})");
                sb.AppendLine($"PV UUID      : {pr.Lvm.PvUuid}");
                sb.AppendLine($"Üst veri sırası: {pr.Lvm.Seq}");
                sb.AppendLine();
                sb.AppendLine("Üst veri metni (ilk kısım):");
                sb.AppendLine(pr.Lvm.Text.Length > 3000 ? pr.Lvm.Text[..3000] + "\n…" : pr.Lvm.Text);
                return sb.ToString();
            }

            // üst veri yok: kurulu bir RAID'in parçası mı?
            sb.AppendLine("Bu disk/bölümde RAID üst verisi (md / DDF / Intel / LVM) YOK.");
            if (sc != null)
            {
                foreach (var v in sc.Virtual.Where(v => v.Config != null))
                {
                    int i = Array.FindIndex(v.Config!.Members, x => x != null && x.Key == m.Key);
                    if (i < 0) continue;
                    sb.AppendLine();
                    sb.AppendLine($"Çözücünün kurduğu dizide yeri: {i + 1}. disk");
                    sb.AppendLine(RaidScanner.RaidInfoText(v.Config));
                }
            }
            sb.AppendLine();
            sb.AppendLine("Donanım RAID'i (HP Smart Array, IBM, Adaptec vb.) üst veriyi okunamaz biçimde ya da diskin sonundaki ayrılmış alanda tutar.");
            sb.AppendLine("→ Aynı dizinin disklerini işaretleyip '🧩 Seçili diskleri çöz' deyin: seviye, blok, yerleşim ve eski sıra bulunur.");
            return sb.ToString();
        }

        static string Norm(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        static string DdfLevel(int prl, int rlq) => prl switch
        {
            0x00 => "RAID 0",
            0x01 => "RAID 1",
            0x03 => "RAID 3",
            0x04 => "RAID 4",
            0x05 => "RAID 5 (" + (rlq == 0 ? "sağ-asimetrik" : rlq == 2 ? "sol-asimetrik" : "sol-simetrik") + ")",
            0x06 => "RAID 6 (" + (rlq == 0 ? "zero-restart" : rlq == 2 ? "N-restart" : "N-continue") + ")",
            0x07 => "RAID 1E",
            0x09 => "JBOD / uç uca",
            0x11 => "RAID 1E",
            0x15 => "RAID 5EE",
            _ => "kod 0x" + prl.ToString("X2")
        };

        // ---------------- ESKİ (DÜŞMÜŞ) DİSKİ BUL — her dosya sisteminde ----------------
        // Mantık: her üye sırayla "eksik" sayılır, sanal disk pariteden kurulur.
        //  · İyi bir disk dışarıdayken: bayat diskin eski verisi pariteye karışır → hesaplanan bloklar ÇÖP olur.
        //  · Bayat disk dışarıdayken: hesaplanan bloklar doğrudur → sonuç, tüm disklerle okunana yakın kalır.
        //  Ölçü (dosya sistemine göre): NTFS MFT/INDX düzeltme dizisi · ext dizin + extent blokları ·
        //  XFS v5 CRC'li bloklar ve inode'lar · Btrfs ağaç blokları (crc32c) · hiçbiri yoksa (VMFS, ZFS,
        //  CCTV, tanınmayan) içerik devamlılığı (blok sınırlarında kopukluk).
        public static string FindStale(RaidConfig c, Action<string> log, Func<bool> cancel, out int staleSlot)
        {
            staleSlot = -1;
            var sb = new StringBuilder();
            if (c.Kind is not (RaidKind.Raid5 or RaidKind.Raid6) || c.Missing > 0) return "Yalnız eksiksiz RAID 5/6/50/60 için.";
            var regions = PickRegions(c);
            var bs = Measure(c, regions, cancel);
            sb.AppendLine($"Ölçülen bölge: {regions.Count} × 1 MB (baş, MFT, dağınık örnekler)");
            sb.AppendLine($"Tüm disklerle: {bs.Text}");
            bool useStruct = bs.Valid >= 20;
            sb.AppendLine(useStruct ? "Ölçü: dosya sistemi yapılarının sağlamlık kontrolü" : "Ölçü: içerik devamlılığı (tanınan yapı az — VMFS/ZFS/CCTV/şifresiz ham veri)");
            double baseM = useStruct ? bs.Valid : -bs.Disc;
            var e = new double[c.Members.Length];
            for (int i = 0; i < c.Members.Length && !cancel(); i++)
            {
                var t = c.Clone(); t.Members[i] = null;
                var r = Measure(t, regions, cancel);
                e[i] = useStruct ? r.Valid : -r.Disc;
                sb.AppendLine($"  {i + 1,2}. disk dışarıda: {r.Text}   ({c.Members[i]!.Name})");
                log($"Eski disk testi: {i + 1}. disk dışarıda → {r.Text}");
            }
            if (cancel()) return sb + "İptal edildi.";
            var order = Enumerable.Range(0, e.Length).OrderByDescending(i => e[i]).ToList();
            int b1 = order[0], b2 = order.Count > 1 ? order[1] : -1;
            bool allSame = order.All(i => Math.Abs(e[i] - baseM) <= Math.Abs(baseM) * 0.03 + (useStruct ? 3 : 0.002));
            if (allSame) sb.AppendLine("→ Eski disk görünmüyor: hangi disk çıkarılsa sonuç aynı, tüm diskler güncel.");
            else if (b2 >= 0 && Math.Abs(e[b1] - baseM) <= Math.Abs(baseM) * 0.06 + (useStruct ? 5 : 0.004)
                     && (useStruct ? e[b2] < e[b1] * 0.92 : e[b2] < e[b1] - Math.Abs(e[b1]) * 0.08))
            {
                staleSlot = b1;
                sb.AppendLine($"→ ESKİ DİSK: {b1 + 1}. disk ({c.Members[b1]!.Name}).");
                sb.AppendLine("  Başka bir disk çıkarılınca veri bozuluyor, yalnız bu disk çıkarılınca bozulmuyor →");
                sb.AppendLine("  bu diskin verisi diğerleriyle uyuşmuyor (diziden önce düşmüş). Dizi bu disk 'eksik' sayılarak kurulmalı.");
            }
            else sb.AppendLine("→ Net bir sonuç çıkmadı (birden fazla disk farklı olabilir ya da ölçülen bölgede değişiklik az). Parite kontrolü ile birlikte değerlendirin.");
            return sb.ToString();
        }

        record Meas(int Valid, double Disc, string Text);

        static List<long> PickRegions(RaidConfig c)
        {
            var r = new List<long>();
            long cap = c.Capacity();
            for (long o = 0; o < Math.Min(cap, 24L << 20); o += 1 << 20) r.Add(o);
            try
            {
                // NTFS ise MFT'nin başından 8 MB
                using var s = new RaidStream(c);
                var starts = new List<long> { 0 };
                foreach (var p in PartitionReader.Read(s, 512)) starts.Add(p.Offset);
                foreach (long st in starts)
                {
                    byte[] b = Fmt.ReadAt(s, st, 512);
                    if (Encoding.ASCII.GetString(b, 3, 8) == "NTFS    ")
                    {
                        int bps = Fmt.U16(b, 0x0B); int spc = b[0x0D]; if (spc > 0x80) spc = 1 << (256 - spc);
                        long mft = st + (long)Fmt.U64(b, 0x30) * spc * bps;
                        for (long o = 0; o < 8L << 20; o += 1 << 20) r.Add(mft + o);
                    }
                    else for (long o = 0; o < 4L << 20; o += 1 << 20) r.Add(st + o);   // diğer FS'lerin başı (süper blok, grup tabloları)
                }
            }
            catch { }
            for (int k = 0; k < 24; k++) r.Add((long)(cap * (k + 0.5) / 24) / (1 << 20) * (1 << 20));
            return r.Where(x => x >= 0 && x + (1 << 20) <= cap).Distinct().ToList();
        }

        static Meas Measure(RaidConfig c, List<long> regions, Func<bool> cancel)
        {
            int ntfs = 0, ext = 0, xfs = 0, btr = 0; double disc = 0; int dn = 0;
            try
            {
                using var s = new RaidStream(c);
                long C = Math.Max(4096, c.Chunk);
                foreach (long o in regions)
                {
                    if (cancel()) break;
                    byte[] b = Fmt.ReadAt(s, o, 1 << 20);
                    ntfs += CountNtfs(b);
                    ext += CountExt(b);
                    xfs += CountXfs(b);
                    btr += CountBtrfs(b);
                    // blok sınırlarında kopukluk (sanal disk sırasıyla)
                    for (long p = ((o + C - 1) / C) * C; p < o + b.Length; p += C)
                    {
                        int at = (int)(p - o);
                        if (at < 4096 || at + 4096 > b.Length) continue;
                        if (Zero(b, at - 4096) && Zero(b, at)) continue;
                        disc += HistDist(b, at - 4096, at); dn++;
                    }
                }
            }
            catch { }
            int valid = ntfs + ext + xfs + btr;
            double d = dn > 0 ? disc / dn : 0;
            var parts = new List<string>();
            if (ntfs > 0) parts.Add("NTFS " + ntfs);
            if (ext > 0) parts.Add("ext " + ext);
            if (xfs > 0) parts.Add("XFS " + xfs);
            if (btr > 0) parts.Add("Btrfs " + btr);
            string txt = (parts.Count > 0 ? "sağlam yapı " + valid + " (" + string.Join(", ", parts) + ")" : "tanınan yapı yok") + $" · sınır kopukluğu {d:0.000}";
            return new Meas(valid, d, txt);
        }

        static bool Zero(byte[] b, int o) { for (int i = 0; i < 4096; i += 8) if (BitConverter.ToUInt64(b, o + i) != 0) return false; return true; }

        static double HistDist(byte[] b, int a, int c)
        {
            Span<int> h1 = stackalloc int[16]; Span<int> h2 = stackalloc int[16];
            for (int i = 0; i < 4096; i++) { h1[b[a + i] >> 4]++; h2[b[c + i] >> 4]++; }
            double d = 0; for (int k = 0; k < 16; k++) d += Math.Abs(h1[k] - h2[k]);
            return d / 8192.0;
        }

        // NTFS: FILE ve INDX kayıtları, düzeltme dizisi (update sequence) tutarlı mı
        static int CountNtfs(byte[] m)
        {
            int ok = 0;
            for (int o = 0; o + 1024 <= m.Length; o += 512)
            {
                uint sig = Fmt.U32(m, o);
                if (sig != 0x454C4946 && sig != 0x58444E49) continue;   // FILE / INDX
                int usa = Fmt.U16(m, o + 4), cnt = Fmt.U16(m, o + 6);
                if (usa < 0x28 || cnt < 2 || cnt > 17 || usa + cnt * 2 > 512) continue;
                int len = (cnt - 1) * 512;
                if (o + len > m.Length) continue;
                ushort usn = Fmt.U16(m, o + usa);
                bool good = true;
                for (int k = 1; k < cnt && good; k++) if (Fmt.U16(m, o + k * 512 - 2) != usn) good = false;
                if (good) ok++;
            }
            return ok;
        }

        // ext2/3/4: dizin blokları (kayıt zinciri tam blok boyunu tutmalı) + extent blokları (F30A başlığı)
        static int CountExt(byte[] m)
        {
            int ok = 0;
            for (int o = 0; o + 4096 <= m.Length; o += 4096)
            {
                // extent ağacı bloğu
                if (Fmt.U16(m, o) == 0xF30A)
                {
                    int ent = Fmt.U16(m, o + 2), max = Fmt.U16(m, o + 4), depth = Fmt.U16(m, o + 6);
                    if (max == (4096 - 12) / 12 && ent <= max && depth <= 5) ok++;
                    continue;
                }
                // dizin bloğu: ilk kayıt "." (inode≠0, rec_len 12, name_len 1, '.')
                if (Fmt.U32(m, o) != 0 && Fmt.U16(m, o + 4) == 12 && m[o + 6] == 1 && m[o + 8] == (byte)'.')
                {
                    int p = 0, guard = 0; bool good = true;
                    while (p < 4096 && guard++ < 512)
                    {
                        int rl = Fmt.U16(m, o + p + 4), nl = m[o + p + 6];
                        if (rl < 8 || rl % 4 != 0 || nl + 8 > rl || p + rl > 4096) { good = false; break; }
                        p += rl;
                    }
                    if (good && p == 4096) ok++;
                }
            }
            return ok;
        }

        // XFS v5: CRC'li bloklar (dizin, B+ ağaç) ve inode'lar
        static int CountXfs(byte[] m)
        {
            int ok = 0;
            for (int o = 0; o + 512 <= m.Length; o += 512)
            {
                uint mg = Fmt.B32(m, o);
                if ((mg >> 16) == 0x494E && m[o + 4] == 3)   // "IN" inode v3
                {
                    foreach (int isz in new[] { 512, 256, 1024 })
                        if (o + isz <= m.Length && CrcOk(m, o, isz, 0x64)) { ok++; break; }
                    continue;
                }
                if (o % 4096 != 0 || o + 4096 > m.Length) continue;
                int crcAt = mg switch
                {
                    0x58444233 or 0x58444433 or 0x58444633 => 4,                       // XDB3 XDD3 XDF3 (dizin)
                    0x41423342 or 0x41423343 or 0x49414233 or 0x46494233 => 0x34,     // AB3B AB3C IAB3 FIB3 (kısa ağaç)
                    0x424D4133 => 0x40,                                                // BMA3 (uzun ağaç)
                    _ => -1
                };
                if (crcAt > 0 && CrcOk(m, o, 4096, crcAt)) ok++;
            }
            return ok;
        }

        static bool CrcOk(byte[] m, int o, int len, int crcAt)
        {
            uint stored = Fmt.U32(m, o + crcAt);
            uint c = Crc32C.Update(0xFFFFFFFF, m, o, crcAt);
            c = Crc32C.Update(c, new byte[4], 0, 4);
            c = Crc32C.Update(c, m, o + crcAt + 4, len - crcAt - 4);
            return ~c == stored;
        }

        // Btrfs ağaç blokları: başlıktaki crc32c (0x20'den blok sonuna)
        static int CountBtrfs(byte[] m)
        {
            int ok = 0;
            for (int o = 0; o + 4096 <= m.Length; o += 4096)
            {
                if (m[o + 0x64] > 7) continue;                         // seviye
                uint nr = Fmt.U32(m, o + 0x60); if (nr == 0 || nr > 2000) continue;
                ulong bytenr = Fmt.U64(m, o + 0x30); if (bytenr == 0 || bytenr % 4096 != 0) continue;
                if (Fmt.U64(m, o + 0x50) == 0) continue;               // nesil
                foreach (int ns in new[] { 16384, 4096, 32768, 65536 })
                {
                    if (o + ns > m.Length) continue;
                    uint c = ~Crc32C.Update(0xFFFFFFFF, m, o + 0x20, ns - 0x20);
                    if (c == Fmt.U32(m, o)) { ok++; break; }
                }
            }
            return ok;
        }
    }
}

namespace VKM.App
{
    public static class Crc32C
    {
        static readonly uint[] T = Make();
        static uint[] Make()
        {
            var t = new uint[256];
            for (uint i = 0; i < 256; i++) { uint c = i; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0x82F63B78 ^ (c >> 1) : c >> 1; t[i] = c; }
            return t;
        }
        public static uint Update(uint crc, byte[] b, int off, int len)
        {
            for (int i = 0; i < len; i++) crc = T[(crc ^ b[off + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
