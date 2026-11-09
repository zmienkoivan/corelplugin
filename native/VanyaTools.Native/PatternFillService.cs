using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class PatternAsset
    {
        internal object Shape;
        internal int Id;
        internal PatternPoint Size;
        internal BitmapSource Preview;
    }

    internal sealed class PatternFrame
    {
        internal object Curve;
        internal double Left, Bottom, Width, Height;
        internal double Top => Bottom + Height;
        internal Geometry Geometry;
    }

    internal sealed class PatternEditorSession
    {
        internal object Application, Document, Target;
        internal PatternFrame Seam;
        internal PatternAsset[] Motifs;
        internal PatternAsset Hero;
        internal bool Replace;
        internal double ExistingAllowance;
    }

    internal sealed class PatternDraft
    {
        internal PatternFrame Frame;
        internal PatternFillOptions Options;
        internal PatternAsset[] Motifs;
        internal List<PatternPlacement> Items;
        internal PatternPlacement Hero;
        internal PatternBox? Reserve;
        internal double Allowance;
        internal Color Color;
    }

    internal sealed class PatternFillService
    {
        private object _motifDocument, _heroDocument, _resultDocument, _result;
        private (object Shape, int Id)[] _motifs = new (object, int)[0];
        private (object Shape, int Id) _hero;
        private double _lastAllowance;

        internal int Remember()
        {
            var selection = CaptureSelection(out object doc, 64, "Выделите от 1 до 64 объектов узора. Группа считается одним элементом.");
            _motifDocument = doc; _motifs = selection;
            return selection.Length;
        }

        internal void RememberHero()
        {
            _hero = CaptureSelection(out _heroDocument, 1, "Выделите один главный элемент или одну группу.")[0];
        }

        internal void ForgetHero()
        {
            _hero = default; _heroDocument = null;
        }

        private static (object Shape, int Id)[] CaptureSelection(out object document, int limit, string hint)
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic selection = app.ActiveSelectionRange;
            int count = selection == null ? 0 : Convert.ToInt32(selection.Count);
            if (count == 0 || count > limit) throw new InvalidOperationException(hint);
            var shapes = new List<(object Shape, int Id)>(count);
            foreach (dynamic shape in selection) shapes.Add(((object)shape, Convert.ToInt32(shape.StaticID)));
            if (shapes.Count != count) throw new InvalidOperationException("Выделение изменилось. Повторите выбор.");
            document = (object)doc;
            return shapes.ToArray();
        }

        internal PatternEditorSession OpenEditor()
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            object target = SelectedContour((object)app);
            bool replace = SameObject((object)doc, _resultDocument) && SameObject(target, _result);
            var session = new PatternEditorSession { Application = app, Document = doc, Target = target,
                Replace = replace, ExistingAllowance = replace ? _lastAllowance : 0 };
            return InMillimeters((object)doc, () =>
            {
                session.Seam = ReadFrame(GetOutline(target));
                var previews = new Dictionary<int, PatternAsset>();
                try
                {
                    session.Motifs = SameObject(session.Document, _motifDocument)
                        ? _motifs.Select(ReadAsset).ToArray() : new PatternAsset[0];
                    if (SameObject(session.Document, _heroDocument)) session.Hero = ReadAsset(_hero);
                }
                finally
                {
                    if (previews.Count > 0) TryRestore(() => ((dynamic)target).CreateSelection(), "pattern contour selection");
                }
                return session;

                PatternAsset ReadAsset((object Shape, int Id) captured)
                {
                    if (previews.TryGetValue(captured.Id, out PatternAsset cached)) return cached;
                    VerifyAsset(captured.Shape, captured.Id);
                    dynamic shape = captured.Shape;
                    double left, bottom, width, height;
                    shape.GetBoundingBox(out left, out bottom, out width, out height, true);
                    if (width < 0 || height < 0 || !Finite(width + height) || Math.Max(width, height) <= .0001)
                        throw new InvalidOperationException("Один из элементов узора имеет нулевой размер.");
                    var asset = new PatternAsset { Shape = captured.Shape, Id = captured.Id, Size = new PatternPoint(width, height) };
                    previews.Add(asset.Id, asset);
                    string temporary = Path.Combine(Path.GetTempPath(), "Vanya-Pattern-" + Guid.NewGuid().ToString("N"));
                    string path = SportArtworkPreview.Sidecar(temporary);
                    try
                    {
                        shape.CreateSelection();
                        SportArtworkPreview.CaptureSelection(session.Application, session.Document, temporary);
                        if (File.Exists(path)) asset.Preview = SportArtworkPreview.Load(path);
                    }
                    catch (Exception error) { Log.Error("Could not read pattern motif preview.", error); }
                    finally { TryRestore(() => File.Delete(path), "pattern thumbnail cleanup"); }
                    return asset;
                }
            });
        }

        internal static PatternFrame FrameFor(PatternEditorSession session, double allowance)
        {
            if (!Finite(allowance) || allowance < 0 || allowance > 100)
                throw new ArgumentException("Припуск на шов должен быть от 0 до 100 мм.");
            if (session.Replace || allowance == 0) return session.Seam;
            return InMillimeters(session.Document, () =>
            {
                dynamic seam = session.Seam.Curve;
                dynamic contour = seam.Contour(allowance, CorelConstants.CdrContourOutside,
                    CorelConstants.CdrContourRoundCap, CorelConstants.CdrContourCornerRound, 0d);
                if (contour == null || !Convert.ToBoolean(contour.Closed))
                    throw new InvalidOperationException("CorelDRAW не построил замкнутый припуск. Проверьте контур и размер припуска.");
                return ReadFrame((object)contour);
            });
        }

        internal int Apply(PatternEditorSession session, PatternDraft draft)
        {
            dynamic app = session.Application, doc = session.Document, target = session.Target;
            if (!SameObject((object)app.ActiveDocument, session.Document) ||
                !SameObject(SelectedContour(session.Application), session.Target))
                throw new InvalidOperationException("Выделение изменилось. Снова откройте окно паттерна.");
            if (!draft.Options.Texture) foreach (var motif in draft.Motifs) VerifyAsset(motif.Shape, motif.Id);
            if (draft.Hero != null) VerifyAsset(session.Hero.Shape, session.Hero.Id);

            int unit = Convert.ToInt32(doc.Unit);
            bool optimized = Convert.ToBoolean(app.Optimization), started = false;
            var created = new List<object>();
            dynamic frame = null;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
                dynamic color = draft.Options.Texture
                    ? app.CreateRGBColor((int)draft.Color.R, (int)draft.Color.G, (int)draft.Color.B) : null;
                app.Optimization = true;
                doc.BeginCommandGroup("Vanya Tools · паттерн по контуру"); started = true;
                frame = layer.CreateCurve((dynamic)draft.Frame.Curve);
                frame.Fill.ApplyNoFill();
                frame.Outline.SetNoOutline();
                frame.Name = "Vanya · паттерн";
                foreach (var item in draft.Items)
                {
                    if (item.Cell == null)
                    {
                        AddMotif(draft.Motifs[item.Motif], item);
                        continue;
                    }
                    dynamic curve = MakeCell((object)doc, item.Cell, draft.Frame.Left, draft.Frame.Bottom,
                        draft.Options.Mode == PatternFillMode.Pebbles ? .37 : .11);
                    dynamic shape = layer.CreateCurve(curve);
                    created.Add((object)shape);
                    shape.Fill.ApplyUniformFill(color);
                    shape.Outline.SetNoOutline();
                    range.Add(shape);
                }
                if (draft.Hero != null) AddMotif(session.Hero, draft.Hero);
                range.AddToPowerClip(frame, 0);
                frame.CreateSelection();
                if (session.Replace) target.Delete();
                _result = (object)frame; _resultDocument = (object)doc; _lastAllowance = draft.Allowance;
                return created.Count;

                void AddMotif(PatternAsset asset, PatternPlacement item)
                {
                    dynamic shape = ((dynamic)asset.Shape).CopyToLayer(layer);
                    created.Add((object)shape);
                    PlaceMotif((object)shape, asset, item, draft.Frame.Left, draft.Frame.Bottom);
                    range.Add(shape);
                }
            }
            catch
            {
                for (int i = created.Count - 1; i >= 0; i--)
                {
                    object shape = created[i];
                    TryRestore(() => ((dynamic)shape).Delete(), "partial pattern shape");
                }
                if (frame != null) TryRestore(() => frame.Delete(), "partial pattern frame");
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

        private static object SelectedContour(object application)
        {
            dynamic selection = ((dynamic)application).ActiveSelectionRange;
            if (selection == null || Convert.ToInt32(selection.Count) != 1)
                throw new InvalidOperationException("Выделите один замкнутый контур детали.");
            return (object)selection.Shapes.Item[1];
        }

        private static void VerifyAsset(object shape, int id)
        {
            try
            {
                if (shape == null || Convert.ToInt32(((dynamic)shape).StaticID) != id) throw new InvalidOperationException();
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("Один из элементов недоступен. Запомните нужные объекты ещё раз.", error);
            }
        }

        private static T InMillimeters<T>(object document, Func<T> action)
        {
            dynamic doc = document;
            int unit = Convert.ToInt32(doc.Unit);
            if (unit == CorelConstants.CdrMillimeter) return action();
            bool dirty = Convert.ToBoolean(doc.Dirty);
            try { doc.Unit = CorelConstants.CdrMillimeter; return action(); }
            finally
            {
                try { doc.Unit = unit; }
                finally { doc.Dirty = dirty; }
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

        private static PatternFrame ReadFrame(object outline)
        {
            dynamic curve = outline;
            double left, bottom, width, height;
            if (!Convert.ToBoolean(curve.GetBoundingBox(out left, out bottom, out width, out height)) ||
                width <= 0 || height <= 0 || !Finite(width + height))
                throw new InvalidOperationException("Не удалось измерить контур детали.");
            double top = bottom + height;
            var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var context = geometry.Open())
            {
                foreach (dynamic path in curve.SubPaths)
                {
                    bool started = false;
                    foreach (dynamic segment in path.Segments)
                    {
                        if (!started)
                        {
                            context.BeginFigure(Point(segment.StartNode.PositionX, segment.StartNode.PositionY), true, true);
                            started = true;
                        }
                        Point end = Point(segment.EndNode.PositionX, segment.EndNode.PositionY);
                        if (Convert.ToInt32(segment.Type) == 0) context.LineTo(end, true, false);
                        else context.BezierTo(Point(segment.StartingControlPointX, segment.StartingControlPointY),
                            Point(segment.EndingControlPointX, segment.EndingControlPointY), end, true, false);
                    }
                }
            }
            geometry.Freeze();
            if (geometry.IsEmpty()) throw new InvalidOperationException("Контур детали пуст.");
            return new PatternFrame { Curve = outline, Geometry = geometry, Left = left, Bottom = bottom, Width = width, Height = height };

            Point Point(double x, double y) => new Point(x - left, top - y);
        }

        private static void PlaceMotif(object shape, PatternAsset asset, PatternPlacement item, double originX, double originY)
        {
            dynamic copy = shape;
            double scale = PatternTransform.Fit(asset.Size, item).Scale;
            if (Math.Abs(scale - 1) > .000001) copy.Stretch(scale, scale, true);
            double left, bottom, width, height;
            copy.GetBoundingBox(out left, out bottom, out width, out height, true);
            var rotated = PatternTransform.RotatedSize(width, height, item.Angle);
            double excess = Math.Max(rotated.X, rotated.Y) - item.Size;
            if (excess > .05)
            {
                var geometry = PatternTransform.RotatedSize(Convert.ToDouble(copy.SizeWidth), Convert.ToDouble(copy.SizeHeight), item.Angle);
                double longest = Math.Max(geometry.X, geometry.Y);
                if (longest <= excess + .001)
                    throw new InvalidOperationException("Обводка элемента шире заданного размера. Увеличьте размер или уменьшите обводку.");
                double correction = (longest - excess) / longest;
                copy.Stretch(correction, correction, true);
                copy.GetBoundingBox(out left, out bottom, out width, out height, true);
                rotated = PatternTransform.RotatedSize(width, height, item.Angle);
                if (Math.Max(rotated.X, rotated.Y) > item.Size + .1)
                    throw new InvalidOperationException("Обводка не помещается в ячейку. Увеличьте размер элемента.");
            }
            double x = originX + item.Center.X, y = originY + item.Center.Y;
            copy.Move(x - left - width / 2, y - bottom - height / 2);
            if (Math.Abs(item.Angle) > .0001) copy.RotateEx(item.Angle, x, y);
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

        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);

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
