using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class AcrylicTrayPlacement
    {
        public AcrylicTrayPart Part;
        public int Number, Rotation;
        public Matrix Transform;
        public Geometry Geometry;
        public Rect Bounds { get { return Geometry.Bounds; } }
    }

    internal sealed class AcrylicTrayCutLayout
    {
        public const double Gap = 3, Margin = 3;
        private const double PackingAllowance = 0.01;
        public readonly List<AcrylicTrayPlacement> Parts = new List<AcrylicTrayPlacement>();
        public double Width, Height;

        public static AcrylicTrayCutLayout Build(AcrylicTrayPattern pattern)
        {
            var result = new AcrylicTrayCutLayout();
            var shifts = new Dictionary<string, double>();
            double y = Margin, x = Margin, rowHeight = 0;
            AcrylicTrayPlacement previous = null;
            // Complementary supporting cheeks close the two ends of the band.
            // Upright parallel dividers share the same diagonal contour between
            // them, so their bounding boxes can overlap without wasting acrylic.
            var top = new[] { pattern.Parts.First(p => p.Id == "side-left") }
                .Concat(pattern.Parts.Where(p => p.Kind == "long"))
                .Concat(new[] { pattern.Parts.First(p => p.Id == "side-right") });
            foreach (var part in top)
            {
                int rotation = part.Id == "side-right" ? 270 : 90;
                var next = Orient(part, pattern.Parts.IndexOf(part) + 1, rotation);
                if (previous != null)
                {
                    string key = previous.Part.Path + ":" + previous.Rotation + "|" + part.Path + ":" + rotation;
                    if (!shifts.TryGetValue(key, out double shift))
                    {
                        var left = Orient(previous.Part, previous.Number, previous.Rotation);
                        shift = RequiredShift(left.Geometry, next.Geometry);
                        shifts.Add(key, shift);
                    }
                    x += shift;
                }
                Place(next, x, y); result.Parts.Add(next); previous = next;
                rowHeight = Math.Max(rowHeight, next.Bounds.Height);
            }
            y += rowHeight + Gap;
            // The horizontal strips form one tight stack, followed by the floor.
            foreach (var part in pattern.Parts.Where(p => p.Kind == "cross")
                .Concat(pattern.Parts.Where(p => p.Kind == "floor")))
            {
                var next = Orient(part, pattern.Parts.IndexOf(part) + 1, 0);
                Place(next, Margin, y); result.Parts.Add(next); y += next.Bounds.Height + Gap;
            }
            result.Width = result.Parts.Max(p => p.Bounds.Right) + Margin;
            result.Height = result.Parts.Max(p => p.Bounds.Bottom) + Margin;
            return result;
        }

        private static AcrylicTrayPlacement Orient(AcrylicTrayPart part, int number, int rotation)
        {
            var matrix = Matrix.Identity; matrix.Rotate(rotation);
            var g = Geometry.Parse(part.Path).Clone(); g.Transform = new MatrixTransform(matrix);
            Rect bounds = g.Bounds; matrix.Translate(-bounds.X, -bounds.Y);
            g.Transform = new MatrixTransform(matrix); g.Freeze();
            return new AcrylicTrayPlacement { Part = part, Number = number, Rotation = rotation, Transform = matrix, Geometry = g };
        }
        private static void Place(AcrylicTrayPlacement part, double x, double y)
        {
            var matrix = part.Transform; matrix.Translate(x, y); part.Transform = matrix;
            var geometry = Geometry.Parse(part.Part.Path).Clone(); geometry.Transform = new MatrixTransform(matrix);
            geometry.Freeze(); part.Geometry = geometry;
        }

        private static double RequiredShift(Geometry left, Geometry right)
        {
            // Grow the previous cut by 3 mm in every direction. The extra
            // 0.01 mm covers stroke flattening; it never reduces the clearance.
            var grown = new GeometryGroup { FillRule = FillRule.Nonzero };
            grown.Children.Add(left);
            grown.Children.Add(left.GetWidenedPathGeometry(new Pen(Brushes.Black, 2 * (Gap + PackingAllowance)),
                0.002, ToleranceType.Absolute));
            var a = Segments(grown); var b = Segments(right);
            double minY = Math.Max(grown.Bounds.Top, right.Bounds.Top), maxY = Math.Min(grown.Bounds.Bottom, right.Bounds.Bottom);
            var levels = a.Concat(b).SelectMany(e => new[] { e.Item1.Y, e.Item2.Y })
                .Where(v => v >= minY && v <= maxY).Concat(new[] { minY, maxY }).Distinct().OrderBy(v => v);
            double shift = Double.NegativeInfinity;
            foreach (double level in levels)
            {
                double maximum = Extent(a, level, true), minimum = Extent(b, level, false);
                if (!Double.IsInfinity(maximum) && !Double.IsInfinity(minimum)) shift = Math.Max(shift, maximum - minimum);
            }
            return Double.IsInfinity(shift) ? left.Bounds.Right + Gap : Math.Max(0, shift);
        }
        private static Tuple<Point, Point>[] Segments(Geometry geometry)
        {
            var flat = geometry.GetFlattenedPathGeometry(0.002, ToleranceType.Absolute);
            var result = new List<Tuple<Point, Point>>();
            foreach (var figure in flat.Figures)
            {
                var points = new List<Point> { figure.StartPoint };
                foreach (var segment in figure.Segments)
                {
                    if (segment is LineSegment line) points.Add(line.Point);
                    else if (segment is PolyLineSegment poly) points.AddRange(poly.Points);
                }
                if (flat.Transform != null && !flat.Transform.Value.IsIdentity)
                    for (int i = 0; i < points.Count; i++) points[i] = flat.Transform.Transform(points[i]);
                for (int i = 0; i < points.Count; i++) result.Add(Tuple.Create(points[i], points[(i + 1) % points.Count]));
            }
            return result.ToArray();
        }
        private static double Extent(Tuple<Point, Point>[] edges, double y, bool maximum)
        {
            double result = maximum ? Double.NegativeInfinity : Double.PositiveInfinity;
            foreach (var edge in edges)
            {
                var a = edge.Item1; var b = edge.Item2;
                if (y < Math.Min(a.Y, b.Y) - 0.0000001 || y > Math.Max(a.Y, b.Y) + 0.0000001) continue;
                double x;
                if (Math.Abs(b.Y - a.Y) < 0.00000001) x = maximum ? Math.Max(a.X, b.X) : Math.Min(a.X, b.X);
                else x = a.X + (b.X - a.X) * Math.Max(0, Math.Min(1, (y - a.Y) / (b.Y - a.Y)));
                result = maximum ? Math.Max(result, x) : Math.Min(result, x);
            }
            return result;
        }
    }
}
