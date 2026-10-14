using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal enum LocalPhotoPreset
    {
        BlackAndWhite,
        Sepia,
        NobleGold,
        WarmMatte,
        Custom
    }

    internal sealed class LocalPhotoStyleSettings
    {
        public LocalPhotoPreset Preset;
        public int Strength, Contrast, Warmth, Saturation, Brightness;
    }

    internal static class LocalPhotoStyleProcessor
    {
        public static void Apply(string source, string output, LocalPhotoStyleSettings settings,
            CancellationToken cancellation)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            BitmapSource input = AiPrintTab.Bitmap(source);
            var bitmap = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
            int stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            bitmap.CopyPixels(pixels, stride, 0);

            double strength = settings.Strength / 100.0;
            double saturation = settings.Saturation / 100.0;
            double contrast = 1.0 + settings.Contrast / 100.0;
            double warmth = settings.Warmth;
            double brightness = settings.Brightness * 1.5;
            for (int y = 0; y < height; y++)
            {
                if ((y & 63) == 0) cancellation.ThrowIfCancellationRequested();
                int row = y * stride;
                for (int x = 0; x < width; x++)
                {
                    int p = row + x * 4;
                    if (pixels[p + 3] == 0) continue;
                    double b = pixels[p], g = pixels[p + 1], r = pixels[p + 2];
                    double luminance = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    double styledR, styledG, styledB;
                    switch (settings.Preset)
                    {
                        case LocalPhotoPreset.BlackAndWhite:
                            styledR = styledG = styledB = luminance;
                            break;
                        case LocalPhotoPreset.Sepia:
                            styledR = luminance * 1.04 + 12;
                            styledG = luminance * 0.96 + 5;
                            styledB = luminance * 0.80 + 2;
                            break;
                        case LocalPhotoPreset.NobleGold:
                            double tone = luminance / 255.0;
                            if (tone < 0.52)
                            {
                                double t = tone / 0.52;
                                styledR = Lerp(34, 158, t);
                                styledG = Lerp(29, 132, t);
                                styledB = Lerp(28, 104, t);
                            }
                            else
                            {
                                double t = (tone - 0.52) / 0.48;
                                styledR = Lerp(158, 242, t);
                                styledG = Lerp(132, 231, t);
                                styledB = Lerp(104, 208, t);
                            }
                            break;
                        case LocalPhotoPreset.WarmMatte:
                            styledR = luminance + (r - luminance) * 0.62 + 9;
                            styledG = luminance + (g - luminance) * 0.62 + 4;
                            styledB = luminance + (b - luminance) * 0.62 - 6;
                            styledR = 128 + (styledR - 128) * 0.90 + 5;
                            styledG = 128 + (styledG - 128) * 0.90 + 5;
                            styledB = 128 + (styledB - 128) * 0.90 + 5;
                            break;
                        default:
                            styledR = r; styledG = g; styledB = b;
                            break;
                    }

                    double styledL = 0.2126 * styledR + 0.7152 * styledG + 0.0722 * styledB;
                    styledR = styledL + (styledR - styledL) * saturation;
                    styledG = styledL + (styledG - styledL) * saturation;
                    styledB = styledL + (styledB - styledL) * saturation;
                    styledR += warmth * 0.42;
                    styledG += warmth * 0.06;
                    styledB -= warmth * 0.42;
                    styledR = 128 + (styledR - 128) * contrast + brightness;
                    styledG = 128 + (styledG - 128) * contrast + brightness;
                    styledB = 128 + (styledB - 128) * contrast + brightness;

                    pixels[p] = ToByte(Lerp(b, styledB, strength));
                    pixels[p + 1] = ToByte(Lerp(g, styledG, strength));
                    pixels[p + 2] = ToByte(Lerp(r, styledR, strength));
                    // Alpha remains byte-for-byte unchanged, including soft edges.
                }
            }
            cancellation.ThrowIfCancellationRequested();
            var result = BitmapSource.Create(width, height, input.DpiX, input.DpiY,
                PixelFormats.Bgra32, null, pixels, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var stream = File.Create(output)) encoder.Save(stream);
        }

        private static double Lerp(double from, double to, double amount)
        {
            return from + (to - from) * amount;
        }

        private static byte ToByte(double value)
        {
            return (byte)Math.Max(0, Math.Min(255, Math.Round(value)));
        }
    }
}
