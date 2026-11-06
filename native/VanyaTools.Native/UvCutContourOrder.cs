using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace VanyaTools.Native
{
    // Only called on the owned export document, never on the source CDR.
    internal static class UvCutContourOrder
    {
        internal const double RowToleranceMm = 2;
        internal static bool ForButton(int button) => button == 4;
        private sealed class Cut
        {
            internal object Shape;
            internal double Left, Bottom, Width, Height, Length, Tint, Pen;
            internal string Color;
            internal double Top => Bottom + Height;
        }

        internal static int Sort(object pageObject)
        {
            var candidates = new List<object>(); var seen = new HashSet<int>();
            dynamic page = pageObject;
            foreach (dynamic layer in Items((object)page.Layers))
                if (Convert.ToBoolean(layer.Printable) && Convert.ToBoolean(layer.Visible))
                    foreach (object shape in Items((object)layer.Shapes)) Collect(shape, candidates, seen, 0, false);
            if (candidates.Count == 0) return 0;

            dynamic target = page.CreateLayer("Vanya · CUT · сверху вниз");
            target.Visible = true; target.Printable = true; target.Editable = true;
            var cuts = new List<Cut>();
            foreach (object item in candidates)
            {
                dynamic shape = item;
                Cut before = Read(item);
                if (Convert.ToInt32(shape.Type) != CorelConstants.CdrCurveShape)
                {
                    shape.ConvertToCurves();
                    if (Convert.ToInt32(shape.Type) != CorelConstants.CdrCurveShape)
                        throw Failure("Corel не преобразовал линию CUT в кривую");
                    Cut converted = Read(item); RequireSame(before, converted); before = converted;
                }
                // A different layer extracts group children into one flat collection.
                // The native move retains world coordinates and the outline itself.
                shape.MoveToLayer(target);
                RequireSame(before, Read(item));
                int count = Convert.ToInt32(shape.Curve.SubPaths.Count);
                if (count < 1 || count > 100000) throw Failure("Некорректное число путей CUT");
                var parts = count == 1 ? new List<object> { item } : Items((object)shape.BreakApartEx());
                if (parts.Count != count) throw Failure("Corel не разделил составной контур CUT на отдельные пути");
                var split = new List<Cut>();
                foreach (object part in parts)
                {
                    dynamic path = part;
                    if (!IsCut(part) || Convert.ToInt32(path.Type) != CorelConstants.CdrCurveShape ||
                        Convert.ToInt32(path.Curve.SubPaths.Count) != 1)
                        throw Failure("Изменился состав путей CUT");
                    Cut cut = Read(part); RequireStyle(before, cut); split.Add(cut);
                }
                var bounds = new Cut { Left = split.Min(p => p.Left), Bottom = split.Min(p => p.Bottom),
                    Length = split.Sum(p => p.Length) };
                bounds.Width = split.Max(p => p.Left + p.Width) - bounds.Left;
                bounds.Height = split.Max(p => p.Top) - bounds.Bottom;
                RequireGeometry(before, bounds);
                cuts.AddRange(split);
                if (cuts.Count > 100000) throw Failure("Слишком много путей CUT");
            }

            // Anchor each row to its highest edge: approximate comparisons in a
            // comparator are not transitive and can chain many rows into one.
            var byTop = cuts.OrderByDescending(p => p.Top).ThenBy(p => p.Left).ToList();
            var ordered = new List<Cut>();
            for (int i = 0; i < byTop.Count;)
            {
                int end = i + 1;
                while (end < byTop.Count && byTop[i].Top - byTop[end].Top <= RowToleranceMm) end++;
                ordered.AddRange(byTop.GetRange(i, end - i).OrderBy(p => p.Left).ThenByDescending(p => p.Top));
                i = end;
            }
            // PDF paints back to front. First path stays at the back, last at
            // the front; reversing this would send the bottom of the roll first.
            foreach (Cut cut in ordered) ((dynamic)cut.Shape).OrderToFront();
            var actual = Items((object)target.Shapes);
            if (actual.Count != ordered.Count || actual.Select(Id).Distinct().Count() != ordered.Count ||
                !actual.AsEnumerable().Reverse().Select(Id).SequenceEqual(ordered.Select(p => Id(p.Shape))))
                throw Failure("Corel не сохранил порядок контуров CUT");
            foreach (Cut cut in ordered) RequireSame(cut, Read(cut.Shape));
            Log.Info("Yellow UV CUT order: " + ordered.Count + " paths, top-to-bottom, left-to-right within 2 mm rows.");
            return ordered.Count;
        }

        private static void Collect(object value, List<object> result, HashSet<int> seen, int depth, bool clipped)
        {
            if (depth > 128 || seen.Count >= 100000) throw Failure("Слишком сложная вложенность контуров CUT");
            if (!seen.Add(Id(value))) return;
            dynamic shape = value;
            object clip = PowerClip(value);
            if (Convert.ToInt32(shape.Type) == CorelConstants.CdrGroupShape)
                foreach (object child in Items((object)shape.Shapes)) Collect(child, result, seen, depth + 1, clipped);
            else if (IsCut(value))
            {
                if (clipped || clip != null)
                    throw Failure("Контур CUT находится в PowerClip. Вынесите линию резки из PowerClip перед отправкой");
                result.Add(value);
            }
            if (clip != null)
                foreach (object child in Items((object)((dynamic)clip).Shapes)) Collect(child, result, seen, depth + 1, true);
        }

        private static bool IsCut(object value)
        {
            dynamic shape = value;
            int type = Convert.ToInt32(shape.Type);
            if (type < 1 || type > 4 || Convert.ToInt32(shape.Outline.Type) == 0) return false;
            dynamic color = shape.Outline.Color;
            if (!Convert.ToBoolean(color.IsSpot)) return false;
            string name = (Convert.ToString(color.SpotColorName) ?? "").Trim();
            return (String.Equals(name, "CUT", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(name, "CutContour", StringComparison.OrdinalIgnoreCase)) && Convert.ToInt32(shape.Fill.Type) == 0;
        }

        private static Cut Read(object value)
        {
            dynamic shape = value;
            double x, y, width, height;
            shape.GetBoundingBox(out x, out y, out width, out height, true);
            bool curve = Convert.ToInt32(shape.Type) == CorelConstants.CdrCurveShape;
            double length = curve ? Convert.ToDouble(shape.Curve.Length) : 0;
            if (!PrintExportBounds.Finite(x) || !PrintExportBounds.Finite(y) || !PrintExportBounds.Finite(width) ||
                !PrintExportBounds.Finite(height) || !PrintExportBounds.Finite(length) || width < 0 || height < 0 || (curve && length <= 0))
                throw Failure("Некорректная геометрия контура CUT");
            return new Cut { Shape = value, Left = x, Bottom = y, Width = width, Height = height, Length = length,
                Color = Convert.ToString(shape.Outline.Color.SpotColorName), Tint = Convert.ToDouble(shape.Outline.Color.Tint),
                Pen = Convert.ToDouble(shape.Outline.Width) };
        }
        private static void RequireSame(Cut before, Cut after)
        { RequireGeometry(before, after); RequireStyle(before, after); }
        private static void RequireGeometry(Cut before, Cut after)
        {
            double tolerance = PrintExportBounds.TransferToleranceMm;
            if (Math.Abs(before.Left - after.Left) > tolerance || Math.Abs(before.Bottom - after.Bottom) > tolerance ||
                Math.Abs(before.Width - after.Width) > tolerance || Math.Abs(before.Height - after.Height) > tolerance ||
                (before.Length > 0 && Math.Abs(before.Length - after.Length) > Math.Max(tolerance, before.Length * 1e-8)))
                throw Failure("При сортировке изменилась геометрия контура CUT");
        }
        private static void RequireStyle(Cut before, Cut after)
        {
            if (!String.Equals(before.Color, after.Color, StringComparison.Ordinal) ||
                Math.Abs(before.Tint - after.Tint) > 1e-6 || Math.Abs(before.Pen - after.Pen) > 1e-6)
                throw Failure("При сортировке изменился цвет или толщина контура CUT");
        }
        private static object PowerClip(object value)
        {
            try { return (object)((dynamic)value).PowerClip; }
            catch (COMException error) when (error.ErrorCode == unchecked((int)0x80004005) ||
                error.ErrorCode == unchecked((int)0x80020009) || error.ErrorCode == unchecked((int)0x80070057)) { return null; }
        }
        private static int Id(object value) => Convert.ToInt32(((dynamic)value).StaticID);
        private static List<object> Items(object range)
        { var result = new List<object>(); foreach (object item in (dynamic)range) result.Add(item); return result; }
        private static InvalidOperationException Failure(string message) => new InvalidOperationException(message + ". PDF не отправлен.");
    }
}
