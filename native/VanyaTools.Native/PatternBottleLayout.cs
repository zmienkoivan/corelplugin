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
                settings, out _, options.Angle, wrapHeight: false);
            double yOffset = (bottle.Slant - settings.RepeatHeightMm) / 2;
            var obstacles = new List<Pose>();
            var groups = new List<PatternPlacement[]>();
            var missed = new List<PatternPlacement>();
            int count = 0;
            foreach (var item in module)
            {
                cancellation.ThrowIfCancellationRequested();
                var center = new PatternPoint(item.Center.X, item.Center.Y + yOffset);
                var group = Place(frame, motifs[item.Motif].Footprint, item, center, options.GapMm, whole, obstacles);
                if (group == null)
                {
                    if (options.Texture)
                        for (int repeat = -1; repeat <= 1; repeat++)
                        {
                            double shift = repeat * bottle.Width;
                            if (item.Cell.Max(p => p.X) + shift <= 0 || item.Cell.Min(p => p.X) + shift >= bottle.Width) continue;
                            double u = center.X + shift;
                            var retry = AtBottle(frame, motifs[item.Motif].Footprint, item.Motif, u, center.Y,
                                item.Angle + bottle.RotationAt(u), PatternTransform.Fit(motifs[item.Motif].Size, item).Scale).Item;
                            retry.Cell = MapCell(frame, item.Cell, shift, yOffset);
                            if (retry.Cell != null) missed.Add(retry);
                        }
                    continue;
                }
                var visible = group.Where(pose => pose.Visible).Select(pose => pose.Item).ToArray();
                count += visible.Length;
                if (count > PatternFillLayout.MaximumElements)
                    throw new InvalidOperationException("Увеличьте размер фигур. В развёртке помещается до 2400 копий.");
                obstacles.AddRange(group);
                groups.Add(visible);
            }

            var placed = groups.SelectMany(group => group).ToList();
            Recover(frame, motifs, options, placed, missed, Geometry.Empty, cancellation);
            if (placed.Count > PatternFillLayout.MaximumElements)
                throw new InvalidOperationException("Увеличьте размер фигур. В развёртке помещается до 2400 копий.");
            for (int i = count; i < placed.Count; i++) groups.Add(new[] { placed[i] });

            var cellGuides = options.Texture ? placed.Where(item => item.Cell != null).Select(item => item.Cell).ToArray() : null;
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
                var reserve = PatternSilhouette.Reserve(hero, pose.Item, settings.HeroGap, draft.Options.Mode == PatternFillMode.Pebbles);
                reserve = PatternSilhouette.MoveReserve(reserve, pose.Item.Center);
                draft.Reserve = draft.Reserve == null ? reserve : Geometry.Combine(draft.Reserve, reserve,
                    GeometryCombineMode.Union, null, Tolerance, ToleranceType.Absolute);
            }
            draft.Reserve.Freeze();

            var keep = new List<PatternPlacement>();
            var lost = new List<PatternPlacement>();
            Rect bounds = draft.Reserve.Bounds;
            foreach (var group in groups)
            {
                cancellation.ThrowIfCancellationRequested();
                if (group.Any(IntersectsReserve)) lost.AddRange(group.Where(placement => placement.Cell != null));
                else keep.AddRange(group);
            }

            Recover(draft.Frame, draft.Motifs, draft.Options, keep, lost, draft.Reserve, cancellation);
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
            List<PatternPlacement> keep, List<PatternPlacement> lost, Geometry reserve, CancellationToken cancellation)
        {
            if (lost.Count == 0) return;
            var bottle = frame.Bottle;
            var region = Rect.Empty;
            foreach (var placement in lost) region.Union(Bounds(placement.Cell));
            region.Inflate(Tolerance, Tolerance);
            var outside = Geometry.Combine(new RectangleGeometry(region), frame.BottleClip,
                GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute);
            var blocked = Geometry.Combine(reserve, outside, GeometryCombineMode.Union,
                null, Tolerance, ToleranceType.Absolute);
            var posesByItem = new Dictionary<PatternPlacement, Pose>();
            PatternHeroFill.Refit(keep, lost, motifs, options, blocked, cancellation, Fits);

            bool Fits(PatternPlacement placement)
            {
                var motif = motifs[placement.Motif].Footprint;
                if (Relation(frame, motif.At(placement)) != IntersectionDetail.FullyContains)
                    return false;
                var local = bottle.Project(new PatternPoint(placement.Center.X + frame.BottleOrigin.X,
                    placement.Center.Y + frame.BottleOrigin.Y));
                var hull = motif.At(placement);
                double reach = hull.Max(p => Distance(p, placement.Center));
                double gap = options.GapMm;
                double seamReach = bottle.SeamReach(reach + gap, local.Y);
                return (local.X > seamReach || ClearsSeam(local.X + bottle.Width)) &&
                    (bottle.Width - local.X > seamReach || ClearsSeam(local.X - bottle.Width));

                bool ClearsSeam(double u)
                {
                    double angle = placement.Angle + bottle.RotationAt(u) - bottle.RotationAt(local.X);
                    var ghost = AtBottle(frame, motif, placement.Motif, u, local.Y, angle,
                        PatternTransform.Fit(motif.Size, placement).Scale);
                    foreach (var existing in keep)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!posesByItem.TryGetValue(existing, out var other))
                        {
                            other = new Pose(existing, motifs[existing.Motif].Footprint.At(existing));
                            posesByItem.Add(existing, other);
                        }
                        if (!Separated(ghost, other, gap)) return false;
                    }
                    return Separated(ghost, new Pose(placement, hull), 0);
                }
            }
        }

        private static List<Pose> Place(PatternFrame frame, PatternFootprint motif, PatternPlacement item, PatternPoint center,
            double gap, bool whole, IReadOnlyList<Pose> obstacles)
        {
            var bottle = frame.Bottle;
            double sourceScale = PatternTransform.Fit(motif.Size, item).Scale;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double factor = Math.Pow(.84, attempt), scale = sourceScale * factor;
                var local = new PatternPlacement { Size = item.Size * factor, Angle = item.Angle };
                var localHull = motif.At(local);
                double reach = localHull.Max(p => Distance(p, default));
                double u = bottle.Wrap(center.X), v = center.Y;
                if (whole)
                {
                    var limits = Bounds(localHull);
                    double bottom = -limits.Top + EdgeInset, top = bottle.Slant - limits.Bottom - EdgeInset;
                    if (bottom > top) continue;
                    v = Math.Max(bottom, Math.Min(top, v));
                }
                else if (v + reach < 0 || v - reach > bottle.Slant) return null;

                var poses = new List<Pose>(3);
                var primary = CreatePose(u);
                if (whole && !bottle.InsideRims(primary.Hull, frame.BottleOrigin, EdgeInset / 2)) continue;
                poses.Add(primary);
                double seamReach = bottle.SeamReach(reach + gap, v);
                if (u <= seamReach) poses.Add(CreatePose(u + bottle.Width));
                if (bottle.Width - u <= seamReach) poses.Add(CreatePose(u - bottle.Width));
                if (!poses.Any(pose => pose.Visible)) return null;
                if (poses.Any(pose => obstacles.Any(other => !Separated(pose, other, gap)))) continue;
                if (poses.Count > 1 && poses.Any(pose => poses.Any(other => !ReferenceEquals(pose, other) &&
                    !Separated(pose, other, 0)))) continue;
                return poses;

                Pose CreatePose(double x)
                {
                    var pose = AtBottle(frame, motif, item.Motif, x, v, item.Angle + bottle.RotationAt(x), scale);
                    pose.Visible = Relation(frame, pose.Hull) != IntersectionDetail.Empty;
                    if (pose.Visible && item.Cell != null)
                    {
                        double dx = x - center.X, dy = center.Y - item.Center.Y;
                        pose.Item.Cell = MapCell(frame, item.Cell, dx, dy);
                    }
                    return pose;
                }
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
            double length = Math.Min(2, height / 4);
            return new[]
            {
                new[] { OnFrame(frame, center, 0), OnFrame(frame, center, length) },
                new[] { OnFrame(frame, center, height - length), OnFrame(frame, center, height) }
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
            internal bool Visible;

            internal Pose(PatternPlacement item, PatternPoint[] hull)
            {
                Item = item; Hull = hull;
                Bounds = PatternBottleLayout.Bounds(hull);
            }
        }
    }
}
