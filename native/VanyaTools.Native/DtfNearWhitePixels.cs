using System;
using System.Collections;
using System.Threading;

namespace VanyaTools.Native
{
    // These are straight BGRA pixels. Diagnosis and correction always return
    // independent buffers; an overlay must never become the exported artwork.
    internal static class DtfNearWhitePixels
    {
        internal const int DefaultTolerance = 6;
        internal const byte DefaultRed = 255, DefaultGreen = 0, DefaultBlue = 168, DefaultOpacity = 128;

        internal sealed class Mask
        {
            private readonly BitArray _marked;
            internal int Width { get; }
            internal int Height { get; }
            internal int Count { get; }
            internal bool IsMarked(int index) => _marked[index];

            internal Mask(int width, int height, BitArray marked, int count)
            { Width = width; Height = height; _marked = marked; Count = count; }
        }

        internal static Mask CreateMask(byte[] bgra, int width, int height, int tolerance,
            CancellationToken cancellation = default(CancellationToken))
        {
            int count = Validate(bgra, width, height);
            if (tolerance < 0 || tolerance > 255) throw new ArgumentOutOfRangeException(nameof(tolerance));
            cancellation.ThrowIfCancellationRequested();
            // A full-resolution 70 MP capture needs under 9 MB for this mask,
            // rather than another full byte per pixel.
            var marked = new BitArray(count);
            int matches = 0, minimum = 255 - tolerance;
            for (int index = 0, offset = 0; index < count; index++, offset += 4)
            {
                if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (bgra[offset + 3] == 0 ||
                    (bgra[offset] == 255 && bgra[offset + 1] == 255 && bgra[offset + 2] == 255)) continue;
                if (bgra[offset] < minimum || bgra[offset + 1] < minimum || bgra[offset + 2] < minimum) continue;
                marked[index] = true; matches++;
            }
            return new Mask(width, height, marked, matches);
        }

        internal static byte[] Whiten(byte[] bgra, Mask mask,
            CancellationToken cancellation = default(CancellationToken))
        {
            int count = Validate(bgra, mask);
            cancellation.ThrowIfCancellationRequested();
            var result = (byte[])bgra.Clone();
            for (int index = 0, offset = 0; index < count; index++, offset += 4)
            {
                if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (!mask.IsMarked(index)) continue;
                result[offset] = result[offset + 1] = result[offset + 2] = 255;
            }
            return result;
        }

        internal static byte[] Highlight(byte[] bgra, Mask mask, byte red = DefaultRed,
            byte green = DefaultGreen, byte blue = DefaultBlue, byte opacity = DefaultOpacity,
            CancellationToken cancellation = default(CancellationToken))
        {
            int count = Validate(bgra, mask);
            cancellation.ThrowIfCancellationRequested();
            var result = (byte[])bgra.Clone();
            for (int index = 0, offset = 0; index < count; index++, offset += 4)
            {
                if ((index & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (!mask.IsMarked(index)) continue;
                result[offset] = Blend(result[offset], blue, opacity);
                result[offset + 1] = Blend(result[offset + 1], green, opacity);
                result[offset + 2] = Blend(result[offset + 2], red, opacity);
            }
            return result;
        }

        private static byte Blend(byte source, byte marker, byte opacity) =>
            (byte)((source * (255 - opacity) + marker * opacity + 127) / 255);

        private static int Validate(byte[] bgra, Mask mask)
        {
            if (mask == null) throw new ArgumentNullException(nameof(mask));
            return Validate(bgra, mask.Width, mask.Height);
        }

        private static int Validate(byte[] bgra, int width, int height)
        {
            if (bgra == null) throw new ArgumentNullException(nameof(bgra));
            if (width <= 0 || height <= 0 || (long)width * height > Int32.MaxValue / 4)
                throw new ArgumentOutOfRangeException(nameof(width), "Invalid BGRA image dimensions.");
            int count = checked(width * height);
            if (bgra.Length != checked(count * 4)) throw new ArgumentException("BGRA buffer does not match image dimensions.", nameof(bgra));
            return count;
        }
    }
}
