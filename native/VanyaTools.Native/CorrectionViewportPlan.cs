using System;
using System.Windows;

namespace VanyaTools.Native
{
    internal sealed class CorrectionViewportPlan
    {
        internal const int MaximumOutputPixels = 4000000;
        internal Int32Rect Target, Input, Work;
        internal int CutEdges;
        internal static CorrectionViewportPlan Create(int width, int height, Rect scene, Rect visible,
            double zoom, int outputScale, int guardX, int guardY)
        {
            if (width <= 0 || height <= 0 || !Valid(scene) || !Valid(visible) || !(zoom > 0) || Double.IsInfinity(zoom) ||
                outputScale < 1 || outputScale > 4 || guardX < 0 || guardY < 0) return null;
            visible.Intersect(scene); if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) return null;
            Int32Rect target = Integral(visible);
            if ((long)target.Width * target.Height * outputScale * outputScale > MaximumOutputPixels) return null;
            double margin = 48 / zoom;
            var padded = visible; padded.Inflate(margin, margin); padded.Intersect(scene);
            Int32Rect expanded = Integral(padded);
            if ((long)expanded.Width * expanded.Height * outputScale * outputScale <= MaximumOutputPixels) target = expanded;
            var work = new Int32Rect(checked(target.X - guardX), checked(target.Y - guardY),
                checked(target.Width + 2 * guardX), checked(target.Height + 2 * guardY));
            // Guard pixels count toward memory too, especially with smoothing.
            if ((long)work.Width * work.Height * outputScale * outputScale > 8000000) return null;
            int left = Math.Max(0, work.X), top = Math.Max(0, work.Y);
            int right = Math.Min(width, work.X + work.Width), bottom = Math.Min(height, work.Y + work.Height);
            // A transparent outside margin can still receive an outline from
            // neighbouring source pixels, so include guard pixels before clipping.
            if (right <= left || bottom <= top) return null;
            return new CorrectionViewportPlan { Target = target, Work = work,
                Input = new Int32Rect(left, top, right - left, bottom - top),
                CutEdges = (left > 0 ? 1 : 0) | (right < width ? 2 : 0) | (top > 0 ? 4 : 0) | (bottom < height ? 8 : 0) };
        }
        internal static Rect WorldRect(Int32Rect value) => new Rect(value.X, value.Y, value.Width, value.Height);
        private static bool Valid(Rect r) => !r.IsEmpty && r.Width > 0 && r.Height > 0 &&
            !Double.IsNaN(r.X + r.Y + r.Width + r.Height) && !Double.IsInfinity(r.X + r.Y + r.Width + r.Height);
        private static Int32Rect Integral(Rect value)
        {
            int x = checked((int)Math.Floor(value.X)), y = checked((int)Math.Floor(value.Y));
            return new Int32Rect(x, y, checked((int)Math.Ceiling(value.Right) - x), checked((int)Math.Ceiling(value.Bottom) - y));
        }
    }
}
