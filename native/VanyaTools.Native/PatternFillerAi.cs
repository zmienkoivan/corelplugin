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

        internal static string ReferenceKey(BitmapSource reference)
        {
            var bitmap = new FormatConvertedBitmap(reference, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            using (var hash = System.Security.Cryptography.SHA256.Create())
                return bitmap.PixelWidth + "x" + bitmap.PixelHeight + ":" + Convert.ToBase64String(hash.ComputeHash(pixels));
        }

        // Each call is made only after its own preview-upload confirmation.
        internal static PatternFillerPlan Analyze(BitmapSource reference, string token, CancellationToken cancellation)
        {
            string path = Path.Combine(Path.GetTempPath(), "Vanya-Pattern-Brief-" + Guid.NewGuid().ToString("N") + ".txt");
            string input = AiPrintTab.DataUri(reference, 2048, 2000000);
            try
            {
                ReplicateWorkerClient.Run(PatternFillerBrief.AnalysisModel, token, new Dictionary<string, object>
                {
                    ["image"] = input, ["prompt"] = PatternFillerBrief.AnalysisPrompt,
                    ["max_tokens"] = 1400, ["max_image_resolution"] = 2.0
                }, path, cancellation: cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (new FileInfo(path).Length > 48000)
                    throw new InvalidDataException("Анализатор вернул слишком длинное описание.");
                return PatternFillerBrief.Parse(File.ReadAllText(path));
            }
            finally { Cleanup(path, input); }
        }

        // Called only after the user confirms sending the reference in PatternFillerWindow.
        internal static BitmapSource[] Generate(BitmapSource reference, string token, PatternFillerPlan plan, CancellationToken cancellation)
        {
            string prompt = PatternFillerBrief.GenerationPrompt(plan);
            string path = Path.Combine(Path.GetTempPath(), "Vanya-Pattern-Fillers-" + Guid.NewGuid().ToString("N") + ".png");
            string input = AiPrintTab.DataUri(reference);
            try
            {
                ReplicateWorkerClient.Run(SublimationModels.Nano2, token, new Dictionary<string, object>
                {
                    ["image_input"] = new[] { input },
                    ["prompt"] = prompt,
                    ["output_format"] = "png",
                    ["aspect_ratio"] = "1:1",
                    ["resolution"] = "2K"
                }, path, cancellation: cancellation);
                cancellation.ThrowIfCancellationRequested();
                return Split(AiPrintTab.Bitmap(path), cancellation);
            }
            finally { Cleanup(path, input); }
        }

        private static void Cleanup(string path, string input)
        {
            TryDelete(path);
            TryDelete(path + ".cancel");
            const string filePrefix = "replicate-file:";
            if (input.StartsWith(filePrefix, StringComparison.Ordinal)) TryDelete(input.Substring(filePrefix.Length));
        }

        internal static BitmapSource[] Split(BitmapSource sheet, CancellationToken cancellation)
        {
            int width = sheet.PixelWidth, height = sheet.PixelHeight;
            if (width < 32 || height < 32 || (long)width * height > 16000000)
                throw new InvalidDataException("ИИ вернул лист неподходящего размера.");
            var bitmap = new FormatConvertedBitmap(sheet, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(width * height * 4)];
            bitmap.CopyPixels(pixels, width * 4, 0);
            PatternFillerMatte.Remove(pixels, width, height, cancellation);
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
