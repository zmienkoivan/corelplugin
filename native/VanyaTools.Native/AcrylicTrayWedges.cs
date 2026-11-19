using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal sealed class AcrylicTrayWedge
    {
        public int Number, FloorIndex, TabIndex;
        public string Id, ParentId;
        public bool Side;
        public Geometry Shape, Opening, GripOpening;
        public Point[] Outline;
        public Point[][] ConvexPieces;
        public double StemLength, HeadWidth, HeadLength, GripWidth, GripHeight, TipWidth, NeckWidth, PassingThickness;
        public Point3D Origin;
        public Vector3D U, V;
        public Vector3D W { get { return Vector3D.CrossProduct(U, -V); } }
        public Point3D Point(double u, double v, double w) { return Origin + U * u + V * v + W * w; }
    }

    internal sealed partial class AcrylicTrayPattern
    {
        public readonly List<AcrylicTrayWedge> Wedges = new List<AcrylicTrayWedge>();
        private double _lockingTabNormal, _keyClearanceNormal, _keyTip, _eyeletRadius;
        private double _capWidth, _capLength, _gripWidth, _gripHeight, _windowMiddle;
        public double SideLugLength { get; private set; }
        public double SideLugWidth { get; private set; }
        public double SideLugRadius { get; private set; }
        public double SideLugProjection { get { return SideLugLength - Options.Thickness; } }
        public bool IsLock(int index, int count)
        {
            return Options.UseWedges && (index == 0 || index == count - 1 || index % Options.WedgeEvery == 0);
        }
        private void ConfigureWedges(double angle)
        {
            if (!Options.UseWedges) return;
            var o = Options; double t = o.Thickness, gap = o.RetainerThicknessClearance;
            if (o.WedgeEvery < 1 || o.WedgeEvery > 20) throw new InvalidOperationException("Шаг фиксаторов: от 1 до 20 шипов.");
            Range(gap, 0, 0.5, "Зазор окна грибочка по толщине");
            Range(o.RetainerWindowLength, t + 0.2, 100, "Длина окна грибочка");
            Range(o.RetainerTabWidth, o.RetainerWindowLength + 2 * t, 150, "Ширина ушка");
            _capWidth = o.RetainerWindowLength + 2 * t;
            _gripWidth = Math.Min(6, o.RetainerWindowLength);
            _gripHeight = Math.Min(2, t);
            _capLength = 2 * t + _gripHeight;
            // A closed grip window leaves one sheet-thickness of material on
            // every side. Reserve room between the crossing mushroom heads.
            double headSpacing = _capWidth + 2 * _capLength + 0.8 * t + gap;
            Range(o.CellWidth, Math.Max(Math.Max(6 * t + 2, o.RetainerTabWidth + 2 * t + o.SlotClearance), headSpacing), 300, "Ширина ячейки для фиксаторов");
            Range(o.CellDepth, Math.Max(6 * t + 2, o.RetainerTabWidth), 300, "Глубина ячейки для фиксаторов");
            // Keep the requested window envelope exact (3.2 x 10 for 3 mm).
            // The corner radius fits entirely in the sheet-thickness clearance,
            // so a broad flat stem can pass without oversizing the opening.
            _eyeletRadius = gap / 2;
            _keyTip = o.RetainerWindowLength - 0.1 * t;
            // Bottom keys lie parallel to the floor. Their nearest broad face
            // sits just below the underside, leaving only half the slot gap.
            _windowMiddle = 1.5 * t + gap / 2;
            _lockingTabNormal = _windowMiddle + (t + gap) / 2 + t;
            // Preserve the existing supporting cheek profile and floor height.
            _keyClearanceNormal = Math.Max(_lockingTabNormal, 3.5 * t + o.SlotClearance);
            _longTabWidth = _crossTabWidth = o.RetainerTabWidth;
            SideLugWidth = o.RetainerTabWidth;
            SideLugLength = 3 * t + gap;
            SideLugRadius = Math.Min(t / 2, SideLugWidth / 4);
        }
        private Geometry LugProfile(double length, double width)
        {
            double r = Math.Min(Options.Thickness / 2, Math.Min(width / 4, length / 5)), half = width / 2;
            var shape = new StreamGeometry();
            using (var c = shape.Open())
            {
                c.BeginFigure(new Point(-0.01, -half), true, true);
                c.LineTo(new Point(length - r, -half), true, false);
                c.ArcTo(new Point(length, -half + r), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(length, half - r), true, false);
                c.ArcTo(new Point(length - r, half), new Size(r, r), 0, false, SweepDirection.Clockwise, true, true);
                c.LineTo(new Point(-0.01, half), true, false);
            }
            return shape.GetFlattenedPathGeometry(0.002, ToleranceType.Absolute);
        }
        public Geometry SideLugCutout()
        {
            if (!Options.UseWedges) throw new InvalidOperationException("Включите фиксаторы-грибочки.");
            var hole = SideEyelet(0, 1).Clone();
            hole.Transform = new TranslateTransform(-InnerWidth, 0);
            return Difference(LugProfile(SideLugLength, SideLugWidth), hole);
        }
        public Rect SideLugOpeningBounds()
        {
            Rect bounds = SideEyelet(0, 1).Bounds; bounds.Offset(-InnerWidth, 0); return bounds;
        }
        private Geometry SideLug(double s, int side)
        {
            var shape = LugProfile(SideLugLength, SideLugWidth);
            shape.Transform = new MatrixTransform(new Matrix(side, 0, 0, 1, side < 0 ? 0 : InnerWidth, s));
            return shape;
        }
        private Geometry CrossLug(double x, double bottom, double depth)
        {
            var shape = LugProfile(depth, _crossTabWidth);
            shape.Transform = new MatrixTransform(new Matrix(0, 1, 1, 0, x, -bottom));
            return shape;
        }
        private Geometry LongLug(AcrylicTrayFloor f, double y, double depth)
        {
            double y1 = y - _longTabWidth / 2, y2 = y + _longTabWidth / 2;
            var a = new Point(y2, -f.Top(y2) + depth / f.Cosine);
            var b = new Point(y1, -f.Top(y1) + depth / f.Cosine);
            double r = Math.Min(Options.Thickness / 2, _longTabWidth / 4);
            var along = new Vector(-f.Cosine, f.Sine);
            var shape = new StreamGeometry();
            using (var c = shape.Open())
            {
                c.BeginFigure(new Point(y1, -f.Top(y1) - 0.01), true, true);
                c.LineTo(new Point(y2, -f.Top(y2) - 0.01), true, false);
                c.LineTo(a - new Vector(0, r), true, false);
                c.QuadraticBezierTo(a, a + along * r, true, true);
                c.LineTo(b - along * r, true, false);
                c.QuadraticBezierTo(b, b - new Vector(0, r), true, true);
            }
            return shape.GetFlattenedPathGeometry(0.002, ToleranceType.Absolute);
        }
        private Geometry BottomEyelet(AcrylicTrayFloor f, double y, double x, bool cross)
        {
            double t = Options.Thickness, gap = Options.RetainerThicknessClearance;
            double z = f.Top(y) - _windowMiddle / f.Cosine;
            double width = Options.RetainerWindowLength, normalLength = t + gap;
            if (cross)
                return new RectangleGeometry(new Rect(x - width / 2, -z - (normalLength / f.Cosine + t * f.Slope) / 2,
                    width, normalLength / f.Cosine + t * f.Slope), _eyeletRadius, _eyeletRadius / f.Cosine);
            var hole = new RectangleGeometry(new Rect(-width / 2, -normalLength / 2,
                width, normalLength), _eyeletRadius, _eyeletRadius);
            hole.Transform = new MatrixTransform(new Matrix(f.Cosine, -f.Sine, f.Sine, f.Cosine, y, -z));
            return hole;
        }
        private Geometry SideEyelet(double s, int side)
        {
            double t = Options.Thickness, gap = Options.RetainerThicknessClearance;
            double x = side < 0 ? -1.5 * t - gap / 2 : InnerWidth + 1.5 * t + gap / 2;
            double width = Options.RetainerWindowLength;
            return new RectangleGeometry(new Rect(x - (t + gap) / 2, s - width / 2,
                t + gap, width), _eyeletRadius, _eyeletRadius);
        }
        private AcrylicTrayWedge NewWedge(string parent, int floor, int index, double passingThickness, double headAllowance = 0)
        {
            double t = Options.Thickness;
            // A single rigid stem, with a short rounded head and no spring
            // arms. Keep only 0.4t beyond each receiving face. The 3 mm side
            // key keeps its broad 10 mm stem. Only the head grows to provide
            // a closed extraction window with solid surrounding material.
            // Anchor the taper at the NEAR face, so longer oblique passages
            // remain clear rather than acquiring an oversized neck inside.
            double allowance = 0.4 * t;
            headAllowance = Math.Max(allowance, headAllowance);
            double start = -passingThickness / 2 - headAllowance;
            double end = passingThickness / 2 + allowance;
            double stemLength = end - start;
            double nearWidth = Options.RetainerWindowLength - 0.01;
            double taper = (nearWidth - _keyTip) / (passingThickness + allowance);
            double neckWidth = nearWidth + taper * headAllowance;
            double center = _capWidth / 2;
            var stem = new[] { new Point(start, center - neckWidth / 2), new Point(end, center - _keyTip / 2),
                new Point(end, center + _keyTip / 2), new Point(start, center + neckWidth / 2) };
            Geometry cap = new RectangleGeometry(new Rect(start - _capLength, 0, _capLength + 0.01, _capWidth),
                t / 2, t / 2);
            // A 0.002 mm chord tolerance keeps the rounded head far finer than
            // the laser kerf while keeping the laser contours manageable.
            cap = cap.GetFlattenedPathGeometry(0.002, ToleranceType.Absolute);
            var capLoop = LaserBendGeometry.Loops(cap)[0];
            Geometry shape = Union(Polygon(stem), cap);
            var outline = LaserBendGeometry.Loops(shape).OrderByDescending(loop => loop.Length).First();
            // Remove the Boolean join's zero-area backtracks before writing
            // laser contours. They can otherwise become self-touches after SVG
            // coordinate rounding or translation far from the sheet origin.
            shape = Polygon(outline);
            Geometry grip = new RectangleGeometry(new Rect(start - (_capLength + _gripHeight) / 2,
                (_capWidth - _gripWidth) / 2, _gripHeight, _gripWidth), _gripHeight / 2, _gripHeight / 2);
            grip = grip.GetFlattenedPathGeometry(0.002, ToleranceType.Absolute);
            shape = Difference(shape, grip);
            int number = Wedges.Count + 1;
            var result = new AcrylicTrayWedge { Id = "wedge-" + number, Number = number, ParentId = parent,
                FloorIndex = floor, TabIndex = index, Outline = outline, Shape = shape, GripOpening = grip,
                // Conservative collision envelopes deliberately fill the grip
                // hole: every neighbouring panel must clear the whole head.
                ConvexPieces = new[] { stem, capLoop }, StemLength = stemLength, HeadWidth = _capWidth,
                HeadLength = _capLength, GripWidth = _gripWidth, GripHeight = _gripHeight,
                TipWidth = _keyTip, NeckWidth = neckWidth, PassingThickness = passingThickness };
            Wedges.Add(result); return result;
        }
        private void AddBottomWedge(string parent, int floor, int index, double x, double y, bool cross)
        {
            var f = Floors[floor]; double t = Options.Thickness;
            // Only the sheet thickness, not the cap width, sweeps through the
            // oblique cross-strip now that the entire key is in the floor plane.
            double passing = cross ? t / f.Cosine + t * f.Slope : t;
            var key = NewWedge(parent, floor, index, passing);
            key.U = cross ? new Vector3D(0, f.Cosine, f.Sine) : new Vector3D(1, 0, 0);
            key.V = cross ? new Vector3D(-1, 0, 0) : new Vector3D(0, f.Cosine, f.Sine);
            var center = new Point3D(x, y, f.Top(y) - _windowMiddle / f.Cosine);
            key.Origin = center - key.V * (_capWidth / 2);
            key.Opening = BottomEyelet(f, y, x, cross); key.Opening.Freeze();
        }
        private void AddSideWedge(string parent, int floor, int index, double s, int side, Geometry hole)
        {
            var f = Floors[floor]; double t = Options.Thickness, gap = Options.RetainerThicknessClearance;
            var key = NewWedge(parent, floor, index, t);
            var down = new Vector3D(0, f.Sine, -f.Cosine);
            double x = side < 0 ? -1.5 * t - gap / 2 : InnerWidth + 1.5 * t + gap / 2;
            double anchor = s - _capWidth / 2;
            key.Origin = new Point3D(x, f.StartY + anchor * f.Cosine, f.FrontZ + anchor * f.Sine) + down * (t / 2);
            // Rotate the complete flat key 90 degrees around its insertion
            // axis: its broad head now lies PARALLEL to the supporting cheek.
            // The sheet thickness bears against that cheek's outside face.
            key.U = down; key.V = new Vector3D(0, f.Cosine, f.Sine); key.Side = true;
            key.Opening = hole; key.Opening.Freeze();
        }
        private void IncludeWedgeBounds()
        {
            if (!Options.UseWedges) return;
            OverallWidth = InnerWidth + 2 * SideLugLength;
            double minY = Floors[0].StartY, maxY = Floors.Last().EndY + Options.Thickness * Floors.Last().Sine;
            foreach (var key in Wedges) foreach (var point in key.Outline) foreach (double w in new[] { -Options.Thickness / 2, Options.Thickness / 2 })
            {
                var p = key.Point(point.X, point.Y, w);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); Height = Math.Max(Height, p.Z);
                if (p.Z < -0.001) throw new InvalidOperationException("Фиксатор касается стола. Увеличьте размер ячейки или уменьшите наклон.");
            }
            OverallDepth = maxY - minY;
        }
    }
}
