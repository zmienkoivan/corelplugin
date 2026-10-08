using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class BackgroundRemovalProcessor
    {
        // Only whites connected to the image border are background. Enclosed white
        // lettering and highlights remain part of the artwork.
        public static void RemoveWhite(string source, string output, int threshold, int cutPixels,
            CancellationToken cancellation)
        {
            BitmapSource input = AiPrintTab.Bitmap(source);
            var bitmap = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            int length = checked(width * height), stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            var background = new byte[length];
            bitmap.CopyPixels(pixels, stride, 0);

            var pending = new Stack<int>(Math.Min(checked(2 * (width + height)), 100000));
            for (int x = 0; x < width; x++)
            {
                pending.Push(x);
                if (height > 1) pending.Push((height - 1) * width + x);
            }
            for (int y = 1; y < height - 1; y++)
            {
                pending.Push(y * width);
                if (width > 1) pending.Push(y * width + width - 1);
            }

            int removed = 0, steps = 0;
            while (pending.Count > 0)
            {
                if ((steps++ & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int seed = pending.Pop();
                if (background[seed] != 0 || !IsWhite(pixels, seed, threshold)) continue;
                int y = seed / width, left = seed % width, right = left;
                while (left > 0 && background[y * width + left - 1] == 0 &&
                    IsWhite(pixels, y * width + left - 1, threshold)) left--;
                while (right + 1 < width && background[y * width + right + 1] == 0 &&
                    IsWhite(pixels, y * width + right + 1, threshold)) right++;
                for (int x = left; x <= right; x++)
                {
                    background[y * width + x] = 1;
                    removed++;
                }
                if (y > 0) PushRuns(y - 1, left, right, width, threshold,
                    pixels, background, pending);
                if (y + 1 < height) PushRuns(y + 1, left, right, width, threshold,
                    pixels, background, pending);
            }
            if (removed == 0)
                throw new InvalidOperationException("Белый фон не найден у края изображения. Уменьшите порог или используйте AI.");
            if (cutPixels > 0)
                background = ExpandBackground(background, width, height, cutPixels, cancellation);

            int softStart = Math.Max(0, threshold - 20);
            int visible = 0;
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int p = y * width + x, i = p * 4;
                    if (background[p] != 0)
                    {
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                        continue;
                    }
                    if (pixels[i + 3] == 0) continue;
                    visible++;
                    // Modify only a one-pixel fringe next to the removed white.
                    // All other RGB values and alpha remain exactly as captured.
                    if (pixels[i + 3] < 250 || !TouchesBackground(background, width, height, x, y)) continue;
                    int min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
                    int max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
                    if (min < softStart || min >= threshold || max - min > 40) continue;
                    double alpha = EstimateEdgeAlpha(pixels, background, width, height,
                        x, y, softStart, min);
                    if (alpha >= 0.98) continue;
                    if (alpha <= 0.01)
                    {
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                        continue;
                    }
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double foreground = (pixels[i + channel] - 255.0 * (1.0 - alpha)) / alpha;
                        pixels[i + channel] = (byte)Math.Max(0, Math.Min(255, Math.Round(foreground)));
                    }
                    pixels[i + 3] = (byte)Math.Max(1, Math.Min(255, Math.Round(alpha * 255)));
                }
            }
            if (visible == 0)
                throw new InvalidOperationException("На изображении не осталось непрозрачных деталей. Увеличьте порог.");
            Save(output, width, height, input.DpiX, input.DpiY, pixels, stride);
        }

        // Move only the boundary connected to the removed background inward.
        // A disk distance keeps diagonal edges from being cut as a square block.
        private static byte[] ExpandBackground(byte[] background, int width, int height,
            int radius, CancellationToken cancellation)
        {
            int length = checked(width * height);
            var horizontalDistance = new byte[length];
            var expanded = new byte[length];
            byte far = (byte)(radius + 1);
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width, last = -radius - 1;
                for (int x = 0; x < width; x++)
                {
                    if (background[row + x] != 0) last = x;
                    horizontalDistance[row + x] = (byte)Math.Min(far, x - last);
                }
                last = width + radius;
                for (int x = width - 1; x >= 0; x--)
                {
                    if (background[row + x] != 0) last = x;
                    int distance = last - x;
                    if (distance < horizontalDistance[row + x])
                        horizontalDistance[row + x] = (byte)distance;
                }
            }
            int radiusSquared = radius * radius;
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int p = row + x;
                    if (background[p] != 0) { expanded[p] = 1; continue; }
                    for (int dy = -radius; dy <= radius; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= height) continue;
                        int dx = horizontalDistance[ny * width + x];
                        if (dx * dx + dy * dy > radiusSquared) continue;
                        expanded[p] = 1;
                        break;
                    }
                }
            }
            return expanded;
        }

        // Bria may return a much smaller PNG. Use its alpha as a mask and keep
        // full-size color pixels from the original Corel capture.
        public static void ApplyModelMask(string source, string modelOutput, string output,
            CancellationToken cancellation)
        {
            BitmapSource original = AiPrintTab.Bitmap(source);
            BitmapSource model = AiPrintTab.Bitmap(modelOutput);
            int width = original.PixelWidth, height = original.PixelHeight;
            int maskWidth = model.PixelWidth, maskHeight = model.PixelHeight;
            double originalRatio = (double)width / height;
            double maskRatio = (double)maskWidth / maskHeight;
            if (Math.Abs(originalRatio - maskRatio) / originalRatio > 0.02)
                throw new InvalidOperationException("Модель изменила пропорции изображения; маску нельзя совместить с исходником.");

            var originalBgra = new FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
            var modelBgra = new FormatConvertedBitmap(model, PixelFormats.Bgra32, null, 0);
            int stride = checked(width * 4), maskStride = checked(maskWidth * 4);
            var pixels = new byte[checked(stride * height)];
            var mask = new byte[checked(maskStride * maskHeight)];
            originalBgra.CopyPixels(pixels, stride, 0);
            modelBgra.CopyPixels(mask, maskStride, 0);
            bool hasTransparency = false;
            for (int i = 3; i < mask.Length; i += 4)
                if (mask[i] < 250) { hasTransparency = true; break; }
            if (!hasTransparency)
                throw new InvalidOperationException("Модель не вернула прозрачную маску фона.");

            int minX = width, minY = height, maxX = -1, maxY = -1;
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                double sy = (y + 0.5) * maskHeight / height - 0.5;
                int y0 = Math.Max(0, Math.Min(maskHeight - 1, (int)Math.Floor(sy)));
                int y1 = Math.Min(maskHeight - 1, y0 + 1);
                double fy = Math.Max(0, Math.Min(1, sy - y0));
                for (int x = 0; x < width; x++)
                {
                    double sx = (x + 0.5) * maskWidth / width - 0.5;
                    int x0 = Math.Max(0, Math.Min(maskWidth - 1, (int)Math.Floor(sx)));
                    int x1 = Math.Min(maskWidth - 1, x0 + 1);
                    double fx = Math.Max(0, Math.Min(1, sx - x0));
                    int a00 = mask[(y0 * maskWidth + x0) * 4 + 3];
                    int a10 = mask[(y0 * maskWidth + x1) * 4 + 3];
                    int a01 = mask[(y1 * maskWidth + x0) * 4 + 3];
                    int a11 = mask[(y1 * maskWidth + x1) * 4 + 3];
                    double top = a00 + (a10 - a00) * fx;
                    double bottom = a01 + (a11 - a01) * fx;
                    int i = (y * width + x) * 4;
                    int alpha = (int)Math.Round(pixels[i + 3] *
                        (top + (bottom - top) * fy) / 255.0);
                    pixels[i + 3] = (byte)Math.Max(0, Math.Min(255, alpha));
                    if (alpha == 0) pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
                    if (alpha > 10)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < 0)
                throw new InvalidOperationException("Модель удалила всё изображение. Результат не вставлен.");
            const int padding = 2;
            int left = Math.Max(0, minX - padding), topEdge = Math.Max(0, minY - padding);
            int right = Math.Min(width - 1, maxX + padding);
            int bottomEdge = Math.Min(height - 1, maxY + padding);
            BitmapSource bitmap = BitmapSource.Create(width, height, original.DpiX, original.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
            BitmapSource result = left == 0 && topEdge == 0 && right == width - 1 && bottomEdge == height - 1
                ? bitmap : new CroppedBitmap(bitmap,
                    new System.Windows.Int32Rect(left, topEdge, right - left + 1, bottomEdge - topEdge + 1));
            cancellation.ThrowIfCancellationRequested();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(output)) encoder.Save(stream);
            Log.Info("Background mask cropped: " + width + "x" + height + " -> " +
                result.PixelWidth + "x" + result.PixelHeight + ".");
        }

        private static bool IsWhite(byte[] pixels, int p, int threshold)
        {
            int i = p * 4;
            if (pixels[i + 3] <= 8) return true;
            int min = Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2]));
            int max = Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2]));
            return min >= threshold && max - min <= 20;
        }

        private static void PushRuns(int y, int left, int right, int width, int threshold,
            byte[] pixels, byte[] background, Stack<int> pending)
        {
            int x = left;
            while (x <= right)
            {
                int p = y * width + x;
                if (background[p] != 0 || !IsWhite(pixels, p, threshold)) { x++; continue; }
                pending.Push(p);
                do { x++; p++; }
                while (x <= right && background[p] == 0 && IsWhite(pixels, p, threshold));
            }
        }

        private static bool TouchesBackground(byte[] background, int width, int height, int x, int y)
        {
            for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                    background[ny * width + nx] != 0) return true;
            }
            return false;
        }

        private static double EstimateEdgeAlpha(byte[] pixels, byte[] background,
            int width, int height, int x, int y, int softStart, int min)
        {
            int i = (y * width + x) * 4;
            double bestError = Double.MaxValue, bestAlpha = Double.NaN;
            for (int dy = -2; dy <= 2; dy++)
            for (int dx = -2; dx <= 2; dx++)
            {
                int nx = x + dx, ny = y + dy;
                if (nx < 0 || nx >= width || ny < 0 || ny >= height || (dx == 0 && dy == 0)) continue;
                int p = ny * width + nx, n = p * 4;
                if (background[p] != 0 || pixels[n + 3] < 250) continue;
                int neighborMin = Math.Min(pixels[n], Math.Min(pixels[n + 1], pixels[n + 2]));
                if (neighborMin >= softStart) continue;
                double numerator = 0, denominator = 0;
                for (int c = 0; c < 3; c++)
                {
                    double fromWhite = 255 - pixels[n + c];
                    numerator += (255 - pixels[i + c]) * fromWhite;
                    denominator += fromWhite * fromWhite;
                }
                if (denominator < 1) continue;
                double alpha = Math.Max(0, Math.Min(1, numerator / denominator));
                double error = 0;
                for (int c = 0; c < 3; c++)
                {
                    double difference = 255 - alpha * (255 - pixels[n + c]) - pixels[i + c];
                    error += difference * difference;
                }
                if (error < bestError) { bestError = error; bestAlpha = alpha; }
            }
            return Double.IsNaN(bestAlpha)
                ? Math.Max(0, Math.Min(1, (255.0 - min) / (255.0 - softStart)))
                : bestAlpha;
        }

        private static void Save(string path, int width, int height, double dpiX, double dpiY,
            byte[] pixels, int stride)
        {
            BitmapSource result = BitmapSource.Create(width, height, dpiX, dpiY,
                PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(path)) encoder.Save(stream);
        }
    }
}
