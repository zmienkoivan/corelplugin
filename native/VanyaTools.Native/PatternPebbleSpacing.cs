using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal static class PatternPebbleSpacing
    {
        private const double MaximumGrowth = 1.4;

        internal static void Balance(List<PatternPlacement> items, IReadOnlyList<PatternFootprint> motifs, double gap)
        {
            var shapes = items.Select(item => new Envelope(item, motifs[item.Motif])).ToArray();
            for (int i = 0; i < shapes.Length; i++)
                for (int j = i + 1; j < shapes.Length; j++)
                    LimitPair(shapes[i], shapes[j], gap);

            double typicalArea = shapes.Select(shape => shape.GrownArea).OrderBy(area => area).ElementAt(shapes.Length / 2);
            var bySpace = shapes.OrderByDescending(shape => shape.Neighbors.Sum(link =>
                Math.Max(0, typicalArea - link.Other.GrownArea))).ToArray();
            foreach (var shape in bySpace)
            {
                double limit = shape.Ceiling;
                foreach (var link in shape.Neighbors)
                    limit = Math.Min(limit, (link.Available - link.OtherSupport * link.Other.Growth) / link.Support);
                shape.Growth = Math.Max(shape.Growth, limit);
            }

            foreach (var shape in shapes)
            {
                if (shape.Growth <= 1) continue;
                shape.Item.Size *= shape.Growth;
                shape.Item.Center = new PatternPoint(shape.Center.X - shape.Offset.X * shape.Growth,
                    shape.Center.Y - shape.Offset.Y * shape.Growth);
            }
        }

        private static void LimitPair(Envelope a, Envelope b, double gap)
        {
            double dx = b.Center.X - a.Center.X, dy = b.Center.Y - a.Center.Y;
            if (Math.Abs(dx) >= a.ReachX + b.ReachX + gap || Math.Abs(dy) >= a.ReachY + b.ReachY + gap) return;

            double direction = Math.Atan2(dy, dx);
            double best = double.NegativeInfinity, available = 0, supportA = 0, supportB = 0;
            for (int offset = -4; offset <= 4; offset++)
            {
                double angle = direction + offset * Math.PI / 9;
                double nx = Math.Cos(angle), ny = Math.Sin(angle);
                double span = nx * dx + ny * dy - gap - 1e-8;
                if (span <= 0) continue;
                double first = Support(a.Points, nx, ny), second = Support(b.Points, -nx, -ny);
                double factor = span / (first + second);
                if (factor >= MaximumGrowth) return;
                if (factor <= best) continue;
                best = factor; available = span; supportA = first; supportB = second;
            }

            if (best < 1)
            {
                // The original cell fit already respects the gap. Keep it when no tested axis proves growth is safe.
                a.Ceiling = a.Growth = b.Ceiling = b.Growth = 1;
                return;
            }
            a.Growth = Math.Min(a.Growth, best);
            b.Growth = Math.Min(b.Growth, best);
            a.Neighbors.Add((b, supportA, supportB, available));
            b.Neighbors.Add((a, supportB, supportA, available));
        }

        private static double Support(PatternPoint[] points, double nx, double ny)
        {
            double support = 0;
            foreach (var point in points)
                support = Math.Max(support, point.X * nx + point.Y * ny);
            return support;
        }

        private sealed class Envelope
        {
            internal readonly PatternPlacement Item;
            internal readonly PatternPoint Center, Offset;
            internal readonly PatternPoint[] Points;
            internal readonly double ReachX, ReachY;
            private readonly double _area;
            internal double Ceiling = MaximumGrowth, Growth = MaximumGrowth;
            internal double GrownArea => _area * Growth * Growth;
            internal readonly List<(Envelope Other, double Support, double OtherSupport, double Available)> Neighbors =
                new List<(Envelope, double, double, double)>();

            internal Envelope(PatternPlacement item, PatternFootprint motif)
            {
                Item = item;
                double scale = PatternTransform.Fit(motif.Size, item).Scale;
                double radians = item.Angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
                Offset = Rotate(motif.Center.X, motif.Center.Y);
                Center = new PatternPoint(item.Center.X + Offset.X, item.Center.Y + Offset.Y);
                Points = motif.Hull.Select(point => Rotate(point.X - motif.Center.X, point.Y - motif.Center.Y)).ToArray();
                ReachX = Points.Max(point => Math.Abs(point.X)) * MaximumGrowth;
                ReachY = Points.Max(point => Math.Abs(point.Y)) * MaximumGrowth;
                double twiceArea = 0;
                for (int i = 0; i < Points.Length; i++)
                {
                    var a = Points[i]; var b = Points[(i + 1) % Points.Length];
                    twiceArea += a.X * b.Y - a.Y * b.X;
                }
                _area = Math.Abs(twiceArea) / 2;

                PatternPoint Rotate(double x, double y) =>
                    new PatternPoint(scale * (x * cos - y * sin), scale * (x * sin + y * cos));
            }
        }
    }
}
