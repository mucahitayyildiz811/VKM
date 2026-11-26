using System;
using System.IO;
using System.Threading;

namespace VKM.App
{
    // Dahua DHFS kaydını DHAV karelerinden çıkarır (blok formülüne GÜVENMEZ).
    // Diskte ilgili bölgeyi (seek ipucu ± pay) tarar; her DHAV karesi kanal+zaman+NAL taşır.
    // Hedef kanalın, zaman aralığındaki video karelerinin NAL yükünü toplayıp temiz .mp4 yazar.
    public static class DhfsExtract
    {
        private const long BACK_MARGIN = 192L * 1024 * 1024;   // seek ipucundan geri pay (blok hatası için)
        private const long SCAN_CAP    = 32L * 1024 * 1024 * 1024; // güvenlik: en çok 32 GB tara
        private const int  CHUNK       = 16 * 1024 * 1024;     // 16 MB okuma bloğu

        // Başarılıysa true. rawOnly=true ise .h265/.h264 (ham, hızlı, mux yok); değilse .mp4.
        public static bool Extract(Func<int, Stream> openRaw, long totalSize, int channel,
                                   DateTime startLocal, DateTime endLocal, long seekHint,
                                   string dstMp4, int fps, CancellationToken tok, out string diag,
                                   Action<long>? onBytes = null, bool rawOnly = false)
        {
            long startUnix = ToUnix(startLocal) - 120;   // 2 dk tolerans
            long endUnix   = ToUnix(endLocal)   + 120;

            // Kanal-farkında haritadan hedef kanalın o saatteki yerini bul (parçalı disk)
            long located = FindOffsetForTime(openRaw, totalSize, startUnix + 120, seekHint, channel, onBytes);
            long begin = Math.Max(0, located - BACK_MARGIN);
            // HIZLI de doğrudan final .h265'e yazar (geçici+taşıma yok → kaybolmaz)
            string tmpEs = System.IO.Path.ChangeExtension(dstMp4, ".h265");
            try { if (File.Exists(tmpEs)) File.Delete(tmpEs); } catch { }
            bool sawParam = false; bool isH265 = false; bool any = false;
            long frames = 0; long scannedTotal = 0; long outBytes = 0;
            long allDhav = 0; int chMask = 0; long tsLo = long.MaxValue, tsHi = 0;   // teşhis için
            diag = "ch" + channel + " harita=" + MapSamples + " · konum=" + (located / 1048576) + "MB"
                 + " · pencere=[" + startUnix + ".." + endUnix + "]";

            try
            {
                using (Stream src = openRaw(CHUNK))
                using (var es = new FileStream(tmpEs, FileMode.Create, FileAccess.Write))
                {
                    src.Position = begin;
                    // Tampon: bir chunk + taşan kareyi (en çok ~8 MB) taşıyabilecek kadar
                    byte[] buf = new byte[CHUNK + 8 * 1024 * 1024];
                    int carry = 0;
                    long pos = begin;
                    long scanned = 0;
                    bool stop = false;

                    while (!stop && scanned < SCAN_CAP && pos < totalSize && !tok.IsCancellationRequested)
                    {
                        int want = buf.Length - carry;          // kalan tampon kadar oku (taşma yok)
                        if (want <= 0) { carry = 0; want = buf.Length; }
                        int got = src.Read(buf, carry, want);
                        if (got <= 0) break;
                        int total = carry + got;
                        onBytes?.Invoke(got);

                        int i = 0;
                        while (i + 24 <= total)
                        {
                            // "DHAV" ara
                            if (buf[i] != 0x44 || buf[i + 1] != 0x48 || buf[i + 2] != 0x41 || buf[i + 3] != 0x56)
                            { i++; continue; }

                            byte type = buf[i + 4];
                            int ch = buf[i + 5];
                            long flen = U32(buf, i + 12);
                            long ts = U32(buf, i + 16);

                            if (flen < 24 || flen > 8_000_000) { i++; continue; }   // mantıksız → kayma

                            // Kare tümüyle tamponda mı? değilse carry'e bırak, sonra oku
                            if (i + flen > total)
                            {
                                if (flen > buf.Length) { i += 4; continue; }        // çok büyük → atla
                                break;   // bu kareyi bir sonraki okumada tamamla
                            }

                            // Teşhis: görülen tüm DHAV'ları say (kanal + zaman aralığı)
                            allDhav++;
                            if (ch >= 0 && ch < 32) chMask |= (1 << ch);
                            if (ts > 1_500_000_000L && ts < 1_900_000_000L) { if (ts < tsLo) tsLo = ts; if (ts > tsHi) tsHi = ts; }

                            // Veri topladıysak ve bu bölge zaman penceresini açıkça geçtiyse dur
                            // (tüm kanallar ~aynı zamanda yazılır → bir kare pencereyi geçtiyse bölge bitti)
                            if (any && ts > endUnix + 30) { stop = true; break; }

                            if (ch == channel && (type == 0xFC || type == 0xFD) && ts >= startUnix && ts <= endUnix)
                            {
                                int p = FindStart(buf, i + 16, (int)(i + flen));   // header sonrası ilk start code
                                if (p >= 0)
                                {
                                    int payEnd = (int)(i + flen - 8);              // footer "dhav"+len (8)
                                    if (payEnd > p)
                                    {
                                        if (!sawParam)
                                        {
                                            // kodek tespiti: start code sonrası ilk NAL başlığı
                                            int q = p + 3; if (buf[p + 2] == 0x00) q = p + 4; // 00 00 01 / 00 00 00 01
                                            if (q < payEnd)
                                            {
                                                byte nb = buf[q];
                                                // HEVC param NAL: tip (nb>>1)&0x3f = 32/33/34 ; H264: nb&0x1f = 7/8
                                                int h265t = (nb >> 1) & 0x3F;
                                                int h264t = nb & 0x1F;
                                                if (h265t == 32 || h265t == 33 || h265t == 34) { isH265 = true; sawParam = true; }
                                                else if (h264t == 7 || h264t == 8) { isH265 = false; sawParam = true; }
                                            }
                                        }
                                        if (outBytes < 6L * 1024 * 1024 * 1024)   // üst sınır: şişmeyi engelle
                                        { es.Write(buf, p, payEnd - p); any = true; frames++; outBytes += (payEnd - p); }
                                    }
                                }
                            }
                            i += (int)flen;
                        }

                        // İşlenmeyen kuyruğu başa taşı (kare sınırı)
                        carry = total - i;
                        if (carry > 0) Array.Copy(buf, i, buf, 0, carry);
                        pos += got;
                        scanned += got; scannedTotal = scanned;

                        // Güvenlik: veri bulduysak ve begin'den 18 GB ilerlediyse dur (bir saat ~12-16 GB olabilir)
                        if (any && pos - begin > 18L * 1024 * 1024 * 1024) stop = true;
                    }
                }

                // Teşhis: görülen kanalları listele
                var chList = new System.Text.StringBuilder();
                for (int c = 0; c < 32; c++) if ((chMask & (1 << c)) != 0) { if (chList.Length > 0) chList.Append(','); chList.Append(c); }
                diag += " · tarandı=" + (scannedTotal / 1048576) + "MB · DHAV=" + allDhav
                      + " · görülen_kanallar=[" + chList + "]"
                      + " · ts=[" + (tsLo == long.MaxValue ? 0 : tsLo) + ".." + tsHi + "] · kare=" + frames;
                if (!any) { TryDel(tmpEs); diag += " · ch" + channel + ' ' + "penceresinde kare yok"; return false; }

                long esLen = new FileInfo(tmpEs).Length;
                diag += " · es=" + (esLen / 1048576) + "MB · " + (isH265 ? "H265" : "H264");

                // Dosya zaten final .h265. H264 ise .h264'e yeniden adlandır (olmazsa .h265 kalsın, yine oynar).
                if (!isH265)
                {
                    string h264 = System.IO.Path.ChangeExtension(dstMp4, ".h264");
                    try { if (File.Exists(h264)) File.Delete(h264); File.Move(tmpEs, h264); } catch { }
                }
                diag += " · ham=OK";
                return true;
            }
            catch (Exception ex)
            {
                TryDel(tmpEs);
                diag += " · HATA: " + ex.Message;
                return false;
            }
        }

