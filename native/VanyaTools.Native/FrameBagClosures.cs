using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal static class FrameBagClosures
    {
        internal static string Name(BagZipper mode) => mode == BagZipper.None ? "без молнии" :
            mode == BagZipper.Slot ? "прорезь" : mode == BagZipper.Split ? "две детали" : "прорезь + клапан";
        internal static BagZipper Mode(FrameBagSetup setup, int side) => side == 0 ? setup.LeftZipper : setup.RightZipper;
        internal static BagPoint Anchor(FrameBagPlan plan, int side, int index)
        {
            var s = plan.Setup;
            if (s.FixedZipperAnchors) return side == 0
                ? new BagPoint(index == 0 ? s.LeftZipStartXmm : s.LeftZipEndXmm, index == 0 ? s.LeftZipStartYmm : s.LeftZipEndYmm)
                : new BagPoint(index == 0 ? s.RightZipStartXmm : s.RightZipEndXmm, index == 0 ? s.RightZipStartYmm : s.RightZipEndYmm);
            double x = side == 0 ? (index == 0 ? s.LeftZipAX : s.LeftZipBX) : (index == 0 ? s.RightZipAX : s.RightZipBX);
            double y = side == 0 ? (index == 0 ? s.LeftZipAY : s.LeftZipBY) : (index == 0 ? s.RightZipAY : s.RightZipBY);
            double left = plan.Bag.Min(p => p.X), top = plan.Bag.Min(p => p.Y);
            return new BagPoint(left + (plan.Bag.Max(p => p.X) - left) * x / 100, top + plan.ActualHeight * y / 100);
        }

        // Migrate legacy percentage presets once. Subsequent size/rounding
        // changes preserve the physical zipper endpoints in frame millimetres.
        internal static void FixAnchors(FrameBagPlan plan)
        {
            var s = plan.Setup;
            if (s.FixedZipperAnchors) return;
            var la = Anchor(plan, 0, 0); var lb = Anchor(plan, 0, 1);
            var ra = Anchor(plan, 1, 0); var rb = Anchor(plan, 1, 1);
            s.LeftZipStartXmm = la.X; s.LeftZipStartYmm = la.Y; s.LeftZipEndXmm = lb.X; s.LeftZipEndYmm = lb.Y;
            s.RightZipStartXmm = ra.X; s.RightZipStartYmm = ra.Y; s.RightZipEndXmm = rb.X; s.RightZipEndYmm = rb.Y;
            s.FixedZipperAnchors = true;
        }

        internal static BagPoint Flat(FrameBagPlan plan, BagPoint p)
        {
            double slope = (plan.Setup.BottomDepth - plan.Setup.TopDepth) / plan.ActualHeight;
            return new BagPoint(p.X, (p.Y - plan.Bag.Min(point => point.Y)) * Math.Sqrt(1 + slope * slope / 4));
        }
        internal static BagPoint Project(FrameBagPlan plan, BagPoint p)
        {
            double slope = (plan.Setup.BottomDepth - plan.Setup.TopDepth) / plan.ActualHeight;
            return new BagPoint(p.X, plan.Bag.Min(point => point.Y) + p.Y / Math.Sqrt(1 + slope * slope / 4));
        }

        internal static void Apply(FrameBagPlan plan)
        {
            var s = plan.Setup;
            if (!Enum.IsDefined(typeof(BagZipper), s.LeftZipper) || !Enum.IsDefined(typeof(BagZipper), s.RightZipper))
                throw new ArgumentException("Выберите конструкцию молнии для каждой стороны.");
            if (s.LeftZipper == BagZipper.None && s.RightZipper == BagZipper.None) return;
            Range(s.ZipperWidth, 1, 40, "Ширина открытия молнии");
            Range(s.FlapDepth, 5, 100, "Ширина клапана"); Range(s.FlapExtra, 0, 50, "Выступ клапана");
            foreach (double value in new[] { s.LeftZipAX, s.LeftZipAY, s.LeftZipBX, s.LeftZipBY,
                s.RightZipAX, s.RightZipAY, s.RightZipBX, s.RightZipBY }) Range(value, 0, 100, "Якорь молнии");
            var sides = new List<FrameBagPart>(); var flaps = new List<FrameBagPart>();
            for (int side = 0; side < 2; side++)
            {
                string name = side == 0 ? "Левая" : "Правая";
                var mode = Mode(s, side);
                if (mode == BagZipper.None) { sides.Add(plan.Parts[side]); continue; }
                var a = Flat(plan, Anchor(plan, side, 0)); var b = Flat(plan, Anchor(plan, side, 1));
                Range(a.X, -10000, 10000, "Якорь молнии X"); Range(a.Y, -10000, 10000, "Якорь молнии Y");
                Range(b.X, -10000, 10000, "Якорь молнии X"); Range(b.Y, -10000, 10000, "Якорь молнии Y");
                var direction = BagPoint.Sub(b, a);
                double length = direction.Length;
                if (length < s.ZipperWidth + 5) throw new ArgumentException(name + ": раздвиньте якоря молнии.");
                var normal = new BagPoint(-direction.Y / length, direction.X / length);
                // Choose the downward normal regardless of anchor order.
                if (normal.Y < 0 || (Math.Abs(normal.Y) < .000001 && normal.X < 0)) normal = BagPoint.Mul(normal, -1);
                if (!Inside(plan.SideSeam, a) || !Inside(plan.SideSeam, b))
                    throw new ArgumentException(name + ": переместите якоря внутрь боковины.");
                if (mode == BagZipper.Split)
                {
                    var upperA = BagPoint.Sub(a, BagPoint.Mul(normal, s.ZipperWidth / 2));
                    var upperB = BagPoint.Sub(b, BagPoint.Mul(normal, s.ZipperWidth / 2));
                    var lowerA = BagPoint.Add(a, BagPoint.Mul(normal, s.ZipperWidth / 2));
                    var lowerB = BagPoint.Add(b, BagPoint.Mul(normal, s.ZipperWidth / 2));
                    bool upper = BagPoint.Cross(BagPoint.Sub(b, a), normal) > 0;
                    var first = FrameBagGeometry.ClipLine(plan.SideSeam, upperA, upperB, upper);
                    var second = FrameBagGeometry.ClipLine(plan.SideSeam, lowerA, lowerB, !upper);
                    if (first.Length < 3 || second.Length < 3 || FrameBagGeometry.Area(first) < 100 || FrameBagGeometry.Area(second) < 100)
                        throw new ArgumentException(name + ": линия молнии оставляет слишком маленькую половину.");
                    sides.Add(SidePart(name + " · часть 1", first, side, s.Allowance));
                    sides.Add(SidePart(name + " · часть 2", second, side, s.Allowance));
                }
                else
                {
                    var slot = Capsule(a, b, s.ZipperWidth);
                    if (slot.Any(p => !Inside(plan.SideSeam, p)))
                        throw new ArgumentException(name + ": прорезь пересекает линию шва. Сдвиньте якоря внутрь.");
                    var part = SidePart("Боковина " + name.ToLowerInvariant(), plan.SideSeam, side, s.Allowance);
                    part.Cutouts = new[] { Transform(slot, side) };
                    part.Marks = new[] { Transform(new[] { a, b }, side) };
                    if (mode == BagZipper.Flap)
                    {
                        var cover = FlapCover(plan, a, b);
                        var attachA = cover[0]; var attachB = cover[1];
                        if (cover.Any(p => !Inside(plan.SideSeam, p)))
                            throw new ArgumentException(name + ": клапан выходит за боковину. Сдвиньте якоря или уменьшите клапан.");
                        part.Marks = part.Marks.Concat(new[] { Transform(new[] { attachA, attachB }, side) }).ToArray();
                        var flap = new[] { new BagPoint(0, 0), new BagPoint(length + s.FlapExtra * 2, 0),
                            new BagPoint(length + s.FlapExtra * 2, s.FlapDepth), new BagPoint(0, s.FlapDepth) };
                        var rounded = FrameBagGeometry.Round(flap, Math.Min(3, s.FlapDepth / 4), out _);
                        flaps.Add(FrameBagGeometry.Part("Клапан · " + name.ToLowerInvariant(), rounded, s.Allowance));
                    }
                    sides.Add(part);
                }
            }
            plan.Parts = sides.Concat(plan.Parts.Skip(2)).Concat(flaps).ToArray();
        }

        internal static BagPoint[] FlapCover(FrameBagPlan plan, BagPoint a, BagPoint b)
        {
            var s = plan.Setup;
            var delta = BagPoint.Sub(b, a); var unit = BagPoint.Mul(delta, 1 / delta.Length);
            var normal = new BagPoint(-unit.Y, unit.X);
            if (normal.Y < 0 || (Math.Abs(normal.Y) < .000001 && normal.X < 0)) normal = BagPoint.Mul(normal, -1);
            var first = BagPoint.Sub(a, BagPoint.Mul(unit, s.FlapExtra));
            var last = BagPoint.Add(b, BagPoint.Mul(unit, s.FlapExtra));
            var attachA = BagPoint.Sub(first, BagPoint.Mul(normal, s.ZipperWidth / 2 + 3));
            var attachB = BagPoint.Sub(last, BagPoint.Mul(normal, s.ZipperWidth / 2 + 3));
            return new[] { attachA, attachB, BagPoint.Add(attachB, BagPoint.Mul(normal, s.FlapDepth)),
                BagPoint.Add(attachA, BagPoint.Mul(normal, s.FlapDepth)) };
        }

        private static FrameBagPart SidePart(string name, BagPoint[] seam, int side, double allowance)
        {
            var points = Transform(seam, side);
            if (FrameBagGeometry.Area(points) < 0) Array.Reverse(points);
            var part = FrameBagGeometry.Part(name, points, allowance); part.Side = side; return part;
        }
        private static BagPoint[] Transform(BagPoint[] points, int side) =>
            side == 0 ? (BagPoint[])points.Clone() : points.Select(p => new BagPoint(-p.X, p.Y)).ToArray();
        internal static BagPoint[] Capsule(BagPoint a, BagPoint b, double width)
        {
            double length = BagPoint.Sub(b, a).Length, radius = width / 2;
            var u = BagPoint.Mul(BagPoint.Sub(b, a), 1 / length);
            var c1 = BagPoint.Add(a, BagPoint.Mul(u, radius)); var c2 = BagPoint.Sub(b, BagPoint.Mul(u, radius));
            double angle = Math.Atan2(u.Y, u.X);
            int steps = Math.Max(8, (int)Math.Ceiling(Math.PI / (2 * Math.Acos(Math.Max(-1, 1 - .005 / radius)))));
            var points = new List<BagPoint>();
            for (int n = 0; n <= steps; n++)
            { double at = angle + Math.PI / 2 + Math.PI * n / steps; points.Add(new BagPoint(c1.X + radius * Math.Cos(at), c1.Y + radius * Math.Sin(at))); }
            for (int n = 0; n <= steps; n++)
            { double at = angle - Math.PI / 2 + Math.PI * n / steps; points.Add(new BagPoint(c2.X + radius * Math.Cos(at), c2.Y + radius * Math.Sin(at))); }
            return points.ToArray();
        }
        internal static bool Inside(BagPoint[] polygon, BagPoint p)
        {
            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                var a = polygon[j]; var b = polygon[i];
                var edge = BagPoint.Sub(b, a); double squared = edge.X * edge.X + edge.Y * edge.Y;
                double t = Math.Max(0, Math.Min(1, (BagPoint.Sub(p, a).X * edge.X + BagPoint.Sub(p, a).Y * edge.Y) / squared));
                if (BagPoint.Sub(p, BagPoint.Add(a, BagPoint.Mul(edge, t))).Length < .00001) return true;
                if ((b.Y > p.Y) != (a.Y > p.Y) && p.X < (a.X - b.X) * (p.Y - b.Y) / (a.Y - b.Y) + b.X) inside = !inside;
            }
            return inside;
        }
        private static void Range(double value, double min, double max, string name)
        { if (Double.IsNaN(value) || Double.IsInfinity(value) || value < min || value > max) throw new ArgumentException(name + ": от " + min + " до " + max + "."); }
    }
}
