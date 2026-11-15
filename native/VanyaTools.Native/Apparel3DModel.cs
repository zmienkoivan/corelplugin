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
        public int PrintPart;
        public MeshGeometry3D Mesh;
    }
    internal static class Apparel3DModel
    {
        // The authored MakeHuman CC0 shirt is prepared offline. Orders only
        // change its print textures and overall scale; there is no fit solver.
        public static ApparelSurface[] Build(ApparelViewMap front, ApparelViewMap back, double folds = .5)
        {
            if (front == null || back == null) throw new ArgumentNullException();
            Validate(front); Validate(back);
            if (!Finite(folds) || folds < 0 || folds > 1)
                throw new InvalidOperationException("Некорректная сила складок.");
            return ApparelShirtMesh.Build(front, folds);
        }
        internal static void Validate(ApparelViewMap map)
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