        // ---- TOPLU çıkarma: TEK disk taramasında birden çok klibi birden toplar ----
        // Parçalı disk için en doğrusu: diski bir kez baştan sona tara, her DHAV video karesini
        // uyan tüm kliplere yönlendir. Kaç klip seçilirse seçilsin tek geçiş.
        public sealed class Clip
        {
            public int Channel;
            public DateTime Start, End;
            public string DstMp4 = "";
            public long SeekHint;        // indeksten: blok × 2MB (yaklaşık disk yeri)
            // iç durum:
            public FileStream? Es; public string TmpEs = "";
            public bool H265; public bool SawParam; public long Frames; public long Bytes;
            public long StartUnix, EndUnix;
        }

        public static int ExtractBatch(Func<int, Stream> openRaw, long totalSize,
                                       System.Collections.Generic.List<Clip> clips, int fps,
                                       CancellationToken tok, Action<long>? onBytes, out string diag,
                                       bool rawOnly = false)
        {
            int okCount = 0;
            foreach (var c in clips)
            {
                c.StartUnix = ToUnix(c.Start) - 120;
                c.EndUnix = ToUnix(c.End) + 120;
                // DOĞRUDAN final .h265'e yaz (geçici+taşıma yok → kaybolmaz). VLC ham akışı içerikten oynatır.
                c.TmpEs = System.IO.Path.ChangeExtension(c.DstMp4, ".h265");
                try { if (File.Exists(c.TmpEs)) File.Delete(c.TmpEs); c.Es = new FileStream(c.TmpEs, FileMode.Create, FileAccess.Write); } catch { c.Es = null; }
            }

            long scanned = 0;
            try
            {
                using Stream src = openRaw(CHUNK);
                src.Position = 0;
                byte[] buf = new byte[CHUNK + 8 * 1024 * 1024];
                int carry = 0; long pos = 0;

                while (pos < totalSize && !tok.IsCancellationRequested)
                {
                    int want = buf.Length - carry; if (want <= 0) { carry = 0; want = buf.Length; }
                    int got = src.Read(buf, carry, want);
                    if (got <= 0) break;
                    int total = carry + got; onBytes?.Invoke(got);

                    int i = 0;
                    while (i + 24 <= total)
                    {
                        if (buf[i] != 0x44 || buf[i + 1] != 0x48 || buf[i + 2] != 0x41 || buf[i + 3] != 0x56) { i++; continue; }
                        byte type = buf[i + 4]; int ch = buf[i + 5];
                        long flen = U32(buf, i + 12); long ts = U32(buf, i + 16);
                        if (flen < 24 || flen > 8_000_000) { i++; continue; }
                        if (i + flen > total) { if (flen > buf.Length) { i += 4; continue; } break; }

                        if (type == 0xFC || type == 0xFD)
                        {
                            foreach (var c in clips)
                            {
                                if (c.Es == null || c.Channel != ch || ts < c.StartUnix || ts > c.EndUnix) continue;
                                int p = FindStart(buf, i + 16, (int)(i + flen));
                                if (p < 0) continue;
                                int payEnd = (int)(i + flen - 8);
                                if (payEnd <= p) continue;
                                if (!c.SawParam)
                                {
                                    int q = p + 3; if (buf[p + 2] == 0x00) q = p + 4;
                                    if (q < payEnd)
                                    {
                                        byte nb = buf[q]; int h5 = (nb >> 1) & 0x3F; int h4 = nb & 0x1F;
                                        if (h5 == 32 || h5 == 33 || h5 == 34) { c.H265 = true; c.SawParam = true; }
                                        else if (h4 == 7 || h4 == 8) { c.H265 = false; c.SawParam = true; }
                                    }
                                }
                                // Üst sınır: bir saatlik klip asla ~6 GB olmaz → şişmeyi engelle
                                if (c.Bytes < 6L * 1024 * 1024 * 1024)
                                { c.Es.Write(buf, p, payEnd - p); c.Frames++; c.Bytes += (payEnd - p); }
                            }
                        }
                        i += (int)flen;
                    }
                    carry = total - i;
                    if (carry > 0) Array.Copy(buf, i, buf, 0, carry);
                    pos += got; scanned += got;
                }
            }
            catch { }

            // Zaten doğrudan final .h265'e yazdık. Kare bulunduysa DOSYA DURUR (kaybolmaz); boşsa sil.
            long totalOut = 0;
            foreach (var c in clips)
            {
                try { c.Es?.Dispose(); } catch { }
                if (c.Frames > 0 && c.Bytes > 0) { okCount++; totalOut += c.Bytes; }
                else TryDel(c.TmpEs);          // hiç kare yoksa boş dosyayı sil
            }

            diag = clips.Count + " klip · tüm-disk tarandı=" + (scanned / 1048576) + "MB · çıkan=" + okCount + "/" + clips.Count
                 + " · toplam=" + (totalOut / 1048576) + "MB";
            return okCount;
        }

