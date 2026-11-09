using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class PatternFillService
    {
        private object _motifDocument, _resultDocument, _result;
        private readonly List<(object Shape, int Id)> _motifs = new List<(object, int)>();
        private double _lastAllowance;

        internal int Remember()
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic selection = app.ActiveSelectionRange;
            int count = selection == null ? 0 : Convert.ToInt32(selection.Count);
            if (count == 0 || count > 64) throw new InvalidOperationException("Выделите от 1 до 64 объектов узора. Группа считается одним элементом.");
            var motifs = new List<(object Shape, int Id)>(count);
            foreach (dynamic shape in selection) motifs.Add(((object)shape, Convert.ToInt32(shape.StaticID)));
            if (motifs.Count != count) throw new InvalidOperationException("Выделение изменилось. Запомните элементы ещё раз.");
            _motifDocument = doc; _motifs.Clear(); _motifs.AddRange(motifs);
            return count;
        }

        internal int Fill(PatternFillOptions options, double allowance, Color cellColor)
        {
            if (Double.IsNaN(allowance) || Double.IsInfinity(allowance) || allowance < 0 || allowance > 100)
                throw new ArgumentException("Припуск на шов должен быть от 0 до 100 мм.");

            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic selection = app.ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) != 1)
                throw new InvalidOperationException("Выделите один замкнутый контур детали.");
            dynamic target = selection.Shapes.Item[1];
            if (target == null) throw new InvalidOperationException("Выделите контур детали.");
            bool replace = SameObject((object)doc, _resultDocument) && SameObject((object)target, _result);
            if (replace && Math.Abs(allowance - _lastAllowance) > .0001)
                throw new InvalidOperationException("Для нового припуска выделите исходный контур детали.");
            if (!options.Texture) VerifyMotifs((object)doc);

            int unit = Convert.ToInt32(doc.Unit);
            bool optimized = Convert.ToBoolean(app.Optimization), started = false;
            var created = new List<object>();
            dynamic frame = null;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                dynamic outline = GetOutline((object)target);
                if (!replace && allowance > 0)
                {
                    outline = outline.Contour(allowance, CorelConstants.CdrContourOutside,
                        CorelConstants.CdrContourRoundCap, CorelConstants.CdrContourCornerRound, 0d);
                    if (outline == null || !Convert.ToBoolean(outline.Closed))
                        throw new InvalidOperationException("CorelDRAW не построил замкнутый припуск. Проверьте контур и размер припуска.");
                }
                double left, bottom, width, height;
                if (!Convert.ToBoolean(outline.GetBoundingBox(out left, out bottom, out width, out height)))
                    throw new InvalidOperationException("Не удалось измерить контур детали.");
                var plan = PatternFillLayout.Build(width, height, _motifs.Count, options);

                dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
                dynamic color = options.Texture ? app.CreateRGBColor((int)cellColor.R, (int)cellColor.G, (int)cellColor.B) : null;
                app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools · паттерн по контуру"); started = true;
                frame = layer.CreateCurve(outline);
                frame.Fill.ApplyNoFill();
                frame.Outline.SetNoOutline();
                frame.Name = "Vanya · паттерн";
                foreach (var item in plan)
                {
                    dynamic shape;
                    if (item.Cell == null)
                    {
                        shape = ((dynamic)_motifs[item.Motif].Shape).CopyToLayer(layer);
                        created.Add((object)shape);
                        PlaceMotif((object)shape, item, left, bottom);
                    }
                    else
                    {
                        dynamic curve = MakeCell((object)doc, item.Cell, left, bottom,
                            options.Mode == PatternFillMode.Pebbles ? .37 : .11);
                        shape = layer.CreateCurve(curve);
                        created.Add((object)shape);
                        shape.Fill.ApplyUniformFill(color);
                        shape.Outline.SetNoOutline();
                    }
                    range.Add(shape);
                }
                // cdrFalse keeps the positions calculated for the contour.
                range.AddToPowerClip(frame, 0);
                frame.CreateSelection();
                if (replace) target.Delete();
                _result = (object)frame; _resultDocument = (object)doc; _lastAllowance = allowance;
                return plan.Count;
            }
            catch
            {
                for (int i = created.Count - 1; i >= 0; i--) Delete(created[i]);
                if (frame != null) Delete((object)frame);
                if (started) TryRestore(() => target.CreateSelection(), "pattern selection");
                throw;
            }
            finally
            {
                if (started) TryRestore(() => doc.EndCommandGroup(), "pattern undo group");
                TryRestore(() => doc.Unit = unit, "document unit");
                TryRestore(() => app.Optimization = optimized, "drawing optimization");
                if (started && !optimized) TryRestore(() => app.ActiveWindow.Refresh(), "drawing refresh");
            }
        }

        private void VerifyMotifs(object document)
        {
            if (_motifs.Count == 0 || !SameObject(document, _motifDocument))
                throw new InvalidOperationException("Запомните элементы узора в текущем документе.");
            try
            {
                foreach (var motif in _motifs)
                    if (Convert.ToInt32(((dynamic)motif.Shape).StaticID) != motif.Id)
                        throw new InvalidOperationException();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Один из элементов недоступен. Выделите и запомните набор ещё раз.", error);
            }
        }

        private static object GetOutline(object target)
        {
            try
            {
                dynamic shape = target;
                int type = Convert.ToInt32(shape.Type);
                if (type == CorelConstants.CdrGroupShape || type == CorelConstants.CdrBitmapShape)
                    throw new InvalidOperationException();
                dynamic curve = shape.DisplayCurve;
                if (curve == null || !Convert.ToBoolean(curve.Closed)) throw new InvalidOperationException();
                return (object)curve.GetCopy();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Выделите один замкнутый векторный контур детали.", error);
            }
        }

        private static void PlaceMotif(object shape, PatternPlacement item, double originX, double originY)
        {
            dynamic copy = shape;
            if (Math.Abs(item.Angle) > .0001) copy.Rotate(item.Angle);
            double left, bottom, width, height;
            copy.GetBoundingBox(out left, out bottom, out width, out height, true);
            double longest = Math.Max(width, height);
            if (Double.IsNaN(longest) || Double.IsInfinity(longest) || longest <= .0001)
                throw new InvalidOperationException("Один из элементов узора имеет нулевой размер.");
            double scale = item.Size / longest;
            if (Math.Abs(scale - 1) > .000001)
            {
                copy.Stretch(scale, scale, true);
                copy.GetBoundingBox(out left, out bottom, out width, out height, true);
                // Some Corel outlines have fixed widths. Keep their visible size within the cell.
                double excess = Math.Max(width, height) - item.Size;
                if (excess > .05)
                {
                    double geometry = Math.Max(Convert.ToDouble(copy.SizeWidth), Convert.ToDouble(copy.SizeHeight));
                    if (geometry <= excess + .001)
                        throw new InvalidOperationException("Обводка элемента шире заданного размера. Увеличьте размер или уменьшите обводку.");
                    double correction = (geometry - excess) / geometry;
                    copy.Stretch(correction, correction, true);
                    copy.GetBoundingBox(out left, out bottom, out width, out height, true);
                    if (Math.Max(width, height) > item.Size + .1)
                        throw new InvalidOperationException("Обводка не помещается в ячейку. Увеличьте размер элемента.");
                }
            }
            copy.Move(originX + item.Center.X - left - width / 2, originY + item.Center.Y - bottom - height / 2);
        }

        private static object MakeCell(object document, PatternPoint[] vertices, double left, double bottom, double rounding)
        {
            dynamic doc = document, curve = doc.CreateCurve();
            int count = vertices.Length;
            PatternPoint first = PatternPoint.Between(vertices[0], vertices[count - 1], rounding);
            dynamic path = curve.CreateSubPath(left + first.X, bottom + first.Y);
            for (int i = 0; i < count; i++)
            {
                PatternPoint corner = vertices[i];
                PatternPoint start = PatternPoint.Between(corner, vertices[(i + count - 1) % count], rounding);
                PatternPoint end = PatternPoint.Between(corner, vertices[(i + 1) % count], rounding);
                if (i != 0) path.AppendLineSegment(left + start.X, bottom + start.Y, false);
                PatternPoint a = PatternPoint.Between(start, corner, 2d / 3), b = PatternPoint.Between(end, corner, 2d / 3);
                path.AppendCurveSegment2(left + end.X, bottom + end.Y, left + a.X, bottom + a.Y,
                    left + b.X, bottom + b.Y, false);
            }
            path.Closed = true;
            return (object)curve;
        }

        private static void Delete(object shape) => TryRestore(() => ((dynamic)shape).Delete(), "partial pattern shape");

        private static void TryRestore(Action action, string context)
        {
            try { action(); }
            catch (Exception error) { Log.Error("Could not restore " + context + ".", error); }
        }

        private static bool SameObject(object a, object b)
        {
            if (a == null || b == null) return false;
            if (ReferenceEquals(a, b)) return true;
            IntPtr first = IntPtr.Zero, second = IntPtr.Zero;
            try
            {
                first = Marshal.GetIUnknownForObject(a); second = Marshal.GetIUnknownForObject(b);
                return first == second;
            }
            finally
            {
                if (first != IntPtr.Zero) Marshal.Release(first);
                if (second != IntPtr.Zero) Marshal.Release(second);
            }
        }
    }
}
