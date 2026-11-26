using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VKM.App
{
    // DHFS Katalog motoru:
    //  - BuildCatalog: diski BİR KEZ baştan sona tarar, her DHAV video karesinin
    //    (kanal, zaman, disk konumu) bilgisini "run"lar halinde bir katalog dosyasına yazar.
    //  - LoadCatalog: kataloğu belleğe alır (kanal bazlı sorgu için).
    //  - ExtractFromCatalog: kanal+zaman seçilince TARAMA YAPMADAN doğrudan
    //    kayıtlı konumlara gidip ham .h265/.h264 çıkarır.
    //
    // Kaynak diske ASLA yazılmaz; katalog ve çıktı başka yere yazılır.
    public static class DhfsCatalog
    {
        const int CHUNK = 16 * 1024 * 1024;        // tarama tampon boyu
        const int TAIL = 8 * 1024 * 1024;          // taşan kareyi taşıyacak kuyruk
        const long RUN_GAP = 256 * 1024;           // bu kadar boşluğa kadar aynı koşuyu sürdür
        const string MAGIC = "VKMCAT2";

        // Bir "run" = aynı kanalın diskte peş peşe duran kareler bloğu
        public sealed class Run
        {
            public byte Ch;
            public long Off;      // run başlangıç disk konumu (ilk DHAV)
            public long End;      // run bitiş (son karenin sonu)
            public uint TsLo, TsHi;
        }

        // ---------- KATALOĞA ALMA (tek seferlik tam tarama) ----------
        // catPath: kataloğun yazılacağı dosya (kaynak diskte DEĞİL).
        // Döner: bulunan run sayısı.
        public static long BuildCatalog(Func<int, Stream> openRaw, long totalSize,
                                        string catPath, CancellationToken tok,
                                        Action<long>? onBytes, out string diag)
        {
            long runCount = 0, frameCount = 0, scanned = 0;
            int chMask = 0;

            using (var outFs = new FileStream(catPath, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(outFs))
            {
                // Başlık (run sayısı sonda düzeltilecek)
                foreach (char c in MAGIC) bw.Write((byte)c);
                bw.Write(totalSize);
                long countPos = outFs.Position;
                bw.Write((long)0);           // placeholder run sayısı

                // Aktif run durumu
                bool have = false; byte curCh = 0; long curOff = 0, curEnd = 0; uint tsLo = 0, tsHi = 0;

                void Flush()
                {
                    if (!have) return;
                    bw.Write(curCh); bw.Write(curOff); bw.Write(curEnd);
                    bw.Write(tsLo); bw.Write(tsHi);
                    runCount++;
                    have = false;
                }

                using (Stream src = openRaw(CHUNK))
                {
                    byte[] buf = new byte[CHUNK + TAIL];
                    long pos = 0; int carry = 0;
                    src.Position = 0;

                    while (pos < totalSize)
                    {
                        tok.ThrowIfCancellationRequested();
                        int want = buf.Length - carry;
                        int got = src.Read(buf, carry, want);
                        if (got <= 0) break;
                        int avail = carry + got;

                        int i = 0;
                        int limit = avail - 40;           // DHAV başlığı için yeterli alan
                        while (i < limit)
                        {
                            // "DHAV" ara
                            if (buf[i] != 0x44 || buf[i + 1] != 0x48 || buf[i + 2] != 0x41 || buf[i + 3] != 0x56)
                            { i++; continue; }

                            byte type = buf[i + 4];
                            int ch = buf[i + 5];              // ham kanal baytı (ana akış=0..15, alt akış=16..31 ayrı tutulur)
                            uint flen = BitConverter.ToUInt32(buf, i + 12);
                            uint ts = BitConverter.ToUInt32(buf, i + 16);

                            // Geçerlilik: makul kare boyu (TAMPONA SIĞMALI → sahte dev kareyle takılmayı önle)
                            if (flen < 100 || flen > 8_000_000)
                            { i++; continue; }

                            // Kare tamamı tampon içinde mi? Değilse carry ile taşı
                            if (i + flen + 8 > (uint)avail)
                                break;

                            long frameDisk = pos + i;         // bu karenin disk konumu
                            frameCount++;

                            // Sadece video kareleri (I=fc, P=fd) kataloglanır
                            if (type == 0xFC || type == 0xFD)
                            {
                                if (ch < 32) chMask |= (1 << ch);
                                long frameEnd = frameDisk + flen;

                                if (have && ch == curCh && frameDisk <= curEnd + RUN_GAP)
                                {
                                    // Koşuyu sürdür
                                    curEnd = frameEnd;
                                    if (ts > 1_000_000_000u && ts < 2_000_000_000u)
                                    { if (ts < tsLo) tsLo = ts; if (ts > tsHi) tsHi = ts; }
                                }
                                else
                                {
                                    Flush();
                                    have = true; curCh = (byte)ch; curOff = frameDisk; curEnd = frameEnd;
                                    tsLo = (ts > 1_000_000_000u && ts < 2_000_000_000u) ? ts : uint.MaxValue;
                                    tsHi = (ts > 1_000_000_000u && ts < 2_000_000_000u) ? ts : 0;
                                }
                            }

                            i += (int)flen;
                        }

                        // GÜVENLİK: hiç ilerleme olmadıysa (i==0) takılma — chunk'ı atla, ileri git
                        if (i == 0) { i = Math.Max(1, avail - 40); carry = 0; }
                        else { carry = avail - i; if (carry > 0) Array.Copy(buf, i, buf, 0, carry); }
                        pos += i;
                        scanned = pos;
                        onBytes?.Invoke(got);        // ARTIMLI (okunan bayt) — çağıran topluyor, mutlak değil
                    }
                }

                Flush();

                // Run sayısını başlığa yaz
                outFs.Seek(countPos, SeekOrigin.Begin);
                bw.Write(runCount);
            }

            var chList = new System.Text.StringBuilder();
            for (int c = 0; c < 32; c++) if ((chMask & (1 << c)) != 0) { if (chList.Length > 0) chList.Append(','); chList.Append(c); }
            diag = "katalog: run=" + runCount + " · kare=" + frameCount
                 + " · tarandı=" + (scanned / 1048576) + "MB · kanallar=[" + chList + "]";
            return runCount;
        }

        // ---------- KATALOĞU YÜKLE ----------
        public sealed class Catalog
        {
            public long DiskSize;
            public byte[] Ch = Array.Empty<byte>();
            public long[] Off = Array.Empty<long>();
            public long[] End = Array.Empty<long>();
            public uint[] TsLo = Array.Empty<uint>();
            public uint[] TsHi = Array.Empty<uint>();
            public int Count;

            public int ChannelMask()
            {
                int m = 0;
                for (int i = 0; i < Count; i++) if (Ch[i] < 32) m |= (1 << Ch[i]);
                return m;
            }
        }

        public static Catalog LoadCatalog(string catPath)
        {
            using var fs = new FileStream(catPath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);
            var magic = new char[MAGIC.Length];
            for (int k = 0; k < MAGIC.Length; k++) magic[k] = (char)br.ReadByte();
            if (new string(magic) != MAGIC) throw new InvalidDataException("Katalog dosyası tanınmadı");
            long diskSize = br.ReadInt64();
            long runCount = br.ReadInt64();
            int n = (int)Math.Min(runCount, int.MaxValue);

            var cat = new Catalog
            {
                DiskSize = diskSize,
                Ch = new byte[n],
                Off = new long[n],
                End = new long[n],
                TsLo = new uint[n],
                TsHi = new uint[n],
                Count = n
            };
            for (int k = 0; k < n; k++)
            {
                cat.Ch[k] = br.ReadByte();
                cat.Off[k] = br.ReadInt64();
                cat.End[k] = br.ReadInt64();
                cat.TsLo[k] = br.ReadUInt32();
                cat.TsHi[k] = br.ReadUInt32();
            }
            return cat;
        }

        public static bool CatalogMatches(string catPath, long totalSize)
        {
            try
            {
                using var fs = new FileStream(catPath, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);
                for (int k = 0; k < MAGIC.Length; k++) if ((char)br.ReadByte() != MAGIC[k]) return false;
                return br.ReadInt64() == totalSize;
            }
            catch { return false; }
        }

        // ---------- KATALOGDAN ÇIKAR (tarama yok) ----------
        // channel: 0-tabanlı. [startUnix,endUnix] penceresindeki kareleri ham yazar.
        // Döner: true = kare bulundu ve dosya yazıldı.
        public static bool ExtractFromCatalog(Func<int, Stream> openRaw, Catalog cat,
                                              int channel, long startUnix, long endUnix,
                                              string dstMp4, CancellationToken tok,
                                              out string diag, Action<long>? onBytes)
        {
            // İlgili run'ları topla: kanal eşleşen + zaman penceresiyle örtüşen
            var hits = new List<int>();
            for (int k = 0; k < cat.Count; k++)
            {
                if (cat.Ch[k] != channel) continue;
                // run zaman aralığı pencereyle kesişiyor mu? (geniş tut; kare bazında yine süzeceğiz)
                if (cat.TsHi[k] < startUnix - 120 || cat.TsLo[k] > endUnix + 120) continue;
                hits.Add(k);
            }
            if (hits.Count == 0)
            { diag = "ch" + channel + " için katalogda bu pencerede kayıt yok"; return false; }

            // TEK fiziksel kayıttan çıkar: pencereyle örtüşen koşular arasından EN BÜYÜĞÜNÜ seç.
            // (Dönmüş diskte aynı saatin birçok kopyası olur; hepsini birleştirmek videoyu bozar.
            //  Tek, en büyük koşu = tek sürekli kayıt = temiz, oynar video.)
            int hitCount = hits.Count;
            int bestK = hits[0]; long bestSize = cat.End[bestK] - cat.Off[bestK];
            foreach (int k in hits)
            {
                long sz = cat.End[k] - cat.Off[k];
                if (sz > bestSize) { bestSize = sz; bestK = k; }
            }
            hits = new List<int> { bestK };

            string outPath = Path.ChangeExtension(dstMp4, ".h265");
            try { if (File.Exists(outPath)) File.Delete(outPath); } catch { }

            bool isH265 = false, sawParam = false, any = false;
            long frames = 0, outBytes = 0;

            using (Stream src = openRaw(CHUNK))
            using (var es = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            {
                byte[] buf = new byte[CHUNK + TAIL];

                foreach (int k in hits)
                {
                    tok.ThrowIfCancellationRequested();
                    long runOff = cat.Off[k];
                    long runEnd = cat.End[k];
                    src.Position = runOff;
                    long pos = runOff; int carry = 0;

                    while (pos < runEnd)
                    {
                        int want = (int)Math.Min(buf.Length - carry, runEnd - pos + TAIL);
                        if (want <= 0) break;
                        int got = src.Read(buf, carry, want);
                        if (got <= 0) break;
                        int avail = carry + got;

                        int i = 0, limit = avail - 40;
                        while (i < limit)
                        {
                            if (buf[i] != 0x44 || buf[i + 1] != 0x48 || buf[i + 2] != 0x41 || buf[i + 3] != 0x56)
                            { i++; continue; }
                            byte type = buf[i + 4]; int ch = buf[i + 5];   // ham kanal (ana akış tam eşleşir → temiz)
                            uint flen = BitConverter.ToUInt32(buf, i + 12);
                            uint ts = BitConverter.ToUInt32(buf, i + 16);
                            if (flen < 100 || flen > 8_000_000) { i++; continue; }
                            if (i + flen + 8 > (uint)avail) break;

                            if ((type == 0xFC || type == 0xFD) && ch == channel
                                && ts >= startUnix && ts <= endUnix)
                            {
                                int p = FindStart(buf, i + 16, (int)(i + flen));
                                if (p >= 0)
                                {
                                    int payEnd = (int)(i + flen - 8);
                                    if (payEnd > p)
                                    {
                                        if (!sawParam)
                                        {
                                            int q = p + 3; if (buf[p + 2] == 0x00) q = p + 4;
                                            if (q < payEnd)
                                            {
                                                byte nb = buf[q];
                                                int h265t = (nb >> 1) & 0x3F, h264t = nb & 0x1F;
                                                if (h265t == 32 || h265t == 33 || h265t == 34) { isH265 = true; sawParam = true; }
                                                else if (h264t == 7 || h264t == 8) { isH265 = false; sawParam = true; }
                                            }
                                        }
                                        if (outBytes < 6L * 1024 * 1024 * 1024)
                                        { es.Write(buf, p, payEnd - p); any = true; frames++; outBytes += (payEnd - p); }
                                    }
                                }
                            }
                            i += (int)flen;
                        }

                        // GÜVENLİK: hiç ilerleme olmadıysa takılma
                        if (i == 0) { i = Math.Max(1, avail - 40); carry = 0; }
                        else { carry = avail - i; if (carry > 0) Array.Copy(buf, i, buf, 0, carry); }
                        pos += i;
                        onBytes?.Invoke(got);        // ARTIMLI (okunan bayt), mutlak değil
                        if (pos >= runEnd) break;
                    }
                }
            }

            if (!any)
            {
                try { File.Delete(outPath); } catch { }
                diag = "ch" + channel + " seçilen koşuda pencerede kare yok (aday=" + hitCount + ")";
                return false;
            }

            // H264 ise uzantıyı düzelt (olmazsa .h265 kalsın, yine oynar)
            if (!isH265)
            {
                string h264 = Path.ChangeExtension(dstMp4, ".h264");
                try { if (File.Exists(h264)) File.Delete(h264); File.Move(outPath, h264); } catch { }
            }
            diag = "ch" + channel + " · aday=" + hitCount + " · kare=" + frames
                 + " · boyut=" + (outBytes / 1048576) + "MB · " + (isH265 ? "H265" : "H264") + " · ham=OK";
            return true;
        }

        // İlk Annex-B start code'u bul (00 00 01 / 00 00 00 01)
        static int FindStart(byte[] b, int from, int end)
        {
            for (int i = from; i + 3 < end; i++)
                if (b[i] == 0 && b[i + 1] == 0 && b[i + 2] == 1) return (i > from && b[i - 1] == 0) ? i - 1 : i;
            return -1;
        }
    }
}
