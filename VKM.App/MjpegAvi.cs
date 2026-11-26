using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace VKM.App
{
    // Carve edilen JPEG karelerini oynatılabilir MJPEG .AVI dosyasına yazar (dış codec/ffmpeg gerekmez; VLC açar).
    // Akış tabanlı: kareler tek tek eklenir, sonda index + boyutlar yamalanır.
    public sealed class MjpegAviWriter : IDisposable
    {
        private readonly FileStream _fs;
        private readonly int _w, _h, _fps;
        private readonly List<(uint off, uint size)> _index = new();
        private long _riffSizePos, _moviSizePos, _moviFourccPos, _avihFramesPos, _strhLenPos;
        private int _frames;

        public MjpegAviWriter(string path, int width, int height, int fps)
        {
            _w = width; _h = height; _fps = Math.Max(1, fps);
            _fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            WriteHeaders();
        }

        private void W4(string s) => _fs.Write(Encoding.ASCII.GetBytes(s), 0, 4);
        private void U32(uint v) => _fs.Write(BitConverter.GetBytes(v), 0, 4);
        private void U16(ushort v) => _fs.Write(BitConverter.GetBytes(v), 0, 2);

        private void WriteHeaders()
        {
            W4("RIFF"); _riffSizePos = _fs.Position; U32(0); W4("AVI ");
            // LIST hdrl  = 'hdrl'(4) + avih chunk(8+56=64) + strl LIST(8+116=124) = 192
            W4("LIST"); U32(192); W4("hdrl");
            //  avih (56 bytes payload)
            W4("avih"); U32(56);
            U32((uint)(1000000 / _fps)); // us/frame
            U32(0); U32(0); U32(0x10);    // flags: HASINDEX
            _avihFramesPos = _fs.Position; U32(0); // total frames (patched)
            U32(0); U32(1); U32(0);
            U32((uint)_w); U32((uint)_h);
            U32(0); U32(0); U32(0); U32(0);
            //  LIST strl
            W4("LIST"); U32(4 + 8 + 56 + 8 + 40); W4("strl");
            //   strh (56)
            W4("strh"); U32(56);
            W4("vids"); W4("MJPG");
            U32(0); U16(0); U16(0); U32(0);
            U32(1); U32((uint)_fps); U32(0);
            _strhLenPos = _fs.Position; U32(0); // length (patched)
            U32(0); U32(0xFFFFFFFF); U32(0);
            U16(0); U16(0); U16((ushort)_w); U16((ushort)_h);
            //   strf (BITMAPINFOHEADER, 40)
            W4("strf"); U32(40);
            U32(40); U32((uint)_w); U32((uint)_h);
            U16(1); U16(24); W4("MJPG");
            U32((uint)(_w * _h * 3)); U32(0); U32(0); U32(0); U32(0);
            // LIST movi
            W4("LIST"); _moviSizePos = _fs.Position; U32(0); _moviFourccPos = _fs.Position; W4("movi");
        }

        public void AddFrame(byte[] jpeg)
        {
            long chunkPos = _fs.Position;
            W4("00dc"); U32((uint)jpeg.Length);
            _fs.Write(jpeg, 0, jpeg.Length);
            if ((jpeg.Length & 1) == 1) _fs.WriteByte(0); // even padding
            _index.Add(((uint)(chunkPos - _moviFourccPos), (uint)jpeg.Length));
            _frames++;
        }

        private void Finalize_()
        {
            long moviEnd = _fs.Position;
            // idx1
            W4("idx1"); U32((uint)(_index.Count * 16));
            foreach (var (off, size) in _index)
            {
                W4("00dc"); U32(0x10); U32(off); U32(size); // KEYFRAME
            }
            long fileEnd = _fs.Position;
            // patch sizes
            _fs.Position = _riffSizePos; U32((uint)(fileEnd - 8));
            _fs.Position = _moviSizePos; U32((uint)(moviEnd - _moviFourccPos));
            _fs.Position = _avihFramesPos; U32((uint)_frames);
            _fs.Position = _strhLenPos; U32((uint)_frames);
            _fs.Position = fileEnd;
        }

        public int FrameCount => _frames;

        public void Dispose()
        {
            try { Finalize_(); } catch { }
            _fs.Dispose();
        }
    }
}
