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

        private static PatternPoint[] Rectangle(PatternPoint size)
        {
            double x = Math.Max(.0001, size.X) / 2, y = Math.Max(.0001, size.Y) / 2;
            return new[] { new PatternPoint(-x, -y), new PatternPoint(x, -y),
                new PatternPoint(x, y), new PatternPoint(-x, y) };
        }

        private static PatternPoint[] ConvexHull(IEnumerable<PatternPoint> points)
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

    internal static class PatternFillLayout
    {
        internal const int MaximumElements = 2400;

        internal static List<PatternPlacement> Build(double width, double height, IReadOnlyList<PatternFootprint> motifs, PatternFillOptions options)
        {
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
                var items = PlaceInCells(TextureCells(width, height, options), motifs, options);
                if (options.Mode == PatternFillMode.Pebbles) PatternPebbleSpacing.Balance(items, motifs, options.GapMm);
                return items;
            }
            if (!Finite(options.MinScale) || !Finite(options.MaxScale) || options.MinScale < .1 ||
                options.MaxScale > 3 || options.MinScale > options.MaxScale)
                throw new ArgumentException("Задайте диапазон размеров от 10% до 300%, сначала меньшее значение.");
            return Motifs(width, height, motifs.Count, options);
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
            int[] order = scattered ? Shuffle(motifs, random) : null;
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
                    if (scattered && result.Count > 0 && result.Count % motifs == 0) Shuffle(order, random, result[result.Count - 1].Motif);
                    int index = scattered ? order[result.Count % motifs] : (result.Count + (int)((uint)options.Seed % (uint)motifs)) % motifs;
                    result.Add(new PatternPlacement { Center = new PatternPoint(x, y), Motif = index, Size = size,
                        Angle = options.Angle + Signed(random) * options.AngleVariation });
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

        private static List<PatternPoint>[,] RelaxSeeds(List<PatternPoint>[,] seeds)
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
                        if (col > 0 && col < cols - 1 && row > 0 && row < rows - 1)
                        {
                            var cell = BuildCell(seed, seeds, col, row, 1, 1, 0, polygon, buffer);
                            if (cell.Count >= 3) point = PatternPoint.Between(seed, AreaCenter(cell), .75);
                        }
                        if (point.X < 0 || point.Y < 0 || point.X >= cols || point.Y >= rows) point = seed;
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
            int col, int row, double pitchX, double pitchY, double gap, List<PatternPoint> polygon, List<PatternPoint> buffer)
        {
            int cols = seeds.GetLength(0), rows = seeds.GetLength(1);
            double halfGap = gap / (2 * Math.Min(pitchX, pitchY));
            polygon.Clear();
            polygon.Add(new PatternPoint(seed.X - 2, seed.Y - 2));
            polygon.Add(new PatternPoint(seed.X + 2, seed.Y - 2));
            polygon.Add(new PatternPoint(seed.X + 2, seed.Y + 2));
            polygon.Add(new PatternPoint(seed.X - 2, seed.Y + 2));
            // Stop once even the nearest seed in the remaining buckets cannot cut the cell.
            for (int ring = 0; ring <= 7 && polygon.Count >= 3; ring++)
            {
                double radiusSquared = polygon.Max(p => SquaredDistance(p, seed));
                if ((ring - 1) / 2d >= Math.Sqrt(radiusSquared) + halfGap) break;
                for (int y = Math.Max(0, row - ring); y <= Math.Min(rows - 1, row + ring) && polygon.Count >= 3; y++)
                for (int x = Math.Max(0, col - ring); x <= Math.Min(cols - 1, col + ring) && polygon.Count >= 3; x++)
                {
                    if (Math.Max(Math.Abs(x - col), Math.Abs(y - row)) != ring || seeds[x, y] == null) continue;
                    foreach (var neighbor in seeds[x, y])
                    {
                        double nx = neighbor.X - seed.X, ny = neighbor.Y - seed.Y;
                        if ((nx == 0 && ny == 0) || polygon.Count < 3) continue;
                        double mx = nx / pitchX, my = ny / pitchY;
                        double cut = (nx * (neighbor.X + seed.X) + ny * (neighbor.Y + seed.Y)) / 2 -
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

        private static List<PatternPoint>[,] ScatterSeeds(int cols, int rows, int seed)
        {
            var random = new Random(seed);
            var buckets = new List<PatternPoint>[cols, rows];
            const double minimumDistance = .74;
            for (int attempt = 0; attempt < cols * rows * 28; attempt++)
            {
                var point = new PatternPoint(random.NextDouble() * cols, random.NextDouble() * rows);
                int col = (int)point.X, row = (int)point.Y;
                bool tooClose = false;
                for (int y = Math.Max(0, row - 1); y <= Math.Min(rows - 1, row + 1) && !tooClose; y++)
                for (int x = Math.Max(0, col - 1); x <= Math.Min(cols - 1, col + 1) && !tooClose; x++)
                {
                    var bucket = buckets[x, y];
                    if (bucket == null) continue;
                    foreach (var neighbor in bucket)
                    {
                        if (SquaredDistance(point, neighbor) >= minimumDistance * minimumDistance) continue;
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

        private static List<PatternPlacement> PlaceInCells(List<PatternPoint[]> cells, IReadOnlyList<PatternFootprint> motifs,
            PatternFillOptions options)
        {
            var random = new Random(options.Seed);
            bool pebbles = options.Mode == PatternFillMode.Pebbles;
            int[] order = pebbles ? Shuffle(motifs.Count, random) : null;
            var result = new List<PatternPlacement>(cells.Count);
            var centers = new List<PatternPoint>(16);
            var buffer = new List<PatternPoint>(16);
            var edges = new List<(double X, double Y, double Limit, double Support)>(16);
            int[] useCounts = pebbles ? null : new int[motifs.Count];
            var neighborDistances = pebbles ? null : new double[motifs.Count];
            var placedCenters = pebbles ? null : new List<PatternPoint>(cells.Count);
            int offset = 0;
            foreach (var cell in cells)
            {
                var center = pebbles ? AreaCenter(cell) : new PatternPoint(cell.Average(p => p.X), cell.Average(p => p.Y));
                var target = center;
                int index = pebbles ? order[offset] :
                    ChooseCrocodileMotif(result, placedCenters, center, options.SizeMm, useCounts, neighborDistances, random);
                var motif = motifs[index];
                double angle = options.Angle + Signed(random) * options.AngleVariation;
                double radians = angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
                double scale = Double.PositiveInfinity, limit = Double.PositiveInfinity;
                edges.Clear();
                for (int edge = 0; edge < cell.Length; edge++)
                {
                    var a = cell[edge]; var b = cell[(edge + 1) % cell.Length];
                    double nx = b.Y - a.Y, ny = a.X - b.X;
                    double localX = nx * cos + ny * sin, localY = -nx * sin + ny * cos;
                    double support = 0, opposite = 0;
                    foreach (var point in motif.Hull)
                    {
                        double projection = localX * (point.X - motif.Center.X) + localY * (point.Y - motif.Center.Y);
                        support = Math.Max(support, projection);
                        opposite = Math.Min(opposite, projection);
                    }
                    if (support <= 1e-10) continue;
                    double cut = nx * a.X + ny * a.Y;
                    edges.Add((nx, ny, cut, support));
                    scale = Math.Min(scale, (cut - nx * center.X - ny * center.Y) / support);
                    double width = cut - cell.Min(p => nx * p.X + ny * p.Y);
                    limit = Math.Min(limit, width / (support - opposite));
                }
                if (!Finite(scale) || scale <= 0) continue;

                // For a given scale, clip the cell by the motif's supports to find all valid translations.
                for (int step = 0; step < 7 && limit > scale * 1.005; step++)
                {
                    double candidate = (scale + limit) / 2;
                    centers.Clear();
                    if (pebbles)
                    {
                        double drift = options.SizeMm * .18;
                        centers.Add(new PatternPoint(target.X - drift, target.Y - drift));
                        centers.Add(new PatternPoint(target.X + drift, target.Y - drift));
                        centers.Add(new PatternPoint(target.X + drift, target.Y + drift));
                        centers.Add(new PatternPoint(target.X - drift, target.Y + drift));
                    }
                    else centers.AddRange(cell);
                    foreach (var edge in edges)
                    {
                        Clip(centers, edge.X, edge.Y, edge.Limit - edge.Support * candidate, buffer);
                        var previous = centers; centers = buffer; buffer = previous;
                        if (centers.Count == 0) break;
                    }
                    if (centers.Count == 0)
                    {
                        limit = candidate;
                        continue;
                    }
                    scale = candidate;
                    center = new PatternPoint(centers.Average(p => p.X), centers.Average(p => p.Y));
                }
                double x = center.X - scale * (motif.Center.X * cos - motif.Center.Y * sin);
                double y = center.Y - scale * (motif.Center.X * sin + motif.Center.Y * cos);
                var rotated = PatternTransform.RotatedSize(motif.Size.X, motif.Size.Y, angle);
                result.Add(new PatternPlacement { Motif = index, Center = new PatternPoint(x, y), Angle = angle,
                    Size = scale * Math.Max(rotated.X, rotated.Y) });
                if (!pebbles)
                {
                    useCounts[index]++;
                    placedCenters.Add(center);
                }
                else if (++offset == order.Length)
                {
                    Shuffle(order, random, index);
                    offset = 0;
                }
            }
            if (result.Count == 0) throw new InvalidOperationException("Уменьшите зазор, чтобы разместить фигуры в ячейках.");
            return result;
        }

        private static int ChooseCrocodileMotif(List<PatternPlacement> placed, List<PatternPoint> centers,
            PatternPoint center, double pitch, int[] useCounts, double[] nearest, Random random)
        {
            double radius = pitch * 1.65;
            double radiusSquared = radius * radius;
            for (int i = 0; i < nearest.Length; i++) nearest[i] = radiusSquared;
            for (int i = 0; i < placed.Count; i++)
            {
                int motif = placed[i].Motif;
                nearest[motif] = Math.Min(nearest[motif], SquaredDistance(center, centers[i]));
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

        private static void Clip(IReadOnlyList<PatternPoint> polygon, double nx, double ny, double cut,
            List<PatternPoint> result)
        {
            result.Clear();
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
