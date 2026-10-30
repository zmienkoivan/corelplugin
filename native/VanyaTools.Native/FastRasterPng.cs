using System;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace VanyaTools.Native
{
    // Lossless straight RGBA PNG for temporary raster processing. Stored
    // DEFLATE blocks avoid spending seconds compressing each working copy.
    // This image is an RGB preview; source-model channels stay in the companion.
    internal static class FastRasterPng
    {
        private static readonly uint[] CrcTable = BuildCrcTable();
        internal static void Write(Stream output, byte[] bgra, int width, int height, double dpiX, double dpiY,
            CancellationToken cancellation = default)
        {
            if (output == null || !output.CanWrite) throw new ArgumentException("PNG stream is not writable.", nameof(output));
            if (width <= 0 || height <= 0 || bgra == null || (long)width * height * 4 != bgra.Length)
                throw new ArgumentException("Invalid BGRA image dimensions.");
            uint ppmX = PixelsPerMeter(dpiX), ppmY = PixelsPerMeter(dpiY);
            cancellation.ThrowIfCancellationRequested();
            output.Write(new byte[] {137,80,78,71,13,10,26,10}, 0, 8);
            var header = new byte[13]; Put(header, 0, (uint)width); Put(header, 4, (uint)height);
            header[8] = 8; header[9] = 6; // eight-bit RGBA, no interlace
            Chunk(output, "IHDR", header, header.Length);
            var physical = new byte[9]; Put(physical, 0, ppmX); Put(physical, 4, ppmY); physical[8] = 1;
            Chunk(output, "pHYs", physical, physical.Length);
            using (var idat = new IdatStream(output, cancellation))
            {
                idat.WriteByte(0x78); idat.WriteByte(0x01); // RFC 1950, fast DEFLATE
                uint a = 1, b = 0;
                using (var deflate = new DeflateStream(idat, CompressionLevel.NoCompression, true))
                {
                    var row = new byte[checked(width * 4 + 1)]; // PNG filter None
                    for (int y = 0; y < height; y++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        int start = y * width * 4;
                        for (int x = 0; x < width; x++)
                        {
                            int p = start + x * 4, to = 1 + x * 4;
                            row[to] = bgra[p + 2]; row[to + 1] = bgra[p + 1]; row[to + 2] = bgra[p]; row[to + 3] = bgra[p + 3];
                        }
                        // Bound accumulators before modulo (Adler's NMAX=5552).
                        for (int startBlock = 0; startBlock < row.Length; startBlock += 5552)
                        {
                            int end = Math.Min(row.Length, startBlock + 5552);
                            for (int p = startBlock; p < end; p++) { a += row[p]; b += a; }
                            a %= 65521; b %= 65521;
                        }
                        deflate.Write(row, 0, row.Length);
                    }
                }
                var checksum = new byte[4]; Put(checksum, 0, (b << 16) | a); idat.Write(checksum, 0, 4);
            }
            cancellation.ThrowIfCancellationRequested();
            Chunk(output, "IEND", Array.Empty<byte>(), 0);
        }

        private static uint PixelsPerMeter(double dpi)
        {
            if (Double.IsNaN(dpi) || Double.IsInfinity(dpi) || dpi <= 0 || dpi / .0254 > UInt32.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(dpi));
            return (uint)Math.Max(1, Math.Round(dpi / .0254));
        }
        private static void Put(byte[] bytes, int offset, uint value)
        { bytes[offset] = (byte)(value >> 24); bytes[offset + 1] = (byte)(value >> 16); bytes[offset + 2] = (byte)(value >> 8); bytes[offset + 3] = (byte)value; }
        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint i = 0; i < table.Length; i++) { uint crc = i; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320); table[i] = crc; }
            return table;
        }
        private static void Chunk(Stream output, string type, byte[] bytes, int length)
        {
            var size = new byte[4]; Put(size, 0, (uint)length); output.Write(size, 0, 4);
            var name = new byte[] {(byte)type[0], (byte)type[1], (byte)type[2], (byte)type[3]}; output.Write(name, 0, 4);
            uint crc = UInt32.MaxValue;
            for (int i = 0; i < 4; i++) crc = CrcTable[(crc ^ name[i]) & 255] ^ (crc >> 8);
            for (int i = 0; i < length; i++) crc = CrcTable[(crc ^ bytes[i]) & 255] ^ (crc >> 8);
            output.Write(bytes, 0, length); Put(size, 0, crc ^ UInt32.MaxValue); output.Write(size, 0, 4);
        }
        private sealed class IdatStream : Stream
        {
            private readonly Stream _output; private readonly CancellationToken _cancellation;
            private readonly byte[] _buffer = new byte[65536]; private int _length;
            internal IdatStream(Stream output, CancellationToken cancellation) { _output = output; _cancellation = cancellation; }
            public override void Write(byte[] bytes, int offset, int count)
            {
                while (count > 0)
                {
                    _cancellation.ThrowIfCancellationRequested();
                    int take = Math.Min(count, _buffer.Length - _length);
                    Buffer.BlockCopy(bytes, offset, _buffer, _length, take); _length += take; offset += take; count -= take;
                    if (_length == _buffer.Length) Flush();
                }
            }
            public override void Flush() { if (_length > 0) { _cancellation.ThrowIfCancellationRequested(); Chunk(_output, "IDAT", _buffer, _length); _length = 0; } }
            protected override void Dispose(bool disposing) { if (disposing) Flush(); base.Dispose(disposing); }
            public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
        }
    }
}
