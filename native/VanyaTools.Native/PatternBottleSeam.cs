using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static partial class PatternBottleLayout
    {
        // Cutting changes ownership across the seam, never the layout on the bottle.
        internal static void CloseSeam(PatternDraft draft, PatternAsset hero, CancellationToken cancellation)
        {
            var frame = draft.Frame;
            var items = draft.Items.Select(item => OwnBestSide(frame, draft.Motifs[item.Motif], item)).ToList();
            if (draft.Hero != null) draft.Hero = OwnBestSide(frame, hero, draft.Hero);
            double padding = Math.Min(.1, draft.Options.GapMm / 5);
            var outsideLeft = OutsideSeam(frame, 0);
            var outsideRight = OutsideSeam(frame, frame.Bottle.Width);
            var moved = new bool[items.Count];
            bool heroMoved = false, ready = false;
            for (int pass = 0; pass <= items.Count + 1; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                var shapes = items.Select(item => Envelope(draft.Motifs[item.Motif].Footprint.At(item), padding)).ToList();
                if (draft.Hero != null) shapes.Add(Envelope(hero.Footprint.At(draft.Hero), padding));
                var left = Extension(shapes, outsideLeft);
                var right = Extension(shapes, outsideRight);
                if (left.Bounds.IsEmpty && right.Bounds.IsEmpty) { ready = true; break; }
                var notchRight = TurnGeometry(frame, left, 1);
                var notchLeft = TurnGeometry(frame, right, -1);
                bool changed = false;
                for (int i = 0; i < items.Count; i++)
                {
                    int turn = RequiredTurn(draft.Motifs[items[i].Motif].Footprint, items[i]);
                    if (turn == 0) continue;
                    if (moved[i]) throw new InvalidOperationException("Не удалось провести стык между целыми рисунками. Увеличьте зазор между ними.");
                    items[i] = TurnPlacement(frame, draft.Motifs[items[i].Motif].Footprint, items[i], turn);
                    moved[i] = true; changed = true;
                }
                if (draft.Hero != null)
                {
                    int turn = RequiredTurn(hero.Footprint, draft.Hero);
                    if (turn != 0)
                    {
                        if (heroMoved) throw new InvalidOperationException("Не удалось сохранить главный рисунок у стыка. Увеличьте его отступ от стыка.");
                        draft.Hero = TurnPlacement(frame, hero.Footprint, draft.Hero, turn);
                        heroMoved = true; changed = true;
                    }
                }
                if (changed) continue;
                var extended = Geometry.Combine(frame.BottleClip,
                    Geometry.Combine(left, right, GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute),
                    GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
                var notches = Geometry.Combine(notchLeft, notchRight, GeometryCombineMode.Union,
                    null, Tolerance, ToleranceType.Absolute);
                var clip = Geometry.Combine(extended, notches, GeometryCombineMode.Exclude,
                    null, Tolerance, ToleranceType.Absolute);
                draft.BottleSeamGuide = SeamGuide(frame, clip);
                clip.Transform = new MatrixTransform(1, 0, 0, -1, 0, frame.Height);
                clip.Freeze();
                draft.BottleCutView = clip;
                ready = true;
                break;

                int RequiredTurn(PatternFootprint footprint, PatternPlacement item)
                {
                    var hull = Envelope(footprint.At(item), 0);
                    bool fromRight = Intersects(notchRight, hull), fromLeft = Intersects(notchLeft, hull);
                    if (!fromRight && !fromLeft) return 0;
                    double u = frame.Bottle.Project(ToBottle(frame, item.Center)).X;
                    if (fromRight && fromLeft || fromRight && u <= frame.Bottle.Width / 2 ||
                        fromLeft && u >= frame.Bottle.Width / 2)
                        throw new InvalidOperationException("Не удалось провести стык между целыми рисунками. Увеличьте зазор или уменьшите размер рисунков.");
                    return fromRight ? -1 : 1;
                }
            }
            if (!ready) throw new InvalidOperationException("Не удалось провести стык через паттерн. Увеличьте зазор между рисунками.");
            draft.Items = items;
            if (draft.Options.Texture)
            {
                var cells = new StreamGeometry();
                using (var path = cells.Open())
                    foreach (var item in items.Where(item => item.Cell != null))
                    {
                        path.BeginFigure(Point(item.Cell[0]), false, true);
                        foreach (var point in item.Cell.Skip(1)) path.LineTo(Point(point), true, false);
                    }
                cells.Freeze();
                draft.CellGuides = cells;
            }
        }

        private static Geometry OutsideSeam(PatternFrame frame, double u)
        {
            var bottle = frame.Bottle;
            double start = u - bottle.Width / 4, end = u + bottle.Width / 4;
            var band = TraceOutline(bottle.Map(start, 0), bottle.Section(start, end));
            band.Transform = new TranslateTransform(-frame.BottleOrigin.X, -frame.BottleOrigin.Y);
            return Geometry.Combine(band, frame.BottleClip, GeometryCombineMode.Exclude,
                null, Tolerance, ToleranceType.Absolute);
        }

        private static Geometry Extension(IEnumerable<Geometry> shapes, Geometry outside)
        {
            Geometry result = Geometry.Empty;
            foreach (var shape in shapes)
            {
                if (!shape.Bounds.IntersectsWith(outside.Bounds)) continue;
                var part = Geometry.Combine(shape, outside, GeometryCombineMode.Intersect,
                    null, Tolerance, ToleranceType.Absolute);
                if (!part.Bounds.IsEmpty)
                    result = Geometry.Combine(result, part, GeometryCombineMode.Union,
                        null, Tolerance, ToleranceType.Absolute);
            }
            return result;
        }

        private static bool Intersects(Geometry first, Geometry second) =>
            first.Bounds.IntersectsWith(second.Bounds) &&
            first.FillContainsWithDetail(second, Tolerance, ToleranceType.Absolute) != IntersectionDetail.Empty;

        internal static bool FitsOnRing(PatternFrame frame, PatternAsset[] motifs, PatternPlacement placement,
            IReadOnlyList<PatternPlacement> items, double gap, bool whole)
        {
            var footprint = motifs[placement.Motif].Footprint;
            var candidate = new Pose(placement, footprint.At(placement));
            if (whole && !frame.Bottle.InsideRims(candidate.Hull, frame.BottleOrigin, EdgeInset / 2)) return false;
            if (Relation(frame, candidate.Hull) == IntersectionDetail.Empty) return false;
            var copies = new[] { candidate, TurnPose(frame, candidate, -1), TurnPose(frame, candidate, 1) };
            if (!Separated(copies[0], copies[1], gap)) return false;
            foreach (var item in items)
            {
                var other = new Pose(item, motifs[item.Motif].Footprint.At(item));
                if (copies.Any(copy => !Separated(copy, other, gap))) return false;
            }
            return true;
        }

        private static PatternPlacement OwnBestSide(PatternFrame frame, PatternAsset asset, PatternPlacement item)
        {
            var best = CopyPlacement(item);
            var shape = PatternSilhouette.At(asset, item);
            double area = shape.GetArea(Tolerance, ToleranceType.Absolute);
            double score = AreaInside(item);
            double improvement = Math.Max(.001, area * .001);
            if (score >= area - improvement) return best;
            foreach (int turn in new[] { -1, 1 })
            {
                var candidate = TurnPlacement(frame, asset.Footprint, item, turn);
                double inside = AreaInside(candidate);
                if (inside <= score + improvement) continue;
                best = candidate; score = inside;
            }
            return best;

            double AreaInside(PatternPlacement placement) =>
                Geometry.Combine(PatternSilhouette.At(asset, placement), frame.BottleClip, GeometryCombineMode.Intersect,
                    null, Tolerance, ToleranceType.Absolute).GetArea(Tolerance, ToleranceType.Absolute);
        }

        private static PatternPlacement CopyPlacement(PatternPlacement item) =>
            new PatternPlacement { Motif = item.Motif, Size = item.Size, Angle = item.Angle, Center = item.Center, Cell = item.Cell };

        private static PatternPlacement TurnPlacement(PatternFrame frame, PatternFootprint footprint,
            PatternPlacement item, int turns)
        {
            double angle = item.Angle + turns * (frame.Bottle.RotationAt(frame.Bottle.Width) - frame.Bottle.RotationAt(0));
            var size = PatternTransform.RotatedSize(footprint.Size.X, footprint.Size.Y, angle);
            var result = new PatternPlacement { Motif = item.Motif, Angle = angle,
                Size = Math.Max(size.X, size.Y) * PatternTransform.Fit(footprint.Size, item).Scale,
                Center = TurnPoint(frame, item.Center, turns) };
            if (item.Cell != null) result.Cell = item.Cell.Select(p => TurnPoint(frame, p, turns)).ToArray();
            return result;
        }

        private static Pose TurnPose(PatternFrame frame, Pose pose, int turns) =>
            new Pose(new PatternPlacement { Center = TurnPoint(frame, pose.Item.Center, turns) },
                pose.Hull.Select(p => TurnPoint(frame, p, turns)).ToArray());

        private static PatternPoint TurnPoint(PatternFrame frame, PatternPoint point, int turns)
        {
            var transformed = frame.Bottle.Repeat(ToBottle(frame, point), turns);
            return new PatternPoint(transformed.X - frame.BottleOrigin.X, transformed.Y - frame.BottleOrigin.Y);
        }

        private static Geometry TurnGeometry(PatternFrame frame, Geometry geometry, int turns)
        {
            var zero = TurnPoint(frame, default, turns);
            var x = TurnPoint(frame, new PatternPoint(1, 0), turns);
            var y = TurnPoint(frame, new PatternPoint(0, 1), turns);
            var transformed = geometry.Clone();
            var turn = new Matrix(x.X - zero.X, x.Y - zero.Y,
                y.X - zero.X, y.Y - zero.Y, zero.X, zero.Y);
            var matrix = geometry.Transform?.Value ?? Matrix.Identity;
            matrix.Append(turn);
            transformed.Transform = new MatrixTransform(matrix);
            transformed.Freeze();
            return transformed;
        }

        private static Geometry Envelope(PatternPoint[] hull, double padding)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(Point(hull[0]), true, true);
                foreach (var point in hull.Skip(1)) path.LineTo(Point(point), true, false);
            }
            if (padding <= 0) { geometry.Freeze(); return geometry; }
            var pen = new Pen(Brushes.Black, padding * 2) { LineJoin = PenLineJoin.Round };
            return Geometry.Combine(geometry, geometry.GetWidenedPathGeometry(pen, Tolerance, ToleranceType.Absolute),
                GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
        }

        private static Geometry SeamGuide(PatternFrame frame, Geometry clip)
        {
            var guide = new StreamGeometry();
            using (var path = guide.Open())
                foreach (var figure in clip.GetFlattenedPathGeometry(.02, ToleranceType.Absolute).Figures)
                {
                    var previous = figure.StartPoint;
                    bool drawing = false;
                    var points = Vertices(figure).Concat(new[] { figure.StartPoint });
                    foreach (var point in points)
                    {
                        var a = frame.Bottle.Project(ToBottle(frame, new PatternPoint(previous.X, previous.Y)));
                        var b = frame.Bottle.Project(ToBottle(frame, new PatternPoint(point.X, point.Y)));
                        bool rim = Math.Abs(a.Y) < .06 && Math.Abs(b.Y) < .06 ||
                            Math.Abs(a.Y - frame.Bottle.Slant) < .06 && Math.Abs(b.Y - frame.Bottle.Slant) < .06;
                        if (rim) drawing = false;
                        else
                        {
                            if (!drawing) path.BeginFigure(previous, false, false);
                            path.LineTo(point, true, false);
                            drawing = true;
                        }
                        previous = point;
                    }
                }
            guide.Freeze();
            return guide;
        }

        private static IEnumerable<Point> Vertices(PathFigure figure)
        {
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment polyline)
                    foreach (var point in polyline.Points) yield return point;
                else if (segment is LineSegment line) yield return line.Point;
            }
        }

        private static PatternPoint ToBottle(PatternFrame frame, PatternPoint point) =>
            new PatternPoint(point.X + frame.BottleOrigin.X, point.Y + frame.BottleOrigin.Y);
    }
}
