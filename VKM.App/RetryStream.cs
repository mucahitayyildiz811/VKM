using System;
using System.Collections.Generic;
using System.IO;

namespace VKM.App
{
    // Zayıf disklerde okuma hatasında: birkaç kez yeniden dener, yine olmazsa
    // o sektörü atlar (sıfırla doldurur) ve loglar. Böylece tek bir bad-sektör
    // tüm kurtarmayı durdurmaz. AligningStream'in ALTINA konur (hizalı okur).
    internal sealed class RetryStream : Stream
    {
        private readonly Stream _base;
        private readonly int _sector;
        private readonly int _retries;
        private long _pos;

        // Süreç geneli bad-sektör sayacı (UI'da göstermek için)
        public static long TotalBadSectors;
        public static readonly List<long> BadOffsets = new();   // kapaklı liste
        public static void Reset() { TotalBadSectors = 0; lock (BadOffsets) BadOffsets.Clear(); }

        public RetryStream(Stream baseStream, int sector, int retries = 3)
        {
            _base = baseStream;
            _sector = sector <= 0 ? 512 : sector;
            _retries = Math.Max(1, retries);
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get { try { return _base.Length; } catch { return 0; } } }
        public override long Position { get => _pos; set => _pos = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int done = 0;
            while (done < count)
            {
                long curPos = _pos + done;
                int want = count - done;
                try
                {
                    _base.Position = curPos;
                    int r = _base.Read(buffer, offset + done, want);
                    if (r <= 0) break;          // dosya sonu
                    done += r;
                }
                catch (IOException)
                {
                    // Hatalı bölge: tek sektör, yeniden denemeli
                    done += ReadOneSector(buffer, offset + done, curPos, want);
                }
                catch (Exception)
                {
                    done += ReadOneSector(buffer, offset + done, curPos, want);
                }
            }
            _pos += done;
            return done;
        }

        private int ReadOneSector(byte[] buffer, int offset, long pos, int maxLen)
        {
            int len = Math.Min(_sector, maxLen);
            for (int t = 0; t < _retries; t++)
            {
                try
                {
                    _base.Position = pos;
                    int r = _base.Read(buffer, offset, len);
                    if (r > 0) return r;
                }
                catch { /* yeniden dene */ }
            }
            // Pes: sektörü sıfırla, logla
            Array.Clear(buffer, offset, len);
            TotalBadSectors++;
            lock (BadOffsets) { if (BadOffsets.Count < 100000) BadOffsets.Add(pos); }
            return len;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => Length + offset,
                _ => _pos
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _base.Dispose();
            base.Dispose(disposing);
        }
    }
}