        // ---- Zaman→offset haritası (disk döngüsel olabilir → ikili arama yetmez) ----
        // Diski ~1 GB aralıklarla örnekler, her noktada ilk DHAV'ın zaman damgasını kaydeder.
        // Böylece sarmalar (overwrite) dahil her zaman diliminin diskteki yeri bilinir.
        // Her örnek için: offset, ilk DHAV ts, VE o bölgede görülen kanallar (bit maskesi).
        // Böylece "hangi kanal hangi bölgede" bilinir — parçalı diskte tek kanalı bulur.
        private static long[]? _mapOff; private static long[]? _mapTs; private static long[]? _mapCh;
        private static long _mapKey = -1; public static int MapSamples;

        private static void EnsureMap(Func<int, Stream> openRaw, long totalSize, Action<long>? onBytes)
        {
            if (_mapKey == totalSize && _mapOff != null) return;
            var offs = new System.Collections.Generic.List<long>();
            var tss  = new System.Collections.Generic.List<long>();
            var chs  = new System.Collections.Generic.List<long>();
            try
            {
                using Stream s = openRaw(4 << 20);
                byte[] b = new byte[4 * 1024 * 1024];     // örnek başına 4 MB
                long step = 1L << 30;                     // 1 GB aralık
                int[] cnt = new int[64];                  // örnek içinde kanal başına GERÇEK kare sayısı
                long[] firstTsCh = new long[64];          // o kanalın örnekteki ilk zamanı
                for (long off = 0; off < totalSize; off += step)
                {
                    s.Position = off;
                    int got = ReadFull(s, b, b.Length);
                    if (got <= 24) continue;
                    onBytes?.Invoke(got);

                    Array.Clear(cnt, 0, 64);
                    for (int z = 0; z < 64; z++) firstTsCh[z] = -1;
                    long ts = -1;
                    int i = 0;
                    while (i + 24 <= got)
                    {
                        if (b[i] != 0x44 || b[i + 1] != 0x48 || b[i + 2] != 0x41 || b[i + 3] != 0x56) { i++; continue; }
                        long fl = U32(b, i + 12); long t = U32(b, i + 16); int ty = b[i + 4]; int ch = b[i + 5];
                        // Katı doğrulama: video karesi (fc/fd), makul boy, makul zaman, VE devamında gerçekten footer+DHAV zinciri
                        if ((ty != 0xFC && ty != 0xFD) || fl < 100 || fl > 8_000_000
                            || t < 1_500_000_000L || t > 1_900_000_000L) { i++; continue; }
                        // zincir doğrulama: bir sonraki karenin de DHAV olması çok daha güvenilir (sahteyi eler)
                        long nxt = i + fl;
                        bool chained = (nxt + 4 <= got) && b[nxt] == 0x44 && b[nxt + 1] == 0x48 && b[nxt + 2] == 0x41 && b[nxt + 3] == 0x56;
                        if (!chained && nxt + 4 <= got) { i++; continue; }   // zincir kırıksa güvenme (örnek sonu hariç)
                        if (ch >= 0 && ch < 64) { cnt[ch]++; if (firstTsCh[ch] < 0) firstTsCh[ch] = t; }
                        if (ts < 0) ts = t;
                        i += (int)fl;
                    }
                    // Kanalı ancak BASKIN ise (>=4 gerçek kare) işaretle → sahte pozitifleri ele
                    long mask = 0; long domTs = -1; int domCnt = 0;
                    for (int ch = 0; ch < 64; ch++)
                        if (cnt[ch] >= 4)
                        {
                            mask |= (1L << ch);
                            if (cnt[ch] > domCnt) { domCnt = cnt[ch]; domTs = firstTsCh[ch]; }
                        }
                    if (mask != 0) { offs.Add(off); tss.Add(domTs > 0 ? domTs : ts); chs.Add(mask); }
                }
            }
            catch { }
            _mapOff = offs.ToArray(); _mapTs = tss.ToArray(); _mapCh = chs.ToArray();
            _mapKey = totalSize; MapSamples = offs.Count;
        }

