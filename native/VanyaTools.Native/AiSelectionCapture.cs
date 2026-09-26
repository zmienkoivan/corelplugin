using System;
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
        public static string Capture()
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
            string path = Path.Combine(Path.GetTempPath(), "Vanya-AI-Selection-" + Guid.NewGuid().ToString("N") + ".png");
            bool commandGroupStarted = false;
            string step = "создание временной копии";

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
                step = "растрирование копии";
                rasterShape = workingShape.ConvertToBitmapEx(
                    CorelConstants.CdrRgbColorImage,
                    true,  // transparent background
                    true,  // anti-aliasing
                    300,
                    CorelConstants.CdrNormalAntiAliasing,
                    true);
                if (rasterShape == null)
                    throw new InvalidOperationException("CorelDRAW не смог растрировать выделение.");

                // Read the bitmap tiles directly. Corel export filters can return E_FAIL
                // even after rasterisation succeeds; WPF writes the PNG instead.
                step = "чтение пикселей и сохранение PNG";
                WritePng(rasterShape.Bitmap, path);
                if (!File.Exists(path) || new FileInfo(path).Length == 0)
                    throw new InvalidOperationException("CorelDRAW не создал PNG выделения.");
                return path;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { }
                Log.Error("AI selection capture failed at: " + step, ex);
                if (ex is InvalidOperationException) throw;
                throw new InvalidOperationException("Не удалось подготовить выделение: " + step + ". " + ex.Message, ex);
            }
            finally
            {
                TryDelete(rasterShape);
                TryDelete(workingShape);
                TryDeleteRange(workingRange);
                try
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
                try { app.ActiveWindow.Refresh(); } catch { }
            }
        }

        private static void WritePng(dynamic bitmap, string path)
        {
            dynamic colorImage = bitmap.Image;
            if (colorImage == null)
                throw new InvalidOperationException("CorelDRAW не вернул цветовые пиксели растра.");
            int width = Convert.ToInt32(colorImage.Width);
            int height = Convert.ToInt32(colorImage.Height);
            if (width <= 0 || height <= 0 || (long)width * height > 32000000)
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

            var image = BitmapSource.Create(width, height, 300, 300,
                PixelFormats.Bgra32, null, pixels, width * 4);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path)) encoder.Save(stream);
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
