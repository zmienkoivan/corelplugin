using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    /// <summary>Builds aligned image/mask files from one bitmap and one CorelDRAW area shape.</summary>
    internal static class GenerativeFillCapture
    {
        internal sealed class Prepared
        {
            public string ImagePath, MaskPath;
            public string ModelImagePath, ModelAspectRatio;
            public int ModelWidth, ModelHeight;
            public int WidthPixels, HeightPixels;
            public Rect PhotoBoundsPixels;
            public double LeftMm, TopMm, WidthMm, HeightMm;
            public string ExtensionDirections;
        }

        public static Prepared Capture()
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic selection = app.ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) != 2)
                throw new InvalidOperationException("Выделите ровно два объекта: фотографию и прямоугольник области правки.");

            var selected = new List<dynamic>();
            dynamic photo = null, area = null;
            foreach (dynamic shape in selection.Shapes)
            {
                selected.Add(shape);
                if (Convert.ToInt32(shape.Type) == CorelConstants.CdrBitmapShape) photo = shape;
                else area = shape;
            }
            if (photo == null || area == null || Convert.ToInt32(area.Type) != 1)
                throw new InvalidOperationException("Нужны один растр и один прямоугольник. Сгруппированный макет сначала превратите в растр.");

            int oldUnit = Convert.ToInt32(doc.Unit);
            double pLeft, pTop, pRight, pBottom, aLeft, aTop, aRight, aBottom;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                pLeft = Convert.ToDouble(photo.LeftX); pTop = Convert.ToDouble(photo.TopY);
                pRight = Convert.ToDouble(photo.RightX); pBottom = Convert.ToDouble(photo.BottomY);
                aLeft = Convert.ToDouble(area.LeftX); aTop = Convert.ToDouble(area.TopY);
                aRight = Convert.ToDouble(area.RightX); aBottom = Convert.ToDouble(area.BottomY);
            }
            finally { doc.Unit = oldUnit; }
            if (pRight <= pLeft || pTop <= pBottom || aRight <= aLeft || aTop <= aBottom)
                throw new InvalidOperationException("У фотографии или области правки нет размера.");

            // A frame drawn around the whole photo means a target canvas, not
            // permission to regenerate the entire source. Ignore tiny drawing
            // inaccuracies at the existing edges when detecting an extension.
            double edgeTolerance = Math.Max(0.01, Math.Max(pRight - pLeft, pTop - pBottom) / 1024.0);
            if (Math.Abs(aLeft - pLeft) <= edgeTolerance) aLeft = pLeft;
            if (Math.Abs(aRight - pRight) <= edgeTolerance) aRight = pRight;
            if (Math.Abs(aTop - pTop) <= edgeTolerance) aTop = pTop;
            if (Math.Abs(aBottom - pBottom) <= edgeTolerance) aBottom = pBottom;
            bool extendLeft = aLeft < pLeft, extendRight = aRight > pRight;
            bool extendTop = aTop > pTop, extendBottom = aBottom < pBottom;
            bool extension = extendLeft || extendRight || extendTop || extendBottom;

            string photoPath;
            try
            {
                photo.CreateSelection();
                photoPath = AiSelectionCapture.Capture(2048, 4000000, false, false);
            }
            finally
            {
                try
                {
                    for (int i = 0; i < selected.Count; i++)
                    {
                        if (i == 0) selected[i].CreateSelection();
                        else selected[i].AddToSelection();
                    }
                    app.ActiveWindow.Refresh();
                }
                catch (Exception ex) { Log.Error("Generative fill selection restore failed.", ex); }
            }

            double left = Math.Min(pLeft, aLeft), top = Math.Max(pTop, aTop);
            double right = Math.Max(pRight, aRight), bottom = Math.Min(pBottom, aBottom);
            double widthMm = right - left, heightMm = top - bottom;
            if (widthMm <= 0 || heightMm <= 0)
                throw new InvalidOperationException("Не удалось определить область генерации.");
            if (aRight < pLeft || aLeft > pRight || aTop < pBottom || aBottom > pTop)
                throw new InvalidOperationException("Прямоугольник должен хотя бы частично пересекать фотографию.");

            string imagePath = Path.Combine(Path.GetTempPath(), "Vanya-Fill-Input-" + Guid.NewGuid().ToString("N") + ".png");
            string maskPath = Path.Combine(Path.GetTempPath(), "Vanya-Fill-Mask-" + Guid.NewGuid().ToString("N") + ".png");
            var prepared = new Prepared { ImagePath = imagePath, MaskPath = maskPath,
                LeftMm = left, TopMm = top, WidthMm = widthMm, HeightMm = heightMm };
            try
            {
                BitmapSource source = AiPrintTab.Bitmap(photoPath);
                // Both image and mask share exactly the same dimensions. This avoids
                // separate Corel exports drifting by a pixel at different resolutions.
                double scale = Math.Min(2048.0 / Math.Max(widthMm, heightMm),
                    Math.Sqrt(4000000.0 / (widthMm * heightMm)));
                int width = Math.Max(64, (int)Math.Floor(widthMm * scale));
                int height = Math.Max(64, (int)Math.Floor(heightMm * scale));
                var photoRect = RectFor(pLeft, pTop, pRight, pBottom, left, top, widthMm, heightMm, width, height);
                var areaRect = RectFor(aLeft, aTop, aRight, aBottom, left, top, widthMm, heightMm, width, height);
                prepared.WidthPixels = width; prepared.HeightPixels = height;
                prepared.PhotoBoundsPixels = photoRect;
                Render(imagePath, width, height, visual =>
                {
                    // New canvas is transparent rather than a white graphic
                    // panel. Its mask, below, explicitly requests continuation.
                    visual.DrawImage(source, photoRect);
                });
                Render(maskPath, width, height, visual =>
                {
                    // Fill every new canvas pixel, including side strips when
                    // the drawn extension is narrower than the source photo.
                    visual.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
                    visual.DrawRectangle(Brushes.Black, null, photoRect);
                    if (extension)
                    {
                        // Only a narrow joining strip is editable inside the
                        // photo. Even a frame covering all of it keeps the rest
                        // anchored, so the model must continue the same shot.
                        double seam = Math.Min(8, Math.Min(photoRect.Width, photoRect.Height) / 20.0);
                        if (extendTop) visual.DrawRectangle(Brushes.White, null,
                            new Rect(photoRect.Left, photoRect.Top, photoRect.Width, seam));
                        if (extendBottom) visual.DrawRectangle(Brushes.White, null,
                            new Rect(photoRect.Left, photoRect.Bottom - seam, photoRect.Width, seam));
                        if (extendLeft) visual.DrawRectangle(Brushes.White, null,
                            new Rect(photoRect.Left, photoRect.Top, seam, photoRect.Height));
                        if (extendRight) visual.DrawRectangle(Brushes.White, null,
                            new Rect(photoRect.Right - seam, photoRect.Top, seam, photoRect.Height));
                    }
                    else visual.DrawRectangle(Brushes.White, null, areaRect);
                });
                var directions = new List<string>();
                if (extendTop) directions.Add("top");
                if (extendBottom) directions.Add("bottom");
                if (extendLeft) directions.Add("left");
                if (extendRight) directions.Add("right");
                Log.Info("Generative fill: " + width + "x" + height + ", " +
                    (directions.Count == 0 ? "local photo edit" : "photo extension " + String.Join(", ", directions)));
                prepared.ExtensionDirections = String.Join(", ", directions);
                PrepareModelCanvas(prepared, width, height);
                return prepared;
            }
            catch
            {
                TryDelete(imagePath); TryDelete(maskPath);
                TryDelete(prepared.ModelImagePath);
                throw;
            }
            finally { TryDelete(photoPath); }
        }

        public static void PreserveOutsideMask(string imagePath, string maskPath,
            string generatedPath, string resultPath, int modelWidth, int modelHeight)
        {
            BitmapSource original = AiPrintTab.Bitmap(imagePath);
            BitmapSource mask = AiPrintTab.Bitmap(maskPath);
            BitmapSource generated = AiPrintTab.Bitmap(generatedPath);
            int width = original.PixelWidth, height = original.PixelHeight;
            if (mask.PixelWidth != width || mask.PixelHeight != height)
                throw new InvalidDataException("Размер маски не совпадает с изображением.");
            if (modelWidth < width || modelHeight < height)
                throw new InvalidDataException("Размер холста генерации меньше исходной области.");
            double aspectError = Math.Abs((double)generated.PixelWidth * modelHeight /
                ((double)generated.PixelHeight * modelWidth) - 1);
            Log.Info("Generative fill output: " + generated.PixelWidth + "x" + generated.PixelHeight +
                ", expected canvas=" + modelWidth + "x" + modelHeight);
            if (aspectError > 0.02)
                throw new InvalidDataException("Модель вернула другие пропорции кадра. Ответ сохранён; " +
                    "его нельзя растянуть в область правки без искажения фотографии.");
            // The model sees a supported aspect ratio with padding only at the
            // right/bottom. Normalize to that canvas, then crop padding away.
            // Stretching the padded output directly into the original union
            // would move the generated content relative to the source photo.
            if (generated.PixelWidth != modelWidth || generated.PixelHeight != modelHeight)
            {
                var visual = new DrawingVisual();
                using (DrawingContext dc = visual.RenderOpen())
                    dc.DrawImage(generated, new Rect(0, 0, modelWidth, modelHeight));
                var resized = new RenderTargetBitmap(modelWidth, modelHeight, 96, 96, PixelFormats.Pbgra32);
                resized.Render(visual);
                generated = resized;
            }
            if (modelWidth != width || modelHeight != height)
                generated = new CroppedBitmap(generated, new Int32Rect(0, 0, width, height));
            byte[] before = Pixels(original), whiteMask = Pixels(mask), after = Pixels(generated);
            int stride = width * 4;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int index = y * stride + x * 4;
                    if (whiteMask[index] < 128)
                    {
                        for (int channel = 0; channel < 4; channel++)
                            after[index + channel] = before[index + channel];
                        continue;
                    }
                    // Outside the source photograph there is nothing to blend
                    // with. Feathering against empty canvas creates a border.
                    if (before[index + 3] == 0) continue;
                    // A small feather hides the seam while leaving the rest of the
                    // source exactly intact, even if the model changes other details.
                    int edge = 5;
                    for (int distance = 1; distance < 5; distance++)
                    {
                        if ((x >= distance && whiteMask[index - distance * 4] < 128) ||
                            (x + distance < width && whiteMask[index + distance * 4] < 128) ||
                            (y >= distance && whiteMask[index - distance * stride] < 128) ||
                            (y + distance < height && whiteMask[index + distance * stride] < 128))
                        { edge = distance; break; }
                    }
                    double blend = Math.Min(1.0, edge / 5.0);
                    if (blend >= 1) continue;
                    for (int channel = 0; channel < 3; channel++)
                        after[index + channel] = (byte)Math.Round(before[index + channel] * (1 - blend) +
                            after[index + channel] * blend);
                }
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32,
                null, after, stride);
            SavePng(result, resultPath);
        }

        private static byte[] Pixels(BitmapSource source)
        {
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            byte[] pixels = new byte[checked(bgra.PixelWidth * bgra.PixelHeight * 4)];
            bgra.CopyPixels(pixels, bgra.PixelWidth * 4, 0);
            return pixels;
        }

        private static void PrepareModelCanvas(Prepared prepared, int width, int height)
        {
            // Nano Banana models expose a fixed set of aspect ratios. Pad to
            // the least extra area instead of asking the model to crop or zoom
            // the photograph to fit one. Both model variants support this set.
            int[,] ratios = { { 1, 1 }, { 2, 3 }, { 3, 2 }, { 3, 4 }, { 4, 3 },
                { 4, 5 }, { 5, 4 }, { 9, 16 }, { 16, 9 }, { 21, 9 } };
            long bestArea = Int64.MaxValue;
            for (int i = 0; i < ratios.GetLength(0); i++)
            {
                int numerator = ratios[i, 0], denominator = ratios[i, 1];
                int units = (int)Math.Ceiling(Math.Max((double)width / numerator, (double)height / denominator));
                int candidateWidth = units * numerator, candidateHeight = units * denominator;
                long area = (long)candidateWidth * candidateHeight;
                if (area >= bestArea) continue;
                bestArea = area;
                prepared.ModelWidth = candidateWidth; prepared.ModelHeight = candidateHeight;
                prepared.ModelAspectRatio = numerator + ":" + denominator;
            }
            prepared.ModelImagePath = Path.Combine(Path.GetTempPath(), "Vanya-Fill-Canvas-" + Guid.NewGuid().ToString("N") + ".png");
            BitmapSource source = AiPrintTab.Bitmap(prepared.ImagePath);
            BitmapSource mask = AiPrintTab.Bitmap(prepared.MaskPath);
            byte[] sourcePixels = Pixels(source), maskPixels = Pixels(mask);
            int stride = prepared.ModelWidth * 4;
            byte[] pixels = new byte[checked(stride * prepared.ModelHeight)];
            for (int index = 0; index < pixels.Length; index += 4)
            { pixels[index] = pixels[index + 1] = pixels[index + 2] = 127; pixels[index + 3] = 255; }

            // Nano Banana accepts reference images, not an inpainting-mask
            // parameter. Send one spatially marked photograph instead of two
            // separate images that can be interpreted as a photo composition.
            // Opaque magenta marks missing canvas; a translucent tint marks
            // edits over existing content without hiding the source context.
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    int src = (y * width + x) * 4, dest = y * stride + x * 4;
                    double alpha = sourcePixels[src + 3] / 255.0;
                    double tint = maskPixels[src] < 128 ? 0 : (alpha == 0 ? 1 : 0.28);
                    for (int channel = 0; channel < 3; channel++)
                    {
                        double color = sourcePixels[src + channel] * alpha + 127 * (1 - alpha);
                        int marker = channel == 1 ? 0 : 255;
                        pixels[dest + channel] = (byte)Math.Round(color * (1 - tint) + marker * tint);
                    }
                }
            var canvas = BitmapSource.Create(prepared.ModelWidth, prepared.ModelHeight, 96, 96,
                PixelFormats.Bgra32, null, pixels, stride);
            SavePng(new FormatConvertedBitmap(canvas, PixelFormats.Bgr24, null, 0), prepared.ModelImagePath);
            Log.Info("Generative fill model canvas: " + prepared.ModelWidth + "x" + prepared.ModelHeight +
                ", " + prepared.ModelAspectRatio + ", original=" + width + "x" + height + ", single annotated photo");
        }

        private static Rect RectFor(double l, double t, double r, double b,
            double unionLeft, double unionTop, double unionWidth, double unionHeight,
            int width, int height)
        {
            return new Rect((l - unionLeft) / unionWidth * width,
                (unionTop - t) / unionHeight * height,
                (r - l) / unionWidth * width, (t - b) / unionHeight * height);
        }

        private static void Render(string path, int width, int height, Action<DrawingContext> draw,
            PixelFormat? outputFormat = null)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen()) draw(dc);
            var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            image.Render(visual);
            SavePng(outputFormat.HasValue ? (BitmapSource)new FormatConvertedBitmap(image,
                outputFormat.Value, null, 0) : image, path);
        }

        private static void SavePng(BitmapSource image, string path)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (Stream stream = File.Create(path)) encoder.Save(stream);
        }

        private static void TryDelete(string path)
        { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
