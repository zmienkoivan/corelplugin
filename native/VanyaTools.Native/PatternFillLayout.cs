using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal enum PatternFillMode { Rows, Staggered, Random, Crocodile, Pebbles }

    internal sealed class PatternFillOptions
    {
        internal PatternFillMode Mode;
        internal double SizeMm = 30, GapMm = 3, MinScale = 1, MaxScale = 1;
        internal double Angle, AngleVariation;
        internal int Seed;
        internal bool Texture => Mode == PatternFillMode.Crocodile || Mode == PatternFillMode.Pebbles;
    }

    internal readonly struct PatternPoint
    {
        internal readonly double X, Y;
        internal PatternPoint(double x, double y) { X = x; Y = y; }

        internal static PatternPoint Between(PatternPoint a, PatternPoint b, double fraction) =>
            new PatternPoint(a.X + (b.X - a.X) * fraction, a.Y + (b.Y - a.Y) * fraction);
    }

    internal sealed class PatternPlacement
    {
        internal int Motif;
        internal double Size, Angle;
        internal PatternPoint Center;
        internal PatternPoint[] Cell;
    }

    internal readonly struct PatternBox
    {
        internal readonly double Left, Bottom, Right, Top;

        internal PatternBox(double left, double bottom, double right, double top)
        { Left = left; Bottom = bottom; Right = right; Top = top; }

        internal bool Overlaps(PatternBox other) =>
            Left < other.Right && Right > other.Left && Bottom < other.Top && Top > other.Bottom;
    }

    internal readonly struct PatternTransform
    {
        internal readonly double Scale, Width, Height, RotatedWidth, RotatedHeight;

        private PatternTransform(double scale, double width, double height, PatternPoint rotated)
        {
            Scale = scale; Width = width * scale; Height = height * scale;
            RotatedWidth = rotated.X * scale; RotatedHeight = rotated.Y * scale;
        }

        internal static PatternPoint RotatedSize(double width, double height, double angle)
        {
            double radians = angle * Math.PI / 180;
            double sin = Math.Abs(Math.Sin(radians)), cos = Math.Abs(Math.Cos(radians));
            return new PatternPoint(width * cos + height * sin, width * sin + height * cos);
        }

        internal static PatternTransform Fit(PatternPoint size, PatternPlacement item)
        {
            var rotated = RotatedSize(size.X, size.Y, item.Angle);
            return new PatternTransform(item.Size / Math.Max(rotated.X, rotated.Y), size.X, size.Y, rotated);
        }

        internal PatternBox Bounds(PatternPoint center, double clearance = 0) =>
            new PatternBox(center.X - RotatedWidth / 2 - clearance, center.Y - RotatedHeight / 2 - clearance,
                center.X + RotatedWidth / 2 + clearance, center.Y + RotatedHeight / 2 + clearance);
    }

    internal static class PatternFillLayout
    {
        internal const int MaximumElements = 2400;

        internal static List<PatternPlacement> Build(double width, double height, int motifs, PatternFillOptions options)
        {
            if (!Finite(width) || !Finite(height) || width <= 0 || height <= 0)
                throw new ArgumentException("Выбранный контур должен иметь ширину и высоту.");
            if (!Finite(options.SizeMm) || options.SizeMm < .5 || options.SizeMm > 2000 ||
                !Finite(options.GapMm) || options.GapMm < 0 || options.GapMm > 1000)
                throw new ArgumentException("Размер элемента 0,5–2000 мм, зазор 0–1000 мм.");
            if (!Finite(options.Angle) || Math.Abs(options.Angle) > 360 ||
                !Finite(options.AngleVariation) || options.AngleVariation < 0 || options.AngleVariation > 180)
                throw new ArgumentException("Поворот от −360° до 360°, разброс от 0° до 180°.");
            if (options.Texture)
            {
                if (options.GapMm > options.SizeMm / 2)
                    throw new ArgumentException("Зазор фактуры должен быть не больше половины размера ячейки.");
                return Texture(width, height, options);
            }
            if (motifs < 1) throw new ArgumentException("Сначала запомните элементы из выделения.");
            if (!Finite(options.MinScale) || !Finite(options.MaxScale) || options.MinScale < .1 ||
                options.MaxScale > 3 || options.MinScale > options.MaxScale)
                throw new ArgumentException("Задайте диапазон размеров от 10% до 300%, сначала меньшее значение.");
            return Motifs(width, height, motifs, options);
        }

        private static List<PatternPlacement> Motifs(double width, double height, int motifs, PatternFillOptions options)
        {
            bool scattered = options.Mode == PatternFillMode.Random;
            double largest = options.SizeMm * options.MaxScale;
            double pitch = largest + options.GapMm + (scattered ? largest * .22 : 0);
            double cols = Math.Ceiling(width / pitch), rows = Math.Ceiling(height / pitch);
            CheckCount(cols, rows);
            double firstX = (width - (cols - 1) * pitch) / 2;
            double firstY = (height + (rows - 1) * pitch) / 2;
            var random = new Random(options.Seed);
            var result = new List<PatternPlacement>();
            for (int row = 0; row < rows; row++)
            {
                double shift = options.Mode == PatternFillMode.Staggered && cols > 1 && (row & 1) != 0 ? pitch / 2 : 0;
                int first = shift == 0 ? 0 : -1;
                for (int col = first; col < cols; col++)
                {
                    double size = options.SizeMm * (options.MinScale + random.NextDouble() * (options.MaxScale - options.MinScale));
                    double x = firstX + col * pitch + shift, y = firstY - row * pitch;
                    if (scattered)
                    {
                        double space = (pitch - size - options.GapMm) / 2;
                        x = Math.Max(0, Math.Min(width, x + Signed(random) * space));
                        y = Math.Max(0, Math.Min(height, y + Signed(random) * space));
                    }
                    if (x + size / 2 <= 0 || x - size / 2 >= width) continue;
                    int index = scattered ? random.Next(motifs) : (result.Count + (int)((uint)options.Seed % (uint)motifs)) % motifs;
                    result.Add(new PatternPlacement { Center = new PatternPoint(x, y), Motif = index, Size = size,
                        Angle = options.Angle + Signed(random) * options.AngleVariation });
                    if (result.Count > MaximumElements) CheckCount(result.Count, 1);
                }
            }
            return result;
        }

        private static List<PatternPlacement> Texture(double width, double height, PatternFillOptions options)
        {
            bool scales = options.Mode == PatternFillMode.Crocodile;
            double pitchX = options.SizeMm * (scales ? 1.28 : 1);
            double pitchY = options.SizeMm * (scales ? .74 : 1);
            double angle = options.Angle * Math.PI / 180;
            double cos = Math.Cos(angle), sin = Math.Sin(angle);
            double localWidth = Math.Abs(cos) * width + Math.Abs(sin) * height;
            double localHeight = Math.Abs(sin) * width + Math.Abs(cos) * height;
            double colCount = Math.Ceiling(localWidth / pitchX) + 4, rowCount = Math.Ceiling(localHeight / pitchY) + 4;
            CheckCount(colCount, rowCount);
            int cols = (int)colCount, rows = (int)rowCount;
            var seeds = new PatternPoint[cols, rows];
            var random = new Random(options.Seed);
            for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                    seeds[col, row] = new PatternPoint(col + Signed(random) * (scales ? .24 : .41),
                        row + Signed(random) * (scales ? .19 : .41));

            double middleX = (cols - 1) / 2d, middleY = (rows - 1) / 2d;
            var result = new List<PatternPlacement>();
            for (int row = 1; row < rows - 1; row++)
            {
                for (int col = 1; col < cols - 1; col++)
                {
                    var seed = seeds[col, row];
                    var polygon = new List<PatternPoint> {
                        new PatternPoint(seed.X - 2, seed.Y - 2), new PatternPoint(seed.X + 2, seed.Y - 2),
                        new PatternPoint(seed.X + 2, seed.Y + 2), new PatternPoint(seed.X - 2, seed.Y + 2)
                    };
                    for (int y = Math.Max(0, row - 2); y <= Math.Min(rows - 1, row + 2) && polygon.Count >= 3; y++)
                        for (int x = Math.Max(0, col - 2); x <= Math.Min(cols - 1, col + 2) && polygon.Count >= 3; x++)
                            if (x != col || y != row)
                            {
                                var neighbor = seeds[x, y];
                                double nx = neighbor.X - seed.X, ny = neighbor.Y - seed.Y;
                                double cut = (nx * (neighbor.X + seed.X) + ny * (neighbor.Y + seed.Y)) / 2 -
                                    options.GapMm / 2 * Math.Sqrt(Math.Pow(nx / pitchX, 2) + Math.Pow(ny / pitchY, 2));
                                polygon = Clip(polygon, nx, ny, cut);
                            }
                    if (polygon.Count < 3) continue;

                    var points = polygon.Select(point =>
                    {
                        double x = (point.X - middleX) * pitchX, y = (point.Y - middleY) * pitchY;
                        return new PatternPoint(width / 2 + x * cos - y * sin, height / 2 + x * sin + y * cos);
                    }).ToArray();
                    if (points.Max(p => p.X) > 0 && points.Min(p => p.X) < width &&
                        points.Max(p => p.Y) > 0 && points.Min(p => p.Y) < height)
                        result.Add(new PatternPlacement { Cell = points });
                }
            }
            if (result.Count == 0) throw new InvalidOperationException("В контуре нет ячеек с таким зазором. Уменьшите зазор.");
            return result;
        }

        internal static void LeaveRoom(List<PatternPlacement> items, IReadOnlyList<PatternPoint> motifSizes, PatternBox reserve)
        {
            items.RemoveAll(item =>
            {
                if (item.Cell == null) return PatternTransform.Fit(motifSizes[item.Motif], item).Bounds(item.Center).Overlaps(reserve);

                var polygon = item.Cell;
                var bounds = new PatternBox(polygon.Min(p => p.X), polygon.Min(p => p.Y),
                    polygon.Max(p => p.X), polygon.Max(p => p.Y));
                if (!bounds.Overlaps(reserve)) return false;
                var overlap = Clip(polygon.ToList(), -1, 0, -reserve.Left);
                if (overlap.Count < 3) return false;
                overlap = Clip(overlap, 1, 0, reserve.Right);
                if (overlap.Count < 3) return false;
                overlap = Clip(overlap, 0, -1, -reserve.Bottom);
                return overlap.Count >= 3 && Clip(overlap, 0, 1, reserve.Top).Count >= 3;
            });
        }

        private static List<PatternPoint> Clip(List<PatternPoint> polygon, double nx, double ny, double cut)
        {
            var result = new List<PatternPoint>(polygon.Count + 1);
            PatternPoint previous = polygon[polygon.Count - 1];
            double previousDistance = nx * previous.X + ny * previous.Y - cut;
            foreach (var current in polygon)
            {
                double distance = nx * current.X + ny * current.Y - cut;
                if ((distance <= 0) != (previousDistance <= 0))
                    result.Add(PatternPoint.Between(previous, current, previousDistance / (previousDistance - distance)));
                if (distance <= 0) result.Add(current);
                previous = current; previousDistance = distance;
            }
            return result;
        }

        private static double Signed(Random random) => 2 * random.NextDouble() - 1;
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
        private static void CheckCount(double cols, double rows)
        {
            if (cols * rows > MaximumElements)
                throw new InvalidOperationException("Паттерн слишком плотный. Увеличьте размер элемента или зазор. Лимит — " + MaximumElements + " элементов.");
        }
    }
}
