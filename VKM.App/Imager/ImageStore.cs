using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VKM.App
{
    // Paylaşımlı imaj deposu: tek sparse imaj dosyası + sektör haritası (blok durumları).
    // Hem "talep üzerine" okuma (ImagingStream: sen dosya seçince) hem de elle tam imaj (Başlat)
    // aynı depoya yazar. Arkada kendi kendine çalışan hiçbir şey yok — sadece okunan yer dolar.
    // Durum: 0=bekliyor 1=tam okundu 2=okunamadı 3=kısmi/talep üzerine 4=kapalı kafa
    public sealed class ImageStore : IDisposable
    {
        public readonly byte[] Status;
        public readonly bool[] HeadOn;
        public readonly int Blocks, Heads, Sector;
        public readonly long BlockBytes, Total;
        public readonly string Path;
        public long CurBlock = -1;

        private readonly FileStream _img;
        private readonly object _lock = new();

        [DllImport("kernel32", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint ctl, IntPtr inB, uint inS, IntPtr outB, uint outS, out uint ret, IntPtr ov);
        const uint FSCTL_SET_SPARSE = 0x000900C4;

        public ImageStore(string path, long total, int sector, int blocks, int heads)
        {
            Path = path; Total = total; Sector = Math.Max(512, sector);
            Blocks = Math.Max(1, blocks); Heads = Math.Max(1, heads);
            BlockBytes = (Total + Blocks - 1) / Blocks;
            BlockBytes = (BlockBytes + Sector - 1) / Sector * Sector;
            Status = new byte[Blocks];
            HeadOn = new bool[Heads];
            for (int i = 0; i < Heads; i++) HeadOn[i] = true;
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            _img = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, 1 << 20);
            try { DeviceIoControl(_img.SafeFileHandle, FSCTL_SET_SPARSE, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero); } catch { }
            try { _img.SetLength(Total); } catch { }
        }

        public void LoadStatus(byte[] saved)
        {
            if (saved == null) return;
            int n = Math.Min(saved.Length, Status.Length);
            Array.Copy(saved, Status, n);
        }

        public int HeadOf(long b) => (int)(b % Heads);
        public long LbaOf(long b) => b * BlockBytes / Sector;

        // okunan bölgeyi imaja yaz + haritada işaretle. state: 1=tam, 3=kısmi
        public void WriteRegion(long off, byte[] buf, int bo, int len, byte state)
        {
            if (len <= 0) return;
            lock (_lock) { try { _img.Position = off; _img.Write(buf, bo, len); } catch { } }
            Mark(off, len, state);
        }

        public void Mark(long off, int len, byte state)
        {
            long b0 = off / BlockBytes, b1 = (off + len - 1) / BlockBytes;
            for (long b = b0; b <= b1 && b < Blocks; b++)
            {
                byte cur = Status[b];
                // 1(tam) > 3(kısmi) > 2(arızalı) > 0 ; 4 yalnız atlananlara
                if (state == 1) Status[b] = 1;
                else if (state == 2) { if (cur != 1 && cur != 3) Status[b] = 2; }
                else if (state == 3) { if (cur != 1) Status[b] = 3; }
                else if (state == 4) { if (cur == 0) Status[b] = 4; }
            }
        }

        public (long ok, long bad, long touched) Counts()
        {
            long ok = 0, bad = 0, touched = 0;
            foreach (var s in Status) { if (s == 1) ok++; else if (s == 2) bad++; if (s == 1 || s == 2 || s == 3) touched++; }
            return (ok, bad, touched);
        }

        public void Dispose() { try { _img.Flush(); _img.Dispose(); } catch { } }
    }

    // Talep üzerine okuyan akış: Veri Kurtarma bir dosya/klasör okudukça buradan geçer,
    // okunan sektörler imaja yazılır ve haritada görünür. Arka planda tarama YOK.
    public sealed class ImagingStream : Stream
    {
        private readonly Stream _src;
        private readonly ImageStore _store;
        private long _pos;

        public ImagingStream(Func<Stream> srcOpen, ImageStore store) { _src = srcOpen(); _store = store; }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _store.Total;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin so) { _pos = so == SeekOrigin.Begin ? o : so == SeekOrigin.Current ? _pos + o : _store.Total + o; return _pos; }
        protected override void Dispose(bool d) { if (d) try { _src.Dispose(); } catch { } base.Dispose(d); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _store.Total || count <= 0) return 0;
            count = (int)Math.Min(count, _store.Total - _pos);
            try
            {
                _src.Position = _pos;
                int got = 0;
                while (got < count) { int r = _src.Read(buffer, offset + got, count - got); if (r <= 0) break; got += r; }
                if (got > 0) _store.WriteRegion(_pos, buffer, offset, got, 3);   // talep üzerine → kısmi (amber)
                if (got < count) { Array.Clear(buffer, offset + got, count - got); _store.Mark(_pos + got, count - got, 2); }
                _pos += count;
                return count;
            }
            catch
            {
                Array.Clear(buffer, offset, count);
                _store.Mark(_pos, count, 2);
                _pos += count;
                return count;
            }
        }
    }
}
