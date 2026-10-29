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
        internal const int DefaultArtworkMaxPixels = 150000000;
        internal const int MaximumArtworkPixels = 250000000;
        private const int ArtworkMaxSide = 20000;

        public static string Capture(int maxDimension = 0, int maxPixels = 32000000,
            bool restoreSelection = true, bool refreshWindow = true)
        {
            return CaptureCore(CaptureDpi, maxDimension, maxPixels, false,
                restoreSelection, refreshWindow);
        }

        public static string CaptureForCorrection()
        {
            string native = NativeRasterCorrection.CaptureIfBitmap(memoryCheck: RasterCaptureMemory.EnsureAvailable);
            if (native != null) return native;
            // Large prints should retain 400 DPI when memory permits. The
            // source is captured once; interactive previews use reduced copies.
            return CaptureCore(CaptureDpi, ArtworkMaxSide, 70000000, false, true, true, true);
        }

        public static int EstimateDefaultCaptureDpi() => EstimateCaptureDpi(0, 32000000);

        public static int EstimateCorrectionCaptureDpi() => EstimateCaptureDpi(ArtworkMaxSide, 70000000);

        private static int EstimateCaptureDpi(int maxDimension, int maxPixels)
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Документ не открыт.");
            dynamic selected = app.ActiveSelectionRange;
            if (selected == null || Convert.ToInt32(selected.Count) == 0)
                throw new InvalidOperationException("Объект не выделен.");
            return ChooseDpi(doc, selected, CaptureDpi, maxDimension, maxPixels);
        }

        public static string CaptureForPublish(int dpi = 300)
        {
            if (dpi < 72 || dpi > 300) throw new InvalidOperationException("DPI публикации: от 72 до 300.");
            return CaptureCore(dpi, 0, 70000000, true, true, true);
        }

        public static string CaptureForArtwork(int dpi = 300, int maxPixels = DefaultArtworkMaxPixels)
        {
            if (dpi < 72 || dpi > 300) throw new InvalidOperationException("DPI рисунка: от 72 до 300.");
            if (maxPixels < 100000000 || maxPixels > MaximumArtworkPixels)
                throw new InvalidOperationException("Предел рисунка: от 100 до 250 Мп.");
            return CaptureCore(dpi, ArtworkMaxSide, maxPixels, true, true, true, true);
        }

        internal static BitmapSource CaptureOrderFrame(bool restoreSelection)
        {
            return OrderFrameCapture.Capture(restoreSelection);
        }

        public static long EstimatePublishPixels(int dpi, out double widthMm, out double heightMm)
        {
            dynamic app = CorelApp.Get();
            dynamic doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Сначала откройте документ CorelDRAW.");
            dynamic selected = app.ActiveSelectionRange;
            if (selected == null || Convert.ToInt32(selected.Count) == 0)
                throw new InvalidOperationException("Выделите нужный макет на холсте.");
            return EstimateShapePixels(doc, selected, dpi, out widthMm, out heightMm);
        }

        private static long EstimateShapePixels(dynamic doc, dynamic shape, int dpi,
            out double widthMm, out double heightMm)
        {
            int oldUnit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                widthMm = Convert.ToDouble(shape.SizeWidth);
                heightMm = Convert.ToDouble(shape.SizeHeight);
                if (widthMm <= 0 || heightMm <= 0 || Double.IsNaN(widthMm) || Double.IsNaN(heightMm) ||
                    Double.IsInfinity(widthMm) || Double.IsInfinity(heightMm))
                    throw new InvalidOperationException("Не удалось определить размер выделения CorelDRAW.");
                return checked((long)Math.Ceiling(widthMm * dpi / 25.4) *
                    (long)Math.Ceiling(heightMm * dpi / 25.4));
            }
            finally { doc.Unit = oldUnit; }
        }

        private static string CaptureCore(int desiredDpi, int maxDimension, int maxPixels,
            bool exactDpi, bool restoreSelection, bool refreshWindow, bool checkMemory = false)
        {
            string path = Path.Combine(Path.GetTempPath(), "Vanya-AI-Selection-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                BitmapSource image = CaptureBitmapCore(desiredDpi, maxDimension, maxPixels,
                    exactDpi, restoreSelection, refreshWindow, checkMemory: checkMemory);
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
            bool whiteBackground = false, bool checkMemory = false)
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
            string step = "проверка размера и памяти";
            var timer = Stopwatch.StartNew();

            try
            {
                try { oldOptimization = Convert.ToBoolean(app.Optimization); } catch { }
                int captureDpi = ChooseDpi(doc, sourceRange, desiredDpi, maxDimension, maxPixels, maxDimensionSum);
                if (exactDpi && captureDpi != desiredDpi)
                    throw SizeLimitError(desiredDpi, maxPixels, maxDimension);
                if (checkMemory)
                {
                    double width, height;
                    long pixels = EstimateShapePixels(doc, sourceRange, captureDpi, out width, out height);
                    RasterCaptureMemory.EnsureAvailable(pixels);
                    Log.Info("Artwork capture: " + width + "x" + height + " mm, " + pixels +
                        " pixels, limit=" + maxPixels + ", " + captureDpi + " DPI.");
                }
                step = "создание временной копии";
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
                captureDpi = ChooseDpi(doc, workingShape, desiredDpi, maxDimension, maxPixels,
                    maxDimensionSum);
                if (exactDpi && captureDpi != desiredDpi)
                    throw SizeLimitError(desiredDpi, maxPixels, maxDimension);
                if (checkMemory)
                {
                    // Duplication itself can consume substantial native memory.
                    double width, height;
                    RasterCaptureMemory.EnsureAvailable(EstimateShapePixels(doc, workingShape, captureDpi, out width, out height));
                }
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
                if (restoreSelection && workingRange != null) try
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

        private static InvalidOperationException SizeLimitError(int dpi, int maxPixels, int maxDimension) =>
            new InvalidOperationException("Макет слишком велик для экспорта при " + dpi + " DPI: предел " +
                (maxPixels / 1000000.0).ToString("0.#") + " Мп" +
                (maxDimension > 0 ? ", до " + maxDimension + " пикселей по каждой стороне" : "") +
                ". Выделите только нужную графику или измените предел захвата.");

        private static int ChooseDpi(dynamic doc, dynamic shape, int desiredDpi, int maxDimension, int maxPixels,
            int maxDimensionSum = 0)
        {
            int oldUnit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                double widthMm = Convert.ToDouble(shape.SizeWidth);
                double heightMm = Convert.ToDouble(shape.SizeHeight);
                if (widthMm <= 0 || heightMm <= 0 || Double.IsNaN(widthMm) || Double.IsNaN(heightMm) ||
                    Double.IsInfinity(widthMm) || Double.IsInfinity(heightMm))
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
            bool whiteBackground) => NativeBitmapPixelReader.Read((object)bitmap, captureDpi, maxPixels, whiteBackground);

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
