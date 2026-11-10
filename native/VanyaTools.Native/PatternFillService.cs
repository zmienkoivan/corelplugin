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
    internal sealed class PatternSource
    {
        internal readonly object Shape;
        internal readonly int Id;
        public string Label { get; }
        internal bool Mirrored;

        internal PatternSource(object shape, int id, string label)
        { Shape = shape; Id = id; Label = label; }
    }

    internal sealed class PatternAsset
    {
        internal object Shape;
        internal int Id;
        internal PatternFootprint Footprint;
        internal PatternPoint Size;
        internal BitmapSource Preview;
        internal Geometry Silhouette;
        internal string Label;
        internal bool OutlineFallback, Mirrored;

        internal PatternAsset MirrorHorizontally()
        {
            var copy = (PatternAsset)MemberwiseClone();
            copy.Mirrored = true;
            copy.Footprint = new PatternFootprint(Size, Footprint.Hull.Select(point => new PatternPoint(-point.X, point.Y)));
            return copy;
        }
    }

    internal sealed class PatternFrame
    {
        internal object Curve;
        internal double Left, Bottom, Width, Height;
        internal double Top => Bottom + Height;
        internal Geometry Geometry, BottleClip;
        internal PatternBottle Bottle;
        internal PatternPoint BottleOrigin;
    }

    internal sealed class PatternEditorSession
    {
        internal object Application, Document, Target, BottleSelection;
        internal int BottlePageIndex;
        internal PatternFrame Seam;
        internal PatternPoint BottleCenter;
        internal bool BottleMode => BottlePageIndex > 0;
        internal PatternAsset[] Motifs;
        internal PatternAsset Hero;
        internal int[] MirroredIds = Array.Empty<int>();
        internal bool Replace;
        internal double ExistingAllowance;
    }

    internal sealed class PatternDraft
    {
        internal PatternFrame Frame;
        internal PatternFillOptions Options;
        internal PatternAsset[] Motifs;
        internal List<PatternPlacement> Items;
        internal HashSet<int> MirroredIds;
        internal PatternPlacement Hero;
        internal PatternPlacement[] HeroCopies;
        internal Geometry Reserve, CellGuides, RepeatGuides, CenterMarks;
        internal double Allowance;
    }

    internal sealed class PatternFillService
    {
        private object _motifDocument, _heroDocument, _resultDocument, _result;
        private readonly List<PatternSource> _motifs = new List<PatternSource>();
        private PatternSource _hero;
        private double _lastAllowance;
        private bool _resultWasBottle;

        internal IReadOnlyList<PatternSource> Motifs => _motifs;

        internal int AddMotifs()
        {
            const int maximum = 64;
            var selection = CaptureSelection(out object doc, maximum, "Выделите от 1 до 64 объектов узора. Группа считается одним элементом.");
            bool sameDocument = SameObject(doc, _motifDocument);
            var ids = sameDocument ? new HashSet<int>(_motifs.Select(source => source.Id)) : new HashSet<int>();
            var added = selection.Where(source => ids.Add(source.Id)).ToArray();
            if (ids.Count > maximum) throw new InvalidOperationException("В наборе помещается до 64 фигур. Удалите лишние из списка.");
            if (!sameDocument) _motifs.Clear();
            _motifDocument = doc;
            _motifs.AddRange(added);
            return added.Length;
        }

        internal void RemoveMotifs(IEnumerable<int> ids)
        {
            var selected = new HashSet<int>(ids);
            _motifs.RemoveAll(source => selected.Contains(source.Id));
        }

        internal void RememberHero()
        {
            _hero = CaptureSelection(out _heroDocument, 1, "Выделите один главный элемент или одну группу.")[0];
        }

        internal void ForgetHero()
        {
            _hero = null; _heroDocument = null;
        }

        private static PatternSource[] CaptureSelection(out object document, int limit, string hint)
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            dynamic selection = app.ActiveSelectionRange;
            int count = selection == null ? 0 : Convert.ToInt32(selection.Count);
            if (count == 0 || count > limit) throw new InvalidOperationException(hint);
            var shapes = new List<PatternSource>(count);
            foreach (dynamic shape in selection)
            {
                int id = Convert.ToInt32(shape.StaticID), type = Convert.ToInt32(shape.Type);
                string name = Convert.ToString(shape.Name);
                if (String.IsNullOrWhiteSpace(name))
                    name = type == CorelConstants.CdrBitmapShape ? "Растр" : type == CorelConstants.CdrGroupShape ? "Группа" : "Объект";
                shapes.Add(new PatternSource((object)shape, id, name + " · #" + id));
            }
            if (shapes.Count != count) throw new InvalidOperationException("Выделение изменилось. Повторите выбор.");
            document = (object)doc;
            return shapes.ToArray();
        }

        internal PatternEditorSession OpenEditor() => Open(false);

        internal PatternEditorSession OpenBottleEditor() => Open(true);

        private PatternEditorSession Open(bool bottle)
        {
            dynamic app = CorelApp.Get(), doc = app.ActiveDocument;
            if (doc == null) throw new InvalidOperationException("Откройте документ CorelDRAW.");
            object target = bottle ? null : SelectedContour((object)app);
            bool replace = _resultWasBottle == bottle && SameObject((object)doc, _resultDocument);
            if (bottle && replace)
            {
                dynamic selected = app.ActiveSelectionRange;
                if (selected != null && Convert.ToInt32(selected.Count) == 1) target = (object)selected.Shapes.Item[1];
            }
            replace = replace && SameObject(target, _result);
            var session = new PatternEditorSession { Application = app, Document = doc, Target = target,
                Replace = replace, ExistingAllowance = replace ? _lastAllowance : 0 };
            return InMillimeters((object)doc, () =>
            {
                if (bottle)
                {
                    dynamic page = doc.ActivePage, selection = app.CreateShapeRange(), selected = app.ActiveSelectionRange;
                    if (page == null) throw new InvalidOperationException("Откройте страницу CorelDRAW.");
                    if (selected != null)
                        foreach (dynamic shape in selected) selection.Add(shape);
                    double x = 0, y = 0, width = 0, height = 0;
                    if (replace) ((dynamic)target).GetBoundingBox(out x, out y, out width, out height, true);
                    else page.GetBoundingBox(ref x, ref y, ref width, ref height);
                    session.BottlePageIndex = Convert.ToInt32(page.Index);
                    session.BottleSelection = (object)selection;
                    session.BottleCenter = new PatternPoint(x + width / 2, y + height / 2);
                }
                else session.Seam = ReadFrame(GetOutline(target));
                var previews = new Dictionary<int, PatternAsset>();
                try
                {
                    bool hasMotifs = SameObject(session.Document, _motifDocument);
                    session.Motifs = hasMotifs ? _motifs.Select(ReadAsset).ToArray() : Array.Empty<PatternAsset>();
                    if (hasMotifs)
                        session.MirroredIds = _motifs.Where(source => source.Mirrored).Select(source => source.Id).ToArray();
                    if (SameObject(session.Document, _heroDocument)) session.Hero = ReadAsset(_hero);
                }
                finally
                {
                    if (previews.Count > 0) TryRestore(() => RestoreSelection(session), "pattern source selection");
                }
                return session;

                PatternAsset ReadAsset(PatternSource captured)
                {
                    if (previews.TryGetValue(captured.Id, out PatternAsset cached)) return cached;
                    VerifyAsset(captured.Shape, captured.Id);
                    dynamic shape = captured.Shape;
                    double left, bottom, width, height;
                    shape.GetBoundingBox(out left, out bottom, out width, out height, true);
                    if (width < 0 || height < 0 || !Finite(width + height) || Math.Max(width, height) <= .0001)
                        throw new InvalidOperationException("Один из элементов узора имеет нулевой размер.");
                    var asset = new PatternAsset { Shape = captured.Shape, Id = captured.Id, Label = captured.Label,
                        Size = new PatternPoint(width, height) };
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

        // The bitmap is frozen during capture. Geometry preparation needs no Corel calls.
        internal static void PrepareSilhouette(PatternAsset asset)
        {
            if (asset.Silhouette != null) return;
            asset.OutlineFallback = asset.Preview == null;
            try { asset.Silhouette = PatternSilhouette.Read(asset.Preview, asset.Size, out asset.Footprint); }
            catch (Exception error)
            {
                Log.Error("Could not trace pattern silhouette.", error);
                asset.OutlineFallback = true;
                asset.Silhouette = PatternSilhouette.Read(null, asset.Size, out asset.Footprint);
            }
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
            if (!SameObject((object)app.ActiveDocument, session.Document))
                throw new InvalidOperationException("Активный документ изменился. Снова откройте окно паттерна.");
            if (session.BottleMode && Convert.ToInt32(doc.ActivePage.Index) != session.BottlePageIndex)
                throw new InvalidOperationException("Активная страница изменилась. Снова откройте окно паттерна.");
            if (!session.BottleMode || session.Replace)
            {
                dynamic selected = SelectedContour(session.Application);
                if (Convert.ToInt32(selected.StaticID) != Convert.ToInt32(target.StaticID))
                    throw new InvalidOperationException("Выделение изменилось. Снова откройте окно паттерна.");
            }
            foreach (var motif in draft.Motifs) VerifyAsset(motif.Shape, motif.Id);
            if (draft.Hero != null) VerifyAsset(session.Hero.Shape, session.Hero.Id);

            int unit = Convert.ToInt32(doc.Unit);
            bool optimized = Convert.ToBoolean(app.Optimization), started = false;
            var created = new List<object>();
            dynamic frame = null;
            try
            {
                doc.Unit = CorelConstants.CdrMillimeter;
                dynamic layer = doc.ActiveLayer, range = app.CreateShapeRange();
                app.Optimization = true;
                doc.BeginCommandGroup(session.BottleMode ? "Vanya Tools · развёртка бутылки" : "Vanya Tools · паттерн по контуру");
                started = true;
                object curve = session.BottleMode ? BottleCurve((object)doc, draft.Frame) : draft.Frame.Curve;
                frame = layer.CreateCurve((dynamic)curve);
                frame.Fill.ApplyNoFill();
                frame.Outline.SetNoOutline();
                frame.Name = session.BottleMode ? "Vanya · развёртка бутылки" : "Vanya · паттерн";
                foreach (var item in draft.Items) AddMotif(draft.Motifs[item.Motif], item);
                if (draft.Hero != null) AddMotif(session.Hero, draft.Hero);
                if (draft.HeroCopies != null)
                    foreach (var item in draft.HeroCopies) AddMotif(session.Hero, item);
                int motifCount = created.Count;
                if (draft.CenterMarks != null)
                    foreach (var line in PatternBottleLayout.CenterMarks(draft.Frame))
                    {
                        dynamic mark = layer.CreateLineSegment(draft.Frame.Left + line[0].X, draft.Frame.Bottom + line[0].Y,
                            draft.Frame.Left + line[1].X, draft.Frame.Bottom + line[1].Y);
                        created.Add((object)mark);
                        mark.Name = "Vanya · метка центра бутылки";
                        mark.Outline.Color.RGBAssign(0, 0, 0);
                        mark.Outline.Width = .15;
                        range.Add(mark);
                    }
                if (created.Count != 0) range.AddToPowerClip(frame, 0);
                frame.CreateSelection();
                if (session.Replace) target.Delete();
                _result = (object)frame; _resultDocument = (object)doc; _lastAllowance = draft.Allowance;
                _resultWasBottle = session.BottleMode;
                foreach (var source in _motifs) source.Mirrored = draft.MirroredIds.Contains(source.Id);
                return motifCount;

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
                if (started) TryRestore(() => RestoreSelection(session), "pattern selection");
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

        private static object BottleCurve(object document, PatternFrame frame)
        {
            dynamic curve = ((dynamic)document).CreateCurve();
            var start = frame.Bottle.Start;
            double x = frame.Left - frame.BottleOrigin.X, y = frame.Bottom - frame.BottleOrigin.Y;
            dynamic path = curve.CreateSubPath(x + start.X, y + start.Y);
            foreach (var edge in frame.Bottle.Outline())
            {
                if (edge.Curved)
                    path.AppendCurveSegment2(x + edge.End.X, y + edge.End.Y, x + edge.First.X, y + edge.First.Y,
                        x + edge.Second.X, y + edge.Second.Y);
                else path.AppendLineSegment(x + edge.End.X, y + edge.End.Y);
            }
            path.Closed = true;
            return (object)curve;
        }

        private static void RestoreSelection(PatternEditorSession session)
        {
            if (!session.BottleMode) ((dynamic)session.Target).CreateSelection();
            else
            {
                dynamic selection = session.BottleSelection;
                if (Convert.ToInt32(selection.Count) == 0) ((dynamic)session.Document).ClearSelection();
                else selection.CreateSelection();
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
            if (asset.Mirrored) copy.Flip(CorelConstants.CdrFlipHorizontal);
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
