using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace VanyaTools.Native
{
    internal static class NativeRasterSourceCapture
    {
        // CropEnvelopeModified describes an existing clipping path, not an
        // unfinished tool. Materialize that path only on a disposable copy.
        internal static NativeRasterBuffer Read(object application, object document, object source,
            int maxPixels, Action<long> memoryCheck)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("Исходный растр нужно читать в интерфейсе Corel.");
            dynamic app = application, doc = document, original = source;
            dynamic bitmap = original.Bitmap;
            int mode = Convert.ToInt32(bitmap.Mode);
            NativeRasterBuffer.ChannelCount(mode);
            long pixels = checked(Convert.ToInt64(bitmap.Image.Width) * Convert.ToInt64(bitmap.Image.Height));
            if (pixels <= 0 || pixels > maxPixels)
                throw new InvalidOperationException("Исходный растр слишком велик для обработки без изменения разрешения.");
            if (pixels > 2000000) memoryCheck?.Invoke(pixels);
            int oldUnit = Convert.ToInt32(doc.Unit);
            bool oldOptimization = Convert.ToBoolean(app.Optimization), group = false;
            object copy = null;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                double width = Convert.ToDouble(original.SizeWidth), height = Convert.ToDouble(original.SizeHeight);
                if (!Finite(width) || !Finite(height) || width <= 0 || height <= 0)
                    throw new InvalidOperationException("Не удалось определить физический размер растра.");
                double dpiX = Convert.ToDouble(bitmap.ResolutionX), dpiY = Convert.ToDouble(bitmap.ResolutionY);
                bool modified = Convert.ToBoolean(bitmap.CropEnvelopeModified);
                bool canCrop = Convert.ToBoolean(bitmap.Cropped);
                Log.Info("Native source clipping: envelopeModified=" + modified + ", canCrop=" + canCrop + ".");
                if (canCrop)
                {
                    doc.BeginCommandGroup("Vanya Tools - capture cropped bitmap"); group = true;
                    app.Optimization = true;
                    object candidate = (object)original.Duplicate(0, 0);
                    if (candidate == null || SameObject(candidate, source))
                        throw new InvalidOperationException("Corel не создал отдельную копию обрезанного растра. Исходник не изменён.");
                    copy = candidate;
                    dynamic cropped = copy;
                    Log.Info("Native source: materializing existing crop on a temporary copy.");
                    cropped.Bitmap.Crop();
                    bitmap = cropped.Bitmap;
                    if (Convert.ToInt32(bitmap.Mode) != mode ||
                        Math.Abs(Convert.ToDouble(bitmap.ResolutionX) - dpiX) > .01 ||
                        Math.Abs(Convert.ToDouble(bitmap.ResolutionY) - dpiY) > .01)
                        throw new InvalidOperationException("Corel изменил модель или разрешение при применении обрезки. Исходник сохранён.");
                    if (!Finite(dpiX) || !Finite(dpiY) || dpiX <= 0 || dpiY <= 0 ||
                        Math.Abs(Convert.ToDouble(cropped.SizeWidth) - width) > 2 * 25.4 / dpiX ||
                        Math.Abs(Convert.ToDouble(cropped.SizeHeight) - height) > 2 * 25.4 / dpiY)
                        throw new InvalidOperationException("Corel изменил видимые габариты при применении обрезки. Исходник сохранён.");
                }
                var buffer = NativeRasterModelIO.Capture((object)bitmap, maxPixels);
                return new NativeRasterBuffer(buffer.Width, buffer.Height, buffer.Mode,
                    buffer.Width * 25.4 / width, buffer.Height * 25.4 / height,
                    buffer.Colors, buffer.Alpha, buffer.CmykOrder);
            }
            finally
            {
                try
                {
                    if (copy != null)
                    {
                        try { ((dynamic)copy).Delete(); }
                        finally { original.CreateSelection(); }
                    }
                }
                finally
                {
                    try { if (group) doc.EndCommandGroup(); }
                    finally
                    {
                        try { doc.Unit = oldUnit; }
                        finally { app.Optimization = oldOptimization; }
                    }
                }
            }
        }
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
        private static bool SameObject(object first, object second)
        {
            if (ReferenceEquals(first, second)) return true;
            IntPtr a = IntPtr.Zero, b = IntPtr.Zero;
            try { a = Marshal.GetIUnknownForObject(first); b = Marshal.GetIUnknownForObject(second); return a == b; }
            finally { if (a != IntPtr.Zero) Marshal.Release(a); if (b != IntPtr.Zero) Marshal.Release(b); }
        }
    }
}
