using System;
using System.Threading;

namespace VanyaTools.Native
{
    internal sealed class DtfPrintPreparationOptions
    {
        internal bool Knockout, Halftone, AlphaOnly = true, AutoDot = true;
        internal byte Red, Green, Blue;
        internal int Tolerance = 10;
        internal double FrequencyLpi = 20, AngleDegrees = 22.5, DotDiameterMm = 1;
        internal double OriginXmm, OriginYmm;
        internal DtfPrintPreparationOptions Copy() => (DtfPrintPreparationOptions)MemberwiseClone();
        internal DtfPrintPreparationOptions Offset(double x, double y)
        { var copy = Copy(); copy.OriginXmm += x; copy.OriginYmm += y; return copy; }
        internal double EffectiveDotMm => AutoDot ? 25.4 / FrequencyLpi * (AlphaOnly ? Math.Sqrt(2) : .75) : DotDiameterMm;
        internal void Validate()
        {
            if (Knockout && (Tolerance < 0 || Tolerance > 255)) throw new ArgumentOutOfRangeException(nameof(Tolerance));
            if (Halftone && (!Finite(FrequencyLpi) || FrequencyLpi < .5 || FrequencyLpi > 200 ||
                !Finite(AngleDegrees) || AngleDegrees < -180 || AngleDegrees > 180 ||
                (!AutoDot && (!Finite(DotDiameterMm) || DotDiameterMm < .01 || DotDiameterMm > 50)) ||
                !Finite(OriginXmm) || !Finite(OriginYmm)))
                throw new ArgumentException("Халфтонинг: растр 0,5–200 LPI, угол −180…180°, диаметр 0,01–50 мм.");
        }
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    // Straight, native colour bytes are read only. Both filters change alpha.
    // RGB/BGRA views and native RGB/CMYK/Gray planes use the same routines.
    internal static class DtfPrintPreparation
    {
        internal static void Knockout(byte[] colors, int stride, int mode, int[] cmykOrder,
            byte[] alpha, int alphaStride, int alphaOffset, int width, int height,
            DtfPrintPreparationOptions options, CancellationToken cancellation)
        {
            if (options == null || !options.Knockout) return;
            options.Validate(); cancellation.ThrowIfCancellationRequested();
            int count = ValidateAlpha(alpha, alphaStride, alphaOffset, width, height);
            int needed = mode == 2 ? 1 : mode == 4 ? 3 : mode == 5 ? 4 : 0;
            if (needed == 0 || stride < needed || colors == null || colors.LongLength < (long)count * stride)
                throw new ArgumentException("Неполные цветовые каналы для нокаута.");
            if (mode == 5 && (cmykOrder == null || cmykOrder.Length != 4))
                throw new InvalidOperationException("Не определён порядок CMYK-каналов для нокаута.");
            if (mode == 5)
            {
                int seen = 0;
                foreach (int channel in cmykOrder)
                {
                    if (channel < 0 || channel > 3 || (seen & (1 << channel)) != 0)
                        throw new InvalidOperationException("Некорректный порядок CMYK-каналов для нокаута.");
                    seen |= 1 << channel;
                }
            }
            int tolerance = options.Tolerance;
            for (int p = 0; p < count; p++)
            {
                if ((p & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                int a = p * alphaStride + alphaOffset; if (alpha[a] == 0) continue;
                int i = p * stride, r, g, b;
                if (mode == 4) { b = colors[i]; g = colors[i + 1]; r = colors[i + 2]; }
                else if (mode == 2) r = g = b = colors[i];
                else
                {
                    int light = 255 - colors[i + cmykOrder[3]];
                    r = ((255 - colors[i + cmykOrder[0]]) * light + 127) / 255;
                    g = ((255 - colors[i + cmykOrder[1]]) * light + 127) / 255;
                    b = ((255 - colors[i + cmykOrder[2]]) * light + 127) / 255;
                }
                if (Math.Abs(r - options.Red) <= tolerance && Math.Abs(g - options.Green) <= tolerance && Math.Abs(b - options.Blue) <= tolerance)
                    alpha[a] = 0; // Includes enclosed areas; no flood fill or trimming.
            }
        }

        internal static void Halftone(byte[] alpha, int alphaStride, int alphaOffset, int width, int height,
            double dpiX, double dpiY, DtfPrintPreparationOptions options, CancellationToken cancellation)
        {
            if (options == null || !options.Halftone) return;
            options.Validate(); cancellation.ThrowIfCancellationRequested();
            ValidateAlpha(alpha, alphaStride, alphaOffset, width, height);
            if (Double.IsNaN(dpiX) || Double.IsInfinity(dpiX) || dpiX <= 0 ||
                Double.IsNaN(dpiY) || Double.IsInfinity(dpiY) || dpiY <= 0) throw new ArgumentOutOfRangeException(nameof(dpiX));
            double pitch = 25.4 / options.FrequencyLpi;
            double angle = options.AngleDegrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
            double dx = 25.4 / dpiX, dy = 25.4 / dpiY;
            double coverage = CircleCoverage(options.EffectiveDotMm / (2 * pitch));
            var radiusSquared = new double[256];
            for (int a = 1; a < 256; a++) radiusSquared[a] = RadiusSquared(coverage * a / 255);
            for (int y = 0; y < height; y++)
            {
                if ((y & 15) == 0) cancellation.ThrowIfCancellationRequested();
                double py = options.OriginYmm + (y + .5) * dy;
                for (int x = 0; x < width; x++)
                {
                    if ((x & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                    int i = (y * width + x) * alphaStride + alphaOffset, a = alpha[i];
                    if (a == 0 || options.AlphaOnly && a == 255) continue;
                    double px = options.OriginXmm + (x + .5) * dx;
                    double u = (px * cos + py * sin) / pitch, v = (-px * sin + py * cos) / pitch;
                    u -= Math.Floor(u + .5); v -= Math.Floor(v + .5);
                    alpha[i] = u * u + v * v <= radiusSquared[a] + 1e-12 ? (byte)255 : (byte)0;
                }
            }
        }

        // Area of a circle intersecting a unit square. This keeps uniform alpha
        // coverage correct even when neighbouring round dots join above ~78%.
        private static double CircleCoverage(double radius)
        {
            if (radius >= Math.Sqrt(.5)) return 1;
            if (radius <= 0) return 0;
            double squared = radius * radius, area = Math.PI * squared;
            if (radius > .5) area -= 4 * (squared * Math.Acos(.5 / radius) - .5 * Math.Sqrt(squared - .25));
            return Math.Max(0, Math.Min(1, area));
        }
        private static double RadiusSquared(double coverage)
        {
            if (coverage <= 0) return 0;
            if (coverage >= 1) return .5;
            if (coverage <= Math.PI / 4) return coverage / Math.PI;
            double low = .5, high = Math.Sqrt(.5);
            for (int n = 0; n < 28; n++) { double middle = (low + high) / 2; if (CircleCoverage(middle) < coverage) low = middle; else high = middle; }
            double radius = (low + high) / 2; return radius * radius;
        }
        private static int ValidateAlpha(byte[] alpha, int stride, int offset, int width, int height)
        {
            if (width <= 0 || height <= 0 || (long)width * height > Int32.MaxValue || stride < 1 || offset < 0 || offset >= stride ||
                alpha == null || (long)width * height * stride > alpha.LongLength)
                throw new ArgumentException("Неполные данные альфа-канала.");
            return checked(width * height);
        }
    }
}
