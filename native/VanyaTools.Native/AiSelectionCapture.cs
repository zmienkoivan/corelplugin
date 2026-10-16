using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    /// <summary>
    /// Captures the current CorelDRAW selection as a transparent PNG without
    /// changing the selected artwork. Corel COM calls must run on the UI/STA thread.
    /// </summary>
    internal static class AiSelectionCapture
    {
        private const int CaptureDpi = 400;

        public static string Capture(int maxDimension = 0, int maxPixels = 32000000,
            bool restoreSelection = true, bool refreshWindow = true)
        {
            return CaptureCore(CaptureDpi, maxDimension, maxPixels, false,
                restoreSelection, refreshWindow);
        }

        public static int EstimateDefaultCaptureDpi()
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Документ не открыт.");
            dynamic selected = app.ActiveSelectionRange;
            if (selected == null || Convert.ToInt32(selected.Count) == 0)
                throw new InvalidOperationException("Объект не выделен.");
            return ChooseDpi(doc, selected, CaptureDpi, 0, 32000000);
        }

        public static string CaptureForPublish(int dpi = 300)
        {
            if (dpi < 72 || dpi > 300) throw new InvalidOperationException("DPI публикации: от 72 до 300.");
            return CaptureCore(dpi, 0, 70000000, true, true, true);
        }

        internal static BitmapSource CaptureOrderFrame(bool restoreSelection)
        {
            // Match the final Telegram photo limits before rasterisation.
            // Return opaque pixels directly, without a PNG disk round trip.
            return CaptureBitmapCore(CaptureDpi, 9500, 24000000, false,
                restoreSelection, false, 9990, true);
        }

        public static long EstimatePublishPixels(int dpi, out double widthMm, out double heightMm)
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Сначала откройте документ CorelDRAW.");
            dynamic selected = app.ActiveSelectionRange;
            if (selected == null || Convert.ToInt32(selected.Count) == 0)
                throw new InvalidOperationException("Выделите макет для публикации.");
            int oldUnit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                widthMm = Convert.ToDouble(selected.SizeWidth);
                heightMm = Convert.ToDouble(selected.SizeHeight);
                if (widthMm <= 0 || heightMm <= 0 || Double.IsNaN(widthMm) || Double.IsNaN(heightMm))
                    throw new InvalidOperationException("Не удалось определить размер выделения CorelDRAW.");
                return checked((long)Math.Ceiling(widthMm * dpi / 25.4) *
                    (long)Math.Ceiling(heightMm * dpi / 25.4));
            }
            finally { doc.Unit = oldUnit; }
        }

        private static string CaptureCore(int desiredDpi, int maxDimension, int maxPixels,
            bool exactDpi, bool restoreSelection, bool refreshWindow)
        {
            string path = Path.Combine(Path.GetTempPath(), "Vanya-AI-Selection-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                BitmapSource image = CaptureBitmapCore(desiredDpi, maxDimension, maxPixels,
                    exactDpi, restoreSelection, refreshWindow);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var stream = File.Create(path)) encoder.Save(stream);
                return path;
            }
            catch
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                throw;
            }
        }

        private static BitmapSource CaptureBitmapCore(int desiredDpi, int maxDimension, int maxPixels,
            bool exactDpi, bool restoreSelection, bool refreshWindow, int maxDimensionSum = 0,
            bool whiteBackground = false)
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Сначала откройте документ CorelDRAW.");

            dynamic sourceRange = app.ActiveSelectionRange;
            if (sourceRange == null || Convert.ToInt32(sourceRange.Count) == 0)
                throw new InvalidOperationException("Выделите принт или несколько объектов на холсте CorelDRAW.");

            var originals = new System.Collections.Generic.List<dynamic>();
            foreach (dynamic shape in sourceRange.Shapes) originals.Add(shape);

            dynamic workingRange = null;
            dynamic workingShape = null;
            dynamic rasterShape = null;
            bool oldOptimization = false;
            bool commandGroupStarted = false;
            string step = "создание временной копии";
            var timer = Stopwatch.StartNew();

            try
            {
                try { oldOptimization = Convert.ToBoolean(app.Optimization); } catch { }
                doc.BeginCommandGroup("Vanya Tools - capture AI selection");
                commandGroupStarted = true;
                app.Optimization = true;

                // Duplicate first: grouping and rasterising only affect this temporary copy.
                workingRange = sourceRange.Duplicate(0, 0);
                if (workingRange == null || Convert.ToInt32(workingRange.Count) == 0)
                    throw new InvalidOperationException("Не удалось создать временную копию выделения.");
                if (Convert.ToInt32(workingRange.Count) == 1)
                {
                    foreach (dynamic duplicate in workingRange.Shapes)
                    {
                        workingShape = duplicate;
                        break;
                    }
                }
                else
                {
                    step = "группировка копии";
                    workingShape = workingRange.Group();
                }
                if (workingShape == null)
                    throw new InvalidOperationException("Не удалось получить временную копию объекта CorelDRAW.");
                Log.Info("Selection capture: duplicate/group " + timer.ElapsedMilliseconds + " ms.");
                step = "растрирование копии";
                int captureDpi = ChooseDpi(doc, workingShape, desiredDpi, maxDimension, maxPixels,
                    maxDimensionSum);
                if (exactDpi && captureDpi != desiredDpi)
                    throw new InvalidOperationException("Макет слишком велик для экспорта с заданным DPI (предел 70 Мп).");
                rasterShape = workingShape.ConvertToBitmapEx(
                    CorelConstants.CdrRgbColorImage,
                    true,  // transparent background
                    true,  // anti-aliasing
                    captureDpi,
                    CorelConstants.CdrNormalAntiAliasing,
                    true);
                if (rasterShape == null)
                    throw new InvalidOperationException("CorelDRAW не смог растрировать выделение.");
                Log.Info("Selection capture: raster ready at " + timer.ElapsedMilliseconds +
                    " ms, " + captureDpi + " DPI.");

                // Read the bitmap tiles directly. Corel export filters can return E_FAIL
                // even after rasterisation succeeds. The returned image owns
                // managed pixels and survives deletion of the temporary shape.
                step = "чтение пикселей";
                BitmapSource image = ReadBitmap(rasterShape.Bitmap, captureDpi, maxPixels, whiteBackground);
                Log.Info("Selection capture: pixels ready at " + timer.ElapsedMilliseconds +
                    " ms, " + image.PixelWidth + "x" + image.PixelHeight + ".");
                return image;
            }
            catch (Exception ex)
            {
                Log.Error("AI selection capture failed at: " + step, ex);
                if (ex is InvalidOperationException) throw;
                throw new InvalidOperationException("Не удалось подготовить выделение: " + step + ". " + ex.Message, ex);
            }
            finally
            {
                TryDelete(rasterShape);
                TryDelete(workingShape);
                TryDeleteRange(workingRange);
                if (restoreSelection) try
                {
                    for (int i = 0; i < originals.Count; i++)
                    {
                        if (i == 0) originals[i].CreateSelection();
                        else originals[i].AddToSelection();
                    }
                }
                catch { }
                try { if (commandGroupStarted) doc.EndCommandGroup(); } catch { }
                try { app.Optimization = oldOptimization; } catch { }
                if (refreshWindow) try { app.ActiveWindow.Refresh(); } catch { }
                Log.Info("Selection capture: cleanup complete at " + timer.ElapsedMilliseconds + " ms.");
            }
        }

        private static int ChooseDpi(dynamic doc, dynamic shape, int desiredDpi, int maxDimension, int maxPixels,
            int maxDimensionSum = 0)
        {
            int oldUnit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                double widthMm = Convert.ToDouble(shape.SizeWidth);
                double heightMm = Convert.ToDouble(shape.SizeHeight);
                if (widthMm <= 0 || heightMm <= 0 || Double.IsNaN(widthMm) || Double.IsNaN(heightMm))
                    throw new InvalidOperationException("Не удалось определить размер выделения CorelDRAW.");
                double widthPx = widthMm * desiredDpi / 25.4;
                double heightPx = heightMm * desiredDpi / 25.4;
                double scale = 1.0;
                if (maxDimension > 0) scale = Math.Min(scale, maxDimension / Math.Max(widthPx, heightPx));
                if (maxDimensionSum > 0) scale = Math.Min(scale, maxDimensionSum / (widthPx + heightPx));
                if (maxPixels > 0) scale = Math.Min(scale, Math.Sqrt(maxPixels / (widthPx * heightPx)));
                if (scale < 1.0) scale *= 0.97; // Room for Corel's edge rounding and anti-aliasing.
                return Math.Max(1, Math.Min(desiredDpi, (int)Math.Floor(desiredDpi * scale)));
            }
            finally { doc.Unit = oldUnit; }
        }

        private static BitmapSource ReadBitmap(dynamic bitmap, int captureDpi, int maxPixels,
            bool whiteBackground)
        {
            dynamic colorImage = bitmap.Image;
            if (colorImage == null)
                throw new InvalidOperationException("CorelDRAW не вернул цветовые пиксели растра.");
            int width = Convert.ToInt32(colorImage.Width);
            int height = Convert.ToInt32(colorImage.Height);
            if (width <= 0 || height <= 0 || (long)width * height > maxPixels)
                throw new InvalidOperationException("Размер выделения слишком велик для обработки. Выделите только нужную графику.");

            dynamic alphaImage = null;
            if (Convert.ToBoolean(bitmap.Transparent))
            {
                alphaImage = bitmap.ImageAlpha;
                if (alphaImage == null)
                    throw new InvalidOperationException("CorelDRAW не вернул альфа-канал растра.");
            }
            byte[] pixels = new byte[checked(width * height * 4)];
            if (alphaImage == null)
                for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

            CopyTiles(colorImage, pixels, width, height, false);
            if (alphaImage != null) CopyTiles(alphaImage, pixels, width, height, true);
            if (whiteBackground && alphaImage != null)
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    int alpha = pixels[i + 3];
                    if (alpha == 255) continue;
                    int white = 255 * (255 - alpha);
                    pixels[i] = (byte)((pixels[i] * alpha + white + 127) / 255);
                    pixels[i + 1] = (byte)((pixels[i + 1] * alpha + white + 127) / 255);
                    pixels[i + 2] = (byte)((pixels[i + 2] * alpha + white + 127) / 255);
                    pixels[i + 3] = 255;
                }

            var image = BitmapSource.Create(width, height, captureDpi, captureDpi,
                PixelFormats.Bgra32, null, pixels, width * 4);
            image.Freeze();
            return image;
        }

        private static void CopyTiles(dynamic image, byte[] pixels, int width, int height, bool alpha)
        {
            dynamic tiles = image.Tiles;
            int count = Convert.ToInt32(tiles.Count);
            if (count == 0) throw new InvalidOperationException("CorelDRAW не вернул тайлы изображения.");
            for (int i = 1; i <= count; i++)
            {
                dynamic tile = tiles.Item[i];
                int left = Convert.ToInt32(tile.Left);
                int top = Convert.ToInt32(tile.Top);
                int tileWidth = Convert.ToInt32(tile.Width);
                int tileHeight = Convert.ToInt32(tile.Height);
                int stride = Math.Abs(Convert.ToInt32(tile.BytesPerLine));
                int bpp = Convert.ToInt32(tile.BytesPerPixel);
                byte[] data = (byte[])tile.PixelData;
                if (bpp < (alpha ? 1 : 3) || stride < tileWidth * bpp ||
                    data == null || data.Length < stride * tileHeight)
                    throw new InvalidOperationException("CorelDRAW вернул повреждённый тайл растра.");

                int bottom = top - tileHeight + 1;
                for (int y = 0; y < tileHeight; y++)
                {
                    int imageY = bottom + y;
                    if (imageY < 0 || imageY >= height) continue;
                    int destinationRow = (height - 1 - imageY) * width * 4;
                    int sourceRow = y * stride;
                    for (int x = 0; x < tileWidth; x++)
                    {
                        int imageX = left + x;
                        if (imageX < 0 || imageX >= width) continue;
                        int destination = destinationRow + imageX * 4;
                        int source = sourceRow + x * bpp;
                        if (alpha) pixels[destination + 3] = data[source];
                        else
                        {
                            pixels[destination] = data[source];
                            pixels[destination + 1] = data[source + 1];
                            pixels[destination + 2] = data[source + 2];
                        }
                    }
                }
            }
        }

        private static void TryDelete(dynamic shape)
        {
            try { if (shape != null) shape.Delete(); } catch { }
        }

        private static void TryDeleteRange(dynamic range)
        {
            try { if (range != null) range.Delete(); } catch { }
        }
    }
}
