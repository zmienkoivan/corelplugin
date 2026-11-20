using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    // Only the owned PDF workspace may be changed. A spot fill's overprint flag
    // cannot prevent later ordinary artwork from knocking that spot plate out.
    // Paint the technical planes after artwork, independently of CDR layer order.
    internal static class PrintSpotPlateOrder
    {
        private sealed class Plate
        {
            internal object Shape;
            internal int Id;
            internal string Ink;
            internal double Tint, X, Y, Width, Height;
        }

        internal static int Prepare(object application, object exportPage)
        {
            var plates = new List<Plate>(); var seen = new HashSet<int>();
            foreach (dynamic layer in Items((object)((dynamic)exportPage).Layers))
                if (Convert.ToBoolean(layer.Printable) && Convert.ToBoolean(layer.Visible))
                    foreach (object root in Items((object)layer.Shapes)) Collect(root, plates, seen, 0);
            if (plates.Count == 0) return 0;

            foreach (string ink in new[] { "WHITE", "CLEAR" })
            {
                var selected = plates.Where(p => p.Ink == ink).ToArray();
                if (selected.Length == 0) continue;
                dynamic target = ((dynamic)exportPage).CreateLayer("Vanya · " + ink + " · печать");
                target.Visible = true; target.Printable = true; target.Editable = true;
                if (!Convert.ToBoolean(target.Visible) || !Convert.ToBoolean(target.Printable))
                    throw Failure("Не удалось включить печать слоя " + ink);
                dynamic batch = ((dynamic)application).CreateShapeRange();
                foreach (Plate plate in selected) batch.Add((dynamic)plate.Shape);
                batch.MoveToLayer(target);
                var ids = Items((object)target.Shapes).Select(Id).ToArray();
                if (ids.Length != selected.Length || !new HashSet<int>(ids).SetEquals(selected.Select(p => p.Id)))
                    throw Failure("Не все объекты " + ink + " перенесены на печатный слой");
                foreach (Plate plate in selected)
                {
                    dynamic shape = plate.Shape;
                    shape.OverprintFill = true;
                    Plate after = Read(plate.Shape);
                    if (after == null || after.Ink != plate.Ink || Math.Abs(after.Tint - plate.Tint) > 1e-6 ||
                        Math.Abs(after.X - plate.X) > PrintExportBounds.TransferToleranceMm ||
                        Math.Abs(after.Y - plate.Y) > PrintExportBounds.TransferToleranceMm ||
                        Math.Abs(after.Width - plate.Width) > PrintExportBounds.TransferToleranceMm ||
                        Math.Abs(after.Height - plate.Height) > PrintExportBounds.TransferToleranceMm ||
                        !Convert.ToBoolean(shape.OverprintFill))
                        throw Failure("При подготовке " + ink + " изменились геометрия, плотность или наложение");
                }
            }
            Log.Info("Print spot planes prepared after artwork: WHITE=" + plates.Count(p => p.Ink == "WHITE") +
                ", CLEAR=" + plates.Count(p => p.Ink == "CLEAR") + "; source CDR untouched.");
            return plates.Count;
        }

        private static void Collect(object value, List<Plate> result, HashSet<int> seen, int depth)
        {
            if (depth > 128 || seen.Count >= 100000) throw Failure("Слишком сложная вложенность печатных плашек");
            if (!seen.Add(Id(value))) return;
            dynamic shape = value;
            Plate plate = Read(value);
            if (plate != null)
            {
                // Extracting a child could discard its group's transparency or
                // PowerClip. Do not silently alter the effective printing mask.
                if (depth != 0 || PowerClip(value) != null)
                    throw Failure("Плашка " + plate.Ink + " находится внутри группы или PowerClip. Разместите её отдельным объектом на своём слое");
                result.Add(plate);
            }
            if (Convert.ToInt32(shape.Type) == CorelConstants.CdrGroupShape)
                foreach (object child in Items((object)shape.Shapes)) Collect(child, result, seen, depth + 1);
            object clip = PowerClip(value);
            if (clip != null)
                foreach (object child in Items((object)((dynamic)clip).Shapes)) Collect(child, result, seen, depth + 1);
        }

        private static Plate Read(object value)
        {
            dynamic shape = value;
            int type = Convert.ToInt32(shape.Type);
            if (type < 1 || type > 4 || Convert.ToInt32(shape.Fill.Type) != 1) return null;
            dynamic color = shape.Fill.UniformColor;
            if (!Convert.ToBoolean(color.IsSpot)) return null;
            string ink = Convert.ToString(color.SpotColorName);
            if (ink != "WHITE" && ink != "CLEAR") return null;
            if (Convert.ToInt32(shape.Outline.Type) != 0)
                throw Failure("Печатная плашка " + ink + " имеет абрис. Отделите абрис от плашки перед отправкой");
            double x, y, w, h;
            shape.GetBoundingBox(out x, out y, out w, out h, true);
            double tint = Convert.ToDouble(color.Tint);
            if (!PrintExportBounds.Finite(x) || !PrintExportBounds.Finite(y) || !PrintExportBounds.Finite(w) ||
                !PrintExportBounds.Finite(h) || !PrintExportBounds.Finite(tint) || w <= 0 || h <= 0 || tint < 0 || tint > 100)
                throw Failure("Некорректный размер или плотность " + ink);
            return new Plate { Shape = value, Id = Id(value), Ink = ink, Tint = tint, X = x, Y = y, Width = w, Height = h };
        }

        private static object PowerClip(object shape)
        {
            try { return (object)((dynamic)shape).PowerClip; }
            catch (COMException error) when (error.ErrorCode == unchecked((int)0x80004005) ||
                error.ErrorCode == unchecked((int)0x80020009) || error.ErrorCode == unchecked((int)0x80070057)) { return null; }
        }
        private static int Id(object shape) => Convert.ToInt32(((dynamic)shape).StaticID);
        private static List<object> Items(object collection)
        { var result = new List<object>(); foreach (object item in (System.Collections.IEnumerable)collection) result.Add(item); return result; }
        private static InvalidOperationException Failure(string message) =>
            new InvalidOperationException("PDF не отправлен: " + message + ". Исходный документ не изменён.");
    }
}
