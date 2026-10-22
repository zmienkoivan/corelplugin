using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    // All calculations are managed. Corel supplies geometry once per size and
    // part; searching candidate placements never calls its native object model.
    internal static class SportContourNesting
    {
        internal struct Point
        {
            public double X, Y;
            public Point(double x, double y) { X = x; Y = y; }
        }

        internal struct Band
        {
            public double Left, Right, Top, Bottom;
        }

        internal sealed class Item
        {
            public double Width, Height;
            public bool Allow180;
            public IList<Band> Bands;
        }

        internal sealed class Profile
        {
            public double[] Top, Bottom;
        }

        internal static void AddLine(ICollection<Band> bands, Point a, Point b)
        {
            int count = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(b.X - a.X),
                Math.Abs(b.Y - a.Y)))); // hulls no larger than 1 mm
            for (int i = 0; i < count; i++)
            {
                double t0 = (double)i / count, t1 = (double)(i + 1) / count;
                AddBounds(bands, new[] { new Point(a.X + (b.X - a.X) * t0,
                    a.Y + (b.Y - a.Y) * t0), new Point(a.X + (b.X - a.X) * t1,
                    a.Y + (b.Y - a.Y) * t1) });
            }
        }

        internal static void AddCubic(ICollection<Band> bands, Point a, Point b,
            Point c, Point d, int depth = 0)
        {
            Point[] points = { a, b, c, d };
            double width = points.Max(p => p.X) - points.Min(p => p.X);
            double height = points.Max(p => p.Y) - points.Min(p => p.Y);
            if ((width <= 1 && height <= 1) || depth >= 20)
            {
                // A Bezier curve is inside its control-point convex hull.
                // Keep that hull's box instead of an underestimating chord.
                AddBounds(bands, points);
                return;
            }
            Point ab = Mid(a, b), bc = Mid(b, c), cd = Mid(c, d);
            Point abc = Mid(ab, bc), bcd = Mid(bc, cd), center = Mid(abc, bcd);
            AddCubic(bands, a, ab, abc, center, depth + 1);
            AddCubic(bands, center, bcd, cd, d, depth + 1);
        }

        private static Point Mid(Point a, Point b) =>
            new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);

        private static void AddBounds(ICollection<Band> bands, Point[] points)
        {
            bands.Add(new Band { Left = points.Min(p => p.X), Right = points.Max(p => p.X),
                Top = points.Min(p => p.Y), Bottom = points.Max(p => p.Y) });
        }

        internal static Profile MakeProfile(Item item, double step, bool rotated)
            => MakeProfile(item, step, rotated ? 180 : 0);

        internal static Profile MakeProfile(Item item, double step, int angle)
        {
            if (angle != 0 && angle != 90 && angle != 180)
                throw new ArgumentOutOfRangeException(nameof(angle));
            double rotatedWidth = angle == 90 ? item.Height : item.Width;
            double rotatedHeight = angle == 90 ? item.Width : item.Height;
            int width = Math.Max(1, (int)Math.Ceiling(rotatedWidth / step - 0.00000001));
            var result = new Profile { Top = Enumerable.Repeat(Double.PositiveInfinity, width).ToArray(),
                Bottom = Enumerable.Repeat(Double.NegativeInfinity, width).ToArray() };
            foreach (Band band in item.Bands)
            {
                SportNestingCancellation.Check();
                // Corel's positive 90 degrees is counterclockwise. These
                // bands use a downward Y axis, measured from the top left.
                double left = angle == 90 ? band.Top : angle == 180 ? item.Width - band.Right : band.Left;
                double right = angle == 90 ? band.Bottom : angle == 180 ? item.Width - band.Left : band.Right;
                double top = angle == 90 ? item.Width - band.Right : angle == 180 ? item.Height - band.Bottom : band.Top;
                double bottom = angle == 90 ? item.Width - band.Left : angle == 180 ? item.Height - band.Top : band.Bottom;
                int first = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(left / step - 0.00000001)));
                int last = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(right / step + 0.00000001)));
                for (int j = first; j <= last; j++)
                {
                    result.Top[j] = Math.Min(result.Top[j], Math.Max(0, top));
                    result.Bottom[j] = Math.Max(result.Bottom[j], Math.Min(rotatedHeight, bottom));
                }
            }
            for (int j = 0; j < width; j++)
                if (Double.IsInfinity(result.Top[j]) || Double.IsInfinity(result.Bottom[j]))
                { result.Top[j] = 0; result.Bottom[j] = rotatedHeight; }
            return result;
        }
    }
}
