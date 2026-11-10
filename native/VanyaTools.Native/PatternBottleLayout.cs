using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class PatternBottleLayout
    {
        private const double Tolerance = .025;
        private const double EdgeInset = .06;

        internal static PatternFrame Frame(PatternBottle bottle, PatternPoint pageCenter)
        {
            var geometry = new StreamGeometry();
            using (var path = geometry.Open())
            {
                path.BeginFigure(Point(bottle.Start), true, true);
                foreach (var edge in bottle.Outline())
                {
                    if (edge.Curved) path.BezierTo(Point(edge.First), Point(edge.Second), Point(edge.End), true, false);
                    else path.LineTo(Point(edge.End), true, false);
                }
            }
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

        internal static List<PatternPlacement[]> Build(PatternFrame frame, PatternAsset[] motifs, PatternFillOptions options,
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
                settings, out var cells, options.Angle);
            double yOffset = (bottle.Slant - settings.RepeatHeightMm) / 2;
            var obstacles = new List<Pose>();
            var groups = new List<PatternPlacement[]>();
            int count = 0;
            foreach (var item in module)
            {
                cancellation.ThrowIfCancellationRequested();
                var center = new PatternPoint(item.Center.X, item.Center.Y + yOffset);
                var group = Place(frame, motifs[item.Motif].Footprint, item, center, options.GapMm, whole, obstacles);
                if (group == null) continue;
                var visible = group.Where(pose => pose.Visible).Select(pose => pose.Item).ToArray();
                count += visible.Length;
                if (count > PatternFillLayout.MaximumElements)
                    throw new InvalidOperationException("Увеличьте размер фигур. В развёртке помещается до 2400 копий.");
                obstacles.AddRange(group);
                groups.Add(visible);
            }

            var cellGuides = cells == null ? null : new List<PatternPoint[]>();
            if (cells != null)
                foreach (var cell in cells)
                    for (int repeat = -1; repeat <= 1; repeat++)
                    {
                        double shift = repeat * bottle.Width;
                        if (cell.Max(p => p.X) + shift <= 0 || cell.Min(p => p.X) + shift >= bottle.Width) continue;
                        cellGuides.Add(cell.Select(p => OnFrame(frame, p.X + shift, p.Y + yOffset)).ToArray());
                    }
            guides = new PatternLayoutGuides { Cells = cellGuides, RepeatLines = new[]
            {
                new[] { OnFrame(frame, 0, 0), OnFrame(frame, 0, bottle.Slant) },
                new[] { OnFrame(frame, bottle.Width, 0), OnFrame(frame, bottle.Width, bottle.Slant) }
            } };
            return groups;
        }

        internal static void AddHero(PatternDraft draft, PatternAsset hero, PatternEditorSettings settings,
            List<PatternPlacement[]> groups, CancellationToken cancellation)
        {
            var bottle = draft.Frame.Bottle;
            var item = new PatternPlacement { Size = settings.HeroSize, Angle = settings.HeroAngle };
            var center = new PatternPoint(bottle.Width * settings.HeroX / 100, bottle.Slant * (1 - settings.HeroY / 100));
            var poses = Place(draft.Frame, hero.Footprint, item, center, settings.HeroGap * 2,
                settings.BottleWhole, Array.Empty<Pose>());
            if (poses == null) throw new InvalidOperationException("Главный рисунок не помещается. Уменьшите размер или свободную зону.");
            var visible = poses.Where(pose => pose.Visible).Select(pose => pose.Item).ToArray();
            draft.Hero = visible[0];
            draft.HeroCopies = visible.Skip(1).ToArray();

            foreach (var pose in poses)
            {
                var reserve = PatternSilhouette.Reserve(hero, pose.Item, settings.HeroGap);
                reserve = PatternSilhouette.MoveReserve(reserve, pose.Item.Center);
                draft.Reserve = draft.Reserve == null ? reserve : Geometry.Combine(draft.Reserve, reserve,
                    GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
            }
            draft.Reserve.Freeze();

            var keep = new List<PatternPlacement>();
            Rect bounds = draft.Reserve.Bounds;
            foreach (var group in groups)
            {
                cancellation.ThrowIfCancellationRequested();
                if (group.Any(IntersectsReserve)) continue;
                keep.AddRange(group);
            }
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

        private static List<Pose> Place(PatternFrame frame, PatternFootprint motif, PatternPlacement item, PatternPoint center,
            double gap, bool whole, IReadOnlyList<Pose> obstacles)
        {
            var bottle = frame.Bottle;
            var extent = whole ? new PatternFootprint(motif.Size) : motif;
            double sourceScale = PatternTransform.Fit(motif.Size, item).Scale;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double factor = Math.Pow(.84, attempt), scale = sourceScale * factor;
                var local = new PatternPlacement { Size = item.Size * factor, Angle = item.Angle };
                var localHull = extent.At(local);
                double reach = localHull.Max(p => Math.Sqrt(p.X * p.X + p.Y * p.Y));
                double u = bottle.Wrap(center.X), v = center.Y;
                if (whole)
                {
                    var limits = Bounds(localHull);
                    double bottom = -limits.Top + EdgeInset, top = bottle.Slant - limits.Bottom - EdgeInset;
                    if (bottom > top) continue;
                    v = Math.Max(bottom, Math.Min(top, v));
                    double left = bottle.SeamReach(Math.Max(0, -limits.Left) + gap / 2 + EdgeInset, v);
                    double right = bottle.Width - bottle.SeamReach(Math.Max(0, limits.Right) + gap / 2 + EdgeInset, v);
                    if (left > right) continue;
                    u = Math.Max(left, Math.Min(right, u));
                }
                else if (v + reach < 0 || v - reach > bottle.Slant) return null;

                var poses = new List<Pose>(3);
                var primary = CreatePose(u, v, true);
                if (whole && Relation(extent.At(primary.Item)) != IntersectionDetail.FullyContains) continue;
                poses.Add(primary);
                double seamReach = bottle.SeamReach(reach + gap, v);
                if (u <= seamReach) poses.Add(CreatePose(u + bottle.Width, v, false));
                if (bottle.Width - u <= seamReach) poses.Add(CreatePose(u - bottle.Width, v, false));
                if (!poses.Any(pose => pose.Visible)) return null;
                if (poses.Any(pose => obstacles.Any(other => !Separated(pose, other, gap)))) continue;
                if (poses.Count > 1 && poses.Any(pose => poses.Any(other => !ReferenceEquals(pose, other) &&
                    !Separated(pose, other, 0)))) continue;
                return poses;

                Pose CreatePose(double x, double y, bool main)
                {
                    double angle = item.Angle + bottle.RotationAt(x);
                    var size = PatternTransform.RotatedSize(motif.Size.X, motif.Size.Y, angle);
                    var placement = new PatternPlacement { Motif = item.Motif, Angle = angle,
                        Size = Math.Max(size.X, size.Y) * scale, Center = OnFrame(frame, x, y) };
                    var pose = new Pose(placement, motif.At(placement));
                    pose.Visible = whole ? main : Relation(pose.Hull) != IntersectionDetail.Empty;
                    return pose;
                }

                IntersectionDetail Relation(PatternPoint[] hull)
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
            }
            return null;
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
            internal bool Visible;

            internal Pose(PatternPlacement item, PatternPoint[] hull)
            {
                Item = item; Hull = hull;
                Bounds = PatternBottleLayout.Bounds(hull);
            }
        }
    }
}
