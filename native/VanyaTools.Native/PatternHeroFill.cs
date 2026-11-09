using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class PatternHeroFill
    {
        private const double Tolerance = .05;

        internal static List<PatternPlacement> Adjust(IReadOnlyList<PatternPlacement> items, Geometry[] silhouettes,
            PatternAsset[] motifs, PatternFillOptions options, Geometry reserve, CancellationToken cancellation)
        {
            var kept = PatternSilhouette.LeaveRoom(items, silhouettes, reserve, cancellation);
            if (kept.Count == items.Count) return kept;
            var retained = new HashSet<PatternPlacement>(kept);
            var lost = items.Where(item => !retained.Contains(item)).ToArray();
            var edge = ReadBoundary(reserve);
            if (edge.Count == 0) return kept;

            Rect nearby = Rect.Empty;
            foreach (var item in lost) nearby.Union(Bounds(item.Cell));
            nearby.Inflate(options.GapMm + Tolerance, options.GapMm + Tolerance);
            var blockers = new List<Obstacle>();
            for (int i = 0; i < items.Count; i++)
                if (retained.Contains(items[i]) && nearby.IntersectsWith(silhouettes[i].Bounds))
                    blockers.Add(new Obstacle(motifs[items[i].Motif].Footprint.At(items[i])));

            var fit = new PatternFillOptions { SizeMm = options.SizeMm,
                Mode = options.Mode == PatternFillMode.Pebbles ? PatternFillMode.Pebbles : PatternFillMode.Crocodile };
            foreach (var item in lost)
            {
                cancellation.ThrowIfCancellationRequested();
                var asset = motifs[item.Motif];
                var cellBounds = Bounds(item.Cell);
                var center = new PatternPoint(item.Cell.Average(p => p.X), item.Cell.Average(p => p.Y));
                bool crossesBoundary = edge.Any(segment =>
                    Math.Max(segment.Start.X, segment.End.X) >= cellBounds.Left &&
                    Math.Min(segment.Start.X, segment.End.X) <= cellBounds.Right &&
                    Math.Max(segment.Start.Y, segment.End.Y) >= cellBounds.Top &&
                    Math.Min(segment.Start.Y, segment.End.Y) <= cellBounds.Bottom);
                if (!crossesBoundary && reserve.FillContains(new Point(center.X, center.Y), Tolerance, ToleranceType.Absolute))
                    continue;
                cellBounds.Inflate(options.GapMm + Tolerance, options.GapMm + Tolerance);
                var neighbors = blockers.Where(blocker => blocker.Bounds.IntersectsWith(cellBounds)).ToArray();
                var samples = new List<PatternPoint> { center };
                for (int i = 0; i < item.Cell.Length; i++)
                {
                    var vertex = item.Cell[i];
                    var middle = PatternPoint.Between(vertex, item.Cell[(i + 1) % item.Cell.Length], .5);
                    samples.Add(PatternPoint.Between(center, vertex, .72));
                    samples.Add(PatternPoint.Between(center, middle, .72));
                }
                var windows = new List<(List<PatternPoint> Polygon, double Area)>();
                foreach (var sample in samples)
                {
                    cancellation.ThrowIfCancellationRequested();
                    var polygon = new List<PatternPoint>(item.Cell.Length);
                    if (crossesBoundary)
                    {
                        if (reserve.FillContains(new Point(sample.X, sample.Y), Tolerance, ToleranceType.Absolute)) continue;
                        Cut(item.Cell, sample, Closest(sample, edge), Tolerance, polygon);
                    }
                    else polygon.AddRange(item.Cell);
                    var buffer = new List<PatternPoint>(polygon.Count);
                    foreach (var neighbor in neighbors)
                    {
                        if (polygon.Count < 3) break;
                        if (Inside(sample, neighbor.Hull))
                        {
                            polygon.Clear();
                            break;
                        }
                        Cut(polygon, sample, Closest(sample, neighbor.Edges), options.GapMm + Tolerance, buffer);
                        var previous = polygon; polygon = buffer; buffer = previous;
                    }
                    double area = Area(polygon);
                    if (area > Tolerance) windows.Add((polygon, area));
                }

                fit.Angle = options.Texture ? options.Angle : item.Angle;
                PatternPlacement best = null;
                double bestScale = PatternTransform.Fit(asset.Size, item).Scale * .35;
                foreach (var window in windows.OrderByDescending(window => window.Area).Take(4))
                {
                    cancellation.ThrowIfCancellationRequested();
                    var fitted = PatternFillLayout.PlaceInCells(new List<PatternPoint[]> { window.Polygon.ToArray() },
                        new[] { asset.Footprint }, fit, allowEmpty: true, fixedAngle: !options.Texture).SingleOrDefault();
                    if (fitted == null) continue;
                    double scale = PatternTransform.Fit(asset.Size, fitted).Scale;
                    if (scale <= bestScale || reserve.FillContainsWithDetail(PatternSilhouette.At(asset, fitted),
                        Tolerance, ToleranceType.Absolute) != IntersectionDetail.Empty) continue;
                    best = fitted;
                    bestScale = scale;
                }
                if (best == null) continue;
                best.Motif = item.Motif;
                kept.Add(best);
                blockers.Add(new Obstacle(asset.Footprint.At(best)));
            }
            return kept;
        }

        private static void Cut(IReadOnlyList<PatternPoint> polygon, PatternPoint sample, PatternPoint edge,
            double gap, List<PatternPoint> result)
        {
            double nx = edge.X - sample.X, ny = edge.Y - sample.Y;
            double length = Math.Sqrt(nx * nx + ny * ny);
            if (length <= gap)
            {
                result.Clear();
                return;
            }
            PatternFillLayout.Clip(polygon, nx, ny, nx * edge.X + ny * edge.Y - gap * length, result);
        }

        private static List<(PatternPoint Start, PatternPoint End)> ReadBoundary(Geometry reserve)
        {
            var path = reserve.GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute);
            var edges = new List<(PatternPoint, PatternPoint)>();
            foreach (var figure in path.Figures)
            {
                var first = ToPoint(figure.StartPoint);
                var previous = first;
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line) Add(line.Point);
                    else if (segment is PolyLineSegment polyline)
                        foreach (var point in polyline.Points) Add(point);
                }
                if (figure.IsClosed) edges.Add((previous, first));

                void Add(Point point)
                {
                    var next = ToPoint(point);
                    edges.Add((previous, next));
                    previous = next;
                }
            }
            return edges;

            PatternPoint ToPoint(Point point)
            {
                if (path.Transform != null) point = path.Transform.Transform(point);
                return new PatternPoint(point.X, point.Y);
            }
        }

        private static PatternPoint Closest(PatternPoint sample, IReadOnlyList<(PatternPoint Start, PatternPoint End)> edges)
        {
            double distance = double.PositiveInfinity;
            PatternPoint closest = sample;
            foreach (var edge in edges)
            {
                double dx = edge.End.X - edge.Start.X, dy = edge.End.Y - edge.Start.Y;
                double x = sample.X - edge.Start.X, y = sample.Y - edge.Start.Y;
                double length = dx * dx + dy * dy;
                if (length <= 1e-12) continue;
                double fraction = Math.Max(0, Math.Min(1, (x * dx + y * dy) / length));
                x -= fraction * dx;
                y -= fraction * dy;
                double squared = x * x + y * y;
                if (squared >= distance) continue;
                distance = squared;
                closest = new PatternPoint(sample.X - x, sample.Y - y);
            }
            return closest;
        }

        private static bool Inside(PatternPoint point, PatternPoint[] polygon)
        {
            for (int i = 0; i < polygon.Length; i++)
            {
                var a = polygon[i]; var b = polygon[(i + 1) % polygon.Length];
                if ((b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X) < 0) return false;
            }
            return true;
        }

        private static Rect Bounds(PatternPoint[] polygon)
        {
            if (polygon == null || polygon.Length == 0) return Rect.Empty;
            var bounds = Rect.Empty;
            foreach (var point in polygon) bounds.Union(new Point(point.X, point.Y));
            return bounds;
        }

        private static double Area(IReadOnlyList<PatternPoint> polygon)
        {
            double twiceArea = 0;
            for (int i = 1; i + 1 < polygon.Count; i++)
            {
                var origin = polygon[0]; var a = polygon[i]; var b = polygon[i + 1];
                twiceArea += (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
            }
            return twiceArea / 2;
        }

        private sealed class Obstacle
        {
            internal readonly PatternPoint[] Hull;
            internal readonly Rect Bounds;
            internal readonly (PatternPoint Start, PatternPoint End)[] Edges;

            internal Obstacle(PatternPoint[] hull)
            {
                Hull = hull;
                Bounds = PatternHeroFill.Bounds(hull);
                Edges = hull.Select((p, i) => (p, hull[(i + 1) % hull.Length])).ToArray();
            }
        }
    }
}
