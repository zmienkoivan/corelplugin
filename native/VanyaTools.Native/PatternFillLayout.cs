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
        internal double RepeatWidthMm, RepeatHeightMm;
        internal bool Repeat => RepeatWidthMm > 0;
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
        internal bool CrossesBottleSeam;
    }

    internal sealed class PatternFootprint
    {
        internal readonly PatternPoint Size;
        internal readonly PatternPoint[] Hull;
        internal readonly PatternPoint Center;

        internal PatternFootprint(PatternPoint size, IEnumerable<PatternPoint> outline = null)
        {
            Size = size;
            Hull = outline == null ? Rectangle(size) : ConvexHull(outline);
            if (Hull.Length < 3) Hull = Rectangle(size);
            Center = new PatternPoint(Hull.Average(p => p.X), Hull.Average(p => p.Y));
        }

        internal PatternPoint[] At(PatternPlacement item)
        {
            double scale = PatternTransform.Fit(Size, item).Scale;
            double radians = item.Angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
            return Hull.Select(p => new PatternPoint(item.Center.X + scale * (p.X * cos - p.Y * sin),
                item.Center.Y + scale * (p.X * sin + p.Y * cos))).ToArray();
        }

        private static PatternPoint[] Rectangle(PatternPoint size)
        {
            double x = Math.Max(.0001, size.X) / 2, y = Math.Max(.0001, size.Y) / 2;
            return new[] { new PatternPoint(-x, -y), new PatternPoint(x, -y),
                new PatternPoint(x, y), new PatternPoint(-x, y) };
        }

        internal static PatternPoint[] ConvexHull(IEnumerable<PatternPoint> points)
        {
            var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
            if (sorted.Length < 3) return sorted;
            var hull = new List<PatternPoint>();
            foreach (var point in sorted) Add(point, 0);
            int lower = hull.Count;
            for (int i = sorted.Length - 2; i >= 0; i--) Add(sorted[i], lower - 1);
            hull.RemoveAt(hull.Count - 1);
            return hull.ToArray();

            void Add(PatternPoint point, int floor)
            {
                while (hull.Count >= floor + 2)
                {
                    var a = hull[hull.Count - 2]; var b = hull[hull.Count - 1];
                    if ((b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X) > 0) break;
                    hull.RemoveAt(hull.Count - 1);
                }
                hull.Add(point);
            }
        }
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

    }

    internal sealed class PatternLayoutGuides
    {
        internal IReadOnlyList<PatternPoint[]> Cells;
        internal IReadOnlyList<PatternPoint[]> RepeatLines;
    }

    internal static partial class PatternFillLayout
    {
        internal const int MaximumElements = 2400;

        internal static List<PatternPlacement> Build(double width, double height, IReadOnlyList<PatternFootprint> motifs,
            PatternFillOptions options) => Build(width, height, motifs, options, out _);

        internal static List<PatternPlacement> Build(double width, double height, IReadOnlyList<PatternFootprint> motifs,
            PatternFillOptions options, out PatternLayoutGuides guides)
        {
            guides = null;
            if (!Finite(width) || !Finite(height) || width <= 0 || height <= 0)
                throw new ArgumentException("Выбранный контур должен иметь ширину и высоту.");
            if (!Finite(options.SizeMm) || options.SizeMm < .5 || options.SizeMm > 2000 ||
                !Finite(options.GapMm) || options.GapMm < 0 || options.GapMm > 1000)
                throw new ArgumentException("Размер элемента 0,5–2000 мм, зазор 0–1000 мм.");
            if (!Finite(options.Angle) || Math.Abs(options.Angle) > 360 ||
                !Finite(options.AngleVariation) || options.AngleVariation < 0 || options.AngleVariation > 180)
                throw new ArgumentException("Поворот от −360° до 360°, разброс от 0° до 180°.");
            if (motifs.Count == 0) throw new ArgumentException("Добавьте фоновые фигуры в набор.");
            if (options.Texture)
            {
                if (options.GapMm > options.SizeMm / 2)
                    throw new ArgumentException("Зазор фактуры должен быть не больше половины размера ячейки.");
                if (options.Repeat) return RepeatPattern(width, height, motifs, options, out guides);
                var cells = TextureCells(width, height, options);
                var items = PlaceInCells(cells, motifs, options);
                if (options.Mode == PatternFillMode.Pebbles) PatternPebbleSpacing.Balance(items, motifs, options.GapMm);
                guides = new PatternLayoutGuides { Cells = cells };
                return items;
            }
            if (!Finite(options.MinScale) || !Finite(options.MaxScale) || options.MinScale < .1 ||
                options.MaxScale > 3 || options.MinScale > options.MaxScale)
                throw new ArgumentException("Задайте диапазон размеров от 10% до 300%, сначала меньшее значение.");
            return options.Repeat ? RepeatPattern(width, height, motifs, options, out guides) :
                Motifs(width, height, motifs.Count, options);
        }

        private static List<PatternPlacement> Motifs(double width, double height, int motifs, PatternFillOptions options,
            double motifAngle = 0, bool wrapHeight = true)
        {
            bool scattered = options.Mode == PatternFillMode.Random;
            double largest = options.SizeMm * options.MaxScale;
            double pitch = largest + options.GapMm + (scattered ? largest * .22 : 0);
            bool repeat = options.Repeat, staggered = options.Mode == PatternFillMode.Staggered;
            double cols = Math.Ceiling(width / pitch), rows = Math.Ceiling(height / pitch);
            if (repeat)
            {
                double minimum = largest + options.GapMm;
                if (width < minimum || height < minimum)
                    throw new ArgumentException("Рапорт должен вмещать самый крупный рисунок с зазором.");
                cols = Math.Max(1, Math.Floor(width / pitch));
                rows = Math.Max(1, Math.Floor(height / pitch));
                if (wrapHeight && staggered && rows > 1 && rows % 2 != 0) rows--;
            }
            CheckCount(cols, rows);
            double pitchX = repeat ? width / cols : pitch, pitchY = repeat ? height / rows : pitch;
            double firstX = (width - (cols - 1) * pitchX) / 2;
            if (staggered && repeat && !wrapHeight) firstX -= pitchX / 4;
            double firstY = (height + (rows - 1) * pitchY) / 2;
            var random = new Random(options.Seed);
            int[] order = scattered ? Shuffle(motifs, random) : null;
            var result = new List<PatternPlacement>();
            for (int row = 0; row < rows; row++)
            {
                double shift = staggered && (repeat || cols > 1) && (row & 1) != 0 ? pitchX / 2 : 0;
                int first = repeat || shift == 0 ? 0 : -1;
                for (int col = first; col < cols; col++)
                {
                    double size = options.SizeMm * (options.MinScale + random.NextDouble() * (options.MaxScale - options.MinScale));
                    double columnX = firstX + col * pitchX + shift, rowY = firstY - row * pitchY;
                    double x = columnX, y = rowY;
                    if (scattered)
                    {
                        x += Signed(random) * (pitchX - size - options.GapMm) / 2;
                        y += Signed(random) * (pitchY - size - options.GapMm) / 2;
                        if (!repeat)
                        {
                            x = Math.Max(0, Math.Min(width, x));
                            y = Math.Max(0, Math.Min(height, y));
                        }
                    }
                    if (!repeat && (x + size / 2 <= 0 || x - size / 2 >= width)) continue;
                    if (scattered && result.Count > 0 && result.Count % motifs == 0) Shuffle(order, random, result[result.Count - 1].Motif);
                    int index = scattered ? order[result.Count % motifs] : (result.Count + (int)((uint)options.Seed % (uint)motifs)) % motifs;
                    double halfWidth = (pitchX - options.GapMm) / 2, halfHeight = (pitchY - options.GapMm) / 2;
                    result.Add(new PatternPlacement { Center = new PatternPoint(x, y), Motif = index, Size = size,
                        Angle = (repeat ? motifAngle : options.Angle) + Signed(random) * options.AngleVariation,
                        Cell = new[] { new PatternPoint(columnX - halfWidth, rowY - halfHeight),
                            new PatternPoint(columnX + halfWidth, rowY - halfHeight),
                            new PatternPoint(columnX + halfWidth, rowY + halfHeight),
                            new PatternPoint(columnX - halfWidth, rowY + halfHeight) } });
                    if (result.Count > MaximumElements) CheckCount(result.Count, 1);
                }
            }
            return result;
        }

        internal static List<PatternPoint[]> TextureCells(double width, double height, PatternFillOptions options)
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
            var seeds = ScatterSeeds(cols, rows, options.Seed);
            if (!scales) seeds = RelaxSeeds(seeds);
            double middleX = cols / 2d, middleY = rows / 2d;
            var result = new List<PatternPoint[]>();
            var polygon = new List<PatternPoint>(16);
            var buffer = new List<PatternPoint>(16);
            for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                var bucket = seeds[col, row];
                if (bucket == null) continue;
                foreach (var seed in bucket)
                {
                    var cell = BuildCell(seed, seeds, col, row, pitchX, pitchY, options.GapMm, polygon, buffer);
                    if (cell.Count < 3) continue;

                    var points = cell.Select(point =>
                    {
                        double x = (point.X - middleX) * pitchX, y = (point.Y - middleY) * pitchY;
                        return new PatternPoint(width / 2 + x * cos - y * sin, height / 2 + x * sin + y * cos);
                    }).ToArray();
                    if (points.Max(p => p.X) > 0 && points.Min(p => p.X) < width &&
                        points.Max(p => p.Y) > 0 && points.Min(p => p.Y) < height)
                    {
                        result.Add(points);
                        if (result.Count > MaximumElements) CheckCount(result.Count, 1);
                    }
                }
            }
            if (result.Count == 0) throw new InvalidOperationException("В контуре нет ячеек с таким зазором. Уменьшите зазор.");
            return result;
        }

        private static List<PatternPoint>[,] RelaxSeeds(List<PatternPoint>[,] seeds, bool periodic = false)
        {
            int cols = seeds.GetLength(0), rows = seeds.GetLength(1);
            var polygon = new List<PatternPoint>(16);
            var buffer = new List<PatternPoint>(16);
            // A few partial moves even out the spacing while preserving the initial random distribution.
            for (int pass = 0; pass < 2; pass++)
            {
                var next = new List<PatternPoint>[cols, rows];
                for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                {
                    if (seeds[col, row] == null) continue;
                    foreach (var seed in seeds[col, row])
                    {
                        var point = seed;
                        if (periodic || (col > 0 && col < cols - 1 && row > 0 && row < rows - 1))
                        {
                            var cell = BuildCell(seed, seeds, col, row, 1, 1, 0, polygon, buffer, periodic);
                            if (cell.Count >= 3) point = PatternPoint.Between(seed, AreaCenter(cell), .75);
                        }
                        if (periodic) point = new PatternPoint(Wrap(point.X, cols), Wrap(point.Y, rows));
                        else if (point.X < 0 || point.Y < 0 || point.X >= cols || point.Y >= rows) point = seed;
                        AddSeed(next, point);
                    }
                }
                seeds = next;
            }
            return seeds;
        }

        private static PatternPoint AreaCenter(IReadOnlyList<PatternPoint> cell)
        {
            var origin = cell[0];
            double area = 0, x = 0, y = 0;
            for (int i = 1; i < cell.Count - 1; i++)
            {
                var a = cell[i]; var b = cell[i + 1];
                double weight = (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);
                area += weight;
                x += (a.X + b.X + origin.X) * weight;
                y += (a.Y + b.Y + origin.Y) * weight;
            }
            return area > 1e-12 ? new PatternPoint(x / (3 * area), y / (3 * area)) : origin;
        }

        private static List<PatternPoint> BuildCell(PatternPoint seed, List<PatternPoint>[,] seeds,
            int col, int row, double pitchX, double pitchY, double gap, List<PatternPoint> polygon,
            List<PatternPoint> buffer, bool periodic = false, bool wrapHeight = true)
        {
            int cols = seeds.GetLength(0), rows = seeds.GetLength(1);
            double halfGap = gap / (2 * Math.Min(pitchX, pitchY));
            polygon.Clear();
            double bottom = wrapHeight ? seed.Y - 2 : Math.Max(0, seed.Y - 2);
            double top = wrapHeight ? seed.Y + 2 : Math.Min(rows, seed.Y + 2);
            polygon.Add(new PatternPoint(seed.X - 2, bottom));
            polygon.Add(new PatternPoint(seed.X + 2, bottom));
            polygon.Add(new PatternPoint(seed.X + 2, top));
            polygon.Add(new PatternPoint(seed.X - 2, top));
            // Stop once even the nearest seed in the remaining buckets cannot cut the cell.
            for (int ring = 0; ring <= 7 && polygon.Count >= 3; ring++)
            {
                double radiusSquared = polygon.Max(p => SquaredDistance(p, seed));
                if ((ring - 1) / 2d >= Math.Sqrt(radiusSquared) + halfGap) break;
                int firstY = periodic && wrapHeight ? row - ring : Math.Max(0, row - ring);
                int lastY = periodic && wrapHeight ? row + ring : Math.Min(rows - 1, row + ring);
                int firstX = periodic ? col - ring : Math.Max(0, col - ring);
                int lastX = periodic ? col + ring : Math.Min(cols - 1, col + ring);
                for (int y = firstY; y <= lastY && polygon.Count >= 3; y++)
                for (int x = firstX; x <= lastX && polygon.Count >= 3; x++)
                {
                    if (Math.Max(Math.Abs(x - col), Math.Abs(y - row)) != ring) continue;
                    int bucketX = periodic ? WrapIndex(x, cols) : x, bucketY = periodic ? WrapIndex(y, rows) : y;
                    var bucket = seeds[bucketX, bucketY];
                    if (bucket == null) continue;
                    foreach (var neighbor in bucket)
                    {
                        double neighborX = neighbor.X + (x - bucketX), neighborY = neighbor.Y + (y - bucketY);
                        double nx = neighborX - seed.X, ny = neighborY - seed.Y;
                        if ((nx == 0 && ny == 0) || polygon.Count < 3) continue;
                        double mx = nx / pitchX, my = ny / pitchY;
                        double cut = (nx * (neighborX + seed.X) + ny * (neighborY + seed.Y)) / 2 -
                            gap / 2 * Math.Sqrt(mx * mx + my * my);
                        Clip(polygon, nx, ny, cut, buffer);
                        var previous = polygon; polygon = buffer; buffer = previous;
                    }
                }
            }
            return polygon;
        }

        private static void AddSeed(List<PatternPoint>[,] buckets, PatternPoint point)
        {
            int col = (int)point.X, row = (int)point.Y;
            if (buckets[col, row] == null) buckets[col, row] = new List<PatternPoint>(2);
            buckets[col, row].Add(point);
        }

        private static List<PatternPoint>[,] ScatterSeeds(int cols, int rows, int seed, bool periodic = false)
        {
            var random = new Random(seed);
            var buckets = new List<PatternPoint>[cols, rows];
            const double minimumDistance = .74;
            for (int attempt = 0; attempt < cols * rows * 28; attempt++)
            {
                var point = new PatternPoint(random.NextDouble() * cols, random.NextDouble() * rows);
                int col = (int)point.X, row = (int)point.Y;
                bool tooClose = false;
                int firstY = periodic ? row - 1 : Math.Max(0, row - 1);
                int lastY = periodic ? row + 1 : Math.Min(rows - 1, row + 1);
                int firstX = periodic ? col - 1 : Math.Max(0, col - 1);
                int lastX = periodic ? col + 1 : Math.Min(cols - 1, col + 1);
                for (int y = firstY; y <= lastY && !tooClose; y++)
                for (int x = firstX; x <= lastX && !tooClose; x++)
                {
                    int bucketX = periodic ? WrapIndex(x, cols) : x, bucketY = periodic ? WrapIndex(y, rows) : y;
                    var bucket = buckets[bucketX, bucketY];
                    if (bucket == null) continue;
                    foreach (var neighbor in bucket)
                    {
                        double dx = point.X - neighbor.X - (x - bucketX), dy = point.Y - neighbor.Y - (y - bucketY);
                        if (dx * dx + dy * dy >= minimumDistance * minimumDistance) continue;
                        tooClose = true;
                        break;
                    }
                }
                if (tooClose) continue;
                AddSeed(buckets, point);
            }
            return buckets;
        }

        private static double SquaredDistance(PatternPoint a, PatternPoint b)
        {
            double x = a.X - b.X, y = a.Y - b.Y;
            return x * x + y * y;
        }

        internal static List<PatternPlacement> PlaceInCells(List<PatternPoint[]> cells, IReadOnlyList<PatternFootprint> motifs,
            PatternFillOptions options, bool allowEmpty = false, bool fixedAngle = false, double motifAngle = 0)
        {
            var random = new Random(options.Seed);
            bool pebbles = options.Mode == PatternFillMode.Pebbles;
            var result = new List<PatternPlacement>(cells.Count);
            var fitBounds = new List<PatternPoint>(16);
            var centers = new List<PatternPoint>(16);
            var buffer = new List<PatternPoint>(16);
            var cellEdges = new List<(double X, double Y, double Limit, double Width)>(16);
            var supports = new List<(double X, double Y, double Limit, double Support)>(16);
            var useCounts = new int[motifs.Count];
            var neighborDistances = new double[motifs.Count];
            var placedCenters = new List<PatternPoint>(cells.Count);
            var period = options.Repeat ? new PatternPoint(options.RepeatWidthMm, options.RepeatHeightMm) : default;
            double baseAngle = options.Repeat ? motifAngle : options.Angle;
            double drift = options.SizeMm * .07;
            foreach (var cell in cells)
            {
                var target = pebbles ? AreaCenter(cell) : new PatternPoint(cell.Average(p => p.X), cell.Average(p => p.Y));
                int index = ChooseNeighborMotif(result, placedCenters, target, options.SizeMm, useCounts,
                    neighborDistances, random, period);
                var motif = motifs[index];
                var center = target;
                double angle = baseAngle, scale = 0;
                cellEdges.Clear();
                for (int i = 0; i < cell.Length; i++)
                {
                    var a = cell[i]; var b = cell[(i + 1) % cell.Length];
                    double nx = b.Y - a.Y, ny = a.X - b.X;
                    double cut = nx * a.X + ny * a.Y;
                    cellEdges.Add((nx, ny, cut, cut - cell.Min(p => nx * p.X + ny * p.Y)));
                }
                fitBounds.Clear();
                if (pebbles)
                {
                    fitBounds.Add(new PatternPoint(target.X - drift, target.Y - drift));
                    fitBounds.Add(new PatternPoint(target.X + drift, target.Y - drift));
                    fitBounds.Add(new PatternPoint(target.X + drift, target.Y + drift));
                    fitBounds.Add(new PatternPoint(target.X - drift, target.Y + drift));
                }
                else fitBounds.AddRange(cell);
                TryAngle(baseAngle);
                if (!fixedAngle)
                {
                    for (int turn = 30; turn <= 180; turn += 30)
                    {
                        TryAngle(baseAngle + turn);
                        if (turn < 180) TryAngle(baseAngle - turn);
                    }
                    for (int step = 10; step >= 1; step /= 3)
                    {
                        double best = angle;
                        TryAngle(best - step);
                        TryAngle(best + step);
                    }
                }
                if (scale <= 0) continue;

                double radians = angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
                double x = center.X - scale * (motif.Center.X * cos - motif.Center.Y * sin);
                double y = center.Y - scale * (motif.Center.X * sin + motif.Center.Y * cos);
                var rotated = PatternTransform.RotatedSize(motif.Size.X, motif.Size.Y, angle);
                result.Add(new PatternPlacement { Motif = index, Center = new PatternPoint(x, y), Angle = angle,
                    Size = scale * Math.Max(rotated.X, rotated.Y), Cell = cell });
                useCounts[index]++;
                placedCenters.Add(center);

                void TryAngle(double candidateAngle)
                {
                    double turn = candidateAngle * Math.PI / 180, c = Math.Cos(turn), s = Math.Sin(turn);
                    double candidateScale = Double.PositiveInfinity, limit = Double.PositiveInfinity;
                    var candidateCenter = target;
                    supports.Clear();
                    foreach (var edge in cellEdges)
                    {
                        double localX = edge.X * c + edge.Y * s, localY = -edge.X * s + edge.Y * c;
                        double support = 0, opposite = 0;
                        foreach (var point in motif.Hull)
                        {
                            double projection = localX * (point.X - motif.Center.X) + localY * (point.Y - motif.Center.Y);
                            support = Math.Max(support, projection);
                            opposite = Math.Min(opposite, projection);
                        }
                        if (support <= 1e-10) continue;
                        supports.Add((edge.X, edge.Y, edge.Limit, support));
                        candidateScale = Math.Min(candidateScale,
                            (edge.Limit - edge.X * target.X - edge.Y * target.Y) / support);
                        limit = Math.Min(limit, edge.Width / (support - opposite));
                    }
                    // Keep the current angle when the gain would be below the precision of the fit.
                    double required = scale * 1.005;
                    if (!Finite(candidateScale) || candidateScale <= 0 || limit <= required) return;
                    if (candidateScale < required)
                    {
                        if (!TryCenter(required, out candidateCenter)) return;
                        candidateScale = required;
                    }
                    for (int step = 0; step < 7 && limit > candidateScale * 1.005; step++)
                    {
                        double mid = (candidateScale + limit) / 2;
                        if (TryCenter(mid, out var nextCenter))
                        {
                            candidateScale = mid;
                            candidateCenter = nextCenter;
                        }
                        else limit = mid;
                    }
                    if (candidateScale <= scale) return;
                    angle = candidateAngle;
                    scale = candidateScale;
                    center = candidateCenter;
                }

                bool TryCenter(double candidate, out PatternPoint centerForScale)
                {
                    centers.Clear();
                    centers.AddRange(fitBounds);
                    foreach (var edge in supports)
                    {
                        Clip(centers, edge.X, edge.Y, edge.Limit - edge.Support * candidate, buffer);
                        var previous = centers; centers = buffer; buffer = previous;
                        if (centers.Count == 0)
                        {
                            centerForScale = default;
                            return false;
                        }
                    }
                    centerForScale = new PatternPoint(centers.Average(p => p.X), centers.Average(p => p.Y));
                    return true;
                }
            }
            if (result.Count == 0 && !allowEmpty)
                throw new InvalidOperationException("Уменьшите зазор, чтобы разместить фигуры в ячейках.");
            return result;
        }

        private static int ChooseNeighborMotif(List<PatternPlacement> placed, List<PatternPoint> centers,
            PatternPoint center, double pitch, int[] useCounts, double[] nearest, Random random, PatternPoint period)
        {
            double radius = pitch * 1.65;
            double radiusSquared = radius * radius;
            for (int i = 0; i < nearest.Length; i++) nearest[i] = radiusSquared;
            bool repeat = period.X > 0 && period.Y > 0;
            for (int i = 0; i < placed.Count; i++)
            {
                int motif = placed[i].Motif;
                double dx = center.X - centers[i].X, dy = center.Y - centers[i].Y;
                if (repeat)
                {
                    dx -= Math.Round(dx / period.X) * period.X;
                    dy -= Math.Round(dy / period.Y) * period.Y;
                }
                nearest[motif] = Math.Min(nearest[motif], dx * dx + dy * dy);
            }

            int best = 0;
            double bestScore = double.NegativeInfinity;
            for (int i = 0; i < nearest.Length; i++)
            {
                // Prefer a different nearby motif, balance usage, then break ties randomly.
                double penalty = nearest[i] < radiusSquared ? 4 * (1 - nearest[i] / radiusSquared) + 2 : 0;
                double score = random.NextDouble() - penalty - .4 * useCounts[i];
                if (score <= bestScore) continue;
                best = i;
                bestScore = score;
            }
            return best;
        }

        private static int[] Shuffle(int count, Random random)
        {
            var order = Enumerable.Range(0, count).ToArray();
            Shuffle(order, random, -1);
            return order;
        }

        private static void Shuffle(int[] order, Random random, int last)
        {
            for (int i = order.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1), previous = order[i];
                order[i] = order[j]; order[j] = previous;
            }
            if (order.Length > 1 && order[0] == last)
            {
                int j = random.Next(1, order.Length);
                order[0] = order[j]; order[j] = last;
            }
        }

        internal static void Clip(IReadOnlyList<PatternPoint> polygon, double nx, double ny, double cut,
            List<PatternPoint> result)
        {
            result.Clear();
            PatternPoint previous = polygon[polygon.Count - 1];
            double previousDistance = nx * previous.X + ny * previous.Y - cut;
            for (int i = 0; i < polygon.Count; i++)
            {
                var current = polygon[i];
                double distance = nx * current.X + ny * current.Y - cut;
                if ((distance <= 0) != (previousDistance <= 0))
                    result.Add(PatternPoint.Between(previous, current, previousDistance / (previousDistance - distance)));
                if (distance <= 0) result.Add(current);
                previous = current; previousDistance = distance;
            }
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