        // Hedef (kanal + zaman) için diskteki yeri bul: O KANALIN bulunduğu, hedef zamana en yakın bölge.
        private static long FindOffsetForTime(Func<int, Stream> openRaw, long totalSize, long targetUnix, long fallback, int channel, Action<long>? onBytes)
        {
            EnsureMap(openRaw, totalSize, onBytes);
            if (_mapOff == null || _mapTs == null || _mapCh == null || _mapOff.Length == 0) return Math.Max(0, fallback);
            long want = (channel >= 0 && channel < 63) ? (1L << channel) : 0;

            // 1) O kanalın olduğu + hedef zamanın hemen öncesindeki örnek (en isabetli)
            long best = -1; long bestTs = long.MinValue;
            for (int k = 0; k < _mapOff.Length; k++)
                if ((_mapCh[k] & want) != 0 && _mapTs[k] <= targetUnix && _mapTs[k] >= targetUnix - 7200)
                    if (_mapTs[k] > bestTs) { bestTs = _mapTs[k]; best = _mapOff[k]; }
            if (best >= 0) return best;

            // 2) O kanalın olduğu, hedefe en yakın (zaman ister önce ister sonra) örnek
            long nb = -1; long nd = long.MaxValue;
            for (int k = 0; k < _mapOff.Length; k++)
                if ((_mapCh[k] & want) != 0)
                {
                    long d = Math.Abs(_mapTs[k] - targetUnix);
                    if (d < nd) { nd = d; nb = _mapOff[k]; }
                }
            if (nb >= 0) return Math.Max(0, nb - (1L << 30));

            // 3) Kanal hiç haritada yok → zamana göre en yakın (eski davranış)
            long tb = -1; long td = long.MaxValue;
            for (int k = 0; k < _mapOff.Length; k++)
            {
                long d = Math.Abs(_mapTs[k] - targetUnix);
                if (d < td) { td = d; tb = _mapOff[k]; }
            }
            return tb >= 0 ? tb : Math.Max(0, fallback);
        }

