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
        internal static BitmapSource Reference(IReadOnlyList<BitmapSource> images)
        {
            if (images == null || images.Count == 0 || images.Count > 64)
                throw new ArgumentException("Для коллажа нужен набор из 1–64 рисунков.", nameof(images));
            foreach (var image in images)
                if (image == null) throw new ArgumentException("Не удалось прочитать один из рисунков набора.", nameof(images));
            int columns = (int)Math.Ceiling(Math.Sqrt(images.Count));
            int rows = (images.Count + columns - 1) / columns;
            int tile = images.Count == 1 ? 2048 : Math.Min(768, 1920 / columns);
            var visual = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
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
        internal static PatternFillerPlan Analyze(BitmapSource reference, string token, CancellationToken cancellation) =>
            Analyze(reference, token, PatternFillerBrief.AnalysisModel, cancellation);

        internal static PatternFillerPlan Analyze(BitmapSource reference, string token, string model, CancellationToken cancellation, int count = 4, bool smallFillers = false, PatternReferenceKind source = PatternReferenceKind.MainPrint)
        {
            PatternFillerBrief.FindAnalysisModel(model);
            cancellation.ThrowIfCancellationRequested();
            string path = Path.Combine(Path.GetTempPath(), "Vanya-Pattern-Brief-" + Guid.NewGuid().ToString("N") + ".txt");
            string input = AiPrintTab.DataUri(reference, 2048, 2000000);
            try
            {
                ReplicateWorkerClient.Run(model, token, PatternFillerBrief.AnalysisInput(model, input, count, smallFillers, source),
                    path, cancellation: cancellation);
                cancellation.ThrowIfCancellationRequested();
                if (new FileInfo(path).Length > 48000)
                    throw new InvalidDataException("Анализатор вернул слишком длинное описание.");
                return PatternFillerBrief.Parse(File.ReadAllText(path), count, smallFillers, source);
            }
            finally { Cleanup(path, input); }
        }

        // Called only after the user confirms sending the reference in PatternFillerWindow.
        internal static PatternFillerResult Generate(BitmapSource reference, string token, PatternFillerPlan plan, CancellationToken cancellation)
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
                    ["aspect_ratio"] = PatternFillerBrief.AspectRatio(plan.Elements.Length),
                    ["resolution"] = PatternFillerBrief.Resolution(plan.Elements.Length)
                }, path, cancellation: cancellation);
                cancellation.ThrowIfCancellationRequested();
                return PatternFillerExtraction.Extract(AiPrintTab.Bitmap(path), plan, cancellation);
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
            var result = PatternFillerExtraction.Extract(sheet, null, cancellation);
            var images = new List<BitmapSource>();
            foreach (var image in result.Cells) if (image != null) images.Add(image);
            if (images.Count == 0) throw new InvalidDataException("На листе ИИ не нашлось целых отдельных фигур.");
            return images.ToArray();
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
