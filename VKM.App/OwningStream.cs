using System;
using System.IO;

namespace VKM.App
{
    // Bir akışı sarar ve kapanınca sahibini (ör. VirtualDisk) de kapatır.
    public class OwningStream : Stream
    {
        private readonly Stream _inner;
        private readonly IDisposable _owner;

        public OwningStream(Stream inner, IDisposable owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => _inner.Position = value; }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _inner.Dispose(); } catch { }
                try { _owner.Dispose(); } catch { }
            }
            base.Dispose(disposing);
        }
    }
}
