using System;
using System.Collections.Generic;
using System.IO;

namespace VKM.App
{
    // Dahua DHFS (CCTV) dosya sistemini KENDİ indeksinden okur — tarama YOK.
    // İndeks @0x100000'de 32 baytlık kayıtlar: tip(1=başlık,2=segment) | kanal | ...
    //   [4:8]=başlangıç zamanı (unix, LE)  [8:12]=bitiş zamanı (unix, LE)  [12:16]=blok no (×blok boyutu = disk offseti)
    // Segmentler saatlik dosyalara gruplanır (UFS'teki CAM/tarih/.dav gibi).
    public static class DhfsReader
    {
        private const long INDEX_OFFSET = 0x100000;        // indeks başlangıcı
        private const int  REC = 32;                        // kayıt boyutu
        private const long BLOCK = 2L * 1024 * 1024;        // DHFS blok boyutu (cluster 2048 KB)
        private const long INDEX_SCAN_MAX = 512L * 1024 * 1024; // indeks için en çok taranacak alan
        private const int  EMPTY_RUN_STOP = 40000;          // bu kadar ardışık boş kayıt → indeks bitti

        public class DhfsSegment
        {
            public int Channel;          // 0 tabanlı
            public DateTime Start;
            public DateTime End;
            public long Block;           // blok no
        }

        public class DhfsFile          // saatlik gruplanmış kayıt (bir .dav gibi)
        {
            public int Channel;
            public DateTime Start;
            public DateTime End;
            public long Offset;          // disk offseti (blok × BLOCK)
            public long Length;          // yaklaşık uzunluk (blok aralığından)
            public int Segments;
        }

        public class DhfsResult
        {
            public List<DhfsFile> Files = new();
            public int TotalSegments;
            public int ChannelCount;
            public DateTime MinTime, MaxTime;
            public string Diagnostic = "";
        }

        // Offset 0'da "DHFS" imzası var mı?
        public static bool Detect(Func<int, Stream> openRaw)
        {
            try
            {
                using Stream s = openRaw(512);
                s.Position = 0;
                byte[] b = new byte[8];
                int n = s.Read(b, 0, 8);
                return n >= 4 && b[0] == (byte)'D' && b[1] == (byte)'H' && b[2] == (byte)'F' && b[3] == (byte)'S';
            }
            catch { return false; }
        }

