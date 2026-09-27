using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    /// <summary>
    /// Removes background-matte color from partially transparent print edges.
    /// It leaves the alpha mask geometry intact and derives the matte color from
    /// the corners of the corresponding opaque image, so it is not tied to one ink.
    /// </summary>
    internal static class PrintEdgeCleanup
    {
        public static void Clean(string mattePath, string alphaPath, string outputPath)
        {
            BitmapSource matte = LoadBgra(mattePath);
            BitmapSource cutout = LoadBgra(alphaPath);
            if (cutout.PixelWidth != matte.PixelWidth || cutout.PixelHeight != matte.PixelHeight)
            {
                matte = new TransformedBitmap(matte, new ScaleTransform(
                    (double)cutout.PixelWidth / matte.PixelWidth,
                    (double)cutout.PixelHeight / matte.PixelHeight));
                matte.Freeze();
            }

            int width = cutout.PixelWidth, height = cutout.PixelHeight, stride = checked(width * 4);
            byte[] source = new byte[checked(stride * height)];
            byte[] pixels = new byte[source.Length];
            matte.CopyPixels(source, stride, 0);
            cutout.CopyPixels(pixels, stride, 0);

            byte bgR = MedianCorner(source, width, height, stride, 2);
            byte bgG = MedianCorner(source, width, height, stride, 1);
            byte bgB = MedianCorner(source, width, height, stride, 0);
            int[] bg = { bgB, bgG, bgR };
            int edgePixels = 0, correctedPixels = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int p = y * stride + x * 4;
                    int a = pixels[p + 3];
                    if (a < 6)
                    {
                        pixels[p] = pixels[p + 1] = pixels[p + 2] = pixels[p + 3] = 0;
                        continue;
                    }
                    if (a >= 250) continue;
                    edgePixels++;

                    int foreground = NearestOpaqueColor(pixels, width, height, stride, x, y);
                    if (foreground < 0) continue;
                    double alpha = a / 255.0;
                    bool contaminated = false;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double fg = pixels[foreground + channel];
                        double expectedMatte = alpha * fg + (1 - alpha) * bg[channel];
                        double current = pixels[p + channel];
                        if (Math.Abs(current - expectedMatte) + 1.0 < Math.Abs(current - fg)) contaminated = true;
                    }
                    if (!contaminated) continue;

                    for (int channel = 0; channel < 3; channel++)
                    {
                        double fg = pixels[foreground + channel];
                        double value = (pixels[p + channel] - bg[channel] * (1 - alpha)) / alpha;
                        // Avoid amplifying compression noise on very soft, low-alpha pixels.
                        value = Math.Max(Math.Max(0, fg - 64), Math.Min(Math.Min(255, fg + 64), value));
                        pixels[p + channel] = (byte)Math.Round(value);
                    }
                    correctedPixels++;
                }
            }

            var output = BitmapSource.Create(width, height, cutout.DpiX, cutout.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
            output.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(output));
            using (var stream = File.Create(outputPath)) encoder.Save(stream);
            Log.Info("Print edge cleanup completed. Size=" + width + "x" + height + ", matte RGB=" + bgR + "," + bgG + "," + bgB +
                ", partial-alpha pixels=" + edgePixels + ", decontaminated=" + correctedPixels + ".");
        }

        private static BitmapSource LoadBgra(string path)
        {
            BitmapSource image;
            using (var stream = File.OpenRead(path))
            {
                image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                image.Freeze();
            }
            if (image.Format != PixelFormats.Bgra32)
            {
                image = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
                image.Freeze();
            }
            return image;
        }

        private static byte MedianCorner(byte[] pixels, int width, int height, int stride, int channel)
        {
            int patchW = Math.Max(1, Math.Min(32, width / 12));
            int patchH = Math.Max(1, Math.Min(32, height / 12));
            var samples = new List<byte>(patchW * patchH * 4);
            for (int y = 0; y < patchH; y += 2)
                for (int x = 0; x < patchW; x += 2)
                {
                    AddSample(pixels, stride, x, y, channel, samples);
                    AddSample(pixels, stride, width - 1 - x, y, channel, samples);
                    AddSample(pixels, stride, x, height - 1 - y, channel, samples);
                    AddSample(pixels, stride, width - 1 - x, height - 1 - y, channel, samples);
                }
            if (samples.Count == 0) return 255;
            samples.Sort();
            return samples[samples.Count / 2];
        }

        private static void AddSample(byte[] pixels, int stride, int x, int y, int channel, List<byte> samples)
        {
            int p = y * stride + x * 4;
            if (pixels[p + 3] > 245) samples.Add(pixels[p + channel]);
        }

        private static int NearestOpaqueColor(byte[] pixels, int width, int height, int stride, int x, int y)
        {
            for (int radius = 1; radius <= 3; radius++)
            {
                int best = -1, bestDistance = Int32.MaxValue;
                for (int dy = -radius; dy <= radius; dy++)
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != radius) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                        int p = ny * stride + nx * 4;
                        if (pixels[p + 3] < 245) continue;
                        int distance = dx * dx + dy * dy;
                        if (distance < bestDistance) { best = p; bestDistance = distance; }
                    }
                if (best >= 0) return best;
            }
            return -1;
        }
    }
}
