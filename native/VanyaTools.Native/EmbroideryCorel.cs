using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace VanyaTools.Native
{
    internal static class EmbroideryCorel
    {
        internal static void Insert(EmbroideryFrame frame)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("Вставка вышивки должна выполняться в основном потоке Corel.");
            if (!frame.CanInsert || !File.Exists(frame.Path) || frame.WidthMm <= 0 || frame.HeightMm <= 0) throw new InvalidOperationException("Проверьте готовый кадр перед вставкой.");
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ для вставки эскиза вышивки.");
            int unit = Convert.ToInt32(doc.Unit); bool optimization = Convert.ToBoolean(app.Optimization), started = false;
            dynamic layer = null;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter; app.Optimization = true;
                dynamic page = doc.ActivePage;
                double left = Convert.ToDouble(page.SizeWidth) + 10, top = Convert.ToDouble(page.SizeHeight);
                dynamic selection = app.ActiveSelectionRange;
                if (selection != null && Convert.ToInt32(selection.Count) > 0)
                { left = Convert.ToDouble(selection.RightX) + 10; top = Convert.ToDouble(selection.TopY); }
                doc.BeginCommandGroup("Vanya Tools · " + frame.Title); started = true;
                layer = page.CreateLayer("Vanya · Вышивка · " + frame.Title);
                layer.Visible = true; layer.Editable = true; layer.Printable = true;
                object target = layer, options = app.CreateStructImportOptions();
                dynamic filter = target.GetType().InvokeMember("ImportEx", BindingFlags.InvokeMethod, null, target,
                    new object[] { new BStrWrapper(Path.GetFullPath(frame.Path)), 0, options }); filter.Finish();
                if (Convert.ToInt32(layer.Shapes.Count) != 1) throw new InvalidOperationException("Corel не вернул один растр вышивки.");
                dynamic image = layer.Shapes[1];
                if (Convert.ToInt32(image.Type) != CorelConstants.CdrBitmapShape) throw new InvalidOperationException("Импортирован не растровый кадр.");
                image.SetSize(frame.WidthMm, frame.HeightMm);
                image.Move(left - Convert.ToDouble(image.LeftX), top - Convert.ToDouble(image.TopY));
                double x, y, w, h; image.GetBoundingBox(out x, out y, out w, out h, false);
                if (Math.Abs(w - frame.WidthMm) > .02 || Math.Abs(h - frame.HeightMm) > .02 || Math.Abs(x - left) > .02 || Math.Abs(y + h - top) > .02)
                    throw new InvalidOperationException("Corel изменил размер или положение кадра вышивки.");
                image.Name = frame.Title + " · " + EmbroideryRequest.F(frame.WidthMm) + " × " + EmbroideryRequest.F(frame.HeightMm) + " мм";
                image.CreateSelection(); doc.EndCommandGroup(); started = false;
            }
            catch
            {
                // Delete only this import's owned layer; never undo an unrelated
                // user command or touch the original selection.
                try { if (layer != null) layer.Delete(); } catch (Exception error) { Log.Error("Could not remove failed embroidery import.", error); }
                throw;
            }
            finally
            {
                try { if (started) doc.EndCommandGroup(); } catch { }
                try { doc.Unit = unit; } catch { }
                try { app.Optimization = optimization; } catch { }
                try { app.Refresh(); } catch { }
            }
        }
    }
}
