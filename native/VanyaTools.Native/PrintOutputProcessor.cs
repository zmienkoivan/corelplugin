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
                int sy = Math.Min(source.PixelHeight - 1, (int)(y / scale));
                for (int x = 0; x < width; x++)
                {
                    int sx = Math.Min(source.PixelWidth - 1, (int)(x / scale));
                    int sourceIndex = (sy * source.PixelWidth + sx) * 4;
                    byte alpha = pixels[sourceIndex + 3];
                    if (alpha < 128) continue;
                    var color = colors[NearestColor(colors, pixels[sourceIndex], pixels[sourceIndex + 1], pixels[sourceIndex + 2])];
                    int targetIndex = ((top + y) * canvasWidth + left + x) * 4;
                    result[targetIndex] = color.B; result[targetIndex + 1] = color.G;
                    result[targetIndex + 2] = color.R; result[targetIndex + 3] = 255;
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
            int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
            for (int py = y; py < y + height; py += 2)
                for (int px = x; px < x + width; px += 2)
                {
                    int i = (py * imageWidth + px) * 4;
                    if (pixels[i + 3] < 128) continue;
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                    sumR += r; sumG += g; sumB += b; count++;
                    minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                    minG = Math.Min(minG, g); maxG = Math.Max(maxG, g);
                    minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
                }
            if (count == 0) throw new InvalidDataException("Не удалось определить цвет принта.");
            // A single-color print stays one solid spot color despite antialias noise.
            if (Math.Max(maxR - minR, Math.Max(maxG - minG, maxB - minB)) < 60)
                return new[] { new Ink { R = (byte)(sumR / count), G = (byte)(sumG / count), B = (byte)(sumB / count) } };

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
