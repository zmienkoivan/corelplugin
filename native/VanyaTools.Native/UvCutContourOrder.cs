using System;
using System.Collections.Generic;
using System.Diagnostics;
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
            internal int Id, Type, Paths;
            internal double Left, Bottom, Width, Height, Length, Tint, Pen;
            internal string Color;
            internal double Top => Bottom + Height;
        }

        internal static int Sort(object application, object pageObject)
        {
            var total = Stopwatch.StartNew(); var phase = Stopwatch.StartNew();
            Log.Info("Yellow UV CUT preparation started.");
            var candidates = new List<Cut>(); var seen = new HashSet<int>();
            dynamic page = pageObject;
            foreach (dynamic layer in Items((object)page.Layers))
                if (Convert.ToBoolean(layer.Printable) && Convert.ToBoolean(layer.Visible))
                    foreach (object shape in Items((object)layer.Shapes)) Collect(shape, candidates, seen, 0, false);
            Log.Info("Yellow UV CUT scan: objects=" + seen.Count + ", contours=" + candidates.Count + ", elapsed_ms=" + phase.ElapsedMilliseconds);
            if (candidates.Count == 0) return 0;

            phase.Restart();
            dynamic target = page.CreateLayer("Vanya · CUT · сверху вниз");
            target.Visible = true; target.Printable = true; target.Editable = true;
            dynamic batch = ((dynamic)application).CreateShapeRange();
            for (int i = 0; i < candidates.Count; i++)
            {
                Cut before = candidates[i]; dynamic shape = before.Shape;
                if (before.Type != CorelConstants.CdrCurveShape)
                {
                    shape.ConvertToCurves();
                    Cut converted = ReadVerified(before.Shape);
                    if (converted.Type != CorelConstants.CdrCurveShape)
                        throw Failure("Corel не преобразовал линию CUT в кривую");
                    RequireSame(before, converted); candidates[i] = converted;
                }
                batch.Add((dynamic)candidates[i].Shape);
            }
            if (Convert.ToInt32(batch.Count) != candidates.Count) throw Failure("Corel не собрал все контуры CUT для переноса");
            // One native operation avoids recalculating the document for every
            // group child. Every path is still verified after all native edits.
            batch.MoveToLayer(target);
            Log.Info("Yellow UV CUT batch move: contours=" + candidates.Count + ", elapsed_ms=" + phase.ElapsedMilliseconds);
            phase.Restart();
            var cuts = new List<Cut>();
            foreach (Cut before in candidates)
            {
                if (before.Paths == 1) { cuts.Add(before); continue; }
                dynamic shape = before.Shape;
                var parts = Items((object)shape.BreakApartEx());
                if (parts.Count != before.Paths) throw Failure("Corel не разделил составной контур CUT на отдельные пути");
                var split = new List<Cut>();
                foreach (object part in parts)
                {
                    Cut cut = ReadVerified(part);
                    if (cut.Type != CorelConstants.CdrCurveShape || cut.Paths != 1)
                        throw Failure("Изменился состав путей CUT");
                    RequireStyle(before, cut); split.Add(cut);
                }
                var bounds = new Cut { Left = split.Min(p => p.Left), Bottom = split.Min(p => p.Bottom),
                    Length = split.Sum(p => p.Length) };
                bounds.Width = split.Max(p => p.Left + p.Width) - bounds.Left;
                bounds.Height = split.Max(p => p.Top) - bounds.Bottom;
                RequireGeometry(before, bounds);
                cuts.AddRange(split);
                if (cuts.Count > 100000) throw Failure("Слишком много путей CUT");
            }
            Log.Info("Yellow UV CUT split: paths=" + cuts.Count + ", elapsed_ms=" + phase.ElapsedMilliseconds);
            if (cuts.Count > 100000) throw Failure("Слишком много путей CUT");
            phase.Restart();

            // A row shares an interior horizontal band, not an almost identical
            // top edge. Its intersection only shrinks, so tall stickers cannot
            // chain several successive rows into one. Alternate row direction.
            var byTop = cuts.OrderByDescending(p => p.Top).ThenBy(p => p.Left).ToList();
            var ordered = new List<Cut>(); int rows = 0;
            while (byTop.Count > 0)
            {
                Cut first = byTop[0]; byTop.RemoveAt(0);
                var row = new List<Cut> { first };
                double top = first.Top + RowToleranceMm / 2, bottom = first.Bottom - RowToleranceMm / 2;
                for (int i = 0; i < byTop.Count;)
                {
                    Cut next = byTop[i];
                    double nextTop = next.Top + RowToleranceMm / 2, nextBottom = next.Bottom - RowToleranceMm / 2;
                    // Remaining top edges are sorted: everything else is below
                    // this row once its top no longer reaches the common band.
                    if (nextTop <= bottom) break;
                    double sharedTop = Math.Min(top, nextTop), sharedBottom = Math.Max(bottom, nextBottom);
                    if (sharedTop - sharedBottom >= .5 * Math.Min(top - bottom, nextTop - nextBottom))
                    {
                        row.Add(next); top = sharedTop; bottom = sharedBottom; byTop.RemoveAt(i);
                    }
                    else i++;
                }
                ordered.AddRange(rows++ % 2 == 0 ? row.OrderBy(p => p.Left).ThenByDescending(p => p.Top) :
                    row.OrderByDescending(p => p.Left).ThenByDescending(p => p.Top));
            }
            // PDF paints back to front. First path stays at the back, last at
            // the front; reversing this would send the bottom of the roll first.
            int[] expected = ordered.Select(p => p.Id).ToArray();
            int[] actual = Items((object)target.Shapes).Select(Id).Reverse().ToArray();
            if (actual.Length != expected.Length || actual.Distinct().Count() != expected.Length ||
                !new HashSet<int>(actual).SetEquals(expected)) throw Failure("Corel не перенёс все пути CUT на отдельный слой");
            // Keep the longest already ordered prefix/subsequence in place.
            // Moving the remaining paths to the front removes any interleaving.
            int keep = 0;
            foreach (int id in actual) if (keep < expected.Length && id == expected[keep]) keep++;
            for (int i = keep; i < ordered.Count; i++) ((dynamic)ordered[i].Shape).OrderToFront();
            if (keep != ordered.Count) actual = Items((object)target.Shapes).Select(Id).Reverse().ToArray();
            if (!actual.SequenceEqual(expected))
                throw Failure("Corel не сохранил порядок контуров CUT");
            Log.Info("Yellow UV CUT order: rows=" + rows + ", moves=" + (ordered.Count - keep) + ", elapsed_ms=" + phase.ElapsedMilliseconds);
            phase.Restart();
            foreach (Cut cut in ordered)
            {
                Cut after = ReadVerified(cut.Shape);
                if (after.Type != CorelConstants.CdrCurveShape || after.Paths != 1) throw Failure("Изменился состав путей CUT");
                RequireSame(cut, after);
            }
            Log.Info("Yellow UV CUT verification: elapsed_ms=" + phase.ElapsedMilliseconds);
            Log.Info("Yellow UV CUT order: " + ordered.Count + " paths, snake rows=" + rows + ", total_ms=" + total.ElapsedMilliseconds);
            return ordered.Count;
        }

        private static void Collect(object value, List<Cut> result, HashSet<int> seen, int depth, bool clipped)
        {
            if (depth > 128 || seen.Count >= 100000) throw Failure("Слишком сложная вложенность контуров CUT");
            int id = Id(value);
            if (!seen.Add(id)) return;
            dynamic shape = value;
            int type = Convert.ToInt32(shape.Type);
            object clip = PowerClip(value);
            if (type == CorelConstants.CdrGroupShape)
                foreach (object child in Items((object)shape.Shapes)) Collect(child, result, seen, depth + 1, clipped);
            else
            {
                Cut cut = ReadCandidate(value, id, type);
                if (cut != null)
                {
                    if (clipped || clip != null)
                        throw Failure("Контур CUT находится в PowerClip. Вынесите линию резки из PowerClip перед отправкой");
                    result.Add(cut);
                }
            }
            if (clip != null)
                foreach (object child in Items((object)((dynamic)clip).Shapes)) Collect(child, result, seen, depth + 1, true);
        }

        private static Cut ReadVerified(object value)
            => ReadCandidate(value, Id(value), Convert.ToInt32(((dynamic)value).Type)) ?? throw Failure("Изменился тип или цвет пути CUT");

        private static Cut ReadCandidate(object value, int id, int type)
        {
            dynamic shape = value;
            if (type < 1 || type > 4) return null;
            dynamic outline = shape.Outline;
            if (Convert.ToInt32(outline.Type) == 0) return null;
            dynamic color = outline.Color;
            if (!Convert.ToBoolean(color.IsSpot)) return null;
            string originalName = Convert.ToString(color.SpotColorName) ?? "";
            string name = originalName.Trim();
            if ((!String.Equals(name, "CUT", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(name, "CutContour", StringComparison.OrdinalIgnoreCase)) || Convert.ToInt32(shape.Fill.Type) != 0) return null;
            double x, y, width, height;
            shape.GetBoundingBox(out x, out y, out width, out height, true);
            bool curve = type == CorelConstants.CdrCurveShape;
            double length = 0; int paths = 0;
            if (curve) { dynamic geometry = shape.Curve; length = Convert.ToDouble(geometry.Length); paths = Convert.ToInt32(geometry.SubPaths.Count); }
            if (!PrintExportBounds.Finite(x) || !PrintExportBounds.Finite(y) || !PrintExportBounds.Finite(width) ||
                !PrintExportBounds.Finite(height) || !PrintExportBounds.Finite(length) || width < 0 || height < 0 ||
                (curve && (length <= 0 || paths < 1 || paths > 100000)))
                throw Failure("Некорректная геометрия контура CUT");
            double tint = Convert.ToDouble(color.Tint), pen = Convert.ToDouble(outline.Width);
            if (!PrintExportBounds.Finite(tint) || !PrintExportBounds.Finite(pen) || pen < 0) throw Failure("Некорректная обводка CUT");
            return new Cut { Shape = value, Id = id, Type = type, Paths = paths, Left = x, Bottom = y, Width = width, Height = height,
                Length = length, Color = originalName, Tint = tint, Pen = pen };
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