        // ========================================================================
        // DOĞRUDAN ÇIKARMA: indeksteki blok×2MB konumunun ETRAFINDA küçük pencere tarar
        // (tüm disk DEĞİL). Sağlam diskte sapma minik → saniyeler. En fazla ~2GB sapmayı
        // pencere karşılar. Tarama/katalog yok. Tek fiziksel koşu → temiz video.
        // ========================================================================
        public static bool ExtractDirect(Func<int, Stream> openRaw, long totalSize,
                int channel, DateTime startLocal, DateTime endLocal, long seekHint,
                string dstMp4, int fps, CancellationToken tok, out string diag, Action<long>? onBytes)
        {
            long startUnix = ToUnix(startLocal), endUnix = ToUnix(endLocal);
            long looseLo = startUnix - 300, looseHi = endUnix + 300;
            // ±1.5GB ilk pencere: tek kanal 1 saat ≈ 0.5–1 GB, örneklenmiş seekHint saatin ortasında
            // olmasa bile tam saat pencereye girsin (zaman filtresi komşu saate taşmayı önler).
            long[] windows = { 1536L << 20, 4096L << 20, 8192L << 20 };  // ±1.5GB, ±4GB, ±8GB
            string davPath = System.IO.Path.ChangeExtension(dstMp4, ".dav");  // HAM Dahua formatı

            foreach (long win in windows)
            {
                tok.ThrowIfCancellationRequested();
                long from = Math.Max(0, seekHint - win);
                long to = Math.Min(totalSize, seekHint + win);

                // 1) pencere içinde bu kanalın karelerinin (offset) listesini çıkar
                var fo = new List<long>();
                ScanChannelFrames(openRaw, from, to, channel, looseLo, looseHi, tok, onBytes, fo);
                if (fo.Count < 10) continue;   // bu pencerede yok → genişlet

                // 2) en yoğun SÜREKLİ koşuyu seç (tek fiziksel kayıt = temiz)
                long GAP = 8L << 20;
                int bs = 0, be = fo.Count, cs = 0, bestLen = 0;
                for (int k = 1; k <= fo.Count; k++)
                    if (k == fo.Count || fo[k] - fo[k - 1] > GAP)
                    { if (k - cs > bestLen) { bestLen = k - cs; bs = cs; be = k; } cs = k; }
                long runFrom = fo[bs];
                long runTo = Math.Min(to + (16 << 20), fo[be - 1] + (16 << 20));

                // 3) koşuyu OLDUĞU GİBİ .dav'a kopyala (parçalama/mux YOK → DVR ile birebir aynı)
                bool ok = WriteRun(openRaw, runFrom, runTo, channel, startUnix, endUnix,
                                   davPath, tok, onBytes, out long frames, out long davBytes);
                if (ok && frames > 0)
                {
                    diag = "ch" + channel + " HAM .dav · ±" + (win >> 20) + "MB · kare=" + frames
                         + " · " + (davBytes / 1048576) + "MB · OK";
                    return true;
                }
                try { if (File.Exists(davPath)) File.Delete(davPath); } catch { }
            }
            diag = "ch" + channel + " doğrudan bulunamadı (±4GB pencerede yok)";
            return false;
        }

