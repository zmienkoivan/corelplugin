using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal sealed class BagNotch
    {
        internal int Edge, Index;
        internal BagPoint SeamPoint, Tip;
        internal double Position, Width;
    }

    internal static class FrameBagNotches
    {
        // Matching marks are measured along the developed SEAM, never the
        // longer allowance. Two near the start, one near the end of each wall.
        internal static void Apply(FrameBagPlan plan)
        {
            if (!plan.Setup.Notches || plan.Setup.Allowance < .4) return;
            double depth = plan.Setup.NotchDepth;
            if (Double.IsNaN(depth) || Double.IsInfinity(depth) || depth < .2 || depth > 5)
                throw new ArgumentException("Глубина надсечки: от 0,2 до 5 мм.");
            depth = Math.Min(depth, plan.Setup.Allowance / 2);
            var marks = plan.Parts.ToDictionary(part => part, _ => new List<BagNotch>());
            for (int edge = 0; edge < plan.Breaks.Length; edge++)
            {
                var path = new List<BagPoint>();
                int index = plan.Breaks[edge], last = plan.Breaks[(edge + 1) % plan.Breaks.Length];
                while (true)
                {
                    path.Add(plan.SideSeam[index]);
                    if (index == last) break;
                    index = (index + 1) % plan.SideSeam.Length;
                }
                double length = Length(path.ToArray(), false);
                if (length < 12) continue;
                double offset = Math.Min(12, length / 5), spacing = Math.Min(3, length / 15);
                double width = Math.Min(2, length / 20);
                var distances = new[] { offset - spacing / 2, offset + spacing / 2, length - offset };
                var wall = plan.Parts.Single(part => part.Edge == edge);
                int half = wall.Seam.Length / 2;
                var upper = wall.Seam.Take(half).ToArray();
                var lower = wall.Seam.Skip(half).Reverse().ToArray();
                for (int n = 0; n < distances.Length; n++)
                {
                    var point = At(path.ToArray(), distances[n], false);
                    for (int side = 0; side < 2; side++)
                    {
                        var mirrored = new BagPoint(side == 0 ? point.X : -point.X, point.Y);
                        var wallMark = Prepare(wall, At(side == 0 ? upper : lower, distances[n], false), edge, n, width, depth);
                        if (wallMark == null || Conflicts(wall, marks[wall], wallMark)) continue;
                        foreach (var piece in plan.Parts.Where(part => part.Side == side))
                        {
                            var sideMark = Prepare(piece, mirrored, edge, n, width, depth);
                            if (sideMark == null || Conflicts(piece, marks[piece], sideMark)) continue;
                            marks[piece].Add(sideMark); marks[wall].Add(wallMark); break;
                        }
                    }
                }
            }
            foreach (var part in plan.Parts)
            {
                part.Notches = marks[part].ToArray();
                if (part.Notches.Length != 0) part.Cut = Insert(part.Cut, part.Notches);
            }
        }

        private static BagNotch Prepare(FrameBagPart part, BagPoint point, int edge, int index, double width, double requestedDepth)
        {
            var seam = part.Seam;
            for (int i = 0; i < seam.Length; i++)
            {
                var delta = BagPoint.Sub(seam[(i + 1) % seam.Length], seam[i]);
                double squared = delta.X * delta.X + delta.Y * delta.Y;
                if (squared < 1e-12) continue;
                var relative = BagPoint.Sub(point, seam[i]);
                double along = (relative.X * delta.X + relative.Y * delta.Y) / squared;
                if (along < -1e-7 || along > 1 + 1e-7 ||
                    BagPoint.Sub(point, BagPoint.Add(seam[i], BagPoint.Mul(delta, along))).Length > .0001) continue;
                double sign = Math.Sign(FrameBagGeometry.Area(seam));
                var normal = BagPoint.Mul(new BagPoint(delta.Y, -delta.X), sign / Math.Sqrt(squared));
                double nearest = Double.MaxValue, distance = 0, position = 0;
                for (int k = 0; k < part.Cut.Length; k++)
                {
                    var a = part.Cut[k]; var segment = BagPoint.Sub(part.Cut[(k + 1) % part.Cut.Length], a);
                    double cross = BagPoint.Cross(normal, segment);
                    if (Math.Abs(cross) > 1e-10)
                    {
                        var r = BagPoint.Sub(a, point);
                        double t = BagPoint.Cross(r, segment) / cross, u = BagPoint.Cross(r, normal) / cross;
                        if (t > .0001 && t < nearest && u >= -1e-8 && u <= 1 + 1e-8)
                        { nearest = t; position = distance + Math.Max(0, Math.Min(1, u)) * segment.Length; }
                    }
                    distance += segment.Length;
                }
                if (nearest == Double.MaxValue) return null;
                double depth = Math.Min(requestedDepth, nearest / 2);
                if (depth < .2) return null;
                var tip = BagPoint.Add(point, BagPoint.Mul(normal, nearest - depth));
                if (!FrameBagClosures.Inside(part.Cut, tip) || FrameBagClosures.Inside(seam, tip)) return null;
                var before = At(part.Cut, position - width / 2, true);
                var after = At(part.Cut, position + width / 2, true);
                // Both V legs must stay outside the stitch line, including
                // very acute corners and a zipper splitting the side panel.
                foreach (var end in new[] { before, after })
                    for (int sample = 0; sample <= 4; sample++)
                    {
                        var p = BagPoint.Add(tip, BagPoint.Mul(BagPoint.Sub(end, tip), sample / 4d));
                        if (!FrameBagClosures.Inside(part.Cut, p) || FrameBagClosures.Inside(seam, p)) return null;
                    }
                return new BagNotch { Edge = edge, Index = index, SeamPoint = point, Tip = tip, Position = position, Width = width };
            }
            return null;
        }

        private static bool Conflicts(FrameBagPart part, List<BagNotch> existing, BagNotch next)
        {
            double perimeter = Length(part.Cut, true);
            return existing.Any(mark =>
            {
                double gap = Math.Abs(mark.Position - next.Position);
                return Math.Min(gap, perimeter - gap) < (mark.Width + next.Width) / 2 + .1;
            });
        }

        private static BagPoint[] Insert(BagPoint[] cut, BagNotch[] notches)
        {
            double perimeter = Length(cut, true);
            var sorted = notches.OrderBy(mark => mark.Position).ToArray();
            double origin = 0, largest = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                double start = sorted[i].Position, end = sorted[(i + 1) % sorted.Length].Position;
                if (i == sorted.Length - 1) end += perimeter;
                if (end - start <= largest) continue;
                largest = end - start; origin = Mod((start + end) / 2, perimeter);
            }
            var ranges = sorted.Select(mark => new { Mark = mark, At = Mod(mark.Position - origin, perimeter) }).OrderBy(mark => mark.At).ToArray();
            var nodes = new List<Tuple<double, BagPoint>>();
            double distance = 0;
            for (int i = 0; i < cut.Length; i++)
            {
                double at = Mod(distance - origin, perimeter);
                if (!ranges.Any(range => at >= range.At - range.Mark.Width / 2 - 1e-8 && at <= range.At + range.Mark.Width / 2 + 1e-8))
                    nodes.Add(Tuple.Create(at, cut[i]));
                distance += BagPoint.Sub(cut[(i + 1) % cut.Length], cut[i]).Length;
            }
            foreach (var range in ranges)
            {
                double half = range.Mark.Width / 2;
                nodes.Add(Tuple.Create(range.At - half, At(cut, range.Mark.Position - half, true)));
                nodes.Add(Tuple.Create(range.At, range.Mark.Tip));
                nodes.Add(Tuple.Create(range.At + half, At(cut, range.Mark.Position + half, true)));
            }
            return nodes.OrderBy(node => node.Item1).Select(node => node.Item2).ToArray();
        }
        private static double Mod(double value, double length) => (value % length + length) % length;
        private static double Length(BagPoint[] path, bool closed) => Enumerable.Range(0, closed ? path.Length : path.Length - 1)
            .Sum(i => BagPoint.Sub(path[(i + 1) % path.Length], path[i]).Length);
        private static BagPoint At(BagPoint[] path, double distance, bool closed)
        {
            if (closed) distance = Mod(distance, Length(path, true));
            for (int i = 0; i < (closed ? path.Length : path.Length - 1); i++)
            {
                var delta = BagPoint.Sub(path[(i + 1) % path.Length], path[i]);
                double length = delta.Length;
                if (distance <= length && length > 1e-10) return BagPoint.Add(path[i], BagPoint.Mul(delta, Math.Max(0, distance) / length));
                distance -= length;
            }
            return path[path.Length - 1];
        }
    }
}
