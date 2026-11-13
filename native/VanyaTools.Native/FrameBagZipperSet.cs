using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal sealed class FrameBagZipperSet
    {
        public BagZipper Left { get; set; }
        public BagZipper Right { get; set; }
        public double Width { get; set; } = 8;
        public double FlapDepth { get; set; } = 20;
        public double FlapExtra { get; set; } = 10;
        public double[] LeftStart { get; set; }
        public double[] LeftEnd { get; set; }
        public double[] RightStart { get; set; }
        public double[] RightEnd { get; set; }
        internal FrameBagZipperSet Copy() => new FrameBagZipperSet { Left = Left, Right = Right, Width = Width,
            FlapDepth = FlapDepth, FlapExtra = FlapExtra, LeftStart = Clone(LeftStart), LeftEnd = Clone(LeftEnd),
            RightStart = Clone(RightStart), RightEnd = Clone(RightEnd) };
        private static double[] Clone(double[] point) => point == null ? null : (double[])point.Clone();
        internal static FrameBagZipperSet Capture(FrameBagSetup s) => new FrameBagZipperSet { Left = s.LeftZipper, Right = s.RightZipper,
            Width = s.ZipperWidth, FlapDepth = s.FlapDepth, FlapExtra = s.FlapExtra,
            LeftStart = new[] { s.LeftZipStartXmm, s.LeftZipStartYmm }, LeftEnd = new[] { s.LeftZipEndXmm, s.LeftZipEndYmm },
            RightStart = new[] { s.RightZipStartXmm, s.RightZipStartYmm }, RightEnd = new[] { s.RightZipEndXmm, s.RightZipEndYmm } };
        internal void ApplyTo(FrameBagSetup s)
        {
            BagPoint la = Point(LeftStart), lb = Point(LeftEnd), ra = Point(RightStart), rb = Point(RightEnd);
            s.LeftZipper = Left; s.RightZipper = Right; s.ZipperWidth = Width; s.FlapDepth = FlapDepth; s.FlapExtra = FlapExtra;
            s.FixedZipperAnchors = true;
            s.LeftZipStartXmm = la.X; s.LeftZipStartYmm = la.Y; s.LeftZipEndXmm = lb.X; s.LeftZipEndYmm = lb.Y;
            s.RightZipStartXmm = ra.X; s.RightZipStartYmm = ra.Y; s.RightZipEndXmm = rb.X; s.RightZipEndYmm = rb.Y;
        }
        internal void Move(int side, int index, double x, double y)
        {
            if (side == 0) { if (index == 0) LeftStart = new[] { x, y }; else LeftEnd = new[] { x, y }; }
            else { if (index == 0) RightStart = new[] { x, y }; else RightEnd = new[] { x, y }; }
        }
        private static BagPoint Point(double[] p)
        {
            if (p == null || p.Length != 2 || p.Any(v => Double.IsNaN(v) || Double.IsInfinity(v) || Math.Abs(v) > 10000))
                throw new ArgumentException("Неверные координаты молнии в проекте.");
            return new BagPoint(p[0], p[1]);
        }
        // Old projects have one closure set. Keep each existing zipper in its
        // original module and create a separate default opening for an empty bag.
        internal static FrameBagZipperSet Initialize(FrameBagPlan parent, FrameBagSetup setup, BagPoint[] bag)
        {
            var result = Capture(parent.Setup);
            for (int side = 0; side < 2; side++)
            {
                var mode = side == 0 ? result.Left : result.Right;
                if (mode == BagZipper.None) continue;
                bool a = FrameBagClosures.Inside(bag, FrameBagClosures.Anchor(parent, side, 0));
                bool b = FrameBagClosures.Inside(bag, FrameBagClosures.Anchor(parent, side, 1));
                if (a != b) throw new ArgumentException("Линия разделения пересекает молнию. Переместите линию или якоря.");
                if (!a) { if (side == 0) result.Left = BagZipper.None; else result.Right = BagZipper.None; }
            }
            if (result.Left == BagZipper.None && result.Right == BagZipper.None) return Place(setup, bag);
            if (result.Left == BagZipper.None) { result.LeftStart = Clone(result.RightStart); result.LeftEnd = Clone(result.RightEnd); }
            if (result.Right == BagZipper.None) { result.RightStart = Clone(result.LeftStart); result.RightEnd = Clone(result.LeftEnd); }
            return result;
        }
        internal static FrameBagZipperSet Place(FrameBagSetup template, BagPoint[] bag)
        {
            double top = bag.Min(p => p.Y), height = bag.Max(p => p.Y) - top;
            var model = new FrameBagPlan { Setup = template, Bag = bag, ActualHeight = height };
            model.SideSeam = bag.Select(p => FrameBagClosures.Flat(model, p)).ToArray();
            double firstWidth = Math.Max(1, Math.Min(8, Math.Min(template.ZipperWidth, height / 3)));
            var widths = new[] { firstWidth, Math.Max(1, firstWidth / 2), 1d }.Distinct().ToArray();
            var fractions = new[] { .3, .4, .5, .6, .7, .2, .8, .15, .85, .1, .9, .35, .45, .55, .65, .25, .75 };
            foreach (bool vertical in new[] { false, true })
            foreach (double width in widths)
            foreach (double fraction in fractions)
            {
                double rowStart = vertical ? bag.Min(p => p.X) : top;
                double rowExtent = vertical ? bag.Max(p => p.X) - rowStart : height;
                double rowPosition = rowStart + rowExtent * fraction;
                var xs = new List<double>();
                for (int i = 0; i < bag.Length; i++)
                {
                    var a = bag[i]; var b = bag[(i + 1) % bag.Length];
                    double ay = vertical ? a.X : a.Y, by = vertical ? b.X : b.Y;
                    double ax = vertical ? a.Y : a.X, bx = vertical ? b.Y : b.X;
                    if ((ay <= rowPosition && by > rowPosition) || (by <= rowPosition && ay > rowPosition))
                        xs.Add(ax + (bx - ax) * (rowPosition - ay) / (by - ay));
                }
                xs.Sort();
                var rows = Enumerable.Range(0, xs.Count / 2).Select(i => new[] { xs[i * 2], xs[i * 2 + 1] }).OrderByDescending(row => row[1] - row[0]);
                foreach (var row in rows)
                {
                    double inset = Math.Max(width * 1.5 + 1, (row[1] - row[0]) * .15);
                    var a = vertical ? new BagPoint(rowPosition, row[0] + inset) : new BagPoint(row[0] + inset, rowPosition);
                    var b = vertical ? new BagPoint(rowPosition, row[1] - inset) : new BagPoint(row[1] - inset, rowPosition);
                    if (BagPoint.Sub(b, a).Length < width + 6) continue;
                    var slot = FrameBagClosures.Capsule(FrameBagClosures.Flat(model, a), FrameBagClosures.Flat(model, b), width);
                    if (!FrameBagClosures.ContainsPath(model.SideSeam, slot)) continue;
                    return new FrameBagZipperSet { Left = BagZipper.Slot, Right = BagZipper.None, Width = width,
                        FlapDepth = template.FlapDepth, FlapExtra = template.FlapExtra,
                        LeftStart = new[] { a.X, a.Y }, LeftEnd = new[] { b.X, b.Y },
                        RightStart = new[] { a.X, a.Y }, RightEnd = new[] { b.X, b.Y } };
                }
            }
            throw new ArgumentException("Недостаточно места для молнии. Увеличьте эту часть сумки или измените линию разделения.");
        }
    }
}
