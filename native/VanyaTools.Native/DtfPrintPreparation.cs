using System;
using System.Collections.Generic;
using System.Threading;

namespace VanyaTools.Native
{
    internal enum DtfDotShape { Circle, Ellipse, Diamond, Hexagon, Star, Square, Cross }

    internal sealed class DtfPrintPreparationOptions
    {
        internal bool Knockout, Halftone, AlphaOnly = true, AutoDot = true;
        internal bool SoftKnockout;
        internal bool RemoveSmall, GrowSmall;
        internal double RemoveBelowMm = .3, GrowToMm = .5;
        internal int CutEdges;
        internal DtfDotShape Shape;
        internal int ColorBoost;
        internal byte Red, Green, Blue;
        internal int Tolerance = 10;
        internal double FrequencyLpi = 20, AngleDegrees = 22.5, DotDiameterMm = 1;
        internal double OriginXmm, OriginYmm;
        internal DtfPrintPreparationOptions Copy() => (DtfPrintPreparationOptions)MemberwiseClone();
        internal DtfPrintPreparationOptions Offset(double x, double y)
        { var copy = Copy(); copy.OriginXmm += x; copy.OriginYmm += y; return copy; }
        internal double EffectiveDotMm => AutoDot ? 25.4 / FrequencyLpi * (AlphaOnly ? DtfDotGeometry.CoverDiameter(Shape) : .75) : DotDiameterMm;
        internal void Validate()
        {
            if (Knockout && (Tolerance < (SoftKnockout ? -255 : 0) || Tolerance > 255)) throw new ArgumentOutOfRangeException(nameof(Tolerance));
            if(Halftone && ((RemoveSmall && (!Finite(RemoveBelowMm) || RemoveBelowMm<.05 || RemoveBelowMm>5)) ||
                (GrowSmall && (!Finite(GrowToMm) || GrowToMm<.05 || GrowToMm>5)) || CutEdges<0 || CutEdges>15))
                throw new ArgumentException("Мелкие элементы: пороги 0,05–5 мм.");
            if (Halftone && (!Finite(FrequencyLpi) || FrequencyLpi < .5 || FrequencyLpi > 200 ||
                !Finite(AngleDegrees) || AngleDegrees < -180 || AngleDegrees > 180 ||
                (!AutoDot && (!Finite(DotDiameterMm) || DotDiameterMm < .01 || DotDiameterMm > 250)) ||
                !Enum.IsDefined(typeof(DtfDotShape), Shape) || ColorBoost < 0 || ColorBoost > 100 ||
                !Finite(OriginXmm) || !Finite(OriginYmm)))
                throw new ArgumentException("Халфтонинг: растр 0,5–200 LPI, угол −180…180°, размер 0,01–250 мм, усиление 0–100%.");
        }
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }

