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
            var obstacles = new List<Pose>();
            var placed = new List<PatternPlacement>();
            var missed = new List<PatternPlacement>();
            foreach (var item in module)
            {
                cancellation.ThrowIfCancellationRequested();
                var center = new PatternPoint(item.Center.X, item.Center.Y + yOffset);
                var motif = motifs[item.Motif].Footprint;
                var pose = Place(frame, motif, item, center, options.GapMm, whole, obstacles);
                if (pose != null)
                {
                    obstacles.Add(pose);
                    placed.Add(pose.Item);
                }
                else if (options.Texture)
                    for (int repeat = -1; repeat <= 1; repeat++)
                    {
                        double shift = repeat * bottle.Width;
                        if (item.Cell.Max(point => point.X) + shift <= 0 || item.Cell.Min(point => point.X) + shift >= bottle.Width) continue;
                        double u = center.X + shift;
                        var retry = AtBottle(frame, motif, item.Motif, u, center.Y, item.Angle + bottle.RotationAt(u),
                            PatternTransform.Fit(motif.Size, item).Scale).Item;
                        retry.Cell = MapCell(frame, item.Cell, shift, yOffset);
                        if (retry.Cell != null) missed.Add(retry);
                    }
            }

            Recover(frame, motifs, options, placed, missed, Geometry.Empty, cancellation);
            if (placed.Count > PatternFillLayout.MaximumElements)
                throw new InvalidOperationException("Увеличьте размер фигур. В развёртке помещается до 2400 копий.");

            var cellGuides = options.Texture ? placed.Where(item => item.Cell != null).Select(item => item.Cell).ToArray() : null;
            guides = new PatternLayoutGuides { Cells = cellGuides };
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

            var keep = new List<PatternPlacement>();
            var lost = new List<PatternPlacement>();
            Rect bounds = draft.Reserve.Bounds;
            foreach (var placement in items)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!IntersectsReserve(placement)) keep.Add(placement);
                else if (placement.Cell != null) lost.Add(placement);
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
            var region = Rect.Empty;
            foreach (var placement in lost) region.Union(Bounds(placement.Cell));
            region.Inflate(Tolerance, Tolerance);
            var outside = Geometry.Combine(new RectangleGeometry(region), frame.BottleClip,
                GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute);
            var blocked = Geometry.Combine(reserve, outside, GeometryCombineMode.Union,
                null, Tolerance, ToleranceType.Absolute);
            PatternHeroFill.Refit(keep, lost, motifs, options, blocked, cancellation, Fits);

            bool Fits(PatternPlacement placement)
            {
                var motif = motifs[placement.Motif].Footprint;
                return FitsSides(frame, motif, placement, options.GapMm) &&
                    Relation(frame, motif.At(placement)) == IntersectionDetail.FullyContains;
            }
        }

        private static Pose Place(PatternFrame frame, PatternFootprint motif, PatternPlacement item, PatternPoint center,
            double gap, bool whole, IReadOnlyList<Pose> obstacles)
        {
            var bottle = frame.Bottle;
            double sourceScale = PatternTransform.Fit(motif.Size, item).Scale;
            double sourceReach = Distance(motif.Size, default) * sourceScale / 2;
            var extent = new PatternFootprint(motif.Size);
            double u = bottle.Wrap(center.X), pad = gap / 2 + EdgeInset;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double factor = Math.Pow(.84, attempt), scale = sourceScale * factor;
                var localHull = motif.At(new PatternPlacement { Size = item.Size * factor, Angle = item.Angle });
                double reach = sourceReach * factor, v = center.Y;
                if (whole)
                {
                    var limits = Bounds(localHull);
                    double bottom = -limits.Top + EdgeInset, top = bottle.Slant - limits.Bottom - EdgeInset;
                    if (bottom > top) continue;
                    v = Math.Max(bottom, Math.Min(top, v));
                }
                else if (v + reach < 0 || v - reach > bottle.Slant) return null;

                double seamReach = bottle.SeamReach(reach + pad, v);
                var pose = Create(u);
                if (Fits(pose, u)) return AttachCell(pose, u);
                if (u > seamReach && bottle.Width - u > seamReach) continue;

                bool left = u <= bottle.Width / 2;
                var shifted = AlongSide(left, left ? u : bottle.Width - u) ?? AlongSide(!left, 0);
                if (shifted != null) return shifted;

                Pose AlongSide(bool fromLeft, double distance)
                {
                    double far = Math.Min(seamReach, bottle.Width / 2);
                    double near = Math.Min(far, distance);
                    double x = Position(near);
                    var candidate = Create(x);
                    if (!ClearsSides(frame, extent.At(candidate.Item), x, v, reach, pad))
                    {
                        x = Position(far);
                        candidate = Create(x);
                        for (int step = 0; step < 8; step++)
                        {
                            double middle = (near + far) / 2, probeX = Position(middle);
                            var probe = Create(probeX);
                            if (ClearsSides(frame, extent.At(probe.Item), probeX, v, reach, pad))
                            {
                                far = middle; x = probeX; candidate = probe;
                            }
                            else near = middle;
                        }
                    }
                    return Fits(candidate, x) ? AttachCell(candidate, x) : null;

                    double Position(double value) => fromLeft ? value : bottle.Width - value;
                }

                Pose Create(double x) => AtBottle(frame, motif, item.Motif, x, v, item.Angle + bottle.RotationAt(x), scale);

                bool Fits(Pose candidate, double x) =>
                    ClearsSides(frame, extent.At(candidate.Item), x, v, reach, pad) &&
                    (!whole || bottle.InsideRims(candidate.Hull, frame.BottleOrigin, EdgeInset / 2)) &&
                    Relation(frame, candidate.Hull) != IntersectionDetail.Empty &&
                    obstacles.All(other => Separated(candidate, other, gap));

                Pose AttachCell(Pose candidate, double x)
                {
                    if (item.Cell != null)
                    {
                        double shift = Math.Round((x - center.X) / bottle.Width) * bottle.Width;
                        candidate.Item.Cell = MapCell(frame, item.Cell, shift, center.Y - item.Center.Y);
                    }
                    return candidate;
                }
            }
            return null;
        }

        internal static bool FitsSides(PatternFrame frame, PatternFootprint motif, PatternPlacement placement, double gap)
        {
            var extent = new PatternFootprint(motif.Size).At(placement);
            var local = frame.Bottle.Project(new PatternPoint(placement.Center.X + frame.BottleOrigin.X,
                placement.Center.Y + frame.BottleOrigin.Y));
            double reach = extent.Max(point => Distance(point, placement.Center));
            return ClearsSides(frame, extent, local.X, local.Y, reach, gap / 2 + EdgeInset);
        }

        private static bool ClearsSides(PatternFrame frame, PatternPoint[] hull, double u, double v, double reach, double pad)
        {
            var bottle = frame.Bottle;
            double limit = bottle.SeamReach(reach + pad, v);
            return (u > limit || Clears(0, 1)) && (bottle.Width - u > limit || Clears(bottle.Width, -1));

            bool Clears(double seam, int side)
            {
                var a = OnFrame(frame, seam, 0);
                var b = OnFrame(frame, seam, bottle.Slant);
                return hull.All(point => side * ((b.Y - a.Y) * (point.X - a.X) - (b.X - a.X) * (point.Y - a.Y)) >=
                    pad * bottle.Slant - 1e-8);
            }
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
