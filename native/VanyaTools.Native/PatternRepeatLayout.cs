using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal static partial class PatternFillLayout
    {
        private static List<PatternPlacement> RepeatPattern(double width, double height,
            IReadOnlyList<PatternFootprint> motifs, PatternFillOptions options, out PatternLayoutGuides guides)
        {
            double repeatWidth = options.RepeatWidthMm, repeatHeight = options.RepeatHeightMm;
            if (!Finite(repeatWidth) || !Finite(repeatHeight) || repeatWidth < options.SizeMm ||
                repeatHeight < options.SizeMm || repeatWidth > 5000 || repeatHeight > 5000)
                throw new ArgumentException("Ширина и высота рапорта должны быть от размера ячейки до 5000 мм.");

            CheckCount(Math.Max(1, Math.Floor(width / Math.Max(repeatWidth, repeatHeight))),
                Math.Max(1, Math.Floor(height / Math.Max(repeatWidth, repeatHeight))));
            var module = RepeatModule(motifs, options, out var cells);

            double radians = options.Angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
            double localWidth = Math.Abs(cos) * width + Math.Abs(sin) * height;
            double localHeight = Math.Abs(sin) * width + Math.Abs(cos) * height;
            double left = (repeatWidth - localWidth) / 2, bottom = (repeatHeight - localHeight) / 2;
            double right = left + localWidth, top = bottom + localHeight;
            var placed = new List<PatternPlacement>();
            foreach (var item in module)
            {
                var motif = motifs[item.Motif];
                double scale = PatternTransform.Fit(motif.Size, item).Scale;
                var hull = motif.At(item);
                var bounds = Bounds(hull.Select(ToFrame).ToArray());
                var center = ToFrame(item.Center);
                var cell = item.Cell.Select(ToFrame).ToArray();
                double finalAngle = item.Angle + options.Angle;
                var size = PatternTransform.RotatedSize(motif.Size.X, motif.Size.Y, finalAngle);
                double finalSize = scale * Math.Max(size.X, size.Y);
                Repeat(Bounds(hull), (dx, dy) =>
                {
                    var offset = Rotate(dx, dy);
                    if (!OnFrame(bounds, offset)) return;
                    placed.Add(new PatternPlacement { Motif = item.Motif, Angle = finalAngle, Size = finalSize,
                        Center = new PatternPoint(center.X + offset.X, center.Y + offset.Y),
                        Cell = Offset(cell, offset) });
                    CheckCount(placed.Count, 1);
                });
            }

            if (placed.Count == 0)
                throw new InvalidOperationException("В пределах детали нет фигур. Измените размер рапорта или выберите другой вариант.");

            var cellGuides = cells == null ? null : new List<PatternPoint[]>();
            if (cells != null)
                foreach (var cell in cells)
                {
                    var path = cell.Select(ToFrame).ToArray();
                    var bounds = Bounds(path);
                    Repeat(Bounds(cell), (dx, dy) =>
                    {
                        var offset = Rotate(dx, dy);
                        if (OnFrame(bounds, offset)) cellGuides.Add(Offset(path, offset));
                    });
                }

            var lines = new List<PatternPoint[]>();
            for (int col = (int)Math.Ceiling(left / repeatWidth); col * repeatWidth <= right; col++)
                lines.Add(new[] { ToFrame(new PatternPoint(col * repeatWidth, bottom)), ToFrame(new PatternPoint(col * repeatWidth, top)) });
            for (int row = (int)Math.Ceiling(bottom / repeatHeight); row * repeatHeight <= top; row++)
                lines.Add(new[] { ToFrame(new PatternPoint(left, row * repeatHeight)), ToFrame(new PatternPoint(right, row * repeatHeight)) });
            guides = new PatternLayoutGuides { Cells = cellGuides, RepeatLines = lines };
            return placed;

            void Repeat((double Left, double Bottom, double Right, double Top) bounds, Action<double, double> add)
            {
                int firstX = (int)Math.Ceiling((left - bounds.Right) / repeatWidth);
                int lastX = (int)Math.Floor((right - bounds.Left) / repeatWidth);
                int firstY = (int)Math.Ceiling((bottom - bounds.Top) / repeatHeight);
                int lastY = (int)Math.Floor((top - bounds.Bottom) / repeatHeight);
                for (int row = firstY; row <= lastY; row++)
                    for (int col = firstX; col <= lastX; col++)
                        add(col * repeatWidth, row * repeatHeight);
            }

            bool OnFrame((double Left, double Bottom, double Right, double Top) bounds, PatternPoint offset) =>
                bounds.Right + offset.X > 0 && bounds.Left + offset.X < width &&
                bounds.Top + offset.Y > 0 && bounds.Bottom + offset.Y < height;

            PatternPoint Rotate(double x, double y) => new PatternPoint(x * cos - y * sin, x * sin + y * cos);

            PatternPoint ToFrame(PatternPoint point)
            {
                var rotated = Rotate(point.X - repeatWidth / 2, point.Y - repeatHeight / 2);
                return new PatternPoint(width / 2 + rotated.X, height / 2 + rotated.Y);
            }

            PatternPoint[] Offset(PatternPoint[] points, PatternPoint offset) =>
                points.Select(p => new PatternPoint(p.X + offset.X, p.Y + offset.Y)).ToArray();
        }

        internal static List<PatternPlacement> RepeatModule(IReadOnlyList<PatternFootprint> motifs,
            PatternFillOptions options, out List<PatternPoint[]> cells, double motifAngle = 0)
        {
            double width = options.RepeatWidthMm, height = options.RepeatHeightMm;
            cells = options.Texture ? RepeatCells(width, height, options) : null;
            var module = cells == null ? Motifs(width, height, motifs.Count, options, motifAngle) :
                PlaceInCells(cells, motifs, options, motifAngle: motifAngle);
            if (options.Mode == PatternFillMode.Pebbles)
                PatternPebbleSpacing.Balance(module, motifs, options.GapMm, width, height);
            return module;
        }

        private static List<PatternPoint[]> RepeatCells(double repeatWidth, double repeatHeight, PatternFillOptions options)
        {
            bool scales = options.Mode == PatternFillMode.Crocodile;
            double cols = Math.Max(1, Math.Round(repeatWidth / (options.SizeMm * (scales ? 1.28 : 1))));
            double rows = Math.Max(1, Math.Round(repeatHeight / (options.SizeMm * (scales ? .74 : 1))));
            CheckCount(cols, rows);
            var seeds = ScatterSeeds((int)cols, (int)rows, options.Seed, true);
            if (!scales) seeds = RelaxSeeds(seeds, true);
            var scratch = new List<PatternPoint>(16);
            var buffer = new List<PatternPoint>(16);
            var cells = new List<PatternPoint[]>();
            double pitchX = repeatWidth / cols, pitchY = repeatHeight / rows;
            for (int row = 0; row < rows; row++)
                for (int col = 0; col < cols; col++)
                {
                    var bucket = seeds[col, row];
                    if (bucket == null) continue;
                    foreach (var seed in bucket)
                    {
                        var cell = BuildCell(seed, seeds, col, row, pitchX, pitchY, options.GapMm, scratch, buffer, true);
                        if (cell.Count < 3) continue;
                        cells.Add(cell.Select(p => new PatternPoint(p.X * pitchX, p.Y * pitchY)).ToArray());
                    }
                }

            CheckCount(cells.Count, 1);
            return cells;
        }

        private static (double Left, double Bottom, double Right, double Top) Bounds(PatternPoint[] points) =>
            (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));

        private static double Wrap(double value, int length)
        {
            double remainder = value % length;
            return remainder < 0 ? remainder + length : remainder;
        }

        private static int WrapIndex(int value, int length)
        {
            int remainder = value % length;
            return remainder < 0 ? remainder + length : remainder;
        }
    }
}
