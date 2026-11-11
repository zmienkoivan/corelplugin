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
            var items = draft.Items.Select(item => OwnLeft(frame, draft.Motifs[item.Motif].Footprint, item)).ToList();
            if (draft.Hero != null) draft.Hero = OwnLeft(frame, hero.Footprint, draft.Hero);
            double padding = Math.Min(.1, draft.Options.GapMm / 5);
            var envelope = Geometry.Empty;
            var leftBand = TraceOutline(frame.Bottle.Map(-frame.Bottle.Width / 4, 0),
                frame.Bottle.Section(-frame.Bottle.Width / 4, frame.Bottle.Width / 4));
            leftBand.Transform = new TranslateTransform(-frame.BottleOrigin.X, -frame.BottleOrigin.Y);
            var outsideLeft = Geometry.Combine(leftBand, frame.BottleClip, GeometryCombineMode.Exclude,
                null, Tolerance, ToleranceType.Absolute);
            var moved = new bool[items.Count];
            bool ready = false;
            for (int pass = 0; pass <= items.Count + 1; pass++)
            {
                cancellation.ThrowIfCancellationRequested();
                var extensions = envelope;
                for (int i = 0; i < items.Count; i++)
                {
                    var shape = Envelope(draft.Motifs[items[i].Motif].Footprint.At(items[i]), padding);
                    if (TouchesLeft(frame, shape.Bounds))
                        extensions = Geometry.Combine(extensions,
                            Geometry.Combine(shape, outsideLeft, GeometryCombineMode.Intersect, null, Tolerance, ToleranceType.Absolute),
                            GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
                }
                if (draft.Hero != null)
                    extensions = Geometry.Combine(extensions,
                        Geometry.Combine(Envelope(hero.Footprint.At(draft.Hero), padding),
                            outsideLeft, GeometryCombineMode.Intersect, null, Tolerance, ToleranceType.Absolute),
                        GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
                envelope = extensions;
                if (envelope.Bounds.IsEmpty) { ready = true; break; }
                var notch = TurnGeometry(frame, envelope, 1);
                bool changed = false;
                for (int i = 0; i < items.Count; i++)
                {
                    if (moved[i]) continue;
                    var hull = Envelope(draft.Motifs[items[i].Motif].Footprint.At(items[i]), 0);
                    if (notch.FillContainsWithDetail(hull, Tolerance, ToleranceType.Absolute) == IntersectionDetail.Empty)
                        continue;
                    var local = frame.Bottle.Project(ToBottle(frame, items[i].Center));
                    if (local.X <= frame.Bottle.Width / 2)
                        throw new InvalidOperationException("Не удалось провести стык между целыми рисунками. Увеличьте зазор или уменьшите размер рисунков.");
                    items[i] = TurnPlacement(frame, draft.Motifs[items[i].Motif].Footprint, items[i], -1);
                    moved[i] = true; changed = true;
                }
                if (draft.Hero != null)
                {
                    var hull = Envelope(hero.Footprint.At(draft.Hero), 0);
                    if (notch.FillContainsWithDetail(hull, Tolerance, ToleranceType.Absolute) != IntersectionDetail.Empty)
                    {
                        var local = frame.Bottle.Project(ToBottle(frame, draft.Hero.Center));
                        if (local.X <= frame.Bottle.Width / 2)
                            throw new InvalidOperationException("Не удалось сохранить главный рисунок у стыка. Увеличьте его отступ от стыка.");
                        draft.Hero = TurnPlacement(frame, hero.Footprint, draft.Hero, -1);
                        changed = true;
                    }
                }
                if (changed) continue;
                var extended = Geometry.Combine(frame.BottleClip, envelope, GeometryCombineMode.Union,
                    null, Tolerance, ToleranceType.Absolute);
                var clip = Geometry.Combine(extended, notch, GeometryCombineMode.Exclude,
                    null, Tolerance, ToleranceType.Absolute);
                draft.BottleSeamGuide = SeamGuide(frame, clip);
                clip.Transform = new MatrixTransform(1, 0, 0, -1, 0, frame.Height);
                clip.Freeze();
                draft.BottleCutView = clip;
                ready = true;
                break;
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

        private static PatternPlacement OwnLeft(PatternFrame frame, PatternFootprint footprint, PatternPlacement item)
        {
            var local = frame.Bottle.Project(ToBottle(frame, item.Center));
            if (local.X <= frame.Bottle.Width / 2) return CopyPlacement(item);
            var start = OnFrame(frame, frame.Bottle.Width, 0);
            var end = OnFrame(frame, frame.Bottle.Width, frame.Bottle.Slant);
            bool crosses = footprint.At(item).Any(p =>
                (end.Y - start.Y) * (p.X - start.X) - (end.X - start.X) * (p.Y - start.Y) > 0);
            return crosses ? TurnPlacement(frame, footprint, item, -1) : CopyPlacement(item);
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
            transformed.Transform = new MatrixTransform(x.X - zero.X, x.Y - zero.Y,
                y.X - zero.X, y.Y - zero.Y, zero.X, zero.Y);
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

        private static bool TouchesLeft(PatternFrame frame, Rect bounds)
        {
            var a = OnFrame(frame, 0, 0);
            var b = OnFrame(frame, 0, frame.Bottle.Slant);
            return new[] { bounds.TopLeft, bounds.TopRight, bounds.BottomLeft, bounds.BottomRight }.Any(p =>
                (b.Y - a.Y) * (p.X - a.X) - (b.X - a.X) * (p.Y - a.Y) < 0);
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
