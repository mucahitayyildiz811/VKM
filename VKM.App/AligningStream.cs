using System;
using System.IO;

namespace VKM.App
{
    // Fiziksel diskten okuma sektör sınırına hizalı olmak zorundadır.
    // Bu sarmalayıcı, istenen aralığı sektöre hizalayıp okur, sonra
    // sadece istenen kısmı geri verir. DiscUtils bunun üstünde çalışır.
    internal sealed class AligningStream : Stream
    {
        private readonly Stream _base;
        private readonly long _length;
        private readonly int _sector;
        private long _pos;

        public AligningStream(Stream baseStream, long length, int sector)
        {
            _base = baseStream;
            _length = length;
            _sector = sector <= 0 ? 512 : sector;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _pos; set => _pos = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _length) return 0;
            if (_pos + count > _length) count = (int)(_length - _pos);
            if (count <= 0) return 0;

            long start = _pos;
            long aStart = start - (start % _sector);
            long end = start + count;
            long aEnd = ((end + _sector - 1) / _sector) * _sector;
            if (aEnd > _length) aEnd = ((_length + _sector - 1) / _sector) * _sector;

            int aLen = (int)(aEnd - aStart);
            byte[] tmp = new byte[aLen];

            _base.Position = aStart;
            int got = 0;
            while (got < aLen)
            {
                int r = _base.Read(tmp, got, aLen - got);
                if (r <= 0) break;
                got += r;
            }

            int skip = (int)(start - aStart);
            int avail = got - skip;
            if (avail <= 0) return 0;

            int toCopy = Math.Min(count, avail);
            Array.Copy(tmp, skip, buffer, offset, toCopy);
            _pos += toCopy;
            return toCopy;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => _length + offset,
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
