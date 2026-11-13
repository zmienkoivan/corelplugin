using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal readonly struct BagPoint
    {
        internal readonly double X, Y;
        internal BagPoint(double x, double y) { X = x; Y = y; }
        internal static BagPoint Add(BagPoint a, BagPoint b) => new BagPoint(a.X + b.X, a.Y + b.Y);
        internal static BagPoint Sub(BagPoint a, BagPoint b) => new BagPoint(a.X - b.X, a.Y - b.Y);
        internal static BagPoint Mul(BagPoint a, double k) => new BagPoint(a.X * k, a.Y * k);
        internal static double Cross(BagPoint a, BagPoint b) => a.X * b.Y - a.Y * b.X;
        internal double Length => Math.Sqrt(X * X + Y * Y);
    }

    internal enum BagZipper { None, Slot, Split, Flap }

    internal sealed class FrameBagSetup
    {
        internal const int MaximumCorners = 6;
        public bool TriangleFrame { get; set; }
        public double Top { get; set; } = 550;
        public double Seat { get; set; } = 461;
        public double Down { get; set; } = 594.1;
        public double Head { get; set; } = 104.4;
        public double Diagonal { get; set; } = 588.6;
        public bool DivideBags { get; set; }
        public double[][] DivisionLine { get; set; }
        public double BagLength { get; set; }
        public bool AttachToFront { get; set; }
        public double Height { get; set; } = 180;
        public bool SplitHeights { get; set; }
        public double FrontHeight { get; set; } = 180;
        public BagZipper LeftZipper { get; set; }
        public BagZipper RightZipper { get; set; }
        public double LeftZipAX { get; set; } = 12;
        public double LeftZipAY { get; set; } = 23;
        public double LeftZipBX { get; set; } = 87;
        public double LeftZipBY { get; set; } = 23;
        public double RightZipAX { get; set; } = 12;
        public double RightZipAY { get; set; } = 23;
        public double RightZipBX { get; set; } = 87;
        public double RightZipBY { get; set; } = 23;
        public bool FixedZipperAnchors { get; set; }
        public double LeftZipStartXmm { get; set; }
        public double LeftZipStartYmm { get; set; }
        public double LeftZipEndXmm { get; set; }
        public double LeftZipEndYmm { get; set; }
        public double RightZipStartXmm { get; set; }
        public double RightZipStartYmm { get; set; }
        public double RightZipEndXmm { get; set; }
        public double RightZipEndYmm { get; set; }
        public double ZipperWidth { get; set; } = 8;
        public double FlapDepth { get; set; } = 20;
        public double FlapExtra { get; set; } = 10;
        public double TopDepth { get; set; } = 60;
        public double BottomDepth { get; set; } = 40;
        public double Clearance { get; set; } = 5;
        public double Allowance { get; set; } = 10;
        public double CornerRadius { get; set; } = 15;
        public double[] CornerRadii { get; set; }
        public bool Notches { get; set; } = true;
        public double NotchDepth { get; set; } = 1.5;
        public string Name { get; set; } = "Велосумка";
        internal FrameBagSetup Copy()
        {
            var copy = (FrameBagSetup)MemberwiseClone();
            copy.CornerRadii = CornerRadii == null ? null : (double[])CornerRadii.Clone();
            copy.DivisionLine = DivisionLine == null ? null : DivisionLine.Select(p => p == null ? null : (double[])p.Clone()).ToArray();
            return copy;
        }
    }

    internal sealed class FrameBagPart
    {
        internal string Name;
        internal BagPoint[] Seam, Cut;
        internal int Edge = -1, Side = -1, CornerCount;
        internal BagPoint[] CornerPoints = Array.Empty<BagPoint>();
        internal BagNotch[] Notches = Array.Empty<BagNotch>();
        internal BagPoint[][] Cutouts = Array.Empty<BagPoint[]>();
        internal BagPoint[][] Marks = Array.Empty<BagPoint[]>();
        internal double Left, Top, Width, Height;
        internal FrameBagPart CopyLayout() => (FrameBagPart)MemberwiseClone();
    }

    internal sealed class FrameBagPlan
    {
        internal FrameBagSetup Setup;
        internal BagPoint[] Frame, Bag;
        internal int[] Breaks;
        internal BagPoint[] SideSeam;
        internal FrameBagPart[] Parts;
        internal FrameBagPlan[] Sections = Array.Empty<FrameBagPlan>();
        internal BagPoint[] DivisionPath = Array.Empty<BagPoint>();
        internal double AvailableHeight, ActualHeight, MaximumRadius, VolumeLitres, KitWidth, KitHeight, LayoutTop;
    }

    // Dimensions are straight chords of the INSIDE opening. A = upper seat,
    // B = upper head, C = lower head, D = lower seat. Triangles use A/B/C
    // for upper seat/front/lower seat. Y points down from AB.
    internal static class FrameBagGeometry
    {
        internal static BagPoint[] Frame(FrameBagSetup setup)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            Positive(setup.Top, "Верхняя труба"); Positive(setup.Seat, "Подседельная");
            Positive(setup.Down, "Нижняя труба");
            if (setup.TriangleFrame)
            {
                Triangle(setup.Top, setup.Seat, setup.Down, "три стороны рамы");
                double rearX = (setup.Top * setup.Top + setup.Seat * setup.Seat - setup.Down * setup.Down) / (2 * setup.Top);
                double rearY = Math.Sqrt(Math.Max(0, setup.Seat * setup.Seat - rearX * rearX));
                var triangle = new[] { new BagPoint(0, 0), new BagPoint(setup.Top, 0), new BagPoint(rearX, rearY) };
                if (rearY < 1 || !Convex(triangle)) throw new ArgumentException("Треугольник рамы слишком плоский. Проверьте три стороны.");
                return triangle;
            }
            Positive(setup.Head, "Стакан"); Positive(setup.Diagonal, "Диагональ");
            Triangle(setup.Top, setup.Head, setup.Diagonal, "верхняя труба / стакан / диагональ");
            Triangle(setup.Seat, setup.Down, setup.Diagonal, "подседельная / нижняя труба / диагональ");
            double x = (setup.Top * setup.Top + setup.Diagonal * setup.Diagonal - setup.Head * setup.Head) / (2 * setup.Top);
            double y = Math.Sqrt(Math.Max(0, setup.Diagonal * setup.Diagonal - x * x));
            double along = (setup.Seat * setup.Seat + setup.Diagonal * setup.Diagonal - setup.Down * setup.Down) / (2 * setup.Diagonal);
            double across = Math.Sqrt(Math.Max(0, setup.Seat * setup.Seat - along * along));
            var c = new BagPoint(x, y);
            var d = new BagPoint((along * x - across * y) / setup.Diagonal,
                (along * y + across * x) / setup.Diagonal);
            var frame = new[] { new BagPoint(0, 0), new BagPoint(setup.Top, 0), c, d };
            if (y < 1 || d.Y < 1 || !Convex(frame))
                throw new ArgumentException("По замерам не получается выпуклый проём рамы. Проверьте диагональ и точки A–D.");
            return frame;
        }

        internal static FrameBagPlan Build(FrameBagSetup setup, bool closures = true)
        {
            if (setup == null) throw new ArgumentNullException(nameof(setup));
            var frame = Frame(setup);
            Range(setup.Clearance, 0, 100, "Зазор до рамы");
            Range(setup.Allowance, 0, 50, "Припуск на шов");
            Range(setup.Height, 10, 2000, "Высота сумки");
            Range(setup.TopDepth, 10, 300, "Глубина сверху");
            Range(setup.BottomDepth, 10, 300, "Глубина снизу");
            var inner = Opening(setup, frame);
            double top = inner.Min(p => p.Y), available = inner.Max(p => p.Y) - top;
            if (setup.SplitHeights) Range(setup.FrontHeight, 10, 2000, "Высота спереди");
            if (Math.Max(setup.Height, setup.SplitHeights ? setup.FrontHeight : setup.Height) > available + .001)
                throw new ArgumentException("Высота сумки больше проёма с учётом зазора: максимум " +
                    available.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " мм.");
            var sharp = Segment(inner, setup.Height, setup.SplitHeights ? setup.FrontHeight : setup.Height);
            Range(setup.CornerRadius, 0, 200, "Радиус углов");
            double maxRadius = MaximumRadius(sharp);
            var radii = Enumerable.Range(0, sharp.Length).Select(n => setup.CornerRadii != null && n < setup.CornerRadii.Length ? setup.CornerRadii[n] : setup.CornerRadius).ToArray();
            for (int n = 0; n < radii.Length; n++) Range(radii[n], 0, 200, "Радиус угла " + (char)('A' + n));
            var bag = Round(sharp, radii, out int[] breaks);
            if (bag.Length < 3 || Math.Abs(Area(bag)) < 100 || bag.Any(p => !Finite(p.X + p.Y)))
                throw new ArgumentException("Увеличьте высоту сумки или уменьшите зазор до рамы.");
            return FromOutline(setup, frame, bag, breaks, available, maxRadius, closures);
        }

        internal static FrameBagPlan FromOutline(FrameBagSetup setup, BagPoint[] frame, BagPoint[] bag, int[] breaks,
            double available, double maxRadius, bool closures)
        {
            double top = bag.Min(p => p.Y);
            double actualHeight = bag.Max(p => p.Y) - top;
            double slope = (setup.BottomDepth - setup.TopDepth) / actualHeight;
            // A tapered bag's side panels are inclined in 3D. Their unfolded
            // vertical measure is longer than the projected frame height.
            double sideStretch = Math.Sqrt(1 + slope * slope / 4);
            var side = bag.Select(p => new BagPoint(p.X, (p.Y - top) * sideStretch)).ToArray();
            var parts = new List<FrameBagPart>
            {
                Part("Боковина левая", side, setup.Allowance),
                Part("Боковина правая", side.Select(p => new BagPoint(-p.X, p.Y)).Reverse().ToArray(), setup.Allowance)
            };
            for (int n = 0; n < breaks.Length; n++)
            {
                var upper = new List<BagPoint>(); var lower = new List<BagPoint>();
                int first = breaks[n], last = breaks[(n + 1) % breaks.Length], index = first;
                double distance = 0;
                do
                {
                    if (index != first) distance += BagPoint.Sub(bag[index], bag[(index + bag.Length - 1) % bag.Length]).Length;
                    double width = setup.TopDepth + slope * (bag[index].Y - top);
                    upper.Add(new BagPoint(distance, -width / 2)); lower.Add(new BagPoint(distance, width / 2));
                    if (index == last) break;
                    index = (index + 1) % bag.Length;
                } while (true);
                lower.Reverse();
                var part = Part("Стенка " + (char)('A' + n) + "–" + (char)('A' + (n + 1) % breaks.Length), upper.Concat(lower).ToArray(), setup.Allowance);
                part.Edge = n; part.CornerCount = breaks.Length; parts.Add(part);
            }
            double centroidY = CentroidY(bag);
            double averageDepth = setup.TopDepth + slope * (centroidY - top);
            var result = new FrameBagPlan { Setup = setup.Copy(), Frame = frame, Bag = bag, Parts = parts.ToArray(),
                SideSeam = side, Breaks = breaks, MaximumRadius = maxRadius, AvailableHeight = available, ActualHeight = actualHeight, VolumeLitres = Area(bag) * averageDepth / 1000000 };
            result.Parts[0].Side = 0; result.Parts[1].Side = 1;
            FrameBagClosures.FixAnchors(result);
            if (setup.DivideBags) FrameBagDivision.EnsureLine(result);
            if (closures && setup.DivideBags) FrameBagDivision.Apply(result);
            else
            {
                if (closures) FrameBagClosures.Apply(result);
                FrameBagNotches.Apply(result);
                foreach (var part in result.Parts.Where(p => p.Side >= 0))
                    part.CornerPoints = breaks.Select(n => new BagPoint(part.Side == 0 ? side[n].X : -side[n].X, side[n].Y)).ToArray();
            }
            Arrange(result);
            return result;
        }

        internal static FrameBagPart Part(string name, BagPoint[] seam, double allowance)
        {
            var cut = CutOutline(seam, allowance);
            return new FrameBagPart { Name = name, Seam = seam, Cut = cut,
                Width = cut.Max(p => p.X) - cut.Min(p => p.X), Height = cut.Max(p => p.Y) - cut.Min(p => p.Y) };
        }

        private static void Arrange(FrameBagPlan plan)
        {
            if (plan.Sections.Length != 0)
            {
                double offset = 0;
                var pieces = new List<FrameBagPart>();
                foreach (var section in plan.Sections)
                {
                    section.LayoutTop = offset;
                    foreach (var source in section.Parts)
                    {
                        var part = source.CopyLayout(); part.Top += offset; pieces.Add(part);
                    }
                    offset += section.KitHeight + 30;
                }
                plan.Parts = pieces.ToArray(); plan.KitWidth = plan.Sections.Max(s => s.KitWidth); plan.KitHeight = offset - 30;
                return;
            }
            double x = 15, y = 25, row = 0, maxX = 0;
            const double width = 1200, gap = 25;
            foreach (var part in plan.Parts)
            {
                if (x > 15 && x + part.Width > width) { x = 15; y += row + gap; row = 0; }
                part.Left = x - part.Cut.Min(p => p.X); part.Top = y - part.Cut.Min(p => p.Y);
                maxX = Math.Max(maxX, x + part.Width);
                row = Math.Max(row, part.Height); x += part.Width + gap;
            }
            plan.KitWidth = maxX + 15; plan.KitHeight = y + row + 15;
        }

        private static double[] CornerFactors(BagPoint[] polygon) => Enumerable.Range(0, polygon.Length).Select(n =>
        {
            var u = BagPoint.Sub(polygon[n], polygon[(n + polygon.Length - 1) % polygon.Length]);
            var v = BagPoint.Sub(polygon[(n + 1) % polygon.Length], polygon[n]);
            return Math.Tan(Math.Atan2(BagPoint.Cross(u, v), u.X * v.X + u.Y * v.Y) / 2);
        }).ToArray();

        internal static double MaximumRadius(BagPoint[] polygon)
        {
            if (polygon.Length < 3) return 0;
            var factors = CornerFactors(polygon);
            return Enumerable.Range(0, polygon.Length).Min(n =>
                BagPoint.Sub(polygon[(n + 1) % polygon.Length], polygon[n]).Length /
                (factors[n] + factors[(n + 1) % polygon.Length])) * .999;
        }
        internal static double RadiusLimit(BagPoint[] polygon, double[] radii, int corner)
        {
            var factors = CornerFactors(polygon);
            int previous = (corner + polygon.Length - 1) % polygon.Length, next = (corner + 1) % polygon.Length;
            return Math.Max(0, Math.Min(
                (BagPoint.Sub(polygon[corner], polygon[previous]).Length * .999 - radii[previous] * factors[previous]) / factors[corner],
                (BagPoint.Sub(polygon[next], polygon[corner]).Length * .999 - radii[next] * factors[next]) / factors[corner]));
        }
        internal static double[] FitRadii(BagPoint[] polygon, double[] radii)
        {
            var result = (double[])radii.Clone(); var factors = CornerFactors(polygon);
            for (int n = 0; n < polygon.Length; n++)
            {
                int next = (n + 1) % polygon.Length;
                double used = result[n] * factors[n] + result[next] * factors[next];
                double available = BagPoint.Sub(polygon[next], polygon[n]).Length * .999;
                if (used > available) { result[n] *= available / used; result[next] *= available / used; }
            }
            return result;
        }
        internal static BagPoint[] Round(BagPoint[] polygon, double radius, out int[] breaks) =>
            Round(polygon, Enumerable.Repeat(radius, polygon.Length).ToArray(), out breaks);

        internal static BagPoint[] Round(BagPoint[] polygon, double[] radii, out int[] breaks)
        {
            var factors = CornerFactors(polygon);
            for (int n = 0; n < polygon.Length; n++)
                if (radii[n] * factors[n] + radii[(n + 1) % polygon.Length] * factors[(n + 1) % polygon.Length] >
                    BagPoint.Sub(polygon[(n + 1) % polygon.Length], polygon[n]).Length * .999 + .00001)
                    throw new ArgumentException("Скругления углов " + (char)('A' + n) + " и " + (char)('A' + (n + 1) % polygon.Length) + " не помещаются на стороне. Уменьшите радиусы.");
            var points = new List<BagPoint>(); breaks = new int[polygon.Length];
            for (int n = 0; n < polygon.Length; n++)
            {
                double radius = radii[n];
                var vertex = polygon[n];
                if (radius <= 0) { breaks[n] = points.Count; points.Add(vertex); continue; }
                var u = BagPoint.Sub(vertex, polygon[(n + polygon.Length - 1) % polygon.Length]);
                var v = BagPoint.Sub(polygon[(n + 1) % polygon.Length], vertex);
                u = BagPoint.Mul(u, 1 / u.Length); v = BagPoint.Mul(v, 1 / v.Length);
                double turn = Math.Atan2(BagPoint.Cross(u, v), u.X * v.X + u.Y * v.Y);
                double tangent = radius * Math.Tan(turn / 2);
                var entry = BagPoint.Sub(vertex, BagPoint.Mul(u, tangent));
                var center = BagPoint.Add(entry, BagPoint.Mul(new BagPoint(-u.Y, u.X), radius));
                double angle = Math.Atan2(entry.Y - center.Y, entry.X - center.X);
                int segments = Math.Max(2, (int)Math.Ceiling(turn / Math.Max(.02, 2 * Math.Acos(Math.Max(-1, 1 - .005 / radius)))));
                if (segments % 2 != 0) segments++;
                breaks[n] = points.Count + segments / 2;
                for (int k = 0; k <= segments; k++)
                {
                    double at = angle + turn * k / segments;
                    points.Add(new BagPoint(center.X + radius * Math.Cos(at), center.Y + radius * Math.Sin(at)));
                }
            }
            return points.ToArray();
        }

        // Widening and union handle concave edges of tapered unfolded walls;
        // a naive miter offset could self-intersect and create extra laser cuts.
        internal static BagPoint[] CutOutline(BagPoint[] seam, double allowance)
        {
            if (allowance == 0) return (BagPoint[])seam.Clone();
            var outline = new StreamGeometry { FillRule = FillRule.Nonzero };
            using (var path = outline.Open())
            {
                path.BeginFigure(new Point(seam[0].X, seam[0].Y), true, true);
                foreach (var point in seam.Skip(1)) path.LineTo(new Point(point.X, point.Y), true, false);
            }
            outline.Freeze();
            var widened = outline.GetWidenedPathGeometry(new Pen(Brushes.Black, allowance * 2) { LineJoin = PenLineJoin.Miter, MiterLimit = 5 },
                .005, ToleranceType.Absolute);
            var union = Geometry.Combine(outline, widened, GeometryCombineMode.Union, null, .005, ToleranceType.Absolute)
                .GetFlattenedPathGeometry(.005, ToleranceType.Absolute);
            if (union.Figures.Count != 1 || !union.Figures[0].IsClosed)
                throw new ArgumentException("Не удалось построить один замкнутый контур припуска.");
            var result = new List<BagPoint>();
            var figure = union.Figures[0];
            result.Add(new BagPoint(figure.StartPoint.X, figure.StartPoint.Y));
            foreach (var segment in figure.Segments)
            {
                if (segment is PolyLineSegment line)
                    foreach (var point in line.Points) result.Add(new BagPoint(point.X, point.Y));
                else if (segment is LineSegment straight) result.Add(new BagPoint(straight.Point.X, straight.Point.Y));
            }
            if (result.Count > 1 && BagPoint.Sub(result[0], result[result.Count - 1]).Length < .00001) result.RemoveAt(result.Count - 1);
            if (Area(result.ToArray()) < 0) result.Reverse();
            return result.ToArray();
        }

        internal static BagPoint[] Offset(BagPoint[] polygon, double inward)
        {
            if (polygon.Length < 3 || !Convex(polygon)) throw new ArgumentException("Контур выкройки вырожден.");
            if (inward == 0) return (BagPoint[])polygon.Clone();
            int count = polygon.Length;
            var result = new BagPoint[count];
            for (int n = 0; n < count; n++)
            {
                int prev = (n + count - 1) % count;
                var u = BagPoint.Sub(polygon[n], polygon[prev]);
                var v = BagPoint.Sub(polygon[(n + 1) % count], polygon[n]);
                var p = BagPoint.Add(polygon[prev], BagPoint.Mul(new BagPoint(-u.Y, u.X), inward / u.Length));
                var q = BagPoint.Add(polygon[n], BagPoint.Mul(new BagPoint(-v.Y, v.X), inward / v.Length));
                double denominator = BagPoint.Cross(u, v);
                if (Math.Abs(denominator) < 1e-8) throw new ArgumentException("Соседние стороны почти совпадают.");
                result[n] = BagPoint.Add(p, BagPoint.Mul(u, BagPoint.Cross(BagPoint.Sub(q, p), v) / denominator));
            }
            if (!Convex(result) || result.Any(p => !Finite(p.X + p.Y)) ||
                result.Any(p => Enumerable.Range(0, count).Any(n => SignedDistance(polygon[n], polygon[(n + 1) % count], p) < inward - .00001)))
                throw new ArgumentException("Зазор слишком большой: внутренний контур исчезает.");
            return result;
        }

        internal static BagPoint[] Opening(FrameBagSetup setup, BagPoint[] frame)
        {
            var inner = Offset(frame, setup.Clearance);
            Range(setup.BagLength, 0, 3000, "Длина сумки");
            if (setup.BagLength == 0) return inner;
            double available = inner[1].X - inner[0].X;
            if (setup.BagLength < 10 || setup.BagLength > available + .001)
                throw new ArgumentException("Длина сумки по верху: от 10 до " + available.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + " мм.");
            double x = setup.AttachToFront ? inner[1].X - setup.BagLength : inner[0].X + setup.BagLength;
            var opening = ClipLine(inner, new BagPoint(x, -10000), new BagPoint(x, 10000), setup.AttachToFront);
            if (opening.Length < 3 || Area(opening) < 100) throw new ArgumentException("Для такой длины не остаётся места в раме.");
            return opening;
        }

        internal static BagPoint[] Segment(BagPoint[] inner, double rearHeight, double frontHeight)
        {
            double top = inner.Min(p => p.Y);
            if (Math.Abs(rearHeight - frontHeight) < .00001) return ClipBelow(inner, top + rearHeight);
            var rear = Row(inner, top + rearHeight).OrderBy(p => p.X).First();
            var front = Row(inner, top + frontHeight).OrderByDescending(p => p.X).First();
            if (front.X - rear.X < 1) throw new ArgumentException("Высоты спереди и сзади не образуют пригодное дно.");
            return ClipLine(inner, rear, front, true);
        }

        private static IEnumerable<BagPoint> Row(BagPoint[] polygon, double y)
        {
            for (int n = 0; n < polygon.Length; n++)
            {
                var a = polygon[n]; var b = polygon[(n + 1) % polygon.Length];
                if (Math.Abs(a.Y - y) < .000001) yield return new BagPoint(a.X, y);
                if ((a.Y < y && b.Y > y) || (a.Y > y && b.Y < y))
                    yield return new BagPoint(a.X + (b.X - a.X) * (y - a.Y) / (b.Y - a.Y), y);
            }
        }

        internal static BagPoint[] ClipLine(BagPoint[] polygon, BagPoint a, BagPoint b, bool upper)
        {
            var direction = BagPoint.Sub(b, a);
            var result = new List<BagPoint>();
            var previous = polygon[polygon.Length - 1];
            double prior = BagPoint.Cross(direction, BagPoint.Sub(previous, a)) * (upper ? 1 : -1);
            foreach (var point in polygon)
            {
                double value = BagPoint.Cross(direction, BagPoint.Sub(point, a)) * (upper ? 1 : -1);
                if ((prior <= 0) != (value <= 0))
                    result.Add(BagPoint.Add(previous, BagPoint.Mul(BagPoint.Sub(point, previous), prior / (prior - value))));
                if (value <= 0) result.Add(point);
                previous = point; prior = value;
            }
            return Clean(result.ToArray());
        }

        internal static BagPoint[] Clean(BagPoint[] polygon)
        {
            var result = polygon.ToList();
            for (int n = result.Count - 1; n >= 0 && result.Count > 2; n--)
                if (BagPoint.Sub(result[n], result[(n + 1) % result.Count]).Length < .00001) result.RemoveAt(n);
            return result.ToArray();
        }

        internal static BagPoint[] ClipBelow(BagPoint[] polygon, double bottom)
        {
            var result = new List<BagPoint>();
            var previous = polygon[polygon.Length - 1];
            foreach (var current in polygon)
            {
                bool from = previous.Y <= bottom, to = current.Y <= bottom;
                if (from != to)
                {
                    double ratio = (bottom - previous.Y) / (current.Y - previous.Y);
                    result.Add(new BagPoint(previous.X + ratio * (current.X - previous.X), bottom));
                }
                if (to) result.Add(current);
                previous = current;
            }
            // Hitting an existing vertex must not create a zero-length wall.
            for (int n = result.Count - 1; n >= 0 && result.Count > 1; n--)
                if (BagPoint.Sub(result[n], result[(n + 1) % result.Count]).Length < .00001) result.RemoveAt(n);
            return result.ToArray();
        }

        internal static double Area(BagPoint[] polygon) => Enumerable.Range(0, polygon.Length)
            .Sum(n => BagPoint.Cross(polygon[n], polygon[(n + 1) % polygon.Length])) / 2;
        internal static double SignedDistance(BagPoint a, BagPoint b, BagPoint p) =>
            BagPoint.Cross(BagPoint.Sub(b, a), BagPoint.Sub(p, a)) / BagPoint.Sub(b, a).Length;
        internal static double Perimeter(BagPoint[] polygon) => Enumerable.Range(0, polygon.Length)
            .Sum(n => BagPoint.Sub(polygon[(n + 1) % polygon.Length], polygon[n]).Length);
        internal static double CentroidY(BagPoint[] polygon) => Enumerable.Range(0, polygon.Length).Sum(n =>
            (polygon[n].Y + polygon[(n + 1) % polygon.Length].Y) * BagPoint.Cross(polygon[n], polygon[(n + 1) % polygon.Length])) / (6 * Area(polygon));
        private static bool Convex(BagPoint[] polygon) => polygon.Length >= 3 &&
            Enumerable.Range(0, polygon.Length).All(n => BagPoint.Cross(BagPoint.Sub(polygon[(n + 1) % polygon.Length], polygon[n]),
                BagPoint.Sub(polygon[(n + 2) % polygon.Length], polygon[(n + 1) % polygon.Length])) > .0001);
        private static bool Finite(double n) => !Double.IsNaN(n) && !Double.IsInfinity(n);
        private static void Positive(double value, string name) => Range(value, 1, 3000, name);
        private static void Range(double value, double min, double max, string name)
        { if (!Finite(value) || value < min || value > max) throw new ArgumentException(name + ": от " + min + " до " + max + " мм."); }
        private static void Triangle(double a, double b, double c, string name)
        {
            if (a + b <= c + .01 || a + c <= b + .01 || b + c <= a + .01)
                throw new ArgumentException("Несовместимые замеры: " + name + ". Проверьте длины по одним и тем же точкам.");
        }
    }
}
