using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal sealed class ColorProofReferenceFrameRect
    {
        public double Xmm { get; private set; }
        public double Ymm { get; private set; }
        public double WidthMm { get; private set; }
        public double HeightMm { get; private set; }
        public bool White { get; private set; }

        internal ColorProofReferenceFrameRect(double x, double y, double width, double height, bool white)
        { Xmm = x; Ymm = y; WidthMm = width; HeightMm = height; White = white; }
    }

    // Filled strips strictly inside a patch; no native outline can enlarge its bounds.
    internal static class ColorProofReferenceFrame
    {
        public static IReadOnlyList<ColorProofReferenceFrameRect> Build(double xmm, double ymm,
            double widthmm, double heightmm)
        {
            if (!Finite(xmm) || !Finite(ymm) || !Finite(widthmm) || !Finite(heightmm))
                throw new ArgumentException("Координаты и размеры рамки должны быть конечными числами.");
            if (widthmm <= 0 || heightmm <= 0)
                throw new ArgumentException("Ширина и высота плашки для рамки должны быть положительными.");
            double right = xmm + widthmm, bottom = ymm + heightmm;
            if (!Finite(right) || !Finite(bottom) || right <= xmm || bottom <= ymm)
                throw new ArgumentException("Границы рамки нельзя представить с заданными координатами и размерами.");
            double thickness = Math.Min(0.3, Math.Min(widthmm, heightmm) / 20);
            if (!Finite(thickness) || thickness <= 0)
                throw new ArgumentException("Размер плашки слишком мал для положительной толщины рамки.");
            // Shared boundaries keep the bands adjacent even when coordinates are rounded.
            double[] x = Edges(xmm, right, thickness), y = Edges(ymm, bottom, thickness);
            var rectangles = new List<ColorProofReferenceFrameRect>(8);
            AddBand(rectangles, x[1], y[1], x[6], y[6], x[2], y[2], x[5], y[5], true);
            AddBand(rectangles, x[2], y[2], x[5], y[5], x[3], y[3], x[4], y[4], false);
            foreach (ColorProofReferenceFrameRect rectangle in rectangles)
            {
                double rectangleRight = rectangle.Xmm + rectangle.WidthMm;
                double rectangleBottom = rectangle.Ymm + rectangle.HeightMm;
                if (!Finite(rectangle.WidthMm) || !Finite(rectangle.HeightMm) ||
                    rectangle.WidthMm <= 0 || rectangle.HeightMm <= 0 ||
                    rectangle.Xmm <= xmm || rectangle.Ymm <= ymm ||
                    !Finite(rectangleRight) || !Finite(rectangleBottom) || rectangleRight >= right || rectangleBottom >= bottom)
                    throw new ArgumentException("При заданных координатах невозможно разместить рамку строго внутри плашки.");
            }
            return rectangles.AsReadOnly();
        }

        private static double[] Edges(double start, double end, double thickness)
        {
            var edges = new[] { start, start + thickness, start + 2 * thickness, start + 3 * thickness,
                end - 3 * thickness, end - 2 * thickness, end - thickness, end };
            for (int index = 1; index < edges.Length; index++)
                if (!Finite(edges[index]) || edges[index] <= edges[index - 1])
                    throw new ArgumentException("Точности координат недостаточно для рамки: уменьшите координаты или увеличьте плашку.");
            return edges;
        }

        private static void AddBand(List<ColorProofReferenceFrameRect> result,
            double left, double top, double right, double bottom,
            double innerLeft, double innerTop, double innerRight, double innerBottom, bool white)
        {
            // Top and bottom own the corners; side strips occupy only the space between them.
            result.Add(new ColorProofReferenceFrameRect(left, top, right - left, innerTop - top, white));
            result.Add(new ColorProofReferenceFrameRect(left, innerBottom, right - left, bottom - innerBottom, white));
            result.Add(new ColorProofReferenceFrameRect(left, innerTop, innerLeft - left, innerBottom - innerTop, white));
            result.Add(new ColorProofReferenceFrameRect(innerRight, innerTop, right - innerRight, innerBottom - innerTop, white));
        }

        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }
}
