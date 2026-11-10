using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal sealed class PatternBottle
    {
        private readonly double _bottomRadius, _slope;
        internal readonly double Width, Slant, TopDiameter, BottomDiameter, Height;

        internal PatternBottle(double topDiameter, double bottomDiameter, double height)
        {
            if (!Valid(topDiameter) || !Valid(bottomDiameter) || !Valid(height))
                throw new ArgumentException("Диаметры и высота по оси бутылки должны быть от 1 до 1000 мм.");
            TopDiameter = topDiameter; BottomDiameter = bottomDiameter; Height = height;
            _bottomRadius = bottomDiameter / 2;
            double delta = (topDiameter - bottomDiameter) / 2;
            Slant = Math.Sqrt(height * height + delta * delta);
            _slope = delta / Slant;
            Width = Math.PI * Math.Min(topDiameter, bottomDiameter);
        }

        internal double CircumferenceAt(double v) => 2 * Math.PI * (_bottomRadius + _slope * v);

        private double Bearing(double u) => _slope * (u / Width - .5) * 2 * Math.PI;

        internal PatternPoint Map(double u, double v)
        {
            if (_slope == 0) return new PatternPoint(u - Width / 2, v);
            double bearing = Bearing(u), radius = _bottomRadius / _slope + v;
            double halfSine = Math.Sin(bearing / 2);
            return new PatternPoint(radius * Math.Sin(bearing), v - 2 * radius * halfSine * halfSine);
        }

        internal PatternPoint Project(PatternPoint point)
        {
            if (_slope == 0) return new PatternPoint(point.X + Width / 2, point.Y);
            double apex = _bottomRadius / _slope, sign = Math.Sign(_slope);
            double bearing = Math.Atan2(point.X * sign, (point.Y + apex) * sign);
            double distance = Math.Sqrt(point.X * point.X + (point.Y + apex) * (point.Y + apex));
            // Difference of radii in a form that remains stable for a nearly cylindrical bottle.
            double v = sign * (point.X * point.X + point.Y * (point.Y + 2 * apex)) / (distance + Math.Abs(apex));
            return new PatternPoint(Width * (.5 + bearing / (_slope * 2 * Math.PI)), v);
        }

        internal double RotationAt(double u) => -Bearing(u) * 180 / Math.PI;

        internal double SeamReach(double reach, double v)
        {
            if (_slope == 0) return reach;
            double radius = Math.Abs(_bottomRadius / _slope + v);
            if (reach >= radius) return Width;
            return Math.Asin(reach / radius) * Width / (2 * Math.PI * Math.Abs(_slope));
        }

        internal double Wrap(double u)
        {
            double remainder = u % Width;
            return remainder < 0 ? remainder + Width : remainder;
        }

        internal PatternPoint Start => Map(0, 0);

        internal IEnumerable<Edge> Outline()
        {
            foreach (var segment in Arc(0, Width, 0)) yield return segment;
            yield return new Edge(Map(Width, Slant));
            foreach (var segment in Arc(Width, 0, Slant)) yield return segment;
        }

        private IEnumerable<Edge> Arc(double start, double end, double v)
        {
            if (_slope == 0)
            {
                yield return new Edge(Map(end, v));
                yield break;
            }

            int count = Math.Max(1, (int)Math.Ceiling(Math.Abs(Bearing(end) - Bearing(start)) / (Math.PI / 4)));
            double radius = _bottomRadius / _slope + v;
            double step = (end - start) / count;
            for (int i = 0; i < count; i++)
            {
                double u1 = start + i * step, u2 = start + (i + 1) * step;
                var a = Map(u1, v); var b = Map(u2, v);
                double t1 = Bearing(u1), t2 = Bearing(u2);
                double control = 4d / 3 * Math.Tan((t2 - t1) / 4) * radius;
                yield return new Edge(b, new PatternPoint(a.X + control * Math.Cos(t1), a.Y - control * Math.Sin(t1)),
                    new PatternPoint(b.X - control * Math.Cos(t2), b.Y + control * Math.Sin(t2)));
            }
        }

        private static bool Valid(double value) => value >= 1 && value <= 1000;

        internal readonly struct Edge
        {
            internal readonly PatternPoint End, First, Second;
            internal readonly bool Curved;

            internal Edge(PatternPoint end)
            {
                End = end; First = Second = default; Curved = false;
            }

            internal Edge(PatternPoint end, PatternPoint first, PatternPoint second)
            {
                End = end; First = first; Second = second; Curved = true;
            }
        }
    }
}
