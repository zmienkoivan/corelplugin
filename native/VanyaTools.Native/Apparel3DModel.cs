using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal sealed class ApparelSleeveMap
    {
        // Normalised coordinates in a complete flat front/back view.
        public Point RootTop, RootBottom, CuffTop, CuffBottom;
        public Point At(double along, double across) => Lerp(Lerp(RootTop, RootBottom, across), Lerp(CuffTop, CuffBottom, across), along);
        private static Point Lerp(Point a, Point b, double t) => a + (b - a) * t;
    }
    internal sealed class ApparelViewMap
    {
        public Rect Body;
        public ApparelSleeveMap ImageLeft, ImageRight;
        public double BodyWidthMm = 520, BodyHeightMm = 710, NeckDepthMm = 100;
        public BitmapSource Image;
        public string Name;
        public static ApparelViewMap Demo(bool back = false) => new ApparelViewMap
        {
            Name = back ? "Спинка" : "Перед", Body = new Rect(.17, .01, .66, .98), NeckDepthMm = back ? 28 : 100,
            ImageLeft = new ApparelSleeveMap { RootTop = new Point(.20, .08), RootBottom = new Point(.17, .35),
                CuffTop = new Point(0, .15), CuffBottom = new Point(.07, .36) },
            ImageRight = new ApparelSleeveMap { RootTop = new Point(.80, .08), RootBottom = new Point(.83, .35),
                CuffTop = new Point(1, .15), CuffBottom = new Point(.93, .36) }
        };
        public Point BodyUv(double x, double y) => new Point(Body.X + x * Body.Width, Body.Y + y * Body.Height);
    }
    internal sealed class ApparelSurface
    {
        public string Name;
        public bool Back, Collar;
        public MeshGeometry3D Mesh;
    }
    internal static class Apparel3DModel
    {
        // A local display mesh, not a cloth/fit simulation. All print samples are
        // addressed in the original 2D view, including the angled sleeve quads.
        public static ApparelSurface[] Build(ApparelViewMap front, ApparelViewMap back, double folds = .5)
        {
            if (front == null || back == null) throw new ArgumentNullException();
            Validate(front); Validate(back);
            if (!Finite(front.BodyWidthMm) || !Finite(front.BodyHeightMm) || front.BodyWidthMm < 50 || front.BodyWidthMm > 2000 ||
                front.BodyHeightMm < 50 || front.BodyHeightMm > 2500 || !Finite(folds) || folds < 0 || folds > 1)
                throw new InvalidOperationException("Некорректный размер модели или сила складок.");
            var result = new List<ApparelSurface>();
            double w = front.BodyWidthMm / 1000, h = front.BodyHeightMm / 1000;
            const double q = .34;
            double[] angles = ArcAngles(q, 48);
            double arcLength = ArcLength(q);
            double ArcScale(double flatWidth) => flatWidth / arcLength;
            double HalfFlat(double y) => w * (.5 - .048 * Math.Max(0, (.345 * h - y) / (.275 * h)));
            double Top(double s, bool rear)
            {
                double a = Math.Abs(s), neck = .405;
                if (a < neck) return Math.Min(.24 * h, (rear ? back.NeckDepthMm : front.NeckDepthMm) / 1000) * Math.Sqrt(Math.Max(0, 1 - a * a / (neck * neck)));
                return .0704 * h * (a - neck) / (1 - neck);
            }
            double EdgeZ(double y)
            {
                double t = (y - .2077 * h) / (.1373 * h);
                return .035 * h * Math.Sqrt(Math.Max(0, 1 - t * t));
            }
            Point3D BodyPoint(double u, double t, bool rear)
            {
                double s = 2 * u - 1, y = h * (1 - t) + t * Top(s, rear);
                double radius = ArcScale(2 * HalfFlat(y)), angle = AngleAt(angles, u);
                double wave = folds * h * (.013 * Math.Sin(u * 38 + t * 5) + .004 * Math.Sin(u * 71 - t * 8)) *
                    Math.Sin(Math.PI * t) * Math.Sin(Math.PI * u);
                double z = q * radius * Math.Cos(angle) + EdgeZ(y) * Math.Pow(Math.Abs(s), 6) + wave;
                return new Point3D(radius * Math.Sin(angle), h / 2 - y, (rear ? -1 : 1) * z);
            }
            foreach (bool rear in new[] { false, true })
            {
                var map = rear ? back : front;
                var mesh = Grid(48, 64, (u, t) => BodyPoint(u, t, rear), (u, t) =>
                {
                    double s = 2 * u - 1, y = h * (1 - t) + t * Top(s, rear);
                    return map.BodyUv(.5 + (rear ? -s : s) * HalfFlat(y) / w, y / h);
                }, rear);
                result.Add(new ApparelSurface { Name = rear ? "Спинка" : "Перед", Back = rear, Mesh = mesh });
            }
            // The two shoulder halves share the original front/back top border.
            foreach (int side in new[] { -1, 1 }) foreach (bool rear in new[] { false, true })
            {
                var map = rear ? back : front;
                result.Add(new ApparelSurface { Name = "Плечо", Back = rear, Mesh = Grid(22, 6, (u, v) =>
                {
                    double s = side * (.405 + .595 * u);
                    var p = BodyPoint((s + 1) / 2, 1, rear);
                    return new Point3D(p.X, p.Y + .012 * h * Math.Sin(v * Math.PI / 2), p.Z * (1 - v));
                }, (u, v) =>
                {
                    double s = side * (.405 + .595 * u), y = Top(s, rear);
                    return map.BodyUv(.5 + (rear ? -s : s) * HalfFlat(y) / w, (y + v * .018 * h) / h);
                }, (side < 0) != rear) });
            }
            // Wearer's right is on the left of the front view; the back view is
            // reversed. Each half samples its own quad, so sleeve artwork is not
            // stretched across a bounding rectangle or mirrored accidentally.
            foreach (int side in new[] { -1, 1 }) foreach (bool rear in new[] { false, true })
            {
                var map = rear ? back : front;
                var sleeve = (side < 0) != rear ? map.ImageLeft : map.ImageRight;
                result.Add(new ApparelSurface { Name = side < 0 ? "Правый рукав" : "Левый рукав", Back = rear, Mesh = Grid(32, 22, (across, along) =>
                {
                    double theta = across * Math.PI, y = .2077 * h - .1373 * h * Math.Cos(theta);
                    double x = ArcScale(2 * HalfFlat(y));
                    double taper = 1 - .32 * along;
                    double yy = .1373 * h * Math.Cos(theta) * taper;
                    double zz = (.035 + .025 * along) * h * Math.Sin(theta);
                    double length = .23 * w;
                    return new Point3D(side * (x + length * along), h / 2 - .2077 * h + yy - .30 * length * along,
                        (rear ? -1 : 1) * zz + folds * .0015 * h * Math.Sin(across * 15) * Math.Sin(Math.PI * along));
                }, (across, along) => sleeve.At(along, across), (side < 0) != rear) });
            }
            // A separate collar rim keeps the neck open when orbiting the model.
            var loop = new List<Point3D>();
            for (int i = 0; i <= 32; i++) loop.Add(BodyPoint((1 - .405) / 2 + .405 * i / 32, 1, false));
            var rightA = loop[loop.Count - 1]; var rightB = BodyPoint((1 + .405) / 2, 1, true);
            for (int i = 1; i < 9; i++) loop.Add(Lerp(rightA, rightB, i / 8d));
            for (int i = 1; i <= 32; i++) loop.Add(BodyPoint((1 + .405) / 2 - .405 * i / 32, 1, true));
            var leftA = loop[loop.Count - 1]; var leftB = loop[0];
            for (int i = 1; i < 8; i++) loop.Add(Lerp(leftA, leftB, i / 8d));
            result.Add(new ApparelSurface { Name = "Горловина", Collar = true, Mesh = Grid(loop.Count, 1, (u, v) =>
            {
                var p = loop[(int)Math.Round(u * loop.Count) % loop.Count];
                return new Point3D(p.X * (1 - .11 * v), p.Y + .002 * h, p.Z * (1 - .11 * v));
            }, (u, v) => new Point(u, v), false) });
            return result.ToArray();
        }
        private static MeshGeometry3D Grid(int cols, int rows, Func<double, double, Point3D> point, Func<double, double, Point> uv, bool reverse)
        {
            var mesh = new MeshGeometry3D();
            for (int row = 0; row <= rows; row++) for (int col = 0; col <= cols; col++)
            {
                mesh.Positions.Add(point(col / (double)cols, row / (double)rows));
                mesh.TextureCoordinates.Add(uv(col / (double)cols, row / (double)rows));
            }
            for (int row = 0; row < rows; row++) for (int col = 0; col < cols; col++)
            {
                int a = row * (cols + 1) + col, b = a + 1, c = a + cols + 2, d = a + cols + 1;
                foreach (int index in reverse ? new[] { a, c, b, a, d, c } : new[] { a, b, c, a, c, d }) mesh.TriangleIndices.Add(index);
            }
            mesh.Freeze(); return mesh;
        }
        private static double ArcLength(double q)
        {
            double total = 0; const int n = 512;
            for (int i = 0; i < n; i++) { double a = -Math.PI / 2 + Math.PI * (i + .5) / n; total += Math.Sqrt(Math.Cos(a) * Math.Cos(a) + q * q * Math.Sin(a) * Math.Sin(a)) * Math.PI / n; }
            return total;
        }
        private static double[] ArcAngles(double q, int count)
        {
            const int n = 1024; var length = new double[n + 1];
            for (int i = 1; i <= n; i++) { double a = -Math.PI / 2 + Math.PI * (i - .5) / n; length[i] = length[i - 1] + Math.Sqrt(Math.Cos(a) * Math.Cos(a) + q * q * Math.Sin(a) * Math.Sin(a)); }
            var result = new double[count + 1]; int at = 1;
            for (int i = 0; i <= count; i++) { double target = length[n] * i / count; while (at < n && length[at] < target) at++; result[i] = -Math.PI / 2 + Math.PI * (at - 1 + (target - length[at - 1]) / (length[at] - length[at - 1])) / n; }
            return result;
        }
        private static double AngleAt(double[] angles, double u) { double t = u * (angles.Length - 1); int i = Math.Min(angles.Length - 2, (int)t); return angles[i] + (angles[i + 1] - angles[i]) * (t - i); }
        private static Point3D Lerp(Point3D a, Point3D b, double t) => a + (b - a) * t;
        private static void Validate(ApparelViewMap map)
        {
            if (map.Body.IsEmpty || map.Body.Width <= 0 || map.Body.Height <= 0 || map.ImageLeft == null || map.ImageRight == null ||
                !Finite(map.NeckDepthMm) || map.NeckDepthMm < 0 || !Finite(map.BodyWidthMm) || !Finite(map.BodyHeightMm) ||
                map.BodyWidthMm < 50 || map.BodyWidthMm > 2000 || map.BodyHeightMm < 50 || map.BodyHeightMm > 2500)
                throw new InvalidOperationException("Некорректные размеры или контуры вида футболки.");
            foreach (var point in new[] { map.Body.TopLeft, map.Body.BottomRight, map.ImageLeft.RootTop, map.ImageLeft.RootBottom,
                map.ImageLeft.CuffTop, map.ImageLeft.CuffBottom, map.ImageRight.RootTop, map.ImageRight.RootBottom,
                map.ImageRight.CuffTop, map.ImageRight.CuffBottom })
                if (!Finite(point.X) || !Finite(point.Y)) throw new InvalidOperationException("Некорректные координаты вида футболки.");
        }
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }
}
