using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class PrintOutputProcessor
    {
        private const int Dpi = 300;
        // A3 page at 300 DPI: 297 x 420 mm, rounded to whole pixels.
        private const int ShortSide = 3508;
        private const int LongSide = 4961;

        public static void Prepare(string inputPath, string outputPath, int requestedColors)
        {
            BitmapSource source;
            using (var stream = File.OpenRead(inputPath))
            {
                source = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                source.Freeze();
            }
            var pixels = new byte[source.PixelWidth * source.PixelHeight * 4];
            int sourceStride = source.PixelWidth * 4;
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            bgra.CopyPixels(pixels, sourceStride, 0);
            var colors = requestedColors == 0
                ? DominantColors(pixels, source.PixelWidth, 0, 0, source.PixelWidth, source.PixelHeight)
                : DominantColors(pixels, source.PixelWidth, 0, 0, source.PixelWidth, source.PixelHeight, requestedColors);
            double ratio = (double)source.PixelWidth / source.PixelHeight;
            int canvasWidth = ratio >= 1.0 ? LongSide : ShortSide;
            int canvasHeight = ratio >= 1.0 ? ShortSide : LongSide;
            double scale = Math.Min((double)canvasWidth / source.PixelWidth, (double)canvasHeight / source.PixelHeight);
            int width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
            int height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
            int left = (canvasWidth - width) / 2, top = (canvasHeight - height) / 2;

            var result = new byte[canvasWidth * canvasHeight * 4];
            for (int y = 0; y < height; y++)
            {
                double sourceY = Math.Max(0, Math.Min(source.PixelHeight - 1, (y + 0.5) / scale - 0.5));
                int y0 = (int)sourceY, y1 = Math.Min(source.PixelHeight - 1, y0 + 1);
                double fy = sourceY - y0;
                for (int x = 0; x < width; x++)
                {
                    double sourceX = Math.Max(0, Math.Min(source.PixelWidth - 1, (x + 0.5) / scale - 0.5));
                    int x0 = (int)sourceX, x1 = Math.Min(source.PixelWidth - 1, x0 + 1);
                    double fx = sourceX - x0;
                    int nearest = (y0 * source.PixelWidth + x0) * 4;
                    if (pixels[nearest + 3] < 128)
                    {
                        nearest = (y0 * source.PixelWidth + x1) * 4;
                        if (pixels[nearest + 3] < 128) nearest = (y1 * source.PixelWidth + x0) * 4;
                    }
                    if (pixels[nearest + 3] < 128) nearest = (y1 * source.PixelWidth + x1) * 4;
                    var color = colors[NearestColor(colors, pixels[nearest], pixels[nearest + 1], pixels[nearest + 2])];
                    double topAlpha = pixels[(y0 * source.PixelWidth + x0) * 4 + 3] * (1 - fx) + pixels[(y0 * source.PixelWidth + x1) * 4 + 3] * fx;
                    double bottomAlpha = pixels[(y1 * source.PixelWidth + x0) * 4 + 3] * (1 - fx) + pixels[(y1 * source.PixelWidth + x1) * 4 + 3] * fx;
                    byte alpha = (byte)Math.Round(topAlpha * (1 - fy) + bottomAlpha * fy);
                    if (alpha < 3 || pixels[nearest + 3] < 128) continue;
                    int targetIndex = ((top + y) * canvasWidth + left + x) * 4;
                    result[targetIndex] = color.B; result[targetIndex + 1] = color.G;
                    result[targetIndex + 2] = color.R; result[targetIndex + 3] = alpha;
                }
            }
            var bitmap = new WriteableBitmap(canvasWidth, canvasHeight, Dpi, Dpi, PixelFormats.Bgra32, null);
            bitmap.WritePixels(new Int32Rect(0, 0, canvasWidth, canvasHeight), result, canvasWidth * 4, 0);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(outputPath)) encoder.Save(stream);
        }

        private struct Ink { public byte B, G, R; }

        private static Ink[] DominantColors(byte[] pixels, int imageWidth, int x, int y, int width, int height, int maximumColors = 4)
        {
            long sumR = 0, sumG = 0, sumB = 0, count = 0;
            var ratiosR = new System.Collections.Generic.List<double>();
            var ratiosB = new System.Collections.Generic.List<double>();
            int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            for (int py = y; py < y + height; py += 2)
                for (int px = x; px < x + width; px += 2)
                {
                    int i = (py * imageWidth + px) * 4;
                    if (pixels[i + 3] < 128) continue;
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    sumR += r; sumG += g; sumB += b; count++;
                    // For the green spot-color family, exclude gray and white matte
                    // fringes so they do not get counted as additional inks.
                    if (g > 20 && r < g * 0.9 && b < g * 1.15)
                    { ratiosR.Add((double)r / g); ratiosB.Add((double)b / g); }
                    minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                    minG = Math.Min(minG, g); maxG = Math.Max(maxG, g);
                    minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
                }
            if (count == 0) throw new InvalidDataException("Не удалось определить цвет принта.");
            // Detect one ink by stable hue, not RGB range: green antialiasing can vary
            // brightness considerably while remaining the same printed spot color.
            if (RatiosShowOneHue(ratiosR, ratiosB))
            {
                double rr = Median(ratiosR), br = Median(ratiosB);
                long coreR = 0, coreG = 0, coreB = 0, coreN = 0;
                for (int py = y; py < y + height; py += 2)
                    for (int px = x; px < x + width; px += 2)
                    {
                        int i = (py * imageWidth + px) * 4; int g = pixels[i + 1];
                        if (pixels[i + 3] < 128 || g <= 20 || Math.Abs((double)pixels[i + 2] / g - rr) > 0.12 || Math.Abs((double)pixels[i] / g - br) > 0.12) continue;
                        coreB += pixels[i]; coreG += g; coreR += pixels[i + 2]; coreN++;
                    }
                if (coreN == 0) { coreR = sumR; coreG = sumG; coreB = sumB; coreN = count; }
                return new[] { new Ink { R = (byte)(coreR / coreN), G = (byte)(coreG / coreN), B = (byte)(coreB / coreN) } };
            }

            maximumColors = Math.Max(1, Math.Min(4, maximumColors));
            if (maximumColors == 1)
                return new[] { new Ink { R = (byte)(sumR / count), G = (byte)(sumG / count), B = (byte)(sumB / count) } };
            var centers = new Ink[maximumColors];
            for (int i = 0; i < centers.Length; i++) centers[i] = new Ink
            {
                B = (byte)(minB + (maxB - minB) * i / Math.Max(1, centers.Length - 1)),
                G = (byte)(minG + (maxG - minG) * i / Math.Max(1, centers.Length - 1)),
                R = (byte)(minR + (maxR - minR) * i / Math.Max(1, centers.Length - 1))
            };
            for (int pass = 0; pass < 8; pass++)
            {
                var sr = new long[centers.Length]; var sg = new long[centers.Length]; var sb = new long[centers.Length]; var n = new long[centers.Length];
                for (int py = y; py < y + height; py += 2)
                    for (int px = x; px < x + width; px += 2)
                    {
                        int i = (py * imageWidth + px) * 4;
                        if (pixels[i + 3] < 128) continue;
                        int k = NearestColor(centers, pixels[i], pixels[i + 1], pixels[i + 2]);
                        sb[k] += pixels[i]; sg[k] += pixels[i + 1]; sr[k] += pixels[i + 2]; n[k]++;
                    }
                for (int k = 0; k < centers.Length; k++) if (n[k] > 0) centers[k] = new Ink
                { B = (byte)(sb[k] / n[k]), G = (byte)(sg[k] / n[k]), R = (byte)(sr[k] / n[k]) };
            }
            return centers;
        }

        private static bool RatiosShowOneHue(System.Collections.Generic.List<double> reds, System.Collections.Generic.List<double> blues)
        {
            if (reds.Count < 100) return false;
            reds.Sort(); blues.Sort();
            return reds[(int)(reds.Count * 0.9)] - reds[(int)(reds.Count * 0.1)] < 0.18 &&
                   blues[(int)(blues.Count * 0.9)] - blues[(int)(blues.Count * 0.1)] < 0.18;
        }

        private static double Median(System.Collections.Generic.List<double> values)
        {
            values.Sort(); return values[values.Count / 2];
        }

        private static int NearestColor(Ink[] colors, byte b, byte g, byte r)
        {
            int nearest = 0, distance = Int32.MaxValue;
            for (int i = 0; i < colors.Length; i++)
            {
                int dr = r - colors[i].R, dg = g - colors[i].G, db = b - colors[i].B;
                int d = dr * dr + dg * dg + db * db;
                if (d < distance) { distance = d; nearest = i; }
            }
            return nearest;
        }
    }
}
