using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace VanyaTools.Native
{
    // A local companion to the RGB screen preview. Actual source-model samples
    // never pass through PNG or an RGB-to-CMYK conversion on their way to Corel.
    internal static class NativeRasterFiles
    {
        internal const string Suffix = ".native-raster";
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("VTRASTER2");
        internal const int MaximumPixels = 70000000;

        internal static void Write(string path, string preview, NativeRasterBuffer buffer, CancellationToken cancellation,
            CompressionLevel? compression = null)
        {
            cancellation.ThrowIfCancellationRequested();
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if ((long)buffer.Width * buffer.Height > MaximumPixels) throw new InvalidDataException("Исходный растр превышает допустимый размер.");
            byte[] previewHash = HashFile(preview, cancellation), rasterHash = HashRaster(buffer, cancellation);
            string pending = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path)), ".vnr-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = File.Create(pending))
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Magic); writer.Write(buffer.Width); writer.Write(buffer.Height); writer.Write(buffer.Mode);
                    writer.Write(buffer.DpiX); writer.Write(buffer.DpiY);
                    int[] order = buffer.CmykOrder;
                    writer.Write(order == null ? 0 : order.Length);
                    if (order != null) foreach (int channel in order) writer.Write(channel);
                    writer.Write(previewHash); writer.Write(rasterHash);
                    writer.Flush();
                    // Large working planes are short-lived. Stored DEFLATE blocks
                    // retain format compatibility while avoiding repeated compression.
                    var level = compression ?? ((long)buffer.Width * buffer.Height >= 8000000
                        ? CompressionLevel.NoCompression : CompressionLevel.Fastest);
                    using (var compressed = new DeflateStream(stream, level, true))
                    {
                        CopyBytes(compressed, buffer.Colors, cancellation);
                        CopyBytes(compressed, buffer.Alpha, cancellation);
                    }
                }
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(path)) File.Replace(pending, path, null);
                else File.Move(pending, path);
            }
            finally { try { if (File.Exists(pending)) File.Delete(pending); } catch { } }
        }

        internal static NativeRasterBuffer Read(string path, string preview, CancellationToken cancellation = default)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                if (stream.Length > (long)MaximumPixels * 5 + 1048576 || !Equal(reader.ReadBytes(Magic.Length), Magic))
                    throw new InvalidDataException("Неверный формат исходных каналов растра.");
                int width = reader.ReadInt32(), height = reader.ReadInt32(), mode = reader.ReadInt32();
                int channels = NativeRasterBuffer.ChannelCount(mode);
                if (width <= 0 || height <= 0 || (long)width * height > MaximumPixels)
                    throw new InvalidDataException("Некорректный размер исходных каналов растра.");
                double dpiX = reader.ReadDouble(), dpiY = reader.ReadDouble();
                if (Double.IsNaN(dpiX) || Double.IsInfinity(dpiX) || dpiX <= 0 ||
                    Double.IsNaN(dpiY) || Double.IsInfinity(dpiY) || dpiY <= 0)
                    throw new InvalidDataException("Некорректное разрешение исходного растра.");
                int count = reader.ReadInt32();
                if (count != 0 && count != 4) throw new InvalidDataException("Неверный порядок цветовых каналов.");
                int[] order = count == 0 ? null : new int[count];
                if (order != null) for (int index = 0; index < count; index++) order[index] = reader.ReadInt32();
                if (order != null)
                {
                    int used = 0;
                    foreach (int channel in order)
                    {
                        if (mode != 5 || channel < 0 || channel > 3 || (used & (1 << channel)) != 0)
                            throw new InvalidDataException("Неверный порядок цветовых каналов.");
                        used |= 1 << channel;
                    }
                }
                byte[] previewHash = ReadHash(reader), expectedRasterHash = ReadHash(reader);
                if (!Equal(previewHash, HashFile(preview, cancellation)))
                    throw new InvalidDataException("Предпросмотр и исходные цветовые каналы не соответствуют друг другу. Повторите захват.");
                int pixels = checked(width * height);
                var colors = new byte[checked(pixels * channels)]; var alpha = new byte[pixels];
                using (var compressed = new DeflateStream(stream, CompressionMode.Decompress, true))
                {
                    ReadBytes(compressed, colors, cancellation); ReadBytes(compressed, alpha, cancellation);
                    if (compressed.ReadByte() != -1) throw new InvalidDataException("Лишние данные исходного растра.");
                }
                var buffer = new NativeRasterBuffer(width, height, mode, dpiX, dpiY, colors, alpha, order);
                if (!Equal(expectedRasterHash, HashRaster(buffer, cancellation)))
                    throw new InvalidDataException("Исходные цветовые каналы повреждены. Повторите захват растра.");
                return buffer;
            }
        }

        private static byte[] HashFile(string path, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            using (var file = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                var block = new byte[1048576];
                int count;
                while ((count = file.Read(block, 0, block.Length)) != 0)
                { cancellation.ThrowIfCancellationRequested(); sha.TransformBlock(block, 0, count, block, 0); }
                cancellation.ThrowIfCancellationRequested(); sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                return sha.Hash;
            }
        }
        private static byte[] HashRaster(NativeRasterBuffer buffer, CancellationToken cancellation)
        {
            using (var header = new MemoryStream())
            using (var writer = new BinaryWriter(header, Encoding.UTF8, true))
            using (var sha = SHA256.Create())
            {
                writer.Write(buffer.Width); writer.Write(buffer.Height); writer.Write(buffer.Mode);
                writer.Write(buffer.DpiX); writer.Write(buffer.DpiY);
                writer.Write(buffer.CmykOrder == null ? 0 : buffer.CmykOrder.Length);
                if (buffer.CmykOrder != null) foreach (int channel in buffer.CmykOrder) writer.Write(channel);
                writer.Flush(); HashBytes(sha, header.ToArray(), cancellation);
                HashBytes(sha, buffer.Colors, cancellation); HashBytes(sha, buffer.Alpha, cancellation);
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return sha.Hash;
            }
        }
        private static void HashBytes(HashAlgorithm sha, byte[] bytes, CancellationToken cancellation)
        {
            for (int start = 0; start < bytes.Length; start += 1048576)
            {
                cancellation.ThrowIfCancellationRequested(); int count = Math.Min(1048576, bytes.Length - start);
                sha.TransformBlock(bytes, start, count, bytes, start);
            }
        }
        private static byte[] ReadHash(BinaryReader reader)
        {
            byte[] value = reader.ReadBytes(32);
            if (value.Length != 32) throw new EndOfStreamException("Неполные данные проверки исходного растра.");
            return value;
        }
        private static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int index = 0; index < a.Length; index++) if (a[index] != b[index]) return false;
            return true;
        }
        private static void CopyBytes(Stream stream, byte[] bytes, CancellationToken cancellation)
        {
            for (int start = 0; start < bytes.Length; start += 1048576)
            { cancellation.ThrowIfCancellationRequested(); stream.Write(bytes, start, Math.Min(1048576, bytes.Length - start)); }
        }
        private static void ReadBytes(Stream stream, byte[] bytes, CancellationToken cancellation)
        {
            for (int start = 0; start < bytes.Length;)
            {
                cancellation.ThrowIfCancellationRequested();
                int count = stream.Read(bytes, start, Math.Min(1048576, bytes.Length - start));
                if (count == 0) throw new EndOfStreamException("Исходные цветовые каналы растра прочитаны не полностью.");
                start += count;
            }
        }
    }
}
