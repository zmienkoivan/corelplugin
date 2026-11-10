using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class PatternFillerAi
    {
        internal const int Count = 4;

        internal static BitmapSource Reference(IReadOnlyList<BitmapSource> images)
        {
            int columns = (int)Math.Ceiling(Math.Sqrt(images.Count));
            int rows = (images.Count + columns - 1) / columns;
            int tile = Math.Min(480, 1920 / columns);
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                drawing.DrawRectangle(Brushes.LightGray, null, new Rect(0, 0, columns * tile, rows * tile));
                for (int i = 0; i < images.Count; i++)
                {
                    var image = images[i];
                    double scale = (tile - 20d) / Math.Max(image.PixelWidth, image.PixelHeight);
                    double width = scale * image.PixelWidth, height = scale * image.PixelHeight;
                    drawing.DrawImage(image, new Rect(i % columns * tile + (tile - width) / 2,
                        i / columns * tile + (tile - height) / 2, width, height));
                }
            }
            var result = new RenderTargetBitmap(columns * tile, rows * tile, 96, 96, PixelFormats.Pbgra32);
            result.Render(visual);
            result.Freeze();
            return result;
        }

        private static readonly string[] PrimitiveSets =
        {
            "a five-pointed star (top left), a round ball or disk (top right), a square (bottom left), a diamond (bottom right)",
            "an open ring (top left), a triangle (top right), a hexagon (bottom left), a rounded capsule (bottom right)",
            "a four-pointed sparkle (top left), a water drop (top right), a crescent (bottom left), a small cluster of three dots (bottom right)"
        };

        // Called only after the user confirms sending the reference in PatternFillTab.
        internal static BitmapSource[] Generate(BitmapSource reference, string token, int set, CancellationToken cancellation)
        {
            string path = Path.Combine(Path.GetTempPath(), "Vanya-Pattern-Fillers-" + Guid.NewGuid().ToString("N") + ".png");
            string input = AiPrintTab.DataUri(reference);
            try
            {
                ReplicateWorkerClient.Run(SublimationModels.Nano2, token, new Dictionary<string, object>
                {
                    ["image_input"] = new[] { input },
                    ["prompt"] = "Draw exactly these four simple geometric primitives: " + PrimitiveSets[set] + ". " +
                        "Each outer silhouette must remain immediately recognizable as the specified primitive. " +
                        "The reference images are style swatches only. Borrow their palette, stroke weight, shading, texture and level of detail. " +
                        "Never reproduce, miniaturize, trace, or place any reference subject, character, animal, logo, or object inside a primitive. " +
                        "Center one isolated primitive in each quadrant of a 2x2 square sheet. Leave generous empty gutters and margins. " +
                        "No frames, text, cast shadows, or mockup. Use a flat vivid magenta #FF00FF background throughout. " +
                        "Do not use that magenta in the primitives.",
                    ["output_format"] = "png",
                    ["aspect_ratio"] = "1:1",
                    ["resolution"] = "2K"
                }, path, cancellation: cancellation);
                cancellation.ThrowIfCancellationRequested();
                return Split(AiPrintTab.Bitmap(path), cancellation);
            }
            finally
            {
                TryDelete(path);
                TryDelete(path + ".cancel");
                const string filePrefix = "replicate-file:";
                if (input.StartsWith(filePrefix, StringComparison.Ordinal)) TryDelete(input.Substring(filePrefix.Length));
            }
        }

        internal static BitmapSource[] Split(BitmapSource sheet, CancellationToken cancellation)
        {
            int width = sheet.PixelWidth, height = sheet.PixelHeight;
            if (width < 32 || height < 32 || (long)width * height > 16000000)
                throw new InvalidDataException("ИИ вернул лист неподходящего размера.");
            var bitmap = new FormatConvertedBitmap(sheet, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(width * height * 4)];
            bitmap.CopyPixels(pixels, width * 4, 0);
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = y * width * 4; i < (y + 1) * width * 4; i += 4)
                    if (pixels[i + 3] <= 8 || (pixels[i] >= 185 && pixels[i + 2] >= 185 && pixels[i + 1] <= 75 &&
                        Math.Min(pixels[i], pixels[i + 2]) - pixels[i + 1] >= 125))
                        pixels[i + 3] = 0;
            }
            var result = new List<BitmapSource>(Count);
            for (int index = 0; index < Count; index++)
            {
                int x0 = width * (index % 2) / 2, y0 = height * (index / 2) / 2;
                int x1 = width * (index % 2 + 1) / 2, y1 = height * (index / 2 + 1) / 2;
                int left = x1, right = x0 - 1, top = y1, bottom = y0 - 1;
                for (int y = y0; y < y1; y++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    for (int x = x0; x < x1; x++)
                        if (pixels[(y * width + x) * 4 + 3] > 0)
                        {
                            left = Math.Min(left, x); right = Math.Max(right, x);
                            top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        }
                }
                int w = right - left + 1, h = bottom - top + 1;
                // Reject a motif that reaches the cell border rather than slicing it into two sprites.
                if (w < 8 || h < 8 || left <= x0 || right >= x1 - 1 || top <= y0 || bottom >= y1 - 1) continue;
                int stride = w * 4;
                var sprite = new byte[stride * h];
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(pixels, ((top + y) * width + left) * 4, sprite, y * stride, stride);
                var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, sprite, stride);
                image.Freeze();
                result.Add(image);
            }
            if (result.Count == 0) throw new InvalidDataException("На листе ИИ не нашлось отдельных фигур на однотонном фоне.");
            return result.ToArray();
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
