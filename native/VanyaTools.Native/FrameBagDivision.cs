using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal static class FrameBagDivision
    {
        internal const int MaximumPoints = 16;
        private sealed class Hit { internal int Edge; internal double Along; internal BagPoint Point; }
        internal sealed class SplitResult { internal BagPoint[][] Polygons; internal BagPoint[] Path; }

        internal static void EnsureLine(FrameBagPlan plan)
        {
            if (plan.Setup.DivisionLine != null) return;
            double left = plan.Bag.Min(p => p.X), right = plan.Bag.Max(p => p.X);
            double y = plan.Bag.Min(p => p.Y) + plan.ActualHeight / 2;
            plan.Setup.DivisionLine = new[] { new[] { left - 20, y }, new[] { right + 20, y } };
        }
        internal static BagPoint[] ReadLine(FrameBagSetup setup)
        {
            var line = setup.DivisionLine;
            if (line == null || line.Length < 2 || line.Length > MaximumPoints ||
                line.Any(p => p == null || p.Length != 2 || p.Any(v => Double.IsNaN(v) || Double.IsInfinity(v) || Math.Abs(v) > 10000)))
                throw new ArgumentException("Линия разделения: от 2 до " + MaximumPoints + " точек с координатами в мм.");
            return line.Select(p => new BagPoint(p[0], p[1])).ToArray();
        }
        internal static void Apply(FrameBagPlan plan)
        {
            var divided = Split(plan.Bag, ReadLine(plan.Setup));
            plan.DivisionPath = divided.Path;
            var sections = new List<FrameBagPlan>();
            double originalTop = plan.Bag.Min(p => p.Y);
            double slope = (plan.Setup.BottomDepth - plan.Setup.TopDepth) / plan.ActualHeight;
            for (int n = 0; n < 2; n++)
            {
                var bag = divided.Polygons[n];
                double top = bag.Min(p => p.Y), bottom = bag.Max(p => p.Y);
                if (bottom - top < 5 || FrameBagGeometry.Area(bag) < 100)
                    throw new ArgumentException("Линия оставляет слишком маленькую сумку. Сдвиньте точки разделения.");
                var setup = plan.Setup.Copy(); setup.DivideBags = false;
                setup.TopDepth = plan.Setup.TopDepth + slope * (top - originalTop);
                setup.BottomDepth = plan.Setup.TopDepth + slope * (bottom - originalTop);
                setup.Height = setup.FrontHeight = bottom - top;
                // Zippers keep their frame coordinates and belong to exactly one
                // bag. Slot/flap intersection is validated by the child builder.
                for (int side = 0; side < 2; side++)
                {
                    var mode = FrameBagClosures.Mode(plan.Setup, side);
                    if (mode == BagZipper.None) continue;
                    bool first = FrameBagClosures.Inside(bag, FrameBagClosures.Anchor(plan, side, 0));
                    bool second = FrameBagClosures.Inside(bag, FrameBagClosures.Anchor(plan, side, 1));
                    if (first != second) throw new ArgumentException("Линия разделения пересекает молнию. Переместите линию или якоря молнии.");
                    if (!first) { if (side == 0) setup.LeftZipper = BagZipper.None; else setup.RightZipper = BagZipper.None; }
                }
                var important = plan.Breaks.Select(i => plan.Bag[i]).Concat(new[] { divided.Path[0], divided.Path[divided.Path.Length - 1] }).ToArray();
                var breaks = Enumerable.Range(0, bag.Length).Where(i => important.Any(p => BagPoint.Sub(bag[i], p).Length < .00001)).ToArray();
                if (breaks.Length < 2) throw new ArgumentException("Линия разделения не образует замкнутую сумку.");
                FrameBagPlan section;
                try { section = FrameBagGeometry.FromOutline(setup, plan.Frame, bag, breaks, plan.AvailableHeight, plan.MaximumRadius, true); }
                catch (ArgumentException error) { throw new ArgumentException((n == 0 ? "Верхняя" : "Нижняя") + " сумка: " + error.Message, error); }
                section.Setup.Name = (n == 0 ? "Верхняя" : "Нижняя") + " сумка";
                foreach (var part in section.Parts) part.Name = section.Setup.Name + " · " + part.Name;
                sections.Add(section);
            }
            // The common line has TWO closing wall sets: each bag remains
            // closed and usable independently. No shared fabric partition.
            plan.Sections = sections.ToArray();
            plan.Parts = sections.SelectMany(section => section.Parts.Select(part => part.CopyLayout())).ToArray();
            plan.VolumeLitres = sections.Sum(section => section.VolumeLitres);
        }

        internal static SplitResult Split(BagPoint[] polygon, BagPoint[] line)
        {
            if (line.Length < 2 || line.Length > MaximumPoints) throw new ArgumentException("Добавьте линию разделения из 2–16 точек.");
            for (int i = 0; i + 1 < line.Length; i++)
            {
                if (BagPoint.Sub(line[i + 1], line[i]).Length < .1) throw new ArgumentException("Точки разделения слишком близко.");
                if (i > 0)
                {
                    var before = BagPoint.Sub(line[i], line[i - 1]); var after = BagPoint.Sub(line[i + 1], line[i]);
                    if (Math.Abs(BagPoint.Cross(before, after)) < .00001 && before.X * after.X + before.Y * after.Y < 0)
                        throw new ArgumentException("Линия разделения идёт обратно по себе. Передвиньте точку.");
                }
                for (int j = i + 2; j + 1 < line.Length; j++)
                    if (Intersect(line[i], line[i + 1], line[j], line[j + 1], out _, out _, true))
                        throw new ArgumentException("Линия разделения пересекает сама себя. Передвиньте её точки.");
            }
            if (FrameBagClosures.Inside(polygon, line[0]) || FrameBagClosures.Inside(polygon, line[line.Length - 1]))
                throw new ArgumentException("Концы линии разделения должны выходить за контур сумки слева и справа.");
            if (line.Skip(1).Take(line.Length - 2).Any(p => !FrameBagClosures.Inside(polygon, p)))
                throw new ArgumentException("Средние точки линии разделения должны оставаться внутри сумки.");
            var hits = new List<Hit>();
            for (int i = 0; i + 1 < line.Length; i++)
            for (int j = 0; j < polygon.Length; j++)
            {
                if (!Intersect(line[i], line[i + 1], polygon[j], polygon[(j + 1) % polygon.Length], out double t, out _, false)) continue;
                var p = BagPoint.Add(line[i], BagPoint.Mul(BagPoint.Sub(line[i + 1], line[i]), t));
                if (hits.Any(h => BagPoint.Sub(h.Point, p).Length < .00001)) continue;
                hits.Add(new Hit { Edge = j, Along = i + t, Point = p });
            }
            hits = hits.OrderBy(h => h.Along).ToList();
            if (hits.Count != 2) throw new ArgumentException("Линия должна пересекать контур ровно два раза: слева и справа.");
            var first = hits[0]; var last = hits[1];
            var path = new List<BagPoint> { first.Point };
            for (int i = 1; i + 1 < line.Length; i++) if (i > first.Along + 1e-8 && i < last.Along - 1e-8) path.Add(line[i]);
            path.Add(last.Point);
            for (int i = 0; i + 1 < path.Count; i++)
                for (int sample = 1; sample < 4; sample++)
                    if (!FrameBagClosures.Inside(polygon, BagPoint.Add(path[i], BagPoint.Mul(BagPoint.Sub(path[i + 1], path[i]), sample / 4d))))
                        throw new ArgumentException("Средние точки линии разделения должны оставаться внутри сумки.");
            var a = Boundary(polygon, first, last).Concat(path.AsEnumerable().Reverse().Skip(1).Take(path.Count - 2)).ToArray();
            var b = Boundary(polygon, last, first).Concat(path.Skip(1).Take(path.Count - 2)).ToArray();
            a = Positive(FrameBagGeometry.Clean(a)); b = Positive(FrameBagGeometry.Clean(b));
            if (a.Length < 3 || b.Length < 3 || FrameBagGeometry.Area(a) < 100 || FrameBagGeometry.Area(b) < 100)
                throw new ArgumentException("Одна из сумок слишком мала. Измените линию разделения.");
            // The upper bag is the component containing the original top edge.
            double top = polygon.Min(p => p.Y);
            var topPoints = polygon.Where(p => Math.Abs(p.Y - top) < .000001).ToArray();
            var probe = new BagPoint(topPoints.Average(p => p.X), top + .0001);
            bool aIsUpper = FrameBagClosures.Inside(a, probe);
            if (!aIsUpper && !FrameBagClosures.Inside(b, probe))
                aIsUpper = FrameBagGeometry.CentroidY(a) < FrameBagGeometry.CentroidY(b);
            return new SplitResult { Polygons = aIsUpper ? new[] { a, b } : new[] { b, a }, Path = path.ToArray() };
        }
        private static BagPoint[] Positive(BagPoint[] points)
        { if (FrameBagGeometry.Area(points) < 0) Array.Reverse(points); return points; }
        private static IEnumerable<BagPoint> Boundary(BagPoint[] polygon, Hit start, Hit end)
        {
            yield return start.Point;
            int i = (start.Edge + 1) % polygon.Length;
            while (i != (end.Edge + 1) % polygon.Length) { yield return polygon[i]; i = (i + 1) % polygon.Length; }
            yield return end.Point;
        }
        private static bool Intersect(BagPoint a, BagPoint b, BagPoint c, BagPoint d, out double t, out double u, bool collinear)
        {
            var v = BagPoint.Sub(b, a); var w = BagPoint.Sub(d, c); var r = BagPoint.Sub(c, a);
            double cross = BagPoint.Cross(v, w); t = u = 0;
            if (Math.Abs(cross) < 1e-9)
            {
                if (!collinear || Math.Abs(BagPoint.Cross(v, r)) > .000001) return false;
                double length = v.X * v.X + v.Y * v.Y;
                double t1 = (r.X * v.X + r.Y * v.Y) / length;
                var q = BagPoint.Sub(d, a); double t2 = (q.X * v.X + q.Y * v.Y) / length;
                return Math.Max(Math.Min(t1, t2), 0) <= Math.Min(Math.Max(t1, t2), 1) + 1e-8;
            }
            t = BagPoint.Cross(r, w) / cross; u = BagPoint.Cross(r, v) / cross;
            return t >= -1e-8 && t <= 1 + 1e-8 && u >= -1e-8 && u <= 1 + 1e-8;
        }
    }
}
