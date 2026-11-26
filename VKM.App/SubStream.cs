using System;
using System.IO;

namespace VKM.App
{
    // Bir akışın belli bir penceresine (offset..offset+length) bakan alt-akış.
    // NTFS/FAT, bölümün başladığı yere sanki 0'danmış gibi açılır.
    internal sealed class SubStream : Stream
    {
        private readonly Stream _b;
        private readonly long _off;
        private readonly long _len;
        private readonly bool _own;
        private long _pos;

        public SubStream(Stream baseStream, long offset, long length, bool ownBase = true)
        {
            _b = baseStream;
            _off = offset;
            _len = length;
            _own = ownBase;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _len;
        public override long Position { get => _pos; set => _pos = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _len) return 0;
            if (_pos + count > _len) count = (int)(_len - _pos);
            if (count <= 0) return 0;

            // Tam dolana kadar oku (alt okuyucu kısa dönebilir)
            int total = 0;
            while (total < count)
            {
                _b.Position = _off + _pos + total;
                int r = _b.Read(buffer, offset + total, count - total);
                if (r <= 0) break;
                total += r;
            }
            _pos += total;
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _pos = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _pos + offset,
                SeekOrigin.End => _len + offset,
                _ => _pos
            };
            return _pos;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && _own) _b.Dispose();
            base.Dispose(disposing);
        }
    }
}