        public static DhfsResult Parse(Func<int, Stream> openRaw, long totalSize)
        {
            var res = new DhfsResult();
            var segs = new List<DhfsSegment>();

            var chans = new HashSet<int>();
            long tmin = long.MaxValue, tmax = 0;

            try
            {
                using Stream s = openRaw(1 << 20);
                s.Position = INDEX_OFFSET;

                byte[] buf = new byte[1 << 20];            // 1 MB (32768 kayıt)
                long scanned = 0;
                int emptyRun = 0;
                bool stop = false;

                while (!stop && scanned < INDEX_SCAN_MAX)
                {
                    int got = ReadFull(s, buf, buf.Length);
                    if (got < REC) break;
                    int recs = got / REC;

                    for (int i = 0; i < recs; i++)
                    {
                        int o = i * REC;
                        byte type = buf[o];

                        // boş kayıt mı? (fe 00.. veya hepsi 0)
                        if (type == 0xFE || IsZero(buf, o, REC))
                        {
                            if (++emptyRun >= EMPTY_RUN_STOP) { stop = true; break; }
                            continue;
                        }
                        emptyRun = 0;

                        if (type != 0x02) continue;        // sadece segment kayıtları

                        int ch = buf[o + 1] - 0x30;        // kanal: byte1 - 0x30
                        if (ch < 0 || ch > 255) continue;

                        uint t1 = U32(buf, o + 4);
                        uint t2 = U32(buf, o + 8);
                        if (t1 < 1_500_000_000u || t1 > 1_900_000_000u) continue;   // 2017..2030 arası makul
                        long blk = U32(buf, o + 12);

                        segs.Add(new DhfsSegment
                        {
                            Channel = ch,
                            Start = DateTimeOffset.FromUnixTimeSeconds(t1).UtcDateTime.ToLocalTime(),
                            End = (t2 > t1 && t2 < 1_900_000_000u)
                                    ? DateTimeOffset.FromUnixTimeSeconds(t2).UtcDateTime.ToLocalTime()
                                    : DateTimeOffset.FromUnixTimeSeconds(t1).UtcDateTime.ToLocalTime(),
                            Block = blk
                        });
                        chans.Add(ch);
                        if (t1 < tmin) tmin = t1;
                        if (t2 > tmax && t2 < 1_900_000_000u) tmax = t2;
                    }
                    scanned += got;
                    if (got < buf.Length) break;
                }
            }
            catch (Exception ex) { res.Diagnostic = "DHFS indeks okuma hatası: " + ex.Message; }

            res.TotalSegments = segs.Count;
            res.ChannelCount = chans.Count;
            if (tmin != long.MaxValue) res.MinTime = DateTimeOffset.FromUnixTimeSeconds(tmin).UtcDateTime.ToLocalTime();
            if (tmax != 0) res.MaxTime = DateTimeOffset.FromUnixTimeSeconds(tmax).UtcDateTime.ToLocalTime();

            // Segmentleri (kanal, tarih, saat) kovasına grupla → saatlik .dav benzeri dosyalar
            var buckets = new Dictionary<(int ch, int y, int mo, int d, int h), DhfsFile>();
            foreach (var sg in segs)
            {
                var k = (sg.Channel, sg.Start.Year, sg.Start.Month, sg.Start.Day, sg.Start.Hour);
                if (!buckets.TryGetValue(k, out var f))
                {
                    f = new DhfsFile { Channel = sg.Channel, Start = sg.Start, End = sg.End, Offset = sg.Block * BLOCK, Length = BLOCK, Segments = 0 };
                    buckets[k] = f;
                }
                if (sg.Start < f.Start) { f.Start = sg.Start; f.Offset = sg.Block * BLOCK; }
                if (sg.End > f.End) f.End = sg.End;
                long endByte = (sg.Block + 1) * BLOCK;
                if (endByte - f.Offset > f.Length) f.Length = endByte - f.Offset;
                f.Segments++;
            }

            foreach (var f in buckets.Values)
            {
                if (totalSize > 0 && f.Offset >= totalSize) continue;          // disk dışı → atla
                if (totalSize > 0 && f.Offset + f.Length > totalSize) f.Length = totalSize - f.Offset;
                res.Files.Add(f);
            }
            res.Files.Sort((a, b) =>
            {
                int c = a.Channel.CompareTo(b.Channel);
                return c != 0 ? c : a.Start.CompareTo(b.Start);
            });
            return res;
        }