        // Pencerede [from,to] kanal=channel, ts∈[tsLo,tsHi] video karelerinin disk offsetlerini topla
        private static void ScanChannelFrames(Func<int, Stream> openRaw, long from, long to,
                int channel, long tsLo, long tsHi, CancellationToken tok, Action<long>? onBytes, List<long> outOff)
        {
            const int CH = 16 << 20, TL = 8 << 20;
            using Stream s = openRaw(CH);
            byte[] buf = new byte[CH + TL];
            s.Position = from; long pos = from; int carry = 0;
            while (pos < to)
            {
                tok.ThrowIfCancellationRequested();
                int got = s.Read(buf, carry, buf.Length - carry);
                if (got <= 0) break;
                int avail = carry + got, i = 0, limit = avail - 40;
                while (i < limit)
                {
                    if (buf[i] != 0x44 || buf[i + 1] != 0x48 || buf[i + 2] != 0x41 || buf[i + 3] != 0x56) { i++; continue; }
                    byte ty = buf[i + 4]; int ch = buf[i + 5];
                    uint fl = BitConverter.ToUInt32(buf, i + 12); uint ts = BitConverter.ToUInt32(buf, i + 16);
                    if (fl < 100 || fl > 8_000_000) { i++; continue; }
                    if (i + fl + 8 > (uint)avail) break;
                    if ((ty == 0xFC || ty == 0xFD) && ch == channel && ts >= tsLo && ts <= tsHi)
                        outOff.Add(pos + i);
                    i += (int)fl;
                }
                if (i == 0) { i = Math.Max(1, avail - 40); carry = 0; }
                else { carry = avail - i; if (carry > 0) Array.Copy(buf, i, buf, 0, carry); }
                pos += i;
                onBytes?.Invoke(got);
            }
        }

