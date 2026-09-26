using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    // Converts the raster alpha channel directly into external vector paths.
    // PowerTRACE can return the white mask background as a single rectangle.
    internal static class AlphaMaskVectorizer
    {
        private struct GridPoint
        {
            public int X;
            public int Y;
            public GridPoint(int x, int y) { X = x; Y = y; }
        }

        private struct Edge
        {
            public GridPoint Start;
            public GridPoint End;
            public int Direction; // east, north, west, south
            public Edge(int x0, int y0, int x1, int y1, int direction)
            {
                Start = new GridPoint(x0, y0);
                End = new GridPoint(x1, y1);
                Direction = direction;
            }
        }

        public static dynamic CreateShapes(dynamic doc, dynamic app, bool[] visible,
            int width, int height, double left, double bottom, double shapeWidth,
            double shapeHeight, int smoothing, int detail, double simplifyToleranceMm,
            bool mergeAdjacent)
        {
            if (visible == null || visible.Length != checked(width * height))
                throw new InvalidOperationException("Некорректный размер альфа-маски.");

            if (mergeAdjacent)
                BridgeSinglePixelGaps(visible, width, height);

            var edges = new List<Edge>();
            var outgoing = new Dictionary<long, List<int>>();
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                if (!visible[y * width + x]) continue;
                if (y == 0 || !visible[(y - 1) * width + x])
                    AddEdge(edges, outgoing, x, y, x + 1, y, 0);
                if (x == width - 1 || !visible[y * width + x + 1])
                    AddEdge(edges, outgoing, x + 1, y, x + 1, y + 1, 1);
                if (y == height - 1 || !visible[(y + 1) * width + x])
                    AddEdge(edges, outgoing, x + 1, y + 1, x, y + 1, 2);
                if (x == 0 || !visible[y * width + x - 1])
                    AddEdge(edges, outgoing, x, y + 1, x, y, 3);
            }

            double scaleX = shapeWidth / width, scaleY = shapeHeight / height;
            double pixelMm = Math.Max(scaleX, scaleY);
            double tolerance = Math.Max(simplifyToleranceMm,
                pixelMm * (0.75 + Math.Max(0, Math.Min(100, smoothing)) / 100.0
                          + (100 - Math.Max(0, Math.Min(100, detail))) / 200.0));
            bool[] used = new bool[edges.Count];
            dynamic range = app.CreateShapeRange();
            int outerCount = 0, holeCount = 0;
            try
            {
                for (int i = 0; i < edges.Count; i++)
                {
                    if (used[i]) continue;
                    List<GridPoint> loop = FollowLoop(i, edges, outgoing, used);
                    if (loop.Count < 3) continue;
                    if (SignedArea(loop) <= 0)
                    {
                        holeCount++;
                        continue;
                    }

                    List<GridPoint> points = SimplifyClosed(loop, scaleX, scaleY, tolerance);
                    if (points.Count < 3) points = loop;
                    dynamic curve = doc.CreateCurve();
                    dynamic path = curve.CreateSubPath(
                        left + points[0].X * scaleX, bottom + points[0].Y * scaleY);
                    for (int j = 1; j < points.Count; j++)
                        path.AppendLineSegment(
                            left + points[j].X * scaleX, bottom + points[j].Y * scaleY);
                    path.Closed = true;
                    dynamic shape = doc.ActiveLayer.CreateCurve(curve);
                    range.Add(shape);
                    outerCount++;
                }
                if (outerCount == 0)
                    throw new InvalidOperationException(
                        "Не удалось найти внешний контур в альфа-канале.");
                Log.Info($"Alpha vectorization: {width}x{height} px, {outerCount} outer paths, " +
                         $"{holeCount} holes ignored, tolerance {tolerance:0.###} mm.");
                return range;
            }
            catch
            {
                try { range.Delete(); } catch { }
                throw;
            }
        }

        private static void BridgeSinglePixelGaps(bool[] visible, int width, int height)
        {
            var source = (bool[])visible.Clone();
            for (int y = 1; y < height - 1; y++)
            for (int x = 1; x < width - 1; x++)
            {
                int index = y * width + x;
                if (source[index]) continue;
                if ((source[index - 1] && source[index + 1]) ||
                    (source[index - width] && source[index + width]))
                    visible[index] = true;
            }
        }

        private static long Key(GridPoint point)
        {
            return ((long)point.Y << 32) | (uint)point.X;
        }

        private static void AddEdge(List<Edge> edges, Dictionary<long, List<int>> outgoing,
            int x0, int y0, int x1, int y1, int direction)
        {
            var edge = new Edge(x0, y0, x1, y1, direction);
            long key = Key(edge.Start);
            if (!outgoing.TryGetValue(key, out List<int> indexes))
            {
                indexes = new List<int>(2);
                outgoing.Add(key, indexes);
            }
            indexes.Add(edges.Count);
            edges.Add(edge);
        }

        private static List<GridPoint> FollowLoop(int first, List<Edge> edges,
            Dictionary<long, List<int>> outgoing, bool[] used)
        {
            var loop = new List<GridPoint>();
            GridPoint start = edges[first].Start;
            int current = first;
            while (true)
            {
                if (used[current])
                    throw new InvalidOperationException("Незамкнутая граница альфа-маски.");
                Edge edge = edges[current];
                used[current] = true;
                loop.Add(edge.Start);
                if (edge.End.X == start.X && edge.End.Y == start.Y)
                    return loop;
                if (!outgoing.TryGetValue(Key(edge.End), out List<int> candidates))
                    throw new InvalidOperationException("Незамкнутая граница альфа-маски.");

                int next = -1, bestRank = int.MaxValue;
                foreach (int candidate in candidates)
                {
                    if (used[candidate]) continue;
                    int turn = (edges[candidate].Direction - edge.Direction + 4) % 4;
                    // At diagonal pixel contacts, the left turn keeps the loops separate.
                    int rank = turn == 1 ? 0 : turn == 0 ? 1 : turn == 3 ? 2 : 3;
                    if (rank >= bestRank) continue;
                    bestRank = rank;
                    next = candidate;
                }
                if (next < 0)
                    throw new InvalidOperationException("Незамкнутая граница альфа-маски.");
                current = next;
            }
        }

        private static double SignedArea(List<GridPoint> points)
        {
            double twiceArea = 0;
            for (int i = 0; i < points.Count; i++)
            {
                GridPoint a = points[i], b = points[(i + 1) % points.Count];
                twiceArea += (double)a.X * b.Y - (double)b.X * a.Y;
            }
            return twiceArea;
        }

        private static List<GridPoint> SimplifyClosed(List<GridPoint> points,
            double scaleX, double scaleY, double tolerance)
        {
            if (points.Count <= 4) return points;
            int farthest = 1;
            double maxDistance = -1;
            for (int i = 1; i < points.Count; i++)
            {
                double dx = (points[i].X - points[0].X) * scaleX;
                double dy = (points[i].Y - points[0].Y) * scaleY;
                double distance = dx * dx + dy * dy;
                if (distance <= maxDistance) continue;
                maxDistance = distance;
                farthest = i;
            }
            var first = points.GetRange(0, farthest + 1);
            var second = points.GetRange(farthest, points.Count - farthest);
            second.Add(points[0]);
            List<GridPoint> a = SimplifyOpen(first, scaleX, scaleY, tolerance);
            List<GridPoint> b = SimplifyOpen(second, scaleX, scaleY, tolerance);
            var result = new List<GridPoint>(a);
            for (int i = 1; i < b.Count - 1; i++)
                result.Add(b[i]);
            return result;
        }

        private static List<GridPoint> SimplifyOpen(List<GridPoint> points,
            double scaleX, double scaleY, double tolerance)
        {
            var keep = new bool[points.Count];
            keep[0] = keep[points.Count - 1] = true;
            var pending = new Stack<Tuple<int, int>>();
            pending.Push(Tuple.Create(0, points.Count - 1));
            double limit = tolerance * tolerance;
            while (pending.Count > 0)
            {
                Tuple<int, int> segment = pending.Pop();
                int start = segment.Item1, end = segment.Item2;
                int farthest = -1;
                double maximum = limit;
                for (int i = start + 1; i < end; i++)
                {
                    double distance = SegmentDistanceSquared(
                        points[i], points[start], points[end], scaleX, scaleY);
                    if (distance <= maximum) continue;
                    maximum = distance;
                    farthest = i;
                }
                if (farthest < 0) continue;
                keep[farthest] = true;
                pending.Push(Tuple.Create(start, farthest));
                pending.Push(Tuple.Create(farthest, end));
            }
            var result = new List<GridPoint>();
            for (int i = 0; i < points.Count; i++)
                if (keep[i]) result.Add(points[i]);
            return result;
        }

        private static double SegmentDistanceSquared(GridPoint point, GridPoint start,
            GridPoint end, double scaleX, double scaleY)
        {
            double ax = start.X * scaleX, ay = start.Y * scaleY;
            double bx = end.X * scaleX, by = end.Y * scaleY;
            double px = point.X * scaleX, py = point.Y * scaleY;
            double dx = bx - ax, dy = by - ay;
            double lengthSquared = dx * dx + dy * dy;
            if (lengthSquared == 0)
                return (px - ax) * (px - ax) + (py - ay) * (py - ay);
            double t = Math.Max(0, Math.Min(1,
                ((px - ax) * dx + (py - ay) * dy) / lengthSquared));
            double vx = px - ax - t * dx, vy = py - ay - t * dy;
            return vx * vx + vy * vy;
        }
    }
}
