using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class NativeRasterCorrection
    {
        private const string SourcePrefix = "Vanya-Native-Raster-";

        internal static string CaptureIfBitmap(int maxPixels = NativeRasterFiles.MaximumPixels, Action<long> memoryCheck = null)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Исходный растр нужно читать в интерфейсе Corel.");
            return CaptureIfBitmap((object)CorelApp.Get(), maxPixels, memoryCheck);
        }

        internal static string CaptureIfBitmap(object application, int maxPixels = NativeRasterFiles.MaximumPixels, Action<long> memoryCheck = null)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Исходный растр нужно читать в интерфейсе Corel.");
            dynamic app = application, doc = app.ActiveDocument, range = app.ActiveSelectionRange;
            if (doc == null || range == null || Convert.ToInt32(range.Count) != 1) return null;
            dynamic shape = range.Shapes.Item[1];
            if (Convert.ToInt32(shape.Type) != CorelConstants.CdrBitmapShape) return null;
            double a, b, c, d, tx, ty;
            shape.GetMatrix(out a, out b, out c, out d, out tx, out ty);
            if (!Finite(a) || !Finite(b) || !Finite(c) || !Finite(d) || !Finite(tx) || !Finite(ty) ||
                a <= 0 || d <= 0 || Math.Abs(b) > 1e-8 * Math.Max(1, Math.Abs(a)) ||
                Math.Abs(c) > 1e-8 * Math.Max(1, Math.Abs(d)))
                throw new InvalidOperationException("Для обработки без изменения цветовой модели нужен растр без поворота, зеркалирования и наклона. Сначала подготовьте отдельную копию в Corel.");
            if (Convert.ToInt32(shape.Effects.Count) != 0 || Convert.ToInt32(shape.Transparency.Type) != 0)
                throw new InvalidOperationException("У растра есть объектные эффекты или прозрачность Corel. Для точного сохранения каналов сначала примените их к отдельной копии растра.");
            if (Convert.ToBoolean(shape.Bitmap.CropEnvelopeModified))
                throw new InvalidOperationException("Сначала завершите обрезку растра в Corel. Незавершённая рамка обрезки не должна менять исходные цветовые каналы.");

            long pixels = checked(Convert.ToInt64(shape.Bitmap.Image.Width) * Convert.ToInt64(shape.Bitmap.Image.Height));
            if (pixels > maxPixels) throw new InvalidOperationException("Исходный растр слишком велик для обработки без изменения разрешения.");
            if (pixels > 2000000) memoryCheck?.Invoke(pixels);
            var buffer = NativeRasterModelIO.Capture((object)shape.Bitmap, maxPixels);
            if (buffer.Mode == 5) buffer = buffer.WithCmykOrder(NativeRasterModelIO.CalibrateCmykOrder((object)doc, application));
            int oldUnit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                double width = Convert.ToDouble(shape.SizeWidth), height = Convert.ToDouble(shape.SizeHeight);
                if (!Finite(width) || !Finite(height) || width <= 0 || height <= 0)
                    throw new InvalidOperationException("Не удалось определить физический размер растра.");
                buffer = new NativeRasterBuffer(buffer.Width, buffer.Height, buffer.Mode,
                    buffer.Width * 25.4 / width, buffer.Height * 25.4 / height,
                    buffer.Colors, buffer.Alpha, buffer.CmykOrder);
            }
            finally { doc.Unit = oldUnit; }
            string path = Path.Combine(Path.GetTempPath(), SourcePrefix + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                Save(path, buffer, CancellationToken.None);
                Log.Info("Native raster capture: model=" + buffer.Mode + ", " + buffer.Width + "x" + buffer.Height + ".");
                return path;
            }
            catch { Delete(path); Delete(path + NativeRasterFiles.Suffix); throw; }
        }

        internal static bool HasSource(string source)
        {
            if (File.Exists(source + NativeRasterFiles.Suffix)) return true;
            if (Path.GetFileName(source).StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Не найдены исходные цветовые каналы растра. Повторите захват.");
            return false;
        }

        internal static void CleanupCapture(string source)
        {
            if (!OwnedTemporary(source) || !Path.GetFileName(source).StartsWith(SourcePrefix, StringComparison.OrdinalIgnoreCase)) return;
            Delete(source); Delete(source + NativeRasterFiles.Suffix);
        }

        internal static void CleanupResultCompanion(string output)
        { if (OwnedTemporary(output)) Delete(output + NativeRasterFiles.Suffix); }

        private static bool OwnedTemporary(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            try
            {
                string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(path).StartsWith("Vanya-", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        internal static DtfCorrectionResult Process(string source, string output, DtfCorrectionOptions options,
            CancellationToken cancellation)
        {
            NativeRasterBuffer buffer = NativeRasterFiles.Read(source + NativeRasterFiles.Suffix, source, cancellation);
            var nativeOptions = new NativeRasterFilterOptions {
                RemoveSemiTransparent = options.RemoveSemiTransparent, Threshold = options.Threshold,
                SmoothStairs = options.SmoothStairs, StairsScale = options.StairsScale,
                Outline = options.Outline, RadiusMm = options.RadiusMm, ExpansionMm = options.ExpansionMm,
                OutlineColor = ColorSamples(buffer, options.Color), OutlineOpacity = options.Color.A,
                WhitenNearWhite = options.WhitenNearWhite, NearWhiteTolerance = options.NearWhiteTolerance
            };
            NativeRasterFilterResult result = NativeRasterFilters.Process(buffer, nativeOptions, cancellation);
            Save(output, result.Buffer, cancellation);
            return new DtfCorrectionResult { OutputPath = output, OffsetMm = result.OffsetMm };
        }

        // Color-based background removal edits alpha only. Keep every native
        // color sample, even the now invisible samples outside the silhouette.
        internal static void AttachProcessedAlpha(string source, string output, CancellationToken cancellation, Color[] backgroundColors = null)
        {
            if (!HasSource(source)) return;
            var buffer = NativeRasterFiles.Read(source + NativeRasterFiles.Suffix, source, cancellation);
            BitmapSource image = Load(output);
            if (image.PixelWidth != buffer.Width || image.PixelHeight != buffer.Height)
                throw new InvalidOperationException("Размер удаления фона не совпадает с исходным растром. Цветовая модель не преобразована; результат не вставлен.");
            var converted = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var rgba = new byte[checked(buffer.Width * buffer.Height * 4)];
            converted.CopyPixels(rgba, checked(buffer.Width * 4), 0);
            byte[] before = backgroundColors == null || backgroundColors.Length == 0 ? null : buffer.ToPreviewBgra(cancellation);
            for (int p = 0; p < buffer.Alpha.Length; p++)
            {
                if ((p & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int index = p * 4; byte alpha = rgba[index + 3];
                // Remove a background matte only where the background processor
                // explicitly changed fringe colors. Do the same arithmetic in
                // the original channels, keeping every other sample untouched.
                if (before != null && buffer.Alpha[p] >= 250 && alpha > 0 && alpha < 250 &&
                    (before[index] != rgba[index] || before[index + 1] != rgba[index + 1] || before[index + 2] != rgba[index + 2]))
                {
                    Color chosen = backgroundColors[0]; int closest = Int32.MaxValue;
                    foreach (Color color in backgroundColors)
                    {
                        int distance = Math.Abs(before[index] - color.B) + Math.Abs(before[index + 1] - color.G) + Math.Abs(before[index + 2] - color.R);
                        if (distance < closest) { closest = distance; chosen = color; }
                    }
                    byte[] background = ColorSamples(buffer, chosen);
                    double coverage = alpha / 255.0;
                    for (int channel = 0; channel < buffer.Channels; channel++)
                    {
                        int location = p * buffer.Channels + channel;
                        double foreground = (buffer.Colors[location] - background[channel] * (1 - coverage)) / coverage;
                        buffer.Colors[location] = (byte)Math.Max(0, Math.Min(255, Math.Round(foreground)));
                    }
                }
                buffer.Alpha[p] = alpha;
            }
            Save(output, buffer, cancellation);
        }

        internal static void AttachDiagnostic(string source, string output, CancellationToken cancellation)
        {
            if (!HasSource(source)) return;
            var buffer = NativeRasterFiles.Read(source + NativeRasterFiles.Suffix, source, cancellation);
            byte[] before = buffer.ToPreviewBgra(cancellation);
            BitmapSource image = Load(output);
            if (image.PixelWidth != buffer.Width || image.PixelHeight != buffer.Height)
                throw new InvalidOperationException("Размер диагностической копии не совпадает с исходным растром.");
            BitmapSource converted = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            byte[] after = new byte[before.Length]; converted.CopyPixels(after, checked(buffer.Width * 4), 0);
            for (int p = 0; p < buffer.Alpha.Length; p++)
            {
                if ((p & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int i = p * 4;
                if (before[i] != after[i] || before[i + 1] != after[i + 1] || before[i + 2] != after[i + 2])
                {
                    byte[] samples = ColorSamples(buffer, Color.FromArgb(after[i + 3], after[i + 2], after[i + 1], after[i]));
                    Buffer.BlockCopy(samples, 0, buffer.Colors, p * buffer.Channels, buffer.Channels);
                }
                buffer.Alpha[p] = after[i + 3];
            }
            Save(output, buffer, cancellation);
        }

        internal static void Save(string output, NativeRasterBuffer buffer, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            byte[] bgra = buffer.ToPreviewBgra(cancellation);
            var image = BitmapSource.Create(buffer.Width, buffer.Height, buffer.DpiX, buffer.DpiY,
                PixelFormats.Bgra32, null, bgra, checked(buffer.Width * 4));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            string directory = Path.GetDirectoryName(Path.GetFullPath(output));
            string pending = StagingPath(directory, ".tmp");
            string native = output + NativeRasterFiles.Suffix;
            string pendingNative = StagingPath(directory, ".tmp");
            string previewBackup = StagingPath(directory, ".bak");
            string nativeBackup = StagingPath(directory, ".bak");
            bool previewExisted = false, nativeExisted = false, previewAttempted = false, nativeAttempted = false;
            bool removeBackups = true;
            try
            {
                using (var stream = File.Create(pending)) encoder.Save(stream);
                cancellation.ThrowIfCancellationRequested();
                NativeRasterFiles.Write(pendingNative, pending, buffer, cancellation);
                cancellation.ThrowIfCancellationRequested();
                previewExisted = File.Exists(output); nativeExisted = File.Exists(native);
                // Prepare both files first. Once commit starts, finish or roll back
                // the pair without observing cancellation between the two renames.
                previewAttempted = true; PublishFile(pending, output, previewBackup, previewExisted);
                nativeAttempted = true; PublishFile(pendingNative, native, nativeBackup, nativeExisted);
            }
            catch (Exception error)
            {
                var failures = new List<Exception>();
                if (nativeAttempted) try { RestoreFile(pendingNative, native, nativeBackup, nativeExisted); } catch (Exception restore) { failures.Add(restore); }
                if (previewAttempted) try { RestoreFile(pending, output, previewBackup, previewExisted); } catch (Exception restore) { failures.Add(restore); }
                if (failures.Count != 0)
                {
                    removeBackups = false; failures.Insert(0, error);
                    throw new InvalidOperationException("Не удалось восстановить прежние файлы результата. Резервные копии сохранены: " +
                        previewBackup + "; " + nativeBackup, new AggregateException(failures));
                }
                throw;
            }
            finally
            {
                Delete(pending); Delete(pendingNative);
                if (removeBackups) { Delete(previewBackup); Delete(nativeBackup); }
            }
        }

        private static void PublishFile(string pending, string output, string backup, bool existed)
        {
            if (existed) File.Replace(pending, output, backup); else File.Move(pending, output);
        }

        private static string StagingPath(string directory, string extension) =>
            Path.Combine(directory, ".vnr-" + Guid.NewGuid().ToString("N") + extension);

        private static void RestoreFile(string pending, string output, string backup, bool existed)
        {
            bool hasBackup = File.Exists(backup);
            if (!hasBackup && File.Exists(pending)) return; // The rename did not publish anything.
            if (existed)
            {
                if (!hasBackup) throw new IOException("Не найдена резервная копия: " + backup);
                if (File.Exists(output)) File.Replace(backup, output, null); else File.Move(backup, output);
            }
            else if (File.Exists(output)) File.Delete(output);
        }

        internal static byte[] ColorSamples(NativeRasterBuffer buffer, Color color)
        {
            if (buffer.Mode == 4) return new[] { color.B, color.G, color.R };
            if (buffer.Mode == 2) return new[] { (byte)((color.R * 299 + color.G * 587 + color.B * 114 + 500) / 1000) };
            int maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            var result = new byte[4]; int[] order = buffer.CmykOrder;
            if (order == null) throw new InvalidOperationException("Не определён порядок каналов CMYK.");
            if (maximum > 0)
            {
                result[order[0]] = (byte)((maximum - color.R) * 255 / maximum);
                result[order[1]] = (byte)((maximum - color.G) * 255 / maximum);
                result[order[2]] = (byte)((maximum - color.B) * 255 / maximum);
            }
            result[order[3]] = (byte)(255 - maximum);
            return result;
        }

        private static BitmapSource Load(string path)
        {
            using (var stream = File.OpenRead(path))
            { var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image.Freeze(); return image; }
        }
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
        private static void Delete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    }
}
