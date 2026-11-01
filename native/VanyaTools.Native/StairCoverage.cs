using System;
using System.Threading;

namespace VanyaTools.Native
{
    internal static class StairCoverage
    {
        internal static void Blend(byte[] blurred, byte[] original, int width, int height, bool refineBinary, CancellationToken cancellation)
        {
            if (blurred.Length != checked(width * height) || original.Length != blurred.Length) throw new ArgumentException("Размер маски сглаживания.");
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int p = y * width + x;
                    // A 2D corner can use the smoothed contour directly. Blending
                    // 35% of the old staircase back there defeated binary x2.
                    // Keep the old protection for isolated dots and 1px strokes.
                    bool horizontal = x > 0 && original[p - 1] != 0 || x + 1 < width && original[p + 1] != 0;
                    bool vertical = y > 0 && original[p - width] != 0 || y + 1 < height && original[p + width] != 0;
                    int keep = !refineBinary ? 35 : horizontal && vertical ? 0 : original[p] != 0 ? 100 : 35;
                    blurred[p] = (byte)((blurred[p] * (100 - keep) + original[p] * keep + 50) / 100);
                }
            }
        }
    }
}