    // Hard knockout and screening edit only alpha. Soft colour-to-alpha and
    // explicit colour boost also edit straight colour samples in the SAME model.
    internal static class DtfPrintPreparation
    {
        internal static void Knockout(byte[] colors, int stride, int mode, int[] cmykOrder,
            byte[] alpha, int alphaStride, int alphaOffset, int width, int height,
            DtfPrintPreparationOptions options, CancellationToken cancellation)
        {
            if (options == null || !options.Knockout) return;
            options.Validate(); cancellation.ThrowIfCancellationRequested();
            int count = ValidateAlpha(alpha, alphaStride, alphaOffset, width, height);
            ValidateColors(colors, stride, mode, cmykOrder, count);
            if (mode == 2 && options.SoftKnockout && (options.Red != options.Green || options.Green != options.Blue))
                throw new InvalidOperationException("Плавный нокаут серого растра: выберите нейтральный цвет ткани (R=G=B) или подготовьте RGB-копию в Corel.");
            int tolerance = options.Tolerance;
            double deadZone = tolerance / 255d;
            for (int p = 0; p < count; p++)
            {
                if ((p & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                int a = p * alphaStride + alphaOffset; if (alpha[a] == 0) continue;
                int i = p * stride; ReadRgb(colors, i, mode, cmykOrder, out int r, out int g, out int b);
                if (!options.SoftKnockout)
                {
                    if (Math.Abs(r - options.Red) <= tolerance && Math.Abs(g - options.Green) <= tolerance && Math.Abs(b - options.Blue) <= tolerance)
                        alpha[a] = 0;
                    continue;
                }
                // Minimal foreground opacity able to recompose this pixel over
                // the key colour. Unmix the key from RGB as well to avoid halos.
                // At tolerance 0: alpha*foreground+(1-alpha)*key == input.
                double opacity = Math.Max(ComponentOpacity(r, options.Red), Math.Max(ComponentOpacity(g, options.Green), ComponentOpacity(b, options.Blue)));
                opacity = deadZone < 0 ? (opacity==0 ? 0 : deadZone<=-1 ? 1 : Math.Min(1,opacity/(1+deadZone))) :
                    deadZone >= 1 ? 0 : Math.Max(0, (opacity - deadZone) / (1 - deadZone));
                int nextAlpha = Byte(alpha[a] * opacity);
                if (nextAlpha == 0) { alpha[a] = 0; continue; }
                if (opacity >= 1 - 1e-12) continue;
                WriteRgb(colors, i, mode, cmykOrder,
                    Byte(options.Red + (r - options.Red) / opacity), Byte(options.Green + (g - options.Green) / opacity),
                    Byte(options.Blue + (b - options.Blue) / opacity));
                alpha[a] = (byte)nextAlpha;
            }
        }

        internal static void Boost(byte[] colors, int stride, int mode, int[] order, byte[] alpha, int alphaStride,
            int alphaOffset, int width, int height, DtfPrintPreparationOptions options, CancellationToken cancellation)
        {
            if (options == null || !options.Halftone || options.AlphaOnly || options.ColorBoost == 0) return;
            options.Validate(); cancellation.ThrowIfCancellationRequested();
            int count = ValidateAlpha(alpha, alphaStride, alphaOffset, width, height); ValidateColors(colors, stride, mode, order, count);
            double amount = options.ColorBoost / 100d, saturation = 1 + amount, contrast = 1 + .35 * amount;
            for (int p = 0; p < count; p++)
            {
                if ((p & 8191) == 0) cancellation.ThrowIfCancellationRequested();
                if (alpha[p * alphaStride + alphaOffset] == 0) continue;
                int i = p * stride; ReadRgb(colors, i, mode, order, out int r, out int g, out int b);
                double luma = .2126 * r + .7152 * g + .0722 * b, baseTone = 127.5 + (luma - 127.5) * contrast;
                int nr = Byte(baseTone + (r - luma) * saturation), ng = Byte(baseTone + (g - luma) * saturation), nb = Byte(baseTone + (b - luma) * saturation);
                if (nr != r || ng != g || nb != b) WriteRgb(colors, i, mode, order, nr, ng, nb);
            }
        }

        private static double ComponentOpacity(int color, int key) => color == key ? 0 : color > key ? (color - key) / (255d - key) : (key - color) / (double)key;
        private static byte Byte(double value) => (byte)Math.Max(0, Math.Min(255, (int)Math.Round(value)));
        private static void ValidateColors(byte[] colors, int stride, int mode, int[] order, int count)
        {
            int needed = mode == 2 ? 1 : mode == 4 ? 3 : mode == 5 ? 4 : 0;
            if (needed == 0 || stride < needed || colors == null || colors.LongLength < (long)count * stride)
                throw new ArgumentException("Неполные цветовые каналы обработки.");
            if (mode != 5) return;
            if (order == null || order.Length != 4) throw new InvalidOperationException("Не определён порядок CMYK-каналов.");
            int seen = 0; foreach (int c in order) {
                if (c < 0 || c > 3 || (seen & (1 << c)) != 0) throw new InvalidOperationException("Некорректный порядок CMYK-каналов.");
                seen |= 1 << c;
            }
        }
        private static void ReadRgb(byte[] colors, int i, int mode, int[] order, out int r, out int g, out int b)
        {
            if (mode == 4) { b = colors[i]; g = colors[i + 1]; r = colors[i + 2]; }
            else if (mode == 2) r = g = b = colors[i];
            else {
                int light = 255 - colors[i + order[3]];
                r = ((255 - colors[i + order[0]]) * light + 127) / 255;
                g = ((255 - colors[i + order[1]]) * light + 127) / 255;
                b = ((255 - colors[i + order[2]]) * light + 127) / 255;
            }
        }
        private static void WriteRgb(byte[] colors, int i, int mode, int[] order, int r, int g, int b)
        {
            if (mode == 4) { colors[i] = (byte)b; colors[i + 1] = (byte)g; colors[i + 2] = (byte)r; }
            else if (mode == 2) colors[i] = Byte(.2126 * r + .7152 * g + .0722 * b);
            else {
                // Keep native CMYK/order; retain existing K wherever the new RGB
                // permits it. This is the same technical (unprofiled) preview
                // model as ReadRgb, not an ICC conversion of the entire image.
                int k = r == g && g == b ? 255-r : Math.Min(colors[i + order[3]], 255 - Math.Max(r, Math.Max(g, b))), light = 255 - k;
                colors[i + order[3]] = (byte)k;
                colors[i + order[0]] = light == 0 ? (byte)0 : Byte(255 - r * 255d / light);
                colors[i + order[1]] = light == 0 ? (byte)0 : Byte(255 - g * 255d / light);
                colors[i + order[2]] = light == 0 ? (byte)0 : Byte(255 - b * 255d / light);
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
            double[] samples = options.Shape == DtfDotShape.Circle ? null : DtfDotGeometry.Samples(options.Shape);
            double maximum = Math.Pow(options.EffectiveDotMm / (2 * pitch), 2);
            double coverage = samples == null ? CircleCoverage(Math.Sqrt(maximum)) : DtfDotGeometry.Coverage(samples, maximum);
            var radiusSquared = new double[256];
            for (int a = 1; a < 256; a++) radiusSquared[a] = samples == null ? RadiusSquared(coverage * a / 255) :
                Math.Min(maximum, DtfDotGeometry.Quantile(samples, coverage * a / 255));
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
                    double score = samples == null ? u * u + v * v : DtfDotGeometry.Score(u, v, options.Shape);
                    alpha[i] = score <= radiusSquared[a] + 1e-12 ? (byte)255 : (byte)0;
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

    // Shape-aware tone calibration is cached once per shape, never per pixel
    // or preview change. Homogeneous distance scores preserve grid phase on ROI.
    internal static class DtfDotGeometry
    {
        private static readonly Dictionary<DtfDotShape, double[]> Cache = new Dictionary<DtfDotShape, double[]>();
        private static readonly double[] StarX = new double[10], StarY = new double[10];
        static DtfDotGeometry() {
            for (int i=0;i<10;i++) { double a=-Math.PI/2+i*Math.PI/5, r=i%2==0?1:.45; StarX[i]=r*Math.Cos(a); StarY[i]=r*Math.Sin(a); }
        }
        internal static double Score(double x, double y, DtfDotShape shape)
        {
            double ax=Math.Abs(x), ay=Math.Abs(y), value;
            switch(shape) {
                case DtfDotShape.Ellipse: return x*x+y*y/.36;
                case DtfDotShape.Diamond: value=ax+ay; break;
                case DtfDotShape.Hexagon: value=Math.Max(ay/.8660254037844386,ax+ay/1.7320508075688772); break;
                case DtfDotShape.Square: value=Math.Max(ax,ay); break;
                case DtfDotShape.Cross: value=Math.Max(Math.Max(ax,ay),Math.Min(ax,ay)/.35); break;
                case DtfDotShape.Star:
                    if(ax+ay<1e-15) return 0;
                    for(int i=0;i<10;i++) {
                        int j=(i+1)%10;
                        if(StarX[i]*y-StarY[i]*x>=-1e-14 && x*StarY[j]-y*StarX[j]>=-1e-14) {
                            double ex=StarX[j]-StarX[i], ey=StarY[j]-StarY[i];
                            value=(ex*y-ey*x)/(ex*StarY[i]-ey*StarX[i]); return value*value;
                        }
                    }
                    throw new InvalidOperationException("Не определена геометрия звезды.");
                default: return x*x+y*y;
            }
            return value*value;
        }
        internal static double CoverDiameter(DtfDotShape shape) {
            double max=0;
            // Scores are piecewise convex; their maximum on the unit cell is
            // attained at the cell boundary. Include edges for concave shapes.
            for(int i=0;i<=256;i++) { double p=i/256d-.5; max=Math.Max(max,Math.Max(Math.Max(Score(p,-.5,shape),Score(p,.5,shape)),Math.Max(Score(-.5,p,shape),Score(.5,p,shape)))); }
            return 2*Math.Sqrt(max);
        }
        internal static double[] Samples(DtfDotShape shape) {
            lock(Cache) {
                if(Cache.TryGetValue(shape,out double[] values)) return values;
                const int side=256; values=new double[side*side];
                for(int y=0;y<side;y++)for(int x=0;x<side;x++) values[y*side+x]=Score((x+.5)/side-.5,(y+.5)/side-.5,shape);
                Array.Sort(values); Cache.Add(shape,values); return values;
            }
        }
        internal static double Coverage(double[] values,double maximum) {
            int lo=0,hi=values.Length; while(lo<hi) {int mid=(lo+hi)/2;if(values[mid]<=maximum)lo=mid+1;else hi=mid;} return lo/(double)values.Length;
        }
        internal static double Quantile(double[] values,double coverage) {
            if(coverage<=0)return -1;
            return coverage>=1 ? Double.PositiveInfinity : values[Math.Min(values.Length-1,(int)(coverage*values.Length))];
        }
    }
}
