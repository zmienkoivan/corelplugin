using System;
using System.Threading;

namespace VanyaTools.Native
{
    internal sealed class ThinAuditOptions
    {
        internal double MinimumMm = .6, SafeMm = .8, DotMm = 1;
        internal int Alpha = 128;
        internal bool IgnoreWhite;
        internal static ThinAuditOptions Preset(bool uv) => new ThinAuditOptions {
            MinimumMm = uv ? .4 : .6, SafeMm = uv ? .6 : .8, DotMm = uv ? .8 : 1 };
        internal void Validate()
        {
            if (!Finite(MinimumMm) || !Finite(SafeMm) || !Finite(DotMm) ||
                MinimumMm < .1 || SafeMm < MinimumMm || SafeMm > 3 || DotMm < MinimumMm || DotMm > 5 || Alpha < 1 || Alpha > 255)
                throw new InvalidOperationException("Проверьте пороги: линии 0,1–3 мм, безопасный не ниже минимального; точки — до 5 мм.");
        }
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }
    internal sealed class ThinAuditResult
    {
        internal byte[] Overlay; // BGRA, transparent outside warnings
        internal int Red, Yellow, Green, SmallDots, Foreground;
        internal double PixelMm;
    }
    // Disk openings detect narrow features without marking every outside edge red.
    // This is a raster estimate, not a certified vector minimum-width measurement.
    internal static class ThinElementAudit
    {
        internal const int MaxPixels = 24000000;
        internal static ThinAuditResult Analyze(byte[] bgra, int width, int height, double widthMm, double heightMm,
            ThinAuditOptions options, CancellationToken cancel)
        {
            options.Validate();
            int count = checked(width * height);
            if (width < 1 || height < 1 || count > MaxPixels || bgra == null || bgra.Length != checked(count * 4) ||
                !(widthMm > 0) || !(heightMm > 0) || Double.IsInfinity(widthMm + heightMm))
                throw new InvalidOperationException("Некорректный размер изображения для аудита.");
            double dx = widthMm / width, dy = heightMm / height;
            if (Math.Max(dx, dy) > options.MinimumMm / 4)
                throw new InvalidOperationException("Разрешения недостаточно для выбранного порога. Выделите меньший фрагмент макета.");
            var mask = new byte[count]; int foreground = 0;
            for (int i = 0; i < count; i++)
            {
                if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested();
                int p = i * 4;
                if (bgra[p + 3] >= options.Alpha && !(options.IgnoreWhite && bgra[p] >= 249 && bgra[p + 1] >= 249 && bgra[p + 2] >= 249))
                { mask[i] = 1; foreground++; }
            }
            var result = new ThinAuditResult { Overlay = new byte[count * 4], PixelMm = Math.Max(dx, dy), Foreground = foreground };
            if (foreground == 0) return result;
            byte[] dots = SmallDots(mask, width, height, dx, dy, options.DotMm, cancel, out int dotCount);
            result.SmallDots = dotCount;
            var inside = new float[count]; var scratch = new float[count];
            Distance(mask, false, width, height, dx, dy, inside, true, cancel);
            // Distances above are between pixel centres. Ink ends at the pixel
            // boundary; otherwise a seven-pixel stroke appears eight pixels wide.
            double halfPixel = Math.Min(dx, dy) / 2;
            for (int i = 0; i < count; i++) { double clearance = Math.Max(0, Math.Sqrt(inside[i]) - halfPixel); inside[i] = (float)(clearance * clearance); }
            var classes = new byte[count];
            MarkCoverage(mask, inside, scratch, classes, 1, options.MinimumMm, width, height, dx, dy, cancel);
            MarkCoverage(mask, inside, scratch, classes, 2, options.SafeMm, width, height, dx, dy, cancel);
            MarkCoverage(mask, inside, scratch, classes, 3, options.SafeMm + .2, width, height, dx, dy, cancel);
            for (int i = 0; i < count; i++)
            {
                if ((i & 65535) == 0) cancel.ThrowIfCancellationRequested();
                if (mask[i] == 0) continue;
                int p = i * 4;
                if (dots[i] != 0 || classes[i] == 0) { result.Overlay[p + 2] = 255; result.Overlay[p + 1] = 35; result.Red++; }
                else if (classes[i] == 1) { result.Overlay[p + 1] = 210; result.Overlay[p + 2] = 255; result.Yellow++; }
                else if (classes[i] == 2) { result.Overlay[p + 1] = 210; result.Overlay[p + 2] = 30; result.Green++; }
                else continue; // Broad, comfortably safe regions stay unpainted.
                result.Overlay[p + 3] = 150;
            }
            return result;
        }
        private static void MarkCoverage(byte[] mask, float[] inside, float[] scratch, byte[] classes, byte level,
            double diameter, int w, int h, double dx, double dy, CancellationToken cancel)
        {
            double radius = diameter / 2;
            var centers = new byte[mask.Length]; bool any = false;
            for (int i = 0; i < centers.Length; i++)
                if (mask[i] != 0 && inside[i] >= radius * radius) { centers[i] = 1; any = true; }
            if (!any) return;
            Distance(centers, true, w, h, dx, dy, scratch, false, cancel);
            double limit = radius * radius + 1e-9;
            for (int i = 0; i < mask.Length; i++) if (mask[i] != 0 && scratch[i] <= limit) classes[i] = level;
        }
        private static byte[] SmallDots(byte[] mask, int w, int h, double dx, double dy, double minimum,
            CancellationToken cancel, out int count)
        {
            var seen = new byte[mask.Length]; var result = new byte[mask.Length]; var queue = new int[mask.Length]; count = 0;
            for (int start = 0; start < mask.Length; start++)
            {
                if ((start & 16383) == 0) cancel.ThrowIfCancellationRequested();
                if (mask[start] == 0 || seen[start] != 0) continue;
                int head = 0, tail = 1, minX = start % w, maxX = minX, minY = start / w, maxY = minY;
                queue[0] = start; seen[start] = 1;
                while (head < tail)
                {
                    if ((head & 65535) == 0) cancel.ThrowIfCancellationRequested();
                    int at = queue[head++], x = at % w, y = at / w;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    for (int oy = -1; oy <= 1; oy++) for (int ox = -1; ox <= 1; ox++)
                    {
                        int nx = x + ox, ny = y + oy;
                        if (nx < 0 || nx >= w || ny < 0 || ny >= h) continue;
                        int next = ny * w + nx;
                        if (mask[next] != 0 && seen[next] == 0) { seen[next] = 1; queue[tail++] = next; }
                    }
                }
                double a = (maxX - minX + 1) * dx, b = (maxY - minY + 1) * dy;
                // A compact isolated component is a dot; a long narrow line is not.
                if (Math.Max(a, b) < minimum && Math.Max(a, b) <= 1.6 * Math.Min(a, b))
                { count++; for (int i = 0; i < tail; i++) result[queue[i]] = 1; }
            }
            return result;
        }
        // Separable squared Euclidean distance in physical mm; one pass per axis.
        private static void Distance(byte[] mask, bool toInk, int w, int h, double dx, double dy,
            float[] output, bool outsideBackground, CancellationToken cancel)
        {
            int length = Math.Max(w, h); var input = new double[length]; var line = new double[length];
            var sites = new int[length]; var cuts = new double[length + 1];
            const double far = 1e12;
            for (int y = 0; y < h; y++)
            {
                if ((y & 31) == 0) cancel.ThrowIfCancellationRequested();
                for (int x = 0; x < w; x++) input[x] = (mask[y * w + x] != 0) == toInk ? 0 : far;
                Transform(input, line, w, dx * dx, sites, cuts);
                for (int x = 0; x < w; x++) output[y * w + x] = (float)(outsideBackground ?
                    Math.Min(line[x], Math.Min((x + 1d) * (x + 1) * dx * dx, (w - x) * (double)(w - x) * dx * dx)) : line[x]);
            }
            for (int x = 0; x < w; x++)
            {
                if ((x & 31) == 0) cancel.ThrowIfCancellationRequested();
                for (int y = 0; y < h; y++) input[y] = output[y * w + x];
                Transform(input, line, h, dy * dy, sites, cuts);
                for (int y = 0; y < h; y++) output[y * w + x] = (float)(outsideBackground ?
                    Math.Min(line[y], Math.Min((y + 1d) * (y + 1) * dy * dy, (h - y) * (double)(h - y) * dy * dy)) : line[y]);
            }
        }
        private static void Transform(double[] f, double[] d, int n, double weight, int[] v, double[] z)
        {
            int k = 0; v[0] = 0; z[0] = Double.NegativeInfinity; z[1] = Double.PositiveInfinity;
            for (int q = 1; q < n; q++)
            {
                double s;
                while (true)
                {
                    int p = v[k]; s = ((f[q] - f[p]) / weight + q * (double)q - p * (double)p) / (2 * (q - p));
                    if (s > z[k] || k == 0) break; k--;
                }
                k++; v[k] = q; z[k] = s; z[k + 1] = Double.PositiveInfinity;
            }
            k = 0;
            for (int q = 0; q < n; q++) { while (z[k + 1] < q) k++; d[q] = weight * (q - v[k]) * (double)(q - v[k]) + f[v[k]]; }
        }
    }
}
