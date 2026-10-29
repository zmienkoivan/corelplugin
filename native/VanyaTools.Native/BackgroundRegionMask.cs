using System;
using System.Collections.Generic;
using System.Threading;

namespace VanyaTools.Native
{
    // Coordinates always refer to the full-size source, never to a scaled preview.
    internal struct BackgroundRemovalSeed
    {
        internal int X, Y;
        internal BackgroundRemovalSeed(int x, int y) { X = x; Y = y; }
    }

    internal static class BackgroundRegionMask
    {
        internal const int MaximumSeeds = 64;

        internal static bool TryMapPoint(double x, double y, double displayWidth, double displayHeight,
            int sourceWidth, int sourceHeight, out BackgroundRemovalSeed seed)
        {
            seed = default;
            if (Double.IsNaN(x) || Double.IsNaN(y) || Double.IsInfinity(x) || Double.IsInfinity(y) ||
                displayWidth <= 0 || displayHeight <= 0 || Double.IsNaN(displayWidth) || Double.IsNaN(displayHeight) ||
                Double.IsInfinity(displayWidth) || Double.IsInfinity(displayHeight) || sourceWidth <= 0 || sourceHeight <= 0 ||
                x < 0 || y < 0 || x >= displayWidth || y >= displayHeight) return false;
            seed = new BackgroundRemovalSeed(Math.Min(sourceWidth - 1, (int)(x * sourceWidth / displayWidth)),
                Math.Min(sourceHeight - 1, (int)(y * sourceHeight / displayHeight)));
            return true;
        }

        internal static byte[] Create(byte[] bgra, int width, int height, int[] borderColors,
            int tolerance, bool fromBorder, BackgroundRemovalSeed[] seeds, CancellationToken cancellation)
        {
            if (width <= 0 || height <= 0 || bgra == null || bgra.Length != checked(width * height * 4))
                throw new ArgumentException("Неверные размеры исходного изображения.");
            if (tolerance < 0 || tolerance > 80) throw new ArgumentOutOfRangeException(nameof(tolerance));
            if (fromBorder && (borderColors == null || borderColors.Length == 0))
                throw new ArgumentException("Не выбран цвет фона.");
            seeds = seeds ?? new BackgroundRemovalSeed[0];
            if (seeds.Length > MaximumSeeds) throw new ArgumentException("Слишком много выбранных областей.");
            var mask = new byte[checked(width * height)];
            var visited = new byte[mask.Length];
            var pending = new Stack<int>();
            if (fromBorder)
            {
                for (int x = 0; x < width; x++) { pending.Push(x); pending.Push((height - 1) * width + x); }
                for (int y = 1; y < height - 1; y++) { pending.Push(y * width); pending.Push(y * width + width - 1); }
                Fill(bgra, width, height, mask, visited, 1, pending, borderColors, tolerance, true, cancellation);
            }
            for (int n = 0; n < seeds.Length; n++)
            {
                cancellation.ThrowIfCancellationRequested();
                int x = seeds[n].X, y = seeds[n].Y;
                if (x < 0 || y < 0 || x >= width || y >= height)
                    throw new ArgumentOutOfRangeException(nameof(seeds), "Точка находится за пределами исходника.");
                int p = y * width + x, i = p * 4;
                // Transparent pixels do not bridge separate visible regions.
                if (bgra[i + 3] <= 8) continue;
                pending.Push(p);
                int color = (bgra[i + 2] << 16) | (bgra[i + 1] << 8) | bgra[i];
                Fill(bgra, width, height, mask, visited, (byte)(n + 2), pending,
                    new[] { color }, tolerance, false, cancellation);
            }
            return mask;
        }

        private static void Fill(byte[] pixels, int width, int height, byte[] mask, byte[] visited,
            byte stamp, Stack<int> pending, int[] colors, int tolerance, bool transparent,
            CancellationToken cancellation)
        {
            int steps = 0;
            while (pending.Count != 0)
            {
                if ((steps++ & 1023) == 0) cancellation.ThrowIfCancellationRequested();
                int seed = pending.Pop();
                if (visited[seed] == stamp || !Matches(pixels, seed, colors, tolerance, transparent)) continue;
                int y = seed / width, left = seed % width, right = left, row = y * width;
                while (left > 0 && visited[row + left - 1] != stamp && Matches(pixels, row + left - 1, colors, tolerance, transparent)) left--;
                while (right + 1 < width && visited[row + right + 1] != stamp && Matches(pixels, row + right + 1, colors, tolerance, transparent)) right++;
                for (int x = left; x <= right; x++) { visited[row + x] = stamp; mask[row + x] = 1; }
                if (y > 0) PushRuns(y - 1, left, right);
                if (y + 1 < height) PushRuns(y + 1, left, right);
            }

            void PushRuns(int y, int left, int right)
            {
                int x = left;
                while (x <= right)
                {
                    int p = y * width + x;
                    if (visited[p] == stamp || !Matches(pixels, p, colors, tolerance, transparent)) { x++; continue; }
                    pending.Push(p);
                    do { x++; p++; }
                    while (x <= right && visited[p] != stamp && Matches(pixels, p, colors, tolerance, transparent));
                }
            }
        }

        private static bool Matches(byte[] pixels, int p, int[] colors, int tolerance, bool transparent)
        {
            int i = p * 4;
            if (pixels[i + 3] <= 8) return transparent;
            foreach (int color in colors)
            {
                int red = (color >> 16) & 255, green = (color >> 8) & 255, blue = color & 255;
                int distance = Math.Max(Math.Abs(pixels[i] - blue),
                    Math.Max(Math.Abs(pixels[i + 1] - green), Math.Abs(pixels[i + 2] - red)));
                if (distance <= tolerance) return true;
                // Preserve the existing neutral endpoint tolerance for border removal.
                if (!transparent) continue;
                int min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                int max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                if (red >= 240 && green >= 240 && blue >= 240 && min >= 255 - tolerance && max - min <= 20) return true;
                if (red <= 24 && green <= 24 && blue <= 24 && max <= tolerance && max - min <= 20) return true;
            }
            return false;
        }
    }
}
