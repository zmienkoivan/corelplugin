using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class OrderFrameCapturePlan
    {
        internal const int MaximumPixels = 24000000, MaximumSide = 9500, MaximumSum = 9990;
        internal int Width, Height, Dpi;

        internal static OrderFrameCapturePlan Build(double widthMm, double heightMm)
        {
            if (!Finite(widthMm) || !Finite(heightMm) || widthMm <= 0 || heightMm <= 0)
                throw new InvalidOperationException("Не удалось определить размер выделения для кадра.");
            double x = widthMm * 400 / 25.4, y = heightMm * 400 / 25.4;
            double scale = Math.Min(1, Math.Min(MaximumSide / Math.Max(x, y),
                Math.Min(MaximumSum / (x + y), Math.Sqrt(MaximumPixels / (x * y)))));
            if (scale < 1) scale *= .97;
            int dpi = Math.Max(1, Math.Min(400, (int)Math.Floor(400 * scale)));
            double width = Math.Ceiling(widthMm * dpi / 25.4), height = Math.Ceiling(heightMm * dpi / 25.4);
            if (!Finite(width) || !Finite(height) || width > MaximumSide || height > MaximumSide ||
                width + height > MaximumSum || width * height > MaximumPixels)
                throw new InvalidOperationException("Макет слишком велик для кадра. Выделите меньшую область.");
            return new OrderFrameCapturePlan { Width = Math.Max(1, (int)width), Height = Math.Max(1, (int)height), Dpi = dpi };
        }

        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    // Render the original selection in Corel. Duplicating mixed text/effect ranges
    // can change their native stacking order before rasterisation.
    internal static class OrderFrameCapture
    {
        internal static BitmapSource Capture(bool restoreSelection)
        {
            RequireSta();
            return Capture((object)CorelApp.Get(), restoreSelection);
        }

        internal static BitmapSource Capture(object application, bool restoreSelection)
        {
            RequireSta();
            if (application == null) throw new InvalidOperationException("CorelDRAW недоступен.");
            dynamic app = application, doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ и выделите макет.");
            object sourceDocument = (object)doc;
            dynamic selection = app.ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) < 1)
                throw new InvalidOperationException("Выделите макет для кадра карточки.");
            int oldUnit = Convert.ToInt32(doc.Unit);
            bool oldOptimization = Convert.ToBoolean(app.Optimization);
            string workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Vanya-OrderFrame-" + Guid.NewGuid().ToString("N")));
            string path = Path.Combine(workspace, "capture.png");
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                double left, bottom, width, height;
                selection.GetBoundingBox(out left, out bottom, out width, out height, true);
                if (Double.IsNaN(left) || Double.IsInfinity(left) || Double.IsNaN(bottom) || Double.IsInfinity(bottom))
                    throw new InvalidOperationException("Не удалось определить границы выделения для кадра.");
                OrderFrameCapturePlan plan = OrderFrameCapturePlan.Build(width, height);
                Directory.CreateDirectory(workspace);
                app.Optimization = false;
                object options = (object)app.CreateStructExportOptions();
                Set(options, "SizeX", plan.Width); Set(options, "SizeY", plan.Height);
                Set(options, "ResolutionX", plan.Dpi); Set(options, "ResolutionY", plan.Dpi);
                Set(options, "ImageType", CorelConstants.CdrRgbColorImage);
                Set(options, "AntiAliasingType", CorelConstants.CdrNormalAntiAliasing);
                Set(options, "Transparent", false); Set(options, "Overwrite", true);
                Set(options, "UseColorProfile", true);
                object palette = (object)app.CreateStructPaletteOptions();
                RequireDocument((object)app, sourceDocument);
                Log.Info("Order frame: direct selection PNG, objects=" + Convert.ToInt32(selection.Count) +
                    ", " + plan.Width + "x" + plan.Height + ", " + plan.Dpi + " DPI.");
                object filter = sourceDocument.GetType().InvokeMember("ExportEx", BindingFlags.InvokeMethod,
                    null, sourceDocument, new object[] { new BStrWrapper(path), CorelConstants.CdrPng,
                        CorelConstants.CdrSelection, options, palette });
                if (filter == null) throw new InvalidOperationException("CorelDRAW не открыл фильтр PNG для кадра.");
                filter.GetType().InvokeMember("Finish", BindingFlags.InvokeMethod, null, filter, null);
                RequireDocument((object)app, sourceDocument);
                if (!File.Exists(path)) throw new InvalidOperationException("CorelDRAW не создал изображение кадра.");
                using (var stream = File.OpenRead(path))
                {
                    BitmapFrame header = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    int actualWidth = header.PixelWidth, actualHeight = header.PixelHeight;
                    if (actualWidth <= 0 || actualHeight <= 0 || (long)actualWidth * actualHeight > OrderFrameCapturePlan.MaximumPixels ||
                        actualWidth > OrderFrameCapturePlan.MaximumSide || actualHeight > OrderFrameCapturePlan.MaximumSide ||
                        actualWidth + actualHeight > OrderFrameCapturePlan.MaximumSum || Math.Abs(actualWidth - plan.Width) > 2 ||
                        Math.Abs(actualHeight - plan.Height) > 2)
                        throw new InvalidOperationException("Размер кадра PNG не совпадает с выделением. Ожидалось " +
                            plan.Width + "×" + plan.Height + ", получено " + actualWidth + "×" + actualHeight + ". Кадр не сохранён.");
                    stream.Position = 0;
                    BitmapFrame result = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                    result.Freeze();
                    return result;
                }
            }
            catch (Exception error)
            {
                Log.Error("Direct order frame capture failed.", error);
                throw new InvalidOperationException("Не удалось создать кадр из исходного выделения. " + error.GetBaseException().Message, error);
            }
            finally
            {
                try { doc.Unit = oldUnit; } catch (Exception error) { Log.Error("Could not restore order-frame units.", error); }
                try { app.Optimization = oldOptimization; } catch (Exception error) { Log.Error("Could not restore order-frame optimization.", error); }
                if (restoreSelection)
                    try { if (SameObject((object)app.ActiveDocument, sourceDocument)) selection.CreateSelection(); }
                    catch (Exception error) { Log.Error("Could not restore order-frame selection.", error); }
                // Delete only the exact PNG and its own empty GUID directory.
                try { if (File.Exists(path)) File.Delete(path); } catch (Exception error) { Log.Error("Could not delete temporary order-frame PNG.", error); }
                try { if (Directory.Exists(workspace)) Directory.Delete(workspace, false); } catch (Exception error) { Log.Error("Could not remove empty order-frame directory.", error); }
            }
        }

        private static void RequireDocument(object application, object source)
        {
            if (!SameObject((object)((dynamic)application).ActiveDocument, source))
                throw new InvalidOperationException("Активный CDR изменился во время создания кадра. Повторите захват в исходном файле.");
        }
        private static void RequireSta()
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Кадр нужно создавать в интерфейсе CorelDRAW.");
        }
        private static void Set(object target, string name, object value) => target.GetType().InvokeMember(name,
            BindingFlags.SetProperty, null, target, new[] { value }, CultureInfo.InvariantCulture);
        private static bool SameObject(object first, object second)
        {
            if (ReferenceEquals(first, second)) return true;
            if (first == null || second == null) return false;
            IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
            try { a = Marshal.GetIUnknownForObject(first); b = Marshal.GetIUnknownForObject(second); return a == b; }
            catch { return false; }
            finally { if (a != IntPtr.Zero) Marshal.Release(a); if (b != IntPtr.Zero) Marshal.Release(b); }
        }
    }
}
