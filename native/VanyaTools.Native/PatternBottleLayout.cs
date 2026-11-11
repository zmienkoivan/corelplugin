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
        private const double Tolerance = .025;
        private const double EdgeInset = .06;

        internal static PatternFrame Frame(PatternBottle bottle, PatternPoint pageCenter)
        {
            var geometry = TraceOutline(bottle, bottle.Outline());
            Rect bounds = geometry.Bounds;
            var clip = geometry.Clone();
            clip.Transform = new TranslateTransform(-bounds.X, -bounds.Y);
            clip.Freeze();
            geometry.Transform = new MatrixTransform(1, 0, 0, -1, -bounds.X, bounds.Bottom);
            geometry.Freeze();
            return new PatternFrame { Bottle = bottle, BottleClip = clip, BottleOrigin = new PatternPoint(bounds.X, bounds.Y),
                Geometry = geometry, Width = bounds.Width, Height = bounds.Height,
                Left = pageCenter.X - bounds.Width / 2, Bottom = pageCenter.Y - bounds.Height / 2 };
        }

        private static StreamGeometry TraceOutline(PatternBottle bottle, IEnumerable<PatternBottle.Edge> edges) =>
            TraceOutline(bottle.Start, edges);

        private static StreamGeometry TraceOutline(PatternPoint start, IEnumerable<PatternBottle.Edge> edges)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(Point(start), true, true);
                foreach (var edge in edges)
                {
                    if (edge.Curved) path.BezierTo(Point(edge.First), Point(edge.Second), Point(edge.End), true, false);
                    else path.LineTo(Point(edge.End), true, false);
                }
            }
            return geometry;
        }

        internal static List<PatternPlacement> Build(PatternFrame frame, PatternAsset[] motifs, PatternFillOptions options,
            bool whole, CancellationToken cancellation, out PatternLayoutGuides guides)
        {
            if (motifs.Length == 0) throw new ArgumentException("Добавьте фоновые фигуры в набор.");
            if (!options.Texture && options.MinScale > options.MaxScale)
                throw new ArgumentException("Укажите диапазон размеров от меньшего к большему.");

            var bottle = frame.Bottle;
            double maxScale = options.Texture ? 1 : options.MaxScale;
            double size = Math.Min(options.SizeMm, (bottle.Width - options.GapMm - 1e-6) / maxScale);
            if (size < .5 || (options.Texture && options.GapMm > size / 2))
                throw new ArgumentException("Уменьшите зазор или размер фигур для этого диаметра.");

            var settings = new PatternFillOptions { Mode = options.Mode, Seed = options.Seed, SizeMm = size,
                GapMm = options.GapMm, MinScale = options.MinScale, MaxScale = options.MaxScale,
                AngleVariation = options.AngleVariation, RepeatWidthMm = bottle.Width,
                RepeatHeightMm = Math.Max(bottle.Slant, size * maxScale + options.GapMm) };
            var module = PatternFillLayout.RepeatModule(motifs.Select(asset => asset.Footprint).ToArray(),
                settings, out _, options.Angle, wrapHeight: false);
            double yOffset = (bottle.Slant - settings.RepeatHeightMm) / 2;
            var poses = new List<Pose>();
            foreach (var item in module)
            {
                cancellation.ThrowIfCancellationRequested();
                var center = new PatternPoint(item.Center.X, item.Center.Y + yOffset);
                var pose = Place(frame, motifs[item.Motif].Footprint, item, center, options.GapMm, whole, poses);
                if (pose != null) poses.Add(pose);
            }
            if (poses.Count > PatternFillLayout.MaximumElements)
                throw new InvalidOperationException("Увеличьте размер фигур. В развёртке помещается до 2400 копий.");
            var placed = poses.Select(pose => pose.Item).ToList();
            guides = new PatternLayoutGuides { Cells = options.Texture ?
                placed.Where(item => item.Cell != null).Select(item => item.Cell).ToArray() : null };
            return placed;
        }

        internal static void AddHero(PatternDraft draft, PatternAsset hero, PatternEditorSettings settings,
            List<PatternPlacement> items, CancellationToken cancellation)
        {
            var bottle = draft.Frame.Bottle;
            var item = new PatternPlacement { Size = settings.HeroSize, Angle = settings.HeroAngle };
            var center = new PatternPoint(bottle.Width * settings.HeroX / 100, bottle.Slant * (1 - settings.HeroY / 100));
            var pose = Place(draft.Frame, hero.Footprint, item, center, settings.HeroGap * 2,
                settings.BottleWhole, Array.Empty<Pose>());
            if (pose == null) throw new InvalidOperationException("Главный рисунок не помещается. Уменьшите размер или свободную зону.");
            draft.Hero = pose.Item;
            draft.Reserve = PatternSilhouette.MoveReserve(PatternSilhouette.Reserve(hero, pose.Item, settings.HeroGap,
                draft.Options.Mode == PatternFillMode.Pebbles), pose.Item.Center);
            var localReserve = draft.Reserve;
            draft.Reserve = Geometry.Combine(localReserve, TurnGeometry(draft.Frame, localReserve, -1),
                GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
            draft.Reserve = Geometry.Combine(draft.Reserve, TurnGeometry(draft.Frame, localReserve, 1),
                GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
            draft.Reserve.Freeze();

            var keep = new List<PatternPlacement>();
            var lost = new List<PatternPlacement>();
            Rect bounds = draft.Reserve.Bounds;
            foreach (var placement in items)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!IntersectsReserve(placement)) keep.Add(placement);
                else if (placement.Cell != null) lost.Add(placement);
            }

            Recover(draft.Frame, draft.Motifs, draft.Options, keep, lost, draft.Reserve, settings.BottleWhole, cancellation);
            draft.Items = keep;

            bool IntersectsReserve(PatternPlacement placement)
            {
                var asset = draft.Motifs[placement.Motif];
                var size = PatternTransform.Fit(asset.Size, placement);
                var area = new Rect(placement.Center.X - size.RotatedWidth / 2, placement.Center.Y - size.RotatedHeight / 2,
                    size.RotatedWidth, size.RotatedHeight);
                return bounds.IntersectsWith(area) &&
                    draft.Reserve.FillContainsWithDetail(PatternSilhouette.At(asset, placement), Tolerance,
                        ToleranceType.Absolute) != IntersectionDetail.Empty;
            }
        }

        private static void Recover(PatternFrame frame, PatternAsset[] motifs, PatternFillOptions options,
            List<PatternPlacement> keep, List<PatternPlacement> lost, Geometry reserve, bool whole, CancellationToken cancellation)
        {
            if (lost.Count == 0) return;
            double minimum = keep.Count == 0 ? options.SizeMm * .75 :
                keep.Select(item => item.Size).OrderBy(size => size).ElementAt(keep.Count / 2) * .75;
            PatternHeroFill.Refit(keep, lost, motifs, options, reserve, cancellation, placement =>
                placement.Size >= minimum && FitsOnRing(frame, motifs, placement, keep, options.GapMm, whole));
        }

        private static Pose Place(PatternFrame frame, PatternFootprint motif, PatternPlacement item, PatternPoint center,
            double gap, bool whole, IReadOnlyList<Pose> obstacles)
        {
            var bottle = frame.Bottle;
            double scale = PatternTransform.Fit(motif.Size, item).Scale;
            double u = bottle.Wrap(center.X), v = center.Y;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                double factor = 1 - attempt * .06;
                var pose = AtBottle(frame, motif, item.Motif, u, v, item.Angle + bottle.RotationAt(u), scale * factor);
                if (whole)
                    for (int step = 0; step < 6 && !bottle.InsideRims(pose.Hull, frame.BottleOrigin, EdgeInset / 2); step++)
                    {
                        var heights = pose.Hull.Select(p => bottle.Project(ToBottle(frame, p)).Y).ToArray();
                        double shift = heights.Min() < EdgeInset ? EdgeInset - heights.Min() :
                            bottle.Slant - EdgeInset - heights.Max();
                        if (heights.Max() - heights.Min() > bottle.Slant) break;
                        v += shift;
                        pose = AtBottle(frame, motif, item.Motif, u, v, item.Angle + bottle.RotationAt(u), scale * factor);
                    }
                if (whole && !bottle.InsideRims(pose.Hull, frame.BottleOrigin, EdgeInset / 2) ||
                    Relation(frame, pose.Hull) == IntersectionDetail.Empty) continue;
                var copies = new[] { pose, TurnPose(frame, pose, -1), TurnPose(frame, pose, 1) };
                if (!Separated(copies[0], copies[1], gap) ||
                    obstacles.Any(other => copies.Any(copy => !Separated(copy, other, gap)))) continue;
                if (item.Cell != null)
                {
                    double shift = Math.Round((u - center.X) / bottle.Width) * bottle.Width;
                    pose.Item.Cell = MapCell(frame, item.Cell, shift, v - item.Center.Y);
                }
                return pose;
            }
            return null;
        }

        private static PatternPoint[] MapCell(PatternFrame frame, PatternPoint[] cell, double dx, double dy)
        {
            var mapped = PatternFootprint.ConvexHull(cell.Select(p => OnFrame(frame, p.X + dx, p.Y + dy)));
            return mapped.Length >= 3 ? mapped : null;
        }

        private static Pose AtBottle(PatternFrame frame, PatternFootprint motif, int index, double u, double v,
            double angle, double scale)
        {
            var size = PatternTransform.RotatedSize(motif.Size.X, motif.Size.Y, angle);
            var item = new PatternPlacement { Motif = index, Angle = angle, Size = Math.Max(size.X, size.Y) * scale,
                Center = OnFrame(frame, u, v) };
            return new Pose(item, motif.At(item));
        }

        private static IntersectionDetail Relation(PatternFrame frame, PatternPoint[] hull)
        {
            if (!frame.BottleClip.Bounds.IntersectsWith(Bounds(hull))) return IntersectionDetail.Empty;
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(Point(hull[0]), true, true);
                for (int i = 1; i < hull.Length; i++) path.LineTo(Point(hull[i]), true, false);
            }
            return frame.BottleClip.FillContainsWithDetail(geometry, Tolerance, ToleranceType.Absolute);
        }

        private static double Distance(PatternPoint a, PatternPoint b)
        {
            double x = a.X - b.X, y = a.Y - b.Y;
            return Math.Sqrt(x * x + y * y);
        }

        private static bool Separated(Pose a, Pose b, double gap)
        {
            if (a.Bounds.Right + gap <= b.Bounds.Left || b.Bounds.Right + gap <= a.Bounds.Left ||
                a.Bounds.Bottom + gap <= b.Bounds.Top || b.Bounds.Bottom + gap <= a.Bounds.Top) return true;

            double dx = b.Item.Center.X - a.Item.Center.X, dy = b.Item.Center.Y - a.Item.Center.Y;
            return OnAxis(dx, dy) || TestEdges(a.Hull) || TestEdges(b.Hull);

            bool TestEdges(PatternPoint[] hull)
            {
                for (int i = 0; i < hull.Length; i++)
                {
                    var p = hull[i]; var q = hull[(i + 1) % hull.Length];
                    if (OnAxis(q.Y - p.Y, p.X - q.X)) return true;
                }
                return false;
            }

            bool OnAxis(double x, double y)
            {
                double length = Math.Sqrt(x * x + y * y);
                if (length < 1e-10) return false;
                double minA = double.PositiveInfinity, maxA = double.NegativeInfinity;
                double minB = double.PositiveInfinity, maxB = double.NegativeInfinity;
                foreach (var p in a.Hull) { double d = p.X * x + p.Y * y; minA = Math.Min(minA, d); maxA = Math.Max(maxA, d); }
                foreach (var p in b.Hull) { double d = p.X * x + p.Y * y; minB = Math.Min(minB, d); maxB = Math.Max(maxB, d); }
                return maxA + gap * length <= minB || maxB + gap * length <= minA;
            }
        }

        internal static PatternPoint[][] CenterMarks(PatternFrame frame)
        {
            double center = frame.Bottle.Width / 2, height = frame.Bottle.Slant;
            return new[]
            {
                new[] { OnFrame(frame, center, -4), OnFrame(frame, center, -2) },
                new[] { OnFrame(frame, center, height + 2), OnFrame(frame, center, height + 4) }
            };
        }

        internal static PatternPoint OnFrame(PatternFrame frame, double u, double v)
        {
            var point = frame.Bottle.Map(u, v);
            return new PatternPoint(point.X - frame.BottleOrigin.X, point.Y - frame.BottleOrigin.Y);
        }

        private static Rect Bounds(PatternPoint[] hull)
        {
            var bounds = Rect.Empty;
            foreach (var point in hull) bounds.Union(Point(point));
            return bounds;
        }

        private static Point Point(PatternPoint point) => new Point(point.X, point.Y);

        private sealed class Pose
        {
            internal readonly PatternPlacement Item;
            internal readonly PatternPoint[] Hull;
            internal readonly Rect Bounds;

            internal Pose(PatternPlacement item, PatternPoint[] hull)
            {
                Item = item; Hull = hull;
                Bounds = PatternBottleLayout.Bounds(hull);
            }
        }
    }
}
