using System;
using System.Threading;

namespace VanyaTools.Native
{
    internal sealed class NativeRasterFilterOptions
    {
        internal bool RemoveSemiTransparent, SmoothStairs, Outline, WhitenNearWhite;
        internal int Threshold = 130, StairsScale = 1, NearWhiteTolerance = 6;
        internal int MaximumOutputPixels = 70000000;
        internal double RadiusMm = 2 * 25.4 / 400, ExpansionMm = .175;
        internal byte[] OutlineColor;
        internal byte OutlineOpacity = 255;
        // Pure managed decoder supplied for CMYK. Never call Corel from here.
        internal Func<NativeRasterBuffer, byte[]> NearWhitePreviewBgra = null;
    }

    internal sealed class NativeRasterFilterResult
    {
        internal NativeRasterBuffer Buffer;
        internal Tuple<double, double> OffsetMm = Tuple.Create(0.0, 0.0);
    }

    // Color planes stay in their original model and channel order. Only alpha,
    // requested interpolation, whitening and new outline pixels are changed.
    internal static class NativeRasterFilters
    {
        internal static NativeRasterFilterResult Process(NativeRasterBuffer source,
            NativeRasterFilterOptions options, CancellationToken cancellation = default(CancellationToken))
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Threshold < 0 || options.Threshold > 255 || options.NearWhiteTolerance < 0 || options.NearWhiteTolerance > 255 ||
                options.MaximumOutputPixels <= 0) throw new ArgumentOutOfRangeException(nameof(options));
            cancellation.ThrowIfCancellationRequested();
            NativeRasterBuffer current = source.Copy();
            double offsetX = 0, offsetY = 0;
            if (options.RemoveSemiTransparent) ThresholdInPlace(current, options.Threshold, cancellation);
            if (options.SmoothStairs)
            {
                current = Smooth(current, options.Threshold, options.StairsScale, Math.Min(32000000, options.MaximumOutputPixels),
                    !options.RemoveSemiTransparent, cancellation);
                current = Trim(current, 2, out int left, out int top, cancellation);
                offsetX -= left * 25.4 / current.DpiX; offsetY -= top * 25.4 / current.DpiY;
            }
            if (options.Outline)
            {
                int radiusX = Pixels(options.RadiusMm, current.DpiX, 12), radiusY = Pixels(options.RadiusMm, current.DpiY, 12);
                int expansionX = Pixels(options.ExpansionMm, current.DpiX, 100), expansionY = Pixels(options.ExpansionMm, current.DpiY, 100);
                int paddingX = checked(expansionX + radiusX + 2), paddingY = checked(expansionY + radiusY + 2);
                current = Outline(current, paddingX, paddingY, radiusX, radiusY, expansionX, expansionY,
                    options.OutlineColor, options.OutlineOpacity, options.MaximumOutputPixels, cancellation);
                current = Trim(current, 2, out int left, out int top, cancellation);
                offsetX += (paddingX - left) * 25.4 / current.DpiX;
                offsetY += (paddingY - top) * 25.4 / current.DpiY;
            }
            if (options.RemoveSemiTransparent && options.Outline) ThresholdInPlace(current, options.Threshold, cancellation);
            if (options.WhitenNearWhite)
            {
                byte[] preview = options.NearWhitePreviewBgra == null ? current.ToPreviewBgra(cancellation) : options.NearWhitePreviewBgra(current);
                var mask = DtfNearWhitePixels.CreateMask(preview, current.Width, current.Height, options.NearWhiteTolerance, cancellation);
                WhitenInPlace(current, mask, cancellation);
            }
            cancellation.ThrowIfCancellationRequested();
            return new NativeRasterFilterResult { Buffer = current, OffsetMm = Tuple.Create(offsetX, offsetY) };
        }

        internal static NativeRasterBuffer Threshold(NativeRasterBuffer source, int threshold,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (threshold < 0 || threshold > 255) throw new ArgumentOutOfRangeException(nameof(threshold));
            cancellation.ThrowIfCancellationRequested();
            NativeRasterBuffer result = source.Copy(); ThresholdInPlace(result, threshold, cancellation); return result;
        }

        internal static NativeRasterBuffer Whiten(NativeRasterBuffer source, DtfNearWhitePixels.Mask mask,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            cancellation.ThrowIfCancellationRequested();
            NativeRasterBuffer result = source.Copy(); WhitenInPlace(result, mask, cancellation); return result;
        }

        private static void ThresholdInPlace(NativeRasterBuffer buffer, int threshold, CancellationToken cancellation)
        {
            for (int pixel = 0; pixel < buffer.Alpha.Length; pixel++)
            {
                if ((pixel & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                buffer.Alpha[pixel] = buffer.Alpha[pixel] > 0 && buffer.Alpha[pixel] >= threshold ? (byte)255 : (byte)0;
            }
        }

        private static void WhitenInPlace(NativeRasterBuffer buffer, DtfNearWhitePixels.Mask mask, CancellationToken cancellation)
        {
            if (mask == null || mask.Width != buffer.Width || mask.Height != buffer.Height)
                throw new ArgumentException("Маска почти белого не совпадает с исходным растром.", nameof(mask));
            for (int pixel = 0; pixel < buffer.Alpha.Length; pixel++)
            {
                if ((pixel & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (buffer.Alpha[pixel] != 0 && mask.IsMarked(pixel)) buffer.SetPureWhite(pixel);
            }
        }

        private static NativeRasterBuffer Smooth(NativeRasterBuffer source, int threshold, int fixedScale,
            int maximumPixels, bool preserveAlpha, CancellationToken cancellation)
        {
            int width = source.Width, height = source.Height, count = source.Alpha.Length, channels = source.Channels;
            int scale = count <= 2000000 ? 4 : count <= 3500000 ? 3 : 2;
            if (fixedScale > 0) scale = Math.Max(1, Math.Min(4, fixedScale));
            scale = Math.Min(scale, Math.Max(1, (int)Math.Sqrt((double)maximumPixels / count)));
            int outWidth = checked(width * scale), outHeight = checked(height * scale);
            int outCount = PixelCount(outWidth, outHeight, channels);
            byte[] mask = new byte[count], originalAlpha = new byte[count];
            for (int pixel = 0; pixel < count; pixel++)
            {
                if ((pixel & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                mask[pixel] = preserveAlpha ? source.Alpha[pixel] :
                    source.Alpha[pixel] > 0 && source.Alpha[pixel] >= threshold ? (byte)255 : (byte)0;
            }
            Gaussian(mask, originalAlpha, width, height, 1, false, cancellation);
            Gaussian(originalAlpha, mask, width, height, 1, true, cancellation);
            for (int pixel = 0; pixel < count; pixel++)
            {
                int original = preserveAlpha ? source.Alpha[pixel] : source.Alpha[pixel] > 0 && source.Alpha[pixel] >= threshold ? 255 : 0;
                originalAlpha[pixel] = (byte)original;
                mask[pixel] = (byte)((mask[pixel] * 65 + original * 35 + 50) / 100);
            }
            byte[] colors = new byte[checked(outCount * channels)], alpha = new byte[outCount];
            double scaleX = (double)outWidth / width, scaleY = (double)outHeight / height;
            for (int y = 0; y < outHeight; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                double sy = (y + .5) / scaleY - .5;
                int y0 = Math.Max(0, Math.Min(height - 1, (int)Math.Floor(sy))), y1 = Math.Min(height - 1, y0 + 1);
                double fy = Math.Max(0, Math.Min(1, sy - y0));
                for (int x = 0; x < outWidth; x++)
                {
                    double sx = (x + .5) / scaleX - .5;
                    int x0 = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(sx))), x1 = Math.Min(width - 1, x0 + 1);
                    double fx = Math.Max(0, Math.Min(1, sx - x0));
                    int p00 = y0 * width + x0, p10 = y0 * width + x1, p01 = y1 * width + x0, p11 = y1 * width + x1;
                    double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;
                    double coverage = mask[p00] * w00 + mask[p10] * w10 + mask[p01] * w01 + mask[p11] * w11;
                    int destination = y * outWidth + x;
                    alpha[destination] = preserveAlpha ? Byte(coverage) : coverage >= 128 ? (byte)255 : (byte)0;
                    double c00 = preserveAlpha ? originalAlpha[p00] * w00 / 255.0 : originalAlpha[p00] != 0 ? w00 : 0;
                    double c10 = preserveAlpha ? originalAlpha[p10] * w10 / 255.0 : originalAlpha[p10] != 0 ? w10 : 0;
                    double c01 = preserveAlpha ? originalAlpha[p01] * w01 / 255.0 : originalAlpha[p01] != 0 ? w01 : 0;
                    double c11 = preserveAlpha ? originalAlpha[p11] * w11 / 255.0 : originalAlpha[p11] != 0 ? w11 : 0;
                    double total = c00 + c10 + c01 + c11;
                    int fallback = total == 0 && preserveAlpha && alpha[destination] > 0
                        ? NearestVisible(source.Alpha, width, height, x0, y0) : p00;
                    if (fallback < 0) { alpha[destination] = 0; fallback = p00; }
                    for (int channel = 0; channel < channels; channel++)
                    {
                        double color = alpha[destination] != 0 && total > 0 ?
                            (source.Colors[p00 * channels + channel] * c00 + source.Colors[p10 * channels + channel] * c10 +
                             source.Colors[p01 * channels + channel] * c01 + source.Colors[p11 * channels + channel] * c11) / total :
                            source.Colors[fallback * channels + channel];
                        colors[destination * channels + channel] = Byte(color);
                    }
                }
            }
            return new NativeRasterBuffer(outWidth, outHeight, source.Mode, source.DpiX * scaleX, source.DpiY * scaleY, colors, alpha, source.CmykOrder);
        }

        private static int NearestVisible(byte[] alpha, int width, int height, int x, int y)
        {
            int found = -1, distance = Int32.MaxValue;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    int sx = x + dx, sy = y + dy;
                    if (sx < 0 || sy < 0 || sx >= width || sy >= height) continue;
                    int candidate = sy * width + sx, square = dx * dx + dy * dy;
                    if (alpha[candidate] == 0 || square >= distance) continue;
                    found = candidate; distance = square;
                }
            return found;
        }

        private static NativeRasterBuffer Outline(NativeRasterBuffer source, int paddingX, int paddingY,
            int radiusX, int radiusY, int expansionX, int expansionY, byte[] fill, byte opacity, int maximumPixels, CancellationToken cancellation)
        {
            if (fill != null && fill.Length != source.Channels) throw new ArgumentException("Цвет обводки не соответствует модели растра.", nameof(fill));
            if (fill == null)
            {
                fill = new byte[source.Channels];
                if (source.Mode != 5) for (int channel = 0; channel < fill.Length; channel++) fill[channel] = 255;
            }
            int width = checked(source.Width + 2 * paddingX), height = checked(source.Height + 2 * paddingY);
            int count = PixelCount(width, height, source.Channels), channels = source.Channels;
            if (count > maximumPixels) throw new InvalidOperationException("Результат обводки превышает допустимый размер растра.");
            byte[] colors = new byte[checked(count * channels)], alpha = new byte[count];
            for (int y = 0; y < source.Height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                Buffer.BlockCopy(source.Colors, y * source.Width * channels, colors, ((y + paddingY) * width + paddingX) * channels, source.Width * channels);
                Buffer.BlockCopy(source.Alpha, y * source.Width, alpha, (y + paddingY) * width + paddingX, source.Width);
            }
            byte[] mask = (byte[])alpha.Clone(), work = new byte[count];
            if (expansionX > 0 || expansionY > 0)
            {
                MaxAlpha(mask, work, width, height, expansionX, false, cancellation);
                MaxAlpha(work, mask, width, height, expansionY, true, cancellation);
            }
            if (radiusX > 0 || radiusY > 0)
            {
                Gaussian(mask, work, width, height, radiusX, false, cancellation);
                Gaussian(work, mask, width, height, radiusY, true, cancellation);
            }
            for (int pixel = 0; pixel < count; pixel++)
            {
                if ((pixel & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                if (alpha[pixel] != 0) continue;
                byte added = (byte)((mask[pixel] * opacity + 127) / 255);
                if (added == 0) continue;
                alpha[pixel] = added;
                for (int channel = 0; channel < channels; channel++) colors[pixel * channels + channel] = fill[channel];
            }
            return new NativeRasterBuffer(width, height, source.Mode, source.DpiX, source.DpiY, colors, alpha, source.CmykOrder);
        }

        internal static NativeRasterBuffer Trim(NativeRasterBuffer source, int padding, out int left, out int top,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (padding < 0) throw new ArgumentOutOfRangeException(nameof(padding));
            int minX = source.Width, minY = source.Height, maxX = -1, maxY = -1;
            for (int y = 0; y < source.Height; y++)
            {
                if ((y & 127) == 0) cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < source.Width; x++)
                    if (source.Alpha[y * source.Width + x] != 0)
                    { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
            }
            left = top = 0;
            if (maxX < 0) return source;
            left = Math.Max(0, minX - padding); top = Math.Max(0, minY - padding);
            int right = Math.Min(source.Width - 1, maxX + padding), bottom = Math.Min(source.Height - 1, maxY + padding);
            int width = right - left + 1, height = bottom - top + 1;
            if (width == source.Width && height == source.Height) return source;
            byte[] colors = new byte[checked(width * height * source.Channels)], alpha = new byte[checked(width * height)];
            for (int y = 0; y < height; y++)
            {
                if ((y & 127) == 0) cancellation.ThrowIfCancellationRequested();
                Buffer.BlockCopy(source.Colors, ((top + y) * source.Width + left) * source.Channels, colors, y * width * source.Channels, width * source.Channels);
                Buffer.BlockCopy(source.Alpha, (top + y) * source.Width + left, alpha, y * width, width);
            }
            return new NativeRasterBuffer(width, height, source.Mode, source.DpiX, source.DpiY, colors, alpha, source.CmykOrder);
        }

        private static void MaxAlpha(byte[] source, byte[] target, int width, int height, int radius,
            bool vertical, CancellationToken cancellation)
        {
            int lines = vertical ? width : height, samples = vertical ? height : width;
            var deque = new int[samples];
            for (int line = 0; line < lines; line++)
            {
                if ((line & 63) == 0) cancellation.ThrowIfCancellationRequested();
                int head = 0, tail = 0, next = 0;
                for (int point = 0; point < samples; point++)
                {
                    int right = Math.Min(samples - 1, point + radius);
                    while (next <= right)
                    {
                        int index = vertical ? next * width + line : line * width + next;
                        while (tail > head && source[vertical ? deque[tail - 1] * width + line : line * width + deque[tail - 1]] <= source[index]) tail--;
                        deque[tail++] = next++;
                    }
                    while (tail > head && deque[head] < point - radius) head++;
                    target[vertical ? point * width + line : line * width + point] = source[vertical ? deque[head] * width + line : line * width + deque[head]];
                }
            }
        }

        private static void Gaussian(byte[] source, byte[] target, int width, int height, int radius,
            bool vertical, CancellationToken cancellation)
        {
            if (radius == 0) { Buffer.BlockCopy(source, 0, target, 0, source.Length); return; }
            var weights = new double[radius * 2 + 1];
            double sigma = Math.Max(.7, radius / 1.5), sum = 0;
            for (int k = -radius; k <= radius; k++) { double weight = Math.Exp(-(k * k) / (2 * sigma * sigma)); weights[k + radius] = weight; sum += weight; }
            for (int index = 0; index < weights.Length; index++) weights[index] /= sum;
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    double value = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sx = vertical ? x : x + k, sy = vertical ? y + k : y;
                        if (sx >= 0 && sy >= 0 && sx < width && sy < height) value += source[sy * width + sx] * weights[k + radius];
                    }
                    target[y * width + x] = Byte(value);
                }
            }
        }

        private static int Pixels(double millimeters, double dpi, int maximum)
        {
            if (Double.IsNaN(millimeters) || Double.IsInfinity(millimeters) || millimeters < 0)
                throw new ArgumentOutOfRangeException(nameof(millimeters));
            double value = millimeters * dpi / 25.4;
            if (value > maximum) throw new InvalidOperationException("Размер обводки превышает " + maximum + " px.");
            return millimeters == 0 ? 0 : Math.Max(1, (int)Math.Round(value, MidpointRounding.AwayFromZero));
        }

        private static int PixelCount(int width, int height, int channels)
        {
            long count = (long)width * height;
            if (width <= 0 || height <= 0 || count > Int32.MaxValue / channels)
                throw new InvalidOperationException("Результат обработки растра слишком велик.");
            return (int)count;
        }
        private static byte Byte(double value) => (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
    }
}
