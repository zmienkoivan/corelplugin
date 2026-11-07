using System;
using System.Collections;
using System.Threading;

namespace VanyaTools.Native
{
    internal enum RasterMaskTool { Brush, Rectangle, Ellipse }

    internal readonly struct RasterMaskPoint
    {
        internal readonly double X, Y;
        internal RasterMaskPoint(double x, double y) { X = x; Y = y; }
    }

    internal sealed class RasterMaskShape
    {
        internal readonly RasterMaskTool Tool;
        internal readonly RasterMaskPoint[] Points;
        internal readonly double Radius;

        internal RasterMaskShape(RasterMaskTool tool, RasterMaskPoint[] points, double radius)
        { Tool = tool; Points = points; Radius = radius; }
    }

    // All coordinates are measured in mm from the original image's top-left.
    // A worker can rasterize the same snapshot at any DPI or viewport origin.
    internal sealed class RasterProtectionMask
    {
        private readonly RasterMaskShape[] _shapes;

        internal RasterProtectionMask(RasterMaskShape[] shapes) { _shapes = shapes; }

        internal BitArray Rasterize(int width, int height, double dpiX, double dpiY,
            double originX, double originY, CancellationToken cancellation)
        {
            BitArray mask = null;
            double stepX = 25.4 / dpiX, stepY = 25.4 / dpiY;
            foreach (var shape in _shapes)
            {
                cancellation.ThrowIfCancellationRequested();
                var points = shape.Points;
                if (shape.Tool == RasterMaskTool.Brush)
                {
                    for (int i = 0; i < points.Length; i++)
                        Stroke(points[Math.Max(0, i - 1)], points[i], shape.Radius);
                }
                else
                {
                    double left = Math.Min(points[0].X, points[1].X), right = Math.Max(points[0].X, points[1].X);
                    double top = Math.Min(points[0].Y, points[1].Y), bottom = Math.Max(points[0].Y, points[1].Y);
                    if (shape.Tool == RasterMaskTool.Rectangle) Paint(left, top, right, bottom, null);
                    else
                    {
                        double rx = (right - left) / 2, ry = (bottom - top) / 2;
                        if (rx <= 0 || ry <= 0) continue;
                        Paint(left, top, right, bottom, (x, y) =>
                        {
                            double dx = (x - left) / rx - 1, dy = (y - top) / ry - 1;
                            return dx * dx + dy * dy <= 1;
                        });
                    }
                }
            }
            return mask;

            void Stroke(RasterMaskPoint from, RasterMaskPoint to, double radius)
            {
                double dx = to.X - from.X, dy = to.Y - from.Y, lengthSquared = dx * dx + dy * dy;
                // Split long drags into short spans so a diagonal doesn't scan
                // the entire rectangle between two far-apart mouse events.
                int sections = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(lengthSquared) /
                    Math.Max(radius * 4, Math.Min(stepX, stepY) * 16)));
                double radiusSquared = radius * radius;
                for (int i = 0; i < sections; i++)
                {
                    double x0 = from.X + dx * i / sections, x1 = from.X + dx * (i + 1) / sections;
                    double y0 = from.Y + dy * i / sections, y1 = from.Y + dy * (i + 1) / sections;
                    Paint(Math.Min(x0, x1) - radius, Math.Min(y0, y1) - radius,
                        Math.Max(x0, x1) + radius, Math.Max(y0, y1) + radius, (x, y) =>
                        {
                            double t = lengthSquared == 0 ? 0 :
                                Math.Max(0, Math.Min(1, ((x - from.X) * dx + (y - from.Y) * dy) / lengthSquared));
                            double xx = x - from.X - t * dx, yy = y - from.Y - t * dy;
                            return xx * xx + yy * yy <= radiusSquared;
                        });
                }
            }

            void Paint(double left, double top, double right, double bottom, Func<double, double, bool> inside)
            {
                int x0 = (int)Math.Max(0, Math.Min(width, Math.Ceiling((left - originX) / stepX - .5)));
                int y0 = (int)Math.Max(0, Math.Min(height, Math.Ceiling((top - originY) / stepY - .5)));
                int x1 = (int)Math.Max(-1, Math.Min(width - 1, Math.Floor((right - originX) / stepX - .5)));
                int y1 = (int)Math.Max(-1, Math.Min(height - 1, Math.Floor((bottom - originY) / stepY - .5)));
                if (x1 < x0 || y1 < y0) return;
                for (int y = y0; y <= y1; y++)
                {
                    if ((y & 31) == 0) cancellation.ThrowIfCancellationRequested();
                    double yy = originY + (y + .5) * stepY;
                    for (int x = x0; x <= x1; x++)
                        if (inside == null || inside(originX + (x + .5) * stepX, yy))
                        {
                            if (mask == null) mask = new BitArray(checked(width * height));
                            mask[y * width + x] = true;
                        }
                }
            }
        }
    }
}
