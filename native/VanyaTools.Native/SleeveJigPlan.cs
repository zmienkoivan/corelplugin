using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal sealed class SleeveJigOptions
    {
        public double GuideDiameter = 3, GuideHeight = 1;
        public bool CenteredGuideConfirmed = true;
        public double Clearance = 0.3, PlasticThickness = 2;
        public double Frame = 25, Bridge = 15, RegistrationHole = 3, Kerf;
    }

    internal sealed class SleeveJigLine
    {
        public string Name;
        public PathGeometry Geometry;
    }

    // Coordinates are physical millimetres, top-left origin. CUT geometry is
    // already compensated for a centre-line laser cut. Kerf=0 means compensation
    // is left to the laser software. Slot axes always remain at needle positions.
    internal sealed class SleeveJigPlan
    {
        public bool Quilting;
        public int PanelCount;
        public SleeveJigOptions Options;
        public double Width, Height, SlotWidth, DrawnSlotWidth;
        public double BlankWidth, BlankHeight;
        public readonly List<SleeveJigLine> Cuts = new List<SleeveJigLine>();
        public readonly List<SleeveJigLine> Engraving = new List<SleeveJigLine>();
        public readonly List<PathGeometry> Axes = new List<PathGeometry>();
        public double Origin { get { return 5 + Options.Kerf / 2; } }
        public double PageWidth { get { return Width + 10 + Options.Kerf; } }
        public double PageHeight { get { return Height + 10 + Options.Kerf; } }
        public string Title { get { return (Quilting ? "Стёжка" : "Форма боковинки") + (PanelCount == 4 ? " · 2×2" : ""); } }
        private const double Tolerance = 0.005, MinimumWeb = 2;

        public static SleeveJigPlan Build(PuffySleevePattern pattern, SleeveJigOptions options, bool quilting)
        {
            if (pattern == null || options == null) throw new ArgumentNullException();
            Check(options.GuideDiameter, 0.5, 20, "Наружный диаметр направляющей");
            Check(options.GuideHeight, 0.1, 5, "Высота направляющей");
            Check(options.PlasticThickness, 0.3, 5, "Толщина одного листа пластика");
            Check(options.Clearance, 0.05, 2, "Рабочий зазор паза");
            Check(options.Frame, 10, 100, "Рамка вокруг заготовки");
            Check(options.Bridge, 5, 50, "Ширина перемычки");
            Check(options.Kerf, 0, 1, "Ширина лазерного реза");
            Check(options.RegistrationHole, 0, 10, "Диаметр отверстий совмещения");
            if (!options.CenteredGuideConfirmed)
                throw new InvalidOperationException("Для шаблонов подтвердите: игла находится по центру направляющего выступа.");
            if (options.GuideHeight > options.PlasticThickness + 0.001)
                throw new InvalidOperationException("Направляющий выступ выше нижнего листа. Уточните толщину пластика: выступ не должен упираться в ткань.");
            double slot = options.GuideDiameter + options.Clearance;
            if (options.Kerf >= slot - 0.1 || (options.RegistrationHole > 0 && options.RegistrationHole <= options.Kerf + 0.2))
                throw new InvalidOperationException("Ширина реза слишком велика для выбранного паза или отверстий совмещения.");
            if (quilting && pattern.QuiltPaths.Count == 0)
                throw new InvalidOperationException("Выберите рисунок стёжки для её пластикового шаблона.");
            if (!quilting && pattern.Radius + pattern.FixingOffset <= slot / 2)
                throw new InvalidOperationException("Радиус фиксации слишком мал для паза. Увеличьте радиус угла изделия.");
            var p = new SleeveJigPlan
            {
                Quilting = quilting, PanelCount = pattern.PanelCount, Options = options, SlotWidth = slot, DrawnSlotWidth = slot - options.Kerf,
                BlankWidth = quilting ? pattern.BatchWidth : pattern.BatchWidth / pattern.ScaleX,
                BlankHeight = quilting ? pattern.BatchHeight : pattern.BatchHeight / pattern.ScaleY
            };
            p.Width = p.BlankWidth + 2 * options.Frame;
            p.Height = p.BlankHeight + 2 * options.Frame;
            double f = options.Frame;
            double cellW = p.BlankWidth / pattern.BatchSide, cellH = p.BlankHeight / pattern.BatchSide;
            double sx = quilting ? pattern.ScaleX : 1, sy = quilting ? pattern.ScaleY : 1;
            var rawAxes = new List<string>();
            if (quilting) rawAxes.AddRange(pattern.QuiltPaths);
            else rawAxes.AddRange(FormAxes(pattern, options.Bridge + slot));
            for (int row = 0; row < pattern.BatchSide; row++)
            for (int col = 0; col < pattern.BatchSide; col++)
            {
                var transform = new MatrixTransform(sx, 0, 0, sy,
                    f + col * cellW + pattern.BlankMarginX * sx, f + row * cellH + pattern.BlankMarginY * sy);
                foreach (string data in rawAxes)
                {
                    Geometry geometry = Geometry.Parse(data).Clone();
                    geometry.Transform = transform;
                    p.Axes.Add(Flat(geometry));
                }
                if (!quilting)
                    foreach (var line in new[] { new { Name = "Пена", Data = pattern.FoamPath },
                        new { Name = "Крой ткани с припуском", Data = pattern.CutPath } })
                    {
                        Geometry g = Geometry.Parse(line.Data).Clone(); g.Transform = transform;
                        p.Engraving.Add(new SleeveJigLine { Name = line.Name + (pattern.FourUp ? " " + (row * 2 + col + 1) : ""), Geometry = Flat(g) });
                    }
            }
            double k = options.Kerf / 2;
            p.Cuts.Add(new SleeveJigLine { Name = "Наружный контур",
                Geometry = Flat(new RectangleGeometry(new Rect(-k, -k, p.Width + 2 * k, p.Height + 2 * k), 5 + k, 5 + k)) });
            var physicalHoles = new List<PathGeometry>();
            for (int i = 0; i < p.Axes.Count; i++)
            {
                p.Cuts.Add(new SleeveJigLine { Name = "Паз " + (i + 1), Geometry = Widen(p.Axes[i], p.DrawnSlotWidth) });
                physicalHoles.Add(Widen(p.Axes[i], slot));
            }
            if (options.RegistrationHole > 0)
            {
                foreach (double y in new[] { f / 2, p.Height - f / 2 })
                {
                    double x = p.Width - f / 2;
                    p.Cuts.Add(new SleeveJigLine { Name = "Совмещение", Geometry = Flat(new EllipseGeometry(
                        new Point(x, y), (options.RegistrationHole - options.Kerf) / 2,
                        (options.RegistrationHole - options.Kerf) / 2)) });
                    physicalHoles.Add(Flat(new EllipseGeometry(new Point(x, y), options.RegistrationHole / 2, options.RegistrationHole / 2)));
                }
            }
            ValidateHoles(p, physicalHoles);
            p.Engraving.Add(new SleeveJigLine { Name = "Положение прямоугольной заготовки", Geometry = Flat(
                new RectangleGeometry(new Rect(f, f, p.BlankWidth, p.BlankHeight))) });
            p.Engraving.Add(new SleeveJigLine { Name = "Зона шарнира", Geometry = Flat(new RectangleGeometry(
                new Rect(3, f, Math.Min(12, f - 5), p.BlankHeight))) });
            if (pattern.FourUp)
            {
                p.Engraving.Add(new SleeveJigLine { Name = "Разделение ткани — не резать пластик", Geometry = Flat(Geometry.Parse(
                    "M " + P(f + cellW, f) + " V " + N(f + p.BlankHeight) +
                    " M " + P(f, f + cellH) + " H " + N(f + p.BlankWidth))) });
            }
            return p;
        }

        private static IEnumerable<string> FormAxes(PuffySleevePattern p, double gap)
        {
            double a = p.Allowance - p.FixingOffset, r = p.Radius + p.FixingOffset;
            double right = p.CutWidth - a, bottom = p.CutHeight - a;
            double cx = p.CutWidth / 2, cy = p.CutHeight / 2, half = gap / 2;
            if (right - a - 2 * r < gap + 2 || bottom - a - 2 * r < gap + 2)
                throw new InvalidOperationException("Для четырёх перемычек недостаточно прямого участка. Уменьшите радиус, паз или ширину перемычки.");
            string Arc(double x, double y) => r > 0 ? " A " + P(r, r) + " 0 0 1 " + P(x, y) : " L " + P(x, y);
            yield return "M " + P(cx + half, a) + " H " + N(right - r) + Arc(right, a + r) + " V " + N(cy - half);
            yield return "M " + P(right, cy + half) + " V " + N(bottom - r) + Arc(right - r, bottom) + " H " + N(cx + half);
            yield return "M " + P(cx - half, bottom) + " H " + N(a + r) + Arc(a, bottom - r) + " V " + N(cy + half);
            yield return "M " + P(a, cy - half) + " V " + N(a + r) + Arc(a + r, a) + " H " + N(cx - half);
        }

        private static void ValidateHoles(SleeveJigPlan p, List<PathGeometry> holes)
        {
            var plate = new RectangleGeometry(new Rect(MinimumWeb, MinimumWeb,
                p.Width - 2 * MinimumWeb, p.Height - 2 * MinimumWeb), 3, 3);
            var enlarged = new List<PathGeometry>();
            foreach (PathGeometry hole in holes)
            {
                Geometry outside = Geometry.Combine(hole, plate, GeometryCombineMode.Exclude, null, Tolerance, ToleranceType.Absolute);
                if (outside.GetArea(Tolerance, ToleranceType.Absolute) > 0.001)
                    throw new InvalidOperationException("Паз или отверстие выходит к краю кассеты. Увеличьте рамку или отступ стёжки.");
                var expanded = Geometry.Combine(hole, Widen(hole, MinimumWeb), GeometryCombineMode.Union, null);
                enlarged.Add(Flat(expanded));
            }
            for (int i = 0; i < enlarged.Count; i++)
                for (int j = i + 1; j < enlarged.Count; j++)
                    if (enlarged[i].Bounds.IntersectsWith(enlarged[j].Bounds) &&
                        Geometry.Combine(enlarged[i], enlarged[j], GeometryCombineMode.Intersect, null,
                            Tolerance, ToleranceType.Absolute).GetArea(Tolerance, ToleranceType.Absolute) > 0.001)
                        throw new InvalidOperationException("Пазы или отверстия слишком близко: остаётся меньше 2 мм пластика. Увеличьте шаг стёжки, рамку или уменьшите паз.");
        }

        public static PathGeometry Flat(Geometry geometry)
        {
            var flat = geometry.GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute);
            // GetFlattenedPathGeometry preserves a transform on some WPF geometries.
            // Apply it to points once so SVG and Corel see exactly the same numbers.
            Matrix m = flat.Transform?.Value ?? Matrix.Identity;
            var result = new PathGeometry { FillRule = flat.FillRule };
            foreach (PathFigure figure in flat.Figures)
            {
                var copy = new PathFigure { StartPoint = m.Transform(figure.StartPoint), IsClosed = figure.IsClosed, IsFilled = figure.IsFilled };
                foreach (Point point in Points(figure)) copy.Segments.Add(new LineSegment(m.Transform(point), true));
                result.Figures.Add(copy);
            }
            return result;
        }
        public static IEnumerable<Point> Points(PathFigure figure)
        {
            foreach (PathSegment segment in figure.Segments)
            {
                if (segment is LineSegment line) yield return line.Point;
                else if (segment is PolyLineSegment poly) foreach (Point point in poly.Points) yield return point;
                else throw new InvalidOperationException("Ожидался линейный контур после развёртки геометрии.");
            }
        }
        public static string Data(PathGeometry geometry)
        {
            var data = new StringBuilder();
            foreach (PathFigure f in geometry.Figures)
            {
                data.Append("M ").Append(P(f.StartPoint.X, f.StartPoint.Y));
                foreach (Point point in Points(f)) data.Append(" L ").Append(P(point.X, point.Y));
                if (f.IsClosed) data.Append(" Z ");
            }
            return data.ToString();
        }
        private static PathGeometry Widen(Geometry g, double width) => Flat(g.GetWidenedPathGeometry(new Pen(Brushes.Black, width)
        { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round }, Tolerance, ToleranceType.Absolute)
            // WPF's widened arcs may contain overlapping stroke pieces and internal
            // edges. A laser needs the boundary of their union, not those edges.
            .GetOutlinedPathGeometry(Tolerance, ToleranceType.Absolute));
        private static string N(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);
        private static string P(double x, double y) => N(x) + " " + N(y);
        private static void Check(double value, double min, double max, string name)
        {
            if (Double.IsNaN(value) || Double.IsInfinity(value) || value < min || value > max)
                throw new InvalidOperationException(name + ": от " + N(min) + " до " + N(max) + " мм.");
        }
    }
}