        // Koşuyu oku: kanal=channel, ts∈[startUnix,endUnix] DHAV karelerini OLDUĞU GİBİ
        // (başlık + yük + footer) .dav dosyasına kopyala. Parçalama/yeniden sarma YOK →
        // DVR'ın diske yazdığıyla BİREBİR aynı, DVR/Dahua oynatıcısında kusursuz açılır.
        // İlk ANAHTAR kareden başlar; zaman büyük geriye atlarsa durur (tek temiz kopya).
        private static bool WriteRun(Func<int, Stream> openRaw, long from, long to, int channel,
                long startUnix, long endUnix, string davPath, CancellationToken tok, Action<long>? onBytes,
                out long frames, out long outBytes)
        {
            const int CH = 16 << 20, TL = 8 << 20;
            frames = 0; outBytes = 0;
            bool started = false, stop = false; long lastTs = -1;
            try { if (File.Exists(davPath)) File.Delete(davPath); } catch { }
            using Stream s = openRaw(CH);
            using var es = new FileStream(davPath, FileMode.Create, FileAccess.Write);
            byte[] buf = new byte[CH + TL];
            s.Position = from; long pos = from; int carry = 0;
            while (pos < to && !stop)
            {
                tok.ThrowIfCancellationRequested();
                int got = s.Read(buf, carry, buf.Length - carry);
                if (got <= 0) break;
                int avail = carry + got, i = 0, limit = avail - 40;
                while (i < limit)
                {
                    if (buf[i] != 0x44 || buf[i + 1] != 0x48 || buf[i + 2] != 0x41 || buf[i + 3] != 0x56) { i++; continue; }
                    byte ty = buf[i + 4]; int ch = buf[i + 5];
                    uint fl = BitConverter.ToUInt32(buf, i + 12); uint ts = BitConverter.ToUInt32(buf, i + 16);
                    if (fl < 100 || fl > 8_000_000) { i++; continue; }
                    if (i + fl > (uint)avail) break;        // tam kare tamponda değil → carry ile taşı
                    if ((ty == 0xFC || ty == 0xFD) && ch == channel && ts >= startUnix && ts <= endUnix)
                    {
                        if (!started)
                        {
                            // ilk ANAHTAR kareden başla: yükünde parametre NAL'ı (VPS/SPS) olan kare
                            if (ContainsParam(buf, i + 16, (int)(i + fl))) started = true;
                            else { i += (int)fl; continue; }
                        }
                        // Zaman BÜYÜK geriye atladıysa (>30sn): ikinci kopya başladı → dur (tek temiz kayıt)
                        if (lastTs >= 0 && (long)ts + 30 < lastTs) { stop = true; break; }
                        if ((long)ts > lastTs) lastTs = ts;

                        if (outBytes < 6L * 1024 * 1024 * 1024)
                        { es.Write(buf, i, (int)fl); frames++; outBytes += fl; }   // TAM DHAV karesi — HAM
                    }
                    i += (int)fl;
                }
                if (i == 0) { i = Math.Max(1, avail - 40); carry = 0; }
                else { carry = avail - i; if (carry > 0) Array.Copy(buf, i, buf, 0, carry); }
                pos += i;
                onBytes?.Invoke(got);
            }
            return frames > 0;
        }

        // Kare yükünde H265 VPS/SPS (0x40/0x42) veya H264 SPS (0x67/0x27) var mı = anahtar kare
        private static bool ContainsParam(byte[] buf, int from, int end)
        {
            for (int k = from; k + 4 < end; k++)
                if (buf[k] == 0 && buf[k + 1] == 0 && buf[k + 2] == 1)
                {
                    byte nb = buf[k + 3];
                    if (nb == 0x40 || nb == 0x42 || nb == 0x67 || nb == 0x27) return true;
                }
            return false;
        }

        // buf[from..to] içinde ilk Annex-B start code (00 00 01) konumu
        private static int FindStart(byte[] buf, int from, int to)
        {
            for (int k = from; k + 3 < to; k++)
                if (buf[k] == 0 && buf[k + 1] == 0 && buf[k + 2] == 1)
                    return (k > from && buf[k - 1] == 0) ? k - 1 : k;   // 4 baytlıysa başı al
            return -1;
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

        public static long ToUnix(DateTime localDt)
        {
            try { return ((DateTimeOffset)DateTime.SpecifyKind(localDt, DateTimeKind.Local)).ToUnixTimeSeconds(); }
            catch { return 0; }
        }

        private static void TryDel(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }
    }
}
