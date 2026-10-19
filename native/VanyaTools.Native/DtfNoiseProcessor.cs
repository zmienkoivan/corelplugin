using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class DtfNoiseProcessor
    {
        internal sealed class Sample
        {
            internal BitmapSource Image;
            internal int X, Y;
        }

        internal static Sample Preview(string path)
        {
            BitmapSource image = AiPrintTab.Bitmap(path);
            int width = Math.Min(220, image.PixelWidth), height = Math.Min(180, image.PixelHeight);
            int x = (image.PixelWidth - width) / 2, y = (image.PixelHeight - height) / 2;
            var crop = new FormatConvertedBitmap(new CroppedBitmap(image, new Int32Rect(x, y, width, height)), PixelFormats.Bgra32, null, 0);
            int stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)]; crop.CopyPixels(pixels, stride, 0);
            var preview = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            preview.Freeze();
            return new Sample { Image = preview, X = x, Y = y };
        }

        internal static BitmapSource Render(BitmapSource input, double percent, int seed, bool protectNeutrals,
            CancellationToken cancellation, int originX = 0, int originY = 0)
        {
            if (Double.IsNaN(percent) || Double.IsInfinity(percent) || percent < 0 || percent > 5)
                throw new InvalidOperationException("Сила шума: от 0 до 5%.");
            var image = new FormatConvertedBitmap(input, PixelFormats.Bgra32, null, 0);
            int width = image.PixelWidth, height = image.PixelHeight, stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)]; image.CopyPixels(pixels, stride, 0);
            double amplitude = 255 * percent / 100;
            for (int y = 0; y < height; y++)
            {
                if ((y & 31) == 0) cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int offset = y * stride + x * 4;
                    if (pixels[offset + 3] == 0 || percent == 0) continue;
                    int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                    if (protectNeutrals && ((r == 255 && g == 255 && b == 255) || (r == 0 && g == 0 && b == 0))) continue;
                    uint hash = Hash(unchecked((uint)(x + originX) * 374761393u ^ (uint)(y + originY) * 668265263u ^ (uint)seed));
                    double noise = (hash / (double)UInt32.MaxValue * 2 - 1) * amplitude;
                    pixels[offset] = Byte(b + noise); pixels[offset + 1] = Byte(g + noise); pixels[offset + 2] = Byte(r + noise);
                    // Alpha stays byte-for-byte unchanged; no new ink islands outside the artwork.
                }
            }
            var result = BitmapSource.Create(width, height, input.DpiX, input.DpiY, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze(); return result;
        }

        private static uint Hash(uint value)
        {
            unchecked { value ^= value >> 16; value *= 2246822519u; value ^= value >> 13; value *= 3266489917u; return value ^ (value >> 16); }
        }
        private static byte Byte(double value) => (byte)Math.Max(0, Math.Min(255, Math.Round(value)));

        internal static void Apply(string source, string output, double percent, int seed, bool protect, CancellationToken cancellation)
        { Save(Render(AiPrintTab.Bitmap(source), percent, seed, protect, cancellation), output); }

        internal static void Comparison(string source, string output, int seed, bool protect, CancellationToken cancellation)
        {
            // WPF's text and visual renderer own COM/Dispatcher objects. Keep the
            // proof renderer on an STA thread rather than the pool's MTA thread.
            Exception failure = null;
            var renderer = new Thread(() => {
                try { BuildComparison(source, output, seed, protect, cancellation); }
                catch (Exception error) { failure = error; }
            }) { IsBackground = true };
            renderer.SetApartmentState(ApartmentState.STA); renderer.Start(); renderer.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        private static void BuildComparison(string source, string output, int seed, bool protect, CancellationToken cancellation)
        {
            BitmapSource input = AiPrintTab.Bitmap(source);
            int width = Math.Min(input.PixelWidth, Math.Max(1, (int)Math.Round(40 * input.DpiX / 25.4)));
            int height = Math.Min(input.PixelHeight, Math.Max(1, (int)Math.Round(40 * input.DpiY / 25.4)));
            int x = (input.PixelWidth - width) / 2, y = (input.PixelHeight - height) / 2;
            var crop = new CroppedBitmap(input, new Int32Rect(x, y, width, height)); crop.Freeze();
            int margin = Math.Max(8, (int)Math.Round(input.DpiX / 25.4));
            int labelHeight = Math.Max(26, (int)Math.Round(3 * input.DpiY / 25.4));
            int canvasWidth = checked((width + margin) * 4 + margin), canvasHeight = checked(height + labelHeight + margin * 2);
            var visual = new DrawingVisual();
            double[] amounts = { 0, 0.5, 1, 2 };
            using (DrawingContext draw = visual.RenderOpen())
                for (int i = 0; i < amounts.Length; i++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    int left = margin + i * (width + margin);
                    draw.DrawText(new FormattedText(amounts[i].ToString("0.#", CultureInfo.CurrentCulture) + "%",
                        CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), labelHeight * 0.65, Brushes.Black, 1.0),
                        new Point(left, margin));
                    draw.DrawImage(Render(crop, amounts[i], seed, protect, cancellation, x, y),
                        new Rect(left, margin + labelHeight, width, height));
                }
            var rendered = new RenderTargetBitmap(canvasWidth, canvasHeight, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(visual); rendered.Freeze();
            var bgra = new FormatConvertedBitmap(rendered, PixelFormats.Bgra32, null, 0);
            int stride = checked(canvasWidth * 4); var pixels = new byte[checked(stride * canvasHeight)]; bgra.CopyPixels(pixels, stride, 0);
            Save(BitmapSource.Create(canvasWidth, canvasHeight, input.DpiX, input.DpiY, PixelFormats.Bgra32, null, pixels, stride), output);
        }

        private static void Save(BitmapSource image, string output)
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (Stream stream = File.Create(output)) encoder.Save(stream);
        }
    }
}
