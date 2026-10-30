using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class PhotoshopExportPlan
    {
        internal const int MaximumSide = 20000, MaximumPixels = 150000000;
        internal int Width, Height, Dpi;
        internal static PhotoshopExportPlan Build(double widthMm, double heightMm, int dpi)
        {
            if (dpi != 150 && dpi != 300 && dpi != 400 && dpi != 600)
                throw new InvalidOperationException("Для Photoshop выберите 150, 300, 400 или 600 DPI.");
            double width = Math.Ceiling(widthMm * dpi / 25.4), height = Math.Ceiling(heightMm * dpi / 25.4);
            if (!Finite(width) || !Finite(height) || width <= 0 || height <= 0)
                throw new InvalidOperationException("Не удалось определить размер изображения для Photoshop.");
            if (width > MaximumSide || height > MaximumSide || width * height > MaximumPixels)
                throw new InvalidOperationException("Изображение слишком велико для Photoshop: предел 150 Мп и 20 000 px по стороне. " +
                    "Уменьшите выделение или DPI в настройках Photoshop. Разрешение автоматически не снижается.");
            return new PhotoshopExportPlan { Width = (int)width, Height = (int)height, Dpi = dpi };
        }
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    internal static class PhotoshopPngExport
    {
        internal static PhotoshopExportPlan Export(object application, string path, int dpi, bool selected,
            Action<long> memoryCheck)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Экспорт нужно запускать в интерфейсе CorelDRAW.");
            if (File.Exists(path)) throw new InvalidOperationException("Файл экспорта уже существует.");
            dynamic app = application, document = app.ActiveDocument;
            if (document == null) throw new InvalidOperationException("Сначала откройте документ CorelDRAW.");
            dynamic page = document.ActivePage, range = app.ActiveSelectionRange;
            if (selected && (range == null || Convert.ToInt32(range.Count) == 0))
                throw new InvalidOperationException("Выделите объекты для Photoshop.");
            int oldUnit = Convert.ToInt32(document.Unit);
            bool oldOptimization = Convert.ToBoolean(app.Optimization);
            PrintSelectionWorkspace workspace = null;
            bool complete = false;
            try
            {
                document.Unit = CorelConstants.CdrMillimeter;
                double width, height;
                if (selected)
                {
                    double left, bottom;
                    range.GetBoundingBox(out left, out bottom, out width, out height, true);
                }
                else { width = Convert.ToDouble(page.SizeWidth); height = Convert.ToDouble(page.SizeHeight); }
                PhotoshopExportPlan plan = PhotoshopExportPlan.Build(width, height, dpi);
                memoryCheck?.Invoke((long)plan.Width * plan.Height);
                object target = (object)document;
                if (selected)
                {
                    // Materialize even a focused child on a normal layer. The original
                    // selection and document are restored when the workspace closes.
                    workspace = PrintSelectionWorkspace.Create(application, (object)document, (object)page, (object)range);
                    target = workspace.Document;
                }
                app.Optimization = false;
                object options = (object)app.CreateStructExportOptions();
                Set(options, "SizeX", plan.Width); Set(options, "SizeY", plan.Height);
                Set(options, "ResolutionX", dpi); Set(options, "ResolutionY", dpi);
                Set(options, "ImageType", CorelConstants.CdrRgbColorImage);
                Set(options, "AntiAliasingType", CorelConstants.CdrNormalAntiAliasing);
                Set(options, "Transparent", true); Set(options, "Overwrite", false);
                Set(options, "UseColorProfile", true);
                object palette = (object)app.CreateStructPaletteOptions();
                object filter = target.GetType().InvokeMember("ExportEx", BindingFlags.InvokeMethod, null, target,
                    new object[] { new BStrWrapper(path), CorelConstants.CdrPng, CorelConstants.CdrCurrentPage, options, palette });
                if (filter == null) throw new InvalidOperationException("CorelDRAW не открыл фильтр PNG.");
                filter.GetType().InvokeMember("Finish", BindingFlags.InvokeMethod, null, filter, null);
                using (var stream = File.OpenRead(path))
                {
                    BitmapFrame image = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    if (Math.Abs(image.PixelWidth - plan.Width) > 2 || Math.Abs(image.PixelHeight - plan.Height) > 2 ||
                        image.PixelWidth > PhotoshopExportPlan.MaximumSide || image.PixelHeight > PhotoshopExportPlan.MaximumSide ||
                        (long)image.PixelWidth * image.PixelHeight > PhotoshopExportPlan.MaximumPixels ||
                        Math.Abs(image.DpiX - dpi) > .1 || Math.Abs(image.DpiY - dpi) > .1)
                        throw new InvalidOperationException("Размер или DPI готового PNG не совпадает с заданием. Photoshop не открыт.");
                    plan.Width = image.PixelWidth; plan.Height = image.PixelHeight;
                }
                complete = true;
                Log.Info("Photoshop PNG ready: " + plan.Width + "x" + plan.Height + ", " + dpi + " DPI, selection=" + selected + ".");
                return plan;
            }
            finally
            {
                workspace?.Dispose();
                try { document.Unit = oldUnit; } catch (Exception error) { Log.Error("Could not restore Photoshop export units.", error); }
                try { app.Optimization = oldOptimization; } catch (Exception error) { Log.Error("Could not restore Photoshop export optimization.", error); }
                if (!complete) try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
        private static void Set(object target, string name, object value) => target.GetType().InvokeMember(name,
            BindingFlags.SetProperty, null, target, new[] { value }, CultureInfo.InvariantCulture);
    }
}
