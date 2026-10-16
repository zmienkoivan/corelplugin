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
            public double Width, Height, Left, Top, Rotation;
            public bool Allow180;
            public IList<Band> Bands;
        }

        private sealed class Profile
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

        internal static double Place(IList<Item> items, double sheetWidth, double gap)
        {
            int columns = Math.Max(1, (int)Math.Ceiling(sheetWidth / 2));
            double step = sheetWidth / columns;
            int gapColumns = (int)Math.Ceiling(gap / step);
            var skyline = new double[columns];
            var profiles = new Dictionary<IList<Band>, Profile[]>();
            double height = 0;
            foreach (Item item in items.OrderByDescending(p => p.Height).ThenByDescending(p => p.Width))
            {
                if (item.Width > sheetWidth + 0.000001)
                    throw new InvalidOperationException("Деталь не помещается на ширину полотна резки.");
                if (!profiles.TryGetValue(item.Bands, out Profile[] pair))
                {
                    pair = new[] { MakeProfile(item, step, false), MakeProfile(item, step, true) };
                    profiles.Add(item.Bands, pair);
                }
                int width = pair[0].Top.Length;
                int bestColumn = 0, bestRotation = 0;
                double bestTop = 0, bestBottom = Double.MaxValue, bestWaste = Double.MaxValue;
                for (int rotation = 0; rotation < (item.Allow180 ? 2 : 1); rotation++)
                {
                    Profile profile = pair[rotation];
                    for (int column = 0; column <= columns - width; column++)
                    {
                        double top = 0;
                        for (int j = 0; j < width; j++)
                            top = Math.Max(top, skyline[column + j] - profile.Top[j]);
                        double bottom = top + item.Height;
                        if (bottom > bestBottom + 0.000001) continue;
                        double waste = 0;
                        for (int j = 0; j < width; j++)
                            waste += top + profile.Top[j] - skyline[column + j];
                        if (bottom < bestBottom - 0.000001 || waste < bestWaste - 0.000001)
                        {
                            bestTop = top; bestBottom = bottom; bestWaste = waste;
                            bestColumn = column; bestRotation = rotation;
                        }
                    }
                }
                item.Left = bestColumn * step;
                item.Top = bestTop;
                item.Rotation = bestRotation * 180;
                height = Math.Max(height, bestBottom);
                Profile chosen = pair[bestRotation];
                for (int j = 0; j < width; j++)
                {
                    double bottom = bestTop + chosen.Bottom[j] + gap;
                    // Expand only occupied geometry; no margin at the roll edge.
                    // Square dilation conservatively enforces the requested gap.
                    for (int k = Math.Max(0, bestColumn + j - gapColumns);
                        k <= Math.Min(columns - 1, bestColumn + j + gapColumns); k++)
                        skyline[k] = Math.Max(skyline[k], bottom);
                }
            }
            return items.Count == 0 ? 1000 : height;
        }

        private static Profile MakeProfile(Item item, double step, bool rotated)
        {
            int width = Math.Max(1, (int)Math.Ceiling(item.Width / step - 0.00000001));
            var result = new Profile { Top = Enumerable.Repeat(Double.PositiveInfinity, width).ToArray(),
                Bottom = Enumerable.Repeat(Double.NegativeInfinity, width).ToArray() };
            foreach (Band band in item.Bands)
            {
                double left = rotated ? item.Width - band.Right : band.Left;
                double right = rotated ? item.Width - band.Left : band.Right;
                double top = rotated ? item.Height - band.Bottom : band.Top;
                double bottom = rotated ? item.Height - band.Top : band.Bottom;
                int first = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(left / step - 0.00000001)));
                int last = Math.Max(0, Math.Min(width - 1, (int)Math.Floor(right / step + 0.00000001)));
                for (int j = first; j <= last; j++)
                {
                    result.Top[j] = Math.Min(result.Top[j], Math.Max(0, top));
                    result.Bottom[j] = Math.Max(result.Bottom[j], Math.Min(item.Height, bottom));
                }
            }
            for (int j = 0; j < width; j++)
                if (Double.IsInfinity(result.Top[j]) || Double.IsInfinity(result.Bottom[j]))
                { result.Top[j] = 0; result.Bottom[j] = item.Height; }
            return result;
        }
    }
}
