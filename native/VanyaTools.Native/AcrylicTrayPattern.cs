using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal enum AcrylicTrayFloorMode { Plane, Steps }

    internal sealed class AcrylicTrayOptions
    {
        public AcrylicTrayFloorMode FloorMode { get; set; } = AcrylicTrayFloorMode.Plane;
        public int Columns { get; set; } = 4;
        public int Rows { get; set; } = 3;
        public double CellWidth { get; set; } = 40;
        public double CellDepth { get; set; } = 20;
        public double BoardHeight { get; set; } = 15;
        public double RearWallHeight { get; set; } = 30;
        public double AngleDegrees { get; set; } = 20;
        public double RowRise { get; set; } = 15;
        public double Thickness { get; set; } = 3;
        public bool DecorativeSides { get; set; } = true;
        public bool UseWedges { get; set; }
        public int WedgeEvery { get; set; } = 3;
        public double RetainerTabWidth { get; set; } = 20;
        public double RetainerWindowLength { get; set; } = 10;
        public double RetainerThicknessClearance { get; set; } = 0.2;
        // Total extra opening, not a laser kerf offset.
        public double SlotClearance { get; set; } = 0.1;
        public AcrylicTrayOptions Copy() { return (AcrylicTrayOptions)MemberwiseClone(); }
    }

    internal sealed class AcrylicTrayFloor
    {
        public double StartY, EndY, FrontZ, Slope, Cosine, Sine;
        public readonly List<Rect> Slots = new List<Rect>();
        public double Top(double y) { return FrontZ + (y - StartY) * Slope; }
        public double Length { get { return (EndY - StartY) / Cosine; } }
        public double SurfacePosition(double y) { return (y - StartY) / Cosine; }
    }

    internal sealed class AcrylicTrayCross
    {
        public double Y, Bottom, JointZ, Height;
        public int FloorIndex;
    }

    internal sealed class AcrylicTrayPart
    {
        public string Id, Name, Kind, Path;
        public double Width, Height;
        // Assembly coordinates: vertical panels use (y,-z) or (x,-z).
        public Geometry AssemblyGeometry, Geometry;
        public Rect AssemblyBounds;
        public AcrylicTrayPart(string id, string name, string kind, Geometry geometry)
        {
            Id = id; Name = name; Kind = kind;
            AssemblyGeometry = geometry.GetFlattenedPathGeometry(0.0001, ToleranceType.Absolute);
            AssemblyBounds = AssemblyGeometry.Bounds;
            if (AssemblyGeometry.IsEmpty() || AssemblyBounds.Width <= 0 || AssemblyBounds.Height <= 0)
                throw new InvalidOperationException("Пустая деталь: " + name);
            Width = AssemblyBounds.Width; Height = AssemblyBounds.Height;
            Geometry = AssemblyGeometry.Clone();
            Geometry.Transform = new TranslateTransform(-AssemblyBounds.X, -AssemblyBounds.Y);
            Path = AcrylicTrayPattern.SvgPath(Geometry);
            Geometry.Freeze(); AssemblyGeometry.Freeze();
        }
    }

    internal sealed partial class AcrylicTrayPattern
    {
        public AcrylicTrayOptions Options { get; private set; }
        public double InnerWidth, OverallWidth, Depth, OverallDepth, Height, Margin, Angle;
        public double CrossSlotWidth, CrossTabDepth;
        public readonly List<AcrylicTrayFloor> Floors = new List<AcrylicTrayFloor>();
        public readonly List<AcrylicTrayCross> Crosses = new List<AcrylicTrayCross>();
        public readonly List<AcrylicTrayPart> Parts = new List<AcrylicTrayPart>();
        public readonly List<double> Longitudinals = new List<double>();
        private readonly List<Tuple<int, double>> _tabs = new List<Tuple<int, double>>();
        private double _longTabWidth, _crossTabWidth, _sideTabHeight;

        public static AcrylicTrayPattern Build(AcrylicTrayOptions input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            var p = new AcrylicTrayPattern { Options = input.Copy() };
            var o = p.Options;
            if (!Enum.IsDefined(typeof(AcrylicTrayFloorMode), o.FloorMode))
                throw new InvalidOperationException("Выберите конструкцию дна.");
            if (o.Columns < 1 || o.Columns > 20 || o.Rows < 1 || o.Rows > 20)
                throw new InvalidOperationException("Число рядов и столбцов: от 1 до 20.");
            Range(o.Thickness, 1, 10, "Толщина акрила");
            Range(o.SlotClearance, 0, 0.5, "Зазор пазов");
            Range(o.CellWidth, 3 * o.Thickness + 2, 300, "Ширина ячейки");
            Range(o.CellDepth, 3 * o.Thickness + 2, 300, "Глубина ячейки");
            // Inactive controls cannot invalidate the current construction.
            p.Angle = o.FloorMode == AcrylicTrayFloorMode.Plane ? o.AngleDegrees : 0;
            Range(p.Angle, 0, 60, "Угол дна");
            if (o.FloorMode == AcrylicTrayFloorMode.Steps) Range(o.RowRise, 0, 150, "Подъём ряда");
            double a = p.Angle * Math.PI / 180, t = o.Thickness;
            Range(o.BoardHeight, 2 * t + t * Math.Tan(a) + o.SlotClearance + 1, 200, "Высота борта");
            Range(o.RearWallHeight, o.BoardHeight, 200, "Высота задней стенки");
            p.Margin = 2 * t;
            p.InnerWidth = o.Columns * o.CellWidth + (o.Columns - 1) * t;
            p.OverallWidth = p.InnerWidth + 2 * t;
            p._longTabWidth = Math.Min(16, o.CellDepth * 0.45);
            p._crossTabWidth = Math.Min(16, o.CellWidth * 0.45);
            p._sideTabHeight = Math.Min(8, o.BoardHeight * 0.2);
            p.ConfigureWedges(a);
            // A perpendicular laser slot must clear the swept intersection across
            // the ENTIRE floor thickness, not just t / cos(a) on one face.
            p.CrossTabDepth = t / Math.Cos(a) + t * Math.Tan(a);
            p.CrossSlotWidth = p.CrossTabDepth + o.SlotClearance;
            for (int c = 1; c < o.Columns; c++)
                p.Longitudinals.Add(c * (o.CellWidth + t) - t / 2);

            if (o.FloorMode == AcrylicTrayFloorMode.Plane)
            {
                double gridDepth = o.Rows * o.CellDepth + (o.Rows + 1) * t;
                p.Floors.Add(new AcrylicTrayFloor { StartY = -p.Margin, EndY = gridDepth + p.Margin,
                    FrontZ = o.UseWedges ? p._keyClearanceNormal / Math.Cos(a) + 2 * t : 2 * t + t / Math.Cos(a),
                    Slope = Math.Tan(a), Cosine = Math.Cos(a), Sine = Math.Sin(a) });
                for (int r = 0; r <= o.Rows; r++) p.AddCross(0, t / 2 + r * (o.CellDepth + t));
                for (int r = 0; r < o.Rows; r++)
                    p._tabs.Add(Tuple.Create(0, t + o.CellDepth / 2 + r * (o.CellDepth + t)));
            }
            else
            {
                double length = o.CellDepth + 2 * t + 2 * p.Margin;
                for (int r = 0; r < o.Rows; r++)
                {
                    double start = r * (length + 2 * t);
                    p.Floors.Add(new AcrylicTrayFloor { StartY = start, EndY = start + length,
                        FrontZ = (o.UseWedges ? p._keyClearanceNormal + 2 * t : 3 * t) + r * o.RowRise, Cosine = 1 });
                    p.AddCross(r, start + p.Margin + t / 2);
                    p.AddCross(r, start + p.Margin + 1.5 * t + o.CellDepth);
                    p._tabs.Add(Tuple.Create(r, start + p.Margin + t + o.CellDepth / 2));
                }
            }
            // Only the final outer wall is taller. Keep the interlocking lap
            // and side tenons at the common board height.
            p.Crosses.Last().Height = o.RearWallHeight;
            p.Depth = p.Floors.Last().EndY - p.Floors[0].StartY;
            p.OverallDepth = p.Depth + t * Math.Sin(a);
            p.Height = Math.Max(p.Floors.Last().Top(p.Floors.Last().EndY) + o.BoardHeight,
                p.Crosses.Last().Bottom + o.RearWallHeight);
            if (Math.Max(p.Depth, p.OverallWidth) > 3000 || p.Height > 3000)
                throw new InvalidOperationException("Габарит детали превышает 3000 мм. Уменьшите размер или число ячеек.");
            p.CreateParts();
            p.IncludeWedgeBounds();
            return p;
        }

        private void AddCross(int floorIndex, double y)
        {
            var f = Floors[floorIndex];
            double bottom = f.Top(y + Options.Thickness / 2);
            Crosses.Add(new AcrylicTrayCross { FloorIndex = floorIndex, Y = y,
                Bottom = bottom, JointZ = bottom + Options.BoardHeight / 2, Height = Options.BoardHeight });
        }

        private void CreateParts()
        {
            double t = Options.Thickness, h = Options.BoardHeight, gap = Options.SlotClearance;
            // WPF's polygon clipper quantizes coordinates. Join each tenon a
            // little INSIDE its parent to avoid coincident-edge loose islands.
            const double overlap = 0.01;
            Geometry longShape = null, sideShape = null;
            for (int i = 0; i < Floors.Count; i++)
            {
                var f = Floors[i];
                longShape = Union(longShape, Band(f.StartY, f.EndY, f.Top(f.StartY), f.Top(f.EndY), h));
                sideShape = Union(sideShape, Polygon(new Point(f.StartY, 0), new Point(f.EndY, 0),
                    new Point(f.EndY, -f.Top(f.EndY) - h), new Point(f.StartY, -f.Top(f.StartY) - h)));
                if (i > 0)
                {
                    var prev = Floors[i - 1];
                    longShape = Union(longShape, Box(prev.EndY, -f.FrontZ - h,
                        f.StartY - prev.EndY, f.FrontZ + h - prev.Top(prev.EndY)));
                    sideShape = Union(sideShape, Box(prev.EndY, -f.FrontZ - h,
                        f.StartY - prev.EndY, f.FrontZ + h));
                }
            }
            if (Options.DecorativeSides) sideShape = DisplaySide();
            for (int tabIndex = 0; tabIndex < _tabs.Count; tabIndex++)
            {
                var tab = _tabs[tabIndex];
                var f = Floors[tab.Item1]; double y = tab.Item2;
                bool locked = IsLock(tabIndex, _tabs.Count);
                double depth = locked ? _lockingTabNormal : t;
                longShape = Union(longShape, locked ? LongLug(f, y, depth) : Band(y - _longTabWidth / 2, y + _longTabWidth / 2,
                    f.Top(y - _longTabWidth / 2) - depth / f.Cosine,
                    f.Top(y + _longTabWidth / 2) - depth / f.Cosine, depth / f.Cosine + overlap));
                if (locked) longShape = Difference(longShape, BottomEyelet(f, y, 0, false));
                // Floor edge tenons engage the full supporting side cheeks.
                Geometry edgeTab = FloorEdgeSection(f, y, Options.UseWedges ? SideLugWidth * f.Cosine : _longTabWidth, gap);
                sideShape = Difference(sideShape, edgeTab);
            }
            double dividerTop = Floors.Last().Top(Floors.Last().EndY) + h;
            foreach (var cross in Crosses)
            {
                longShape = Difference(longShape, Box(cross.Y - (t + gap) / 2, -dividerTop - 1,
                    t + gap, dividerTop + 1 - cross.JointZ + gap / 2));
                double z = SideTabBottom(cross);
                sideShape = Difference(sideShape, Box(cross.Y - (t + gap) / 2,
                    -z - _sideTabHeight - gap / 2, t + gap, _sideTabHeight + gap));
            }
            Parts.Add(new AcrylicTrayPart("side-left", "Боковина левая", "side", sideShape));
            Parts.Add(new AcrylicTrayPart("side-right", "Боковина правая", "side", sideShape));
            for (int c = 0; c < Longitudinals.Count; c++)
            {
                string id = "divider-" + (c + 1);
                Parts.Add(new AcrylicTrayPart(id, "Продольная перегородка " + (c + 1), "long", longShape));
                for (int r = 0; r < _tabs.Count; r++) if (IsLock(r, _tabs.Count))
                    AddBottomWedge(id, _tabs[r].Item1, r, Longitudinals[c], _tabs[r].Item2, false);
            }

            for (int i = 0; i < Crosses.Count; i++)
            {
                var cross = Crosses[i]; var f = Floors[cross.FloorIndex];
                Geometry shape = Box(0, -cross.Bottom - cross.Height, InnerWidth, cross.Height);
                double sideZ = SideTabBottom(cross);
                shape = Union(shape, Box(-t, -sideZ - _sideTabHeight, t + overlap, _sideTabHeight));
                shape = Union(shape, Box(InnerWidth - overlap, -sideZ - _sideTabHeight, t + overlap, _sideTabHeight));
                for (int c = 0; c < Options.Columns; c++)
                {
                    double x = CellCenter(c);
                    bool locked = IsLock(c, Options.Columns);
                    double depth = locked ? _lockingTabNormal / f.Cosine + t * f.Slope : CrossTabDepth;
                    shape = Union(shape, locked ? CrossLug(x, cross.Bottom, depth) : Box(x - _crossTabWidth / 2,
                        -cross.Bottom - overlap, _crossTabWidth, depth + overlap));
                    if (locked)
                    {
                        shape = Difference(shape, BottomEyelet(f, cross.Y, x, true));
                        AddBottomWedge("cross-" + (i + 1), cross.FloorIndex, c, x, cross.Y, true);
                    }
                    AddFloorSlot(f, x, cross.Y, _crossTabWidth, t);
                }
                foreach (double x in Longitudinals)
                    shape = Difference(shape, Box(x - (t + gap) / 2, -cross.JointZ - gap / 2,
                        t + gap, h + Math.Max(CrossTabDepth, _lockingTabNormal / f.Cosine + t * f.Slope) + 1));
                Parts.Add(new AcrylicTrayPart("cross-" + (i + 1), i == Crosses.Count - 1 ? "Задняя стенка" : "Поперечная полоска " + (i + 1), "cross", shape));
            }
            foreach (var tab in _tabs)
                foreach (double x in Longitudinals)
                    AddFloorSlot(Floors[tab.Item1], x, tab.Item2, t, _longTabWidth);

            for (int i = 0; i < Floors.Count; i++)
            {
                var f = Floors[i];
                Geometry floor = Box(0, 0, InnerWidth, f.Length);
                var openings = new GeometryGroup { FillRule = FillRule.Nonzero };
                for (int r = 0; r < _tabs.Count; r++)
                {
                    var tab = _tabs[r]; if (tab.Item1 != i) continue;
                    double s = f.SurfacePosition(tab.Item2), width = Options.UseWedges ? SideLugWidth : _longTabWidth / f.Cosine;
                    bool locked = IsLock(r, _tabs.Count);
                    double length = t;
                    floor = Union(floor, locked ? SideLug(s, -1) : Box(-length, s - width / 2, length + overlap, width));
                    floor = Union(floor, locked ? SideLug(s, 1) : Box(InnerWidth - overlap, s - width / 2, length + overlap, width));
                    if (locked)
                    {
                        foreach (int side in new[] { -1, 1 })
                        {
                            Geometry eyelet = SideEyelet(s, side);
                            floor = Difference(floor, eyelet);
                            AddSideWedge("floor-" + (i + 1), i, r, s, side, eyelet);
                        }
                    }
                }
                for (int j = 0; j < f.Slots.Count; j++)
                {
                    Rect slot = f.Slots[j];
                    if (slot.Left <= 0 || slot.Right >= InnerWidth || slot.Top <= 0 || slot.Bottom >= f.Length)
                        throw new InvalidOperationException("Паз выходит за край дна. Увеличьте ячейку или уменьшите толщину.");
                    for (int k = 0; k < j; k++)
                    {
                        Rect web = slot; web.Inflate(t * 0.2, t * 0.2);
                        if (web.IntersectsWith(f.Slots[k]))
                            throw new InvalidOperationException("Между пазами дна мало материала. Увеличьте ячейку или уменьшите угол.");
                    }
                    openings.Children.Add(new RectangleGeometry(slot));
                }
                if (openings.Children.Count > 0) floor = Difference(floor, openings);
                Parts.Add(new AcrylicTrayPart("floor-" + (i + 1), "Дно " + (i + 1), "floor", floor));
            }
            foreach (var wedge in Wedges)
                Parts.Add(new AcrylicTrayPart(wedge.Id, "Грибочек " + wedge.Number + " · " + wedge.ParentId, "wedge", wedge.Shape));
        }

        public double CellCenter(int c) { return c * (Options.CellWidth + Options.Thickness) + Options.CellWidth / 2; }
        public double SideTabBottom(AcrylicTrayCross cross)
        {
            return cross.Bottom - Options.Thickness * Floors[cross.FloorIndex].Slope / 2 + Options.BoardHeight * 0.4;
        }

        private Geometry DisplaySide()
        {
            // Rounded entrance, gently eased terraces between the vertical
            // dividers, and a softened rear corner. The lower supporting edge
            // stays straight; all tenons keep their original positions.
            double h = Options.BoardHeight;
            double radius = Math.Min(Options.Thickness * 1.5, h * 0.25);
            var shape = new StreamGeometry();
            using (var c = shape.Open())
            {
                var first = Floors[0]; var last = Floors.Last();
                c.BeginFigure(new Point(first.StartY, 0), true, true);
                double y = first.StartY + radius;
                c.LineTo(new Point(first.StartY, -first.FrontZ - h + radius), true, false);
                c.BezierTo(new Point(first.StartY, -first.FrontZ - h),
                    new Point(y - radius * 0.4, -first.Top(y) - h), new Point(y, -first.Top(y) - h), true, true);
                for (int i = 0; i < Floors.Count; i++)
                {
                    var f = Floors[i];
                    if (i > 0)
                    {
                        var prev = Floors[i - 1];
                        c.BezierTo(new Point(prev.EndY, -f.FrontZ - h),
                            new Point(f.StartY, -prev.Top(prev.EndY) - h),
                            new Point(f.StartY, -f.FrontZ - h), true, true);
                        y = f.StartY;
                    }
                    double end = i == Floors.Count - 1 ? f.EndY - radius : f.EndY;
                    var stops = Crosses.Where(q => q.FloorIndex == i && q.Y > y && q.Y < end)
                        .Select(q => q.Y).Concat(new[] { end }).OrderBy(v => v);
                    foreach (double next in stops)
                    {
                        double run = next - y, rise = f.Top(next) - f.Top(y);
                        double blend = Math.Abs(rise) < 0.000001 ? 0 : Math.Min(1, h / Math.Abs(rise));
                        c.BezierTo(new Point(y + run * 0.4, -f.Top(y) - h - rise * 0.4 * (1 - blend)),
                            new Point(next - run * 0.4, -f.Top(next) - h + rise * 0.4 * (1 - blend)),
                            new Point(next, -f.Top(next) - h), true, true);
                        y = next;
                    }
                }
                c.BezierTo(new Point(last.EndY, -last.Top(last.EndY) - h),
                    new Point(last.EndY, -last.Top(last.EndY) - h + radius * 0.5),
                    new Point(last.EndY, -last.Top(last.EndY) - h + radius), true, true);
                c.LineTo(new Point(last.EndY, 0), true, false);
            }
            return shape;
        }

        private void AddFloorSlot(AcrylicTrayFloor f, double x, double y, double widthX, double widthY)
        {
            double gap = Options.SlotClearance, t = Options.Thickness;
            // n ranges from -t to 0 inside the floor. In floor coordinates,
            // s = (y-y0)/cos(a) + n*tan(a): both the width AND centre move.
            double center = f.SurfacePosition(y) - t * f.Slope / 2;
            double length = widthY / f.Cosine + t * f.Slope + gap;
            f.Slots.Add(new Rect(x - (widthX + gap) / 2, center - length / 2, widthX + gap, length));
        }

        public Geometry FloorEdgeSection(AcrylicTrayFloor f, double y, double width, double clearance = 0)
        {
            double center = f.SurfacePosition(y), half = width / (2 * f.Cosine) + clearance / 2;
            Func<double, double, Point> point = (u, v) => new Point(f.StartY + u * f.Cosine + v * f.Sine,
                -f.FrontZ - u * f.Sine + v * f.Cosine);
            return Polygon(point(center - half, -clearance / 2), point(center + half, -clearance / 2),
                point(center + half, Options.Thickness + clearance / 2),
                point(center - half, Options.Thickness + clearance / 2));
        }
        private static Geometry Band(double y1, double y2, double z1, double z2, double height)
        {
            return Polygon(new Point(y1, -z1), new Point(y2, -z2),
                new Point(y2, -z2 - height), new Point(y1, -z1 - height));
        }
        private static Geometry Box(double x, double y, double w, double h) { return new RectangleGeometry(new Rect(x, y, w, h)); }
        private static Geometry Union(Geometry a, Geometry b) { return a == null ? b : Combine(a, b, GeometryCombineMode.Union); }
        private static Geometry Difference(Geometry a, Geometry b) { return Combine(a, b, GeometryCombineMode.Exclude); }
        private static Geometry Combine(Geometry a, Geometry b, GeometryCombineMode mode)
        {
            // WPF clips in device-scale coordinates and can round millimetre
            // geometry by several microns. Work enlarged, then return to mm;
            // a nominal zero-clearance opening must not shrink during clipping.
            const double scale = 100;
            var left = new GeometryGroup { Transform = new ScaleTransform(scale, scale) };
            var right = new GeometryGroup { Transform = new ScaleTransform(scale, scale) };
            left.Children.Add(a); right.Children.Add(b);
            var result = Geometry.Combine(left, right, mode, null, 0.0001 * scale, ToleranceType.Absolute);
            result.Transform = new ScaleTransform(1 / scale, 1 / scale);
            return result.GetFlattenedPathGeometry(0.0001, ToleranceType.Absolute);
        }
        private static Geometry Polygon(params Point[] points)
        {
            var g = new StreamGeometry();
            using (var c = g.Open()) { c.BeginFigure(points[0], true, true); c.PolyLineTo(points.Skip(1).ToArray(), true, false); }
            return g;
        }
        private static void Range(double value, double min, double max, string name)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < min || value > max)
                throw new InvalidOperationException(name + ": от " + N(min) + " до " + N(max) + ".");
        }
        public static string N(double value) { return value.ToString("0.####", CultureInfo.InvariantCulture); }
        public static string SvgPath(Geometry geometry)
        {
            var path = geometry.GetFlattenedPathGeometry(0.0001, ToleranceType.Absolute);
            var result = new StringBuilder();
            foreach (var figure in path.Figures)
            {
                result.Append("M ").Append(N(figure.StartPoint.X)).Append(' ').Append(N(figure.StartPoint.Y));
                foreach (var segment in figure.Segments)
                {
                    var line = segment as LineSegment;
                    if (line != null) result.Append(" L ").Append(N(line.Point.X)).Append(' ').Append(N(line.Point.Y));
                    var poly = segment as PolyLineSegment;
                    if (poly != null) foreach (Point pt in poly.Points)
                        result.Append(" L ").Append(N(pt.X)).Append(' ').Append(N(pt.Y));
                }
                if (figure.IsClosed) result.Append(" Z ");
            }
            return result.ToString();
        }
    }
}
