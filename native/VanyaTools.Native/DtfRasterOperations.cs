using System;
using System.Threading;

namespace VanyaTools.Native
{
    // Point operations need no PNG, resampling, morphology or color conversion.
    // Diagnostic colors only affect detached display copies.
    internal static class DtfRasterOperations
    {
        internal static byte[] Correct(byte[] source, bool removeAlpha, int threshold, bool whiten, int tolerance,
            CancellationToken cancellation)
        {
            Validate(source, tolerance);
            if (threshold < 0 || threshold > 255) throw new ArgumentOutOfRangeException(nameof(threshold));
            cancellation.ThrowIfCancellationRequested();
            var result = (byte[])source.Clone(); int minimum = 255 - tolerance;
            for (int i = 0; i < result.Length; i += 4)
            {
                if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
                if (removeAlpha) result[i + 3] = result[i + 3] > 0 && result[i + 3] >= threshold ? (byte)255 : (byte)0;
                if (whiten && NearWhite(result, i, minimum)) result[i] = result[i + 1] = result[i + 2] = 255;
            }
            return result;
        }
        internal static byte[] Audit(byte[] source, bool semi, bool red, bool white, int tolerance,
            byte markerR, byte markerG, byte markerB, CancellationToken cancellation)
        {
            Validate(source, tolerance); cancellation.ThrowIfCancellationRequested();
            var result = (byte[])source.Clone(); int minimum = 255 - tolerance;
            for (int i = 0; i < result.Length; i += 4)
            {
                if ((i & 16383) == 0) cancellation.ThrowIfCancellationRequested();
                if (white && NearWhite(result, i, minimum))
                {
                    result[i] = Blend(result[i], markerB); result[i + 1] = Blend(result[i + 1], markerG); result[i + 2] = Blend(result[i + 2], markerR);
                }
                if (semi && result[i + 3] > 0 && result[i + 3] < 255)
                {
                    result[i] = 30; result[i + 1] = red ? (byte)35 : (byte)245;
                    result[i + 2] = red ? (byte)255 : (byte)20; result[i + 3] = 255;
                }
            }
            return result;
        }
        private static byte Blend(byte source, byte marker) => (byte)((source * 127 + marker * 128 + 127) / 255);
        private static bool NearWhite(byte[] pixels, int i, int minimum) => pixels[i + 3] != 0 &&
            pixels[i] >= minimum && pixels[i + 1] >= minimum && pixels[i + 2] >= minimum &&
            (pixels[i] != 255 || pixels[i + 1] != 255 || pixels[i + 2] != 255);
        private static void Validate(byte[] pixels, int tolerance)
        {
            if (pixels == null || pixels.Length == 0 || pixels.Length % 4 != 0) throw new ArgumentException("Invalid BGRA pixels.");
            if (tolerance < 0 || tolerance > 255) throw new ArgumentOutOfRangeException(nameof(tolerance));
        }
    }
}