        // İNDEKS BOŞSA (takılmış DVR): DHAV karelerini doğrudan tara, listeyi videodan yeniden kur.
        // Her DHAV karesinde kanal + zaman var → index olmadan kayıt listesi çıkar.
        //   'DHAV' | tip(b4) | kanal(b5) | ... | [12:16]=kare uzunluğu | [16:20]=zaman(unix,LE)
        public static DhfsResult CarveDhav(Func<int, Stream> openRaw, long totalSize, Action<long, int>? onProgress,
                                           System.Threading.CancellationToken tok = default)
        {
            var res = new DhfsResult();
            var chans = new HashSet<int>();
            long tmin = long.MaxValue, tmax = 0;

            // (kanal, yıl, ay, gün, saat) → saatlik dosya kovası (index yolundakiyle aynı yapı)
            var buckets = new Dictionary<(int ch, int y, int mo, int d, int h), DhfsFile>();

            // HIZLI ÖRNEKLEME: tüm diski okumak yerine disk boyunca ~N noktada küçük pencere oku.
            // CCTV tüm kanalları ZAMANDA İÇ İÇE kaydeder → herhangi bir andaki pencere o anın
            // bütün aktif kanallarını yakalar. Birkaç dakikada: kanal listesi + tarih aralığı +
            // saatlik kayıt haritası. (Çıkarma zaten hedef bölgeyi yerel tarar → tam offset gerekmez.)
            // Örnek sıklığı: tek kanal ~1 saat video ≈ 0.5–1 GB tutar. Her saati yakalamak için
            // stride ~400 MB (saat boyutundan küçük) olmalı. ~16000 örnek × 2 MB ≈ 32 GB okuma →
            // 7.4 TB diskte bile dakikalar (tüm diski okumak yerine 1/230'u).
            const int WIN = 2 << 20;                           // her örnekte 2 MB oku
            long stride = totalSize > 0
                ? Math.Max(256L << 20, totalSize / 16000)
                : (256L << 20);
            if (stride < WIN) stride = WIN;
            byte[] buf = new byte[WIN];

            try
            {
                using Stream s = openRaw(WIN);
                int sampleNo = 0;

                for (long p = 0; p < totalSize; p += stride)
                {
                    if (tok.IsCancellationRequested) break;
                    sampleNo++;
                    s.Position = p;
                    int got = ReadFull(s, buf, WIN);
                    if (got >= 24)
                    {
                        int lim = got - 20, i = 0;
                        while (i <= lim)
                        {
                            // 'DHAV' ara
                            if (buf[i] == 0x44 && buf[i + 1] == 0x48 && buf[i + 2] == 0x41 && buf[i + 3] == 0x56)
                            {
                                uint fl = U32(buf, i + 12);
                                if (fl >= 40 && fl <= 4_000_000)
                                {
                                    byte ty = buf[i + 4];
                                    int ch = buf[i + 5];
                                    uint ts = U32(buf, i + 16);
                                    bool tsok = ts >= 1_500_000_000u && ts <= 1_900_000_000u;
                                    // sadece VIDEO kareleri (0xFC=I, 0xFD=P) kovaya girer
                                    if ((ty == 0xFC || ty == 0xFD) && ch >= 0 && ch < 64 && tsok)
                                    {
                                        var dt = DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime.ToLocalTime();
                                        long off = p + i;
                                        var k = (ch, dt.Year, dt.Month, dt.Day, dt.Hour);
                                        if (!buckets.TryGetValue(k, out var f))
                                        {
                                            f = new DhfsFile { Channel = ch, Start = dt, End = dt, Offset = off, Length = fl, Segments = 0 };
                                            buckets[k] = f;
                                        }
                                        if (off < f.Offset) f.Offset = off;
                                        if (dt < f.Start) f.Start = dt;
                                        if (dt > f.End) f.End = dt;
                                        long span = (off + fl) - f.Offset;
                                        if (span > f.Length) f.Length = span;
                                        f.Segments++;
                                        chans.Add(ch);
                                        if (ts < tmin) tmin = ts;
                                        if (ts > tmax) tmax = ts;
                                    }
                                    // geçerli kareyi uzunluğu kadar atla → pencere içinde hızlı yürü
                                    if (ch >= 0 && ch < 64 && tsok && i + (int)fl <= got) { i += (int)fl; continue; }
                                }
                            }
                            i++;
                        }
                    }
                    if ((sampleNo & 31) == 0) onProgress?.Invoke(Math.Min(totalSize, p + stride), buckets.Count);
                }
                onProgress?.Invoke(totalSize, buckets.Count);   // son durum
            }
            catch (Exception ex) { res.Diagnostic = "DHAV örnekleme hatası: " + ex.Message; }

            foreach (var f in buckets.Values)
            {
                if (totalSize > 0 && f.Offset >= totalSize) continue;
                if (totalSize > 0 && f.Offset + f.Length > totalSize) f.Length = totalSize - f.Offset;
                res.Files.Add(f);
            }
            res.Files.Sort((a, b) =>
            {
                int c = a.Channel.CompareTo(b.Channel);
                return c != 0 ? c : a.Start.CompareTo(b.Start);
            });
            res.TotalSegments = 0;
            foreach (var f in res.Files) res.TotalSegments += f.Segments;
            res.ChannelCount = chans.Count;
            if (tmin != long.MaxValue) res.MinTime = DateTimeOffset.FromUnixTimeSeconds(tmin).UtcDateTime.ToLocalTime();
            if (tmax != 0) res.MaxTime = DateTimeOffset.FromUnixTimeSeconds(tmax).UtcDateTime.ToLocalTime();
            return res;
        }

        private static bool IsZero(byte[] b, int off, int len)
        {
            for (int i = 0; i < len; i++) if (b[off + i] != 0) return false;
            return true;
        }
        private static uint U32(byte[] b, int o) =>
            (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

        private static int ReadFull(Stream s, byte[] buf, int want)
        {
            int off = 0;
            while (off < want)
            {
                int r = s.Read(buf, off, want - off);
                if (r <= 0) break;
                off += r;
            }
            return off;
        }

    }
}
