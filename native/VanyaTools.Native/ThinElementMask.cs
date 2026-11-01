using System;
using System.IO;
using System.Threading;

namespace VanyaTools.Native
{
    internal static class ThinElementMask
    {
        internal static RasterInsertionSource CaptureSource(bool includeOutline = true)
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument, range = app.ActiveSelectionRange;
            int unit = Convert.ToInt32(doc.Unit);
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                double left, bottom, width, height;
                range.GetBoundingBox(out left, out bottom, out width, out height, includeOutline);
                if (!(width > 0) || !(height > 0) || Double.IsInfinity(width + height))
                    throw new InvalidOperationException("Не удалось определить границы выделения с обводками.");
                var source = new RasterInsertionSource { Document = (object)doc, NativePage = (object)doc.ActivePage, IncludeOutlineBounds = includeOutline,
                    LeftMm = left, TopMm = bottom + height, WidthMm = width, HeightMm = height, NativeShapes = new System.Collections.Generic.List<object>() };
                foreach (dynamic shape in range.Shapes)
                {
                    shape.GetBoundingBox(out left, out bottom, out width, out height, includeOutline);
                    source.NativeShapes.Add((object)shape); source.Shapes.Add(new RasterInsertionShape {
                        LeftMm = left, TopMm = bottom + height, WidthMm = width, HeightMm = height });
                }
                return source;
            }
            finally { doc.Unit = unit; }
        }
        internal static void Insert(RasterInsertionSource placement, byte[] overlay, int pixelWidth, int pixelHeight)
        {
            if (overlay == null || placement == null) return;
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (!Same((object)doc, placement.Document) || !Same((object)doc.ActivePage, placement.NativePage))
                throw new InvalidOperationException("Вернитесь на исходную страницу и повторите проверку.");
            for (int i = 0; i < placement.NativeShapes.Count; i++)
            {
                dynamic shape = placement.NativeShapes[i]; var before = placement.Shapes[i];
                int old = Convert.ToInt32(doc.Unit);
                try { doc.Unit = CorelConstants.CdrMillimeter;
                    double left, bottom, width, height; shape.GetBoundingBox(out left, out bottom, out width, out height, placement.IncludeOutlineBounds);
                    if (Math.Abs(left - before.LeftMm) > .02 || Math.Abs(bottom + height - before.TopMm) > .02 ||
                        Math.Abs(width - before.WidthMm) > .02 || Math.Abs(height - before.HeightMm) > .02)
                        throw new InvalidOperationException("Исходник перемещён или изменён. Откройте проверку заново."); }
                finally { doc.Unit = old; }
            }
            string path = Path.Combine(Path.GetTempPath(), "Vanya-Thin-Mask-" + Guid.NewGuid().ToString("N") + ".png");
            object layer = null, oldLayer = (object)doc.ActiveLayer; int unit = Convert.ToInt32(doc.Unit); bool started = false;
            try
            {
                using (var stream = File.Create(path)) FastRasterPng.Write(stream, overlay, pixelWidth, pixelHeight, 400, 400, CancellationToken.None);
                doc.Unit = CorelConstants.CdrMillimeter; doc.BeginCommandGroup("Vanya Tools · маска тонких элементов"); started = true;
                layer = (object)doc.ActivePage.CreateLayer("Vanya · аудит тонких элементов · НЕ ПЕЧАТАТЬ");
                dynamic target = layer; target.Printable = false; target.Visible = true; target.Editable = true; target.Activate();
                object importOptions = (object)app.CreateStructImportOptions();
                dynamic filter = layer.GetType().InvokeMember("ImportEx", System.Reflection.BindingFlags.InvokeMethod, null, layer,
                    new object[] { new System.Runtime.InteropServices.BStrWrapper(Path.GetFullPath(path)), 0, importOptions });
                filter.Finish(); dynamic bitmap = null;
                if (Convert.ToInt32(target.Shapes.Count) != 1) throw new InvalidOperationException("Corel не вернул один растр маски.");
                foreach (dynamic imported in target.Shapes) { bitmap = imported; break; }
                if (bitmap == null || Convert.ToInt32(bitmap.Type) != CorelConstants.CdrBitmapShape) throw new InvalidOperationException("Маска не импортирована.");
                bitmap.SetSize(placement.WidthMm, placement.HeightMm);
                bitmap.Move(placement.LeftMm - Convert.ToDouble(bitmap.LeftX), placement.TopMm - Convert.ToDouble(bitmap.TopY));
                bitmap.Name = "Диагностическая маска · удалить перед печатью";
                // A locked diagnostic layer stays out of ordinary selection and
                // cannot become the next print job merely because import selected it.
                target.Editable = false;
                for (int i = 0; i < placement.NativeShapes.Count; i++)
                    if (i == 0) ((dynamic)placement.NativeShapes[i]).CreateSelection();
                    else ((dynamic)placement.NativeShapes[i]).AddToSelection();
            }
            catch { if (layer != null) try { ((dynamic)layer).Delete(); } catch { } throw; }
            finally { try { if (started) doc.EndCommandGroup(); } finally { doc.Unit = unit; ((dynamic)oldLayer).Activate(); try { File.Delete(path); } catch { } } }
        }
        private static bool Same(object a, object b)
        {
            if (a == null || b == null) return false;
            IntPtr x = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(a), y = System.Runtime.InteropServices.Marshal.GetIUnknownForObject(b);
            try { return x == y; } finally { System.Runtime.InteropServices.Marshal.Release(x); System.Runtime.InteropServices.Marshal.Release(y); }
        }
    }
}
