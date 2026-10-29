using System;
using System.Collections.Generic;
using System.Globalization;

namespace VanyaTools.Native
{
    internal sealed class ColorProofGridOptions
    {
        public bool Cmyk = true;
        public bool Compound;
        public bool PrintLegend { get; set; } = true;
        public int[] BaseChannels;
        public int HorizontalChannel;
        public int VerticalChannel = 1;
        public int Columns = 15;
        public int Rows = 15;
        public int StepX = 1;
        public int StepY = 1;
        public int ExtraStep = 1;
        public double CellMm = 20;
        public double TileMm = 30;
        public double GapMm = 1;
        public double SheetWidthMm = 570;
        // Indexed by channel. Axis entries are null; ordinary grids ignore these levels.
        public int[][] ExtraLevels;

        public ColorProofGridOptions Copy()
        {
            var result = (ColorProofGridOptions)MemberwiseClone();
            result.BaseChannels = BaseChannels == null ? null : (int[])BaseChannels.Clone();
            result.ExtraLevels = CloneLevels(ExtraLevels);
            return result;
        }

        public ColorProofGridOptions Clone() => Copy();

        internal static int[][] CloneLevels(int[][] levels)
        {
            if (levels == null) return null;
            var copy = new int[levels.Length][];
            for (int channel = 0; channel < levels.Length; channel++)
                copy[channel] = levels[channel] == null ? null : (int[])levels[channel].Clone();
            return copy;
        }
    }

    internal sealed class ColorProofGridPatch
    {
        public int Row { get; private set; }
        public int Column { get; private set; }
        // Zero-based TL, TR, BL, BR; ordinary cells use zero.
        public int Quadrant { get; private set; }
        public int[] Values { get; private set; }
        // Coordinates start at the grid's top-left and increase right/down, in mm.
        public double Xmm { get; private set; }
        public double Ymm { get; private set; }
        public double WidthMm { get; private set; }
        public double HeightMm { get; private set; }

        internal ColorProofGridPatch(int row, int column, int quadrant, int[] values,
            double x, double y, double width, double height)
        {
            Row = row; Column = column; Quadrant = quadrant; Values = (int[])values.Clone();
            Xmm = x; Ymm = y; WidthMm = width; HeightMm = height;
        }
    }

    // Pure planning only: no Corel COM objects or presentation dependencies.
    internal sealed class ColorProofGridPlan
    {
        public const double MarginMm = 8;
        public const double AxisWidthMm = 0;
        public const double NumberBandMm = 3.5;
        public const int MaxPatches = 3844;
        public const int MaxAxisCount = 31;
        public const double MaxGridHeightMm = 45000;

        public ColorProofGridOptions Options { get; private set; }
        public IReadOnlyList<ColorProofGridPatch> Patches { get; private set; }
        public int[] AxisX { get; private set; }
        public int[] AxisY { get; private set; }
        public int[] BaseChannels { get; private set; }
        public int[][] ExtraLevels { get; private set; }
        public int ReferenceRow { get; private set; }
        public int ReferenceColumn { get; private set; }
        public int ReferenceQuadrant { get; private set; }
        public double WidthMm { get; private set; }
        public double HeightMm { get; private set; }
        public double RequiredWidthMm => 2 * MarginMm + AxisWidthMm + WidthMm +
            (Options.PrintLegend ? ColorProofGridLegendPlan.SideWidthMm : 0);

        public static ColorProofGridPlan Build(ColorProofGridOptions options)
        {
            if (options == null) throw new InvalidOperationException("Не заданы параметры сетки цветопробы.");
            ColorProofGridOptions snapshot = options.Copy();
            int channels = snapshot.Cmyk ? 4 : 3, limit = snapshot.Cmyk ? 100 : 255;
            if (snapshot.BaseChannels == null || snapshot.BaseChannels.Length != channels)
                throw new InvalidOperationException("Для эталона " + (snapshot.Cmyk ? "CMYK нужны четыре канала." : "RGB нужны три канала."));
            for (int channel = 0; channel < channels; channel++)
                CheckValue(snapshot.BaseChannels[channel], limit, "Эталон: канал " + ChannelName(channel, snapshot.Cmyk));
            if (snapshot.HorizontalChannel < 0 || snapshot.HorizontalChannel >= channels ||
                snapshot.VerticalChannel < 0 || snapshot.VerticalChannel >= channels)
                throw new InvalidOperationException("Выберите допустимые каналы горизонтальной и вертикальной осей.");
            if (snapshot.HorizontalChannel == snapshot.VerticalChannel)
                throw new InvalidOperationException("Для горизонтальной и вертикальной осей нужны разные каналы.");
            if (snapshot.Columns < 1 || snapshot.Columns > MaxAxisCount || snapshot.Rows < 1 || snapshot.Rows > MaxAxisCount)
                throw new InvalidOperationException("Число строк и столбцов цветопробы: от 1 до " + MaxAxisCount + ".");
            if (snapshot.StepX <= 0 || snapshot.StepY <= 0)
                throw new InvalidOperationException("Шаг каждой оси должен быть положительным целым числом.");
            if (snapshot.Compound && snapshot.ExtraStep <= 0)
                throw new InvalidOperationException("Шаг дополнительных каналов должен быть положительным целым числом.");
            if (!Finite(snapshot.CellMm) || snapshot.CellMm <= 0 || !Finite(snapshot.TileMm) || snapshot.TileMm <= 0)
                throw new InvalidOperationException("Размер плашки и составной ячейки должен быть положительным числом в миллиметрах.");
            if (!Finite(snapshot.GapMm) || snapshot.GapMm < 0)
                throw new InvalidOperationException("Промежуток между ячейками должен быть неотрицательным числом в миллиметрах.");
            if (!Finite(snapshot.SheetWidthMm) || snapshot.SheetWidthMm < 100 || snapshot.SheetWidthMm > 4000)
                throw new InvalidOperationException("Ширина листа цветопробы: от 100 до 4000 мм.");

            int[] axisX = Axis(snapshot.BaseChannels[snapshot.HorizontalChannel], snapshot.Columns, snapshot.StepX, limit,
                "Горизонтальная ось " + ChannelName(snapshot.HorizontalChannel, snapshot.Cmyk));
            int[] axisY = Axis(snapshot.BaseChannels[snapshot.VerticalChannel], snapshot.Rows, snapshot.StepY, limit,
                "Вертикальная ось " + ChannelName(snapshot.VerticalChannel, snapshot.Cmyk));
            var remaining = new List<int>();
            for (int channel = 0; channel < channels; channel++)
                if (channel != snapshot.HorizontalChannel && channel != snapshot.VerticalChannel) remaining.Add(channel);
            var levels = new int[channels][];
            if (snapshot.Compound)
            {
                if (snapshot.ExtraLevels != null && snapshot.ExtraLevels.Length != channels)
                    throw new InvalidOperationException("Уровни дополнительных каналов должны быть заданы по индексам каналов " +
                        (snapshot.Cmyk ? "CMYK." : "RGB."));
                if (snapshot.ExtraLevels != null && (snapshot.ExtraLevels[snapshot.HorizontalChannel] != null ||
                    snapshot.ExtraLevels[snapshot.VerticalChannel] != null))
                    throw new InvalidOperationException("Для каналов осей отдельные уровни не задаются.");
                int[] levelCounts = snapshot.Cmyk ? new[] { 2, 2 } : new[] { 4 };
                if (snapshot.Cmyk)
                {
                    int[] first = snapshot.ExtraLevels == null ? null : snapshot.ExtraLevels[remaining[0]];
                    int[] second = snapshot.ExtraLevels == null ? null : snapshot.ExtraLevels[remaining[1]];
                    if ((first != null && !AllowedLevelCount(first.Length)) ||
                        (second != null && !AllowedLevelCount(second.Length)))
                        throw new InvalidOperationException("Для дополнительного канала CMYK допустимы 1, 2 или 4 различных уровня.");
                    levelCounts[0] = first == null ? (second == null ? 2 : 4 / second.Length) : first.Length;
                    levelCounts[1] = second == null ? 4 / levelCounts[0] : second.Length;
                    if (levelCounts[0] * levelCounts[1] != 4)
                        throw new InvalidOperationException("Дополнительные каналы CMYK должны давать четыре сочетания: 1×4, 2×2 или 4×1.");
                }
                for (int residual = 0; residual < remaining.Count; residual++)
                {
                    int channel = remaining[residual], expected = levelCounts[residual];
                    int[] specified = snapshot.ExtraLevels == null ? null : snapshot.ExtraLevels[channel];
                    if (specified == null)
                    {
                        if (snapshot.Cmyk && expected == 1)
                            levels[channel] = new[] { snapshot.BaseChannels[channel] };
                        else if (snapshot.Cmyk && expected == 2)
                        {
                            int baseline = snapshot.BaseChannels[channel];
                            long upper = (long)baseline + snapshot.ExtraStep, lower = (long)baseline - snapshot.ExtraStep;
                            if (upper <= limit) levels[channel] = new[] { baseline, (int)upper };
                            else if (lower >= 0) levels[channel] = new[] { baseline, (int)lower };
                            else throw new InvalidOperationException("Канал " + ChannelName(channel, snapshot.Cmyk) +
                                ": дополнительный шаг не помещается в диапазон 0–" + limit + ". Уменьшите шаг.");
                        }
                        else levels[channel] = Axis(snapshot.BaseChannels[channel], expected, snapshot.ExtraStep, limit,
                            "Дополнительный канал " + ChannelName(channel, snapshot.Cmyk));
                    }
                    else
                    {
                        if (specified.Length != expected)
                            throw new InvalidOperationException("Канал " + ChannelName(channel, snapshot.Cmyk) +
                                ": задайте " + expected + " различных уровня, включая эталон.");
                        var seen = new HashSet<int>();
                        foreach (int value in specified)
                        {
                            CheckValue(value, limit, "Канал " + ChannelName(channel, snapshot.Cmyk));
                            if (!seen.Add(value)) throw new InvalidOperationException("Канал " + ChannelName(channel, snapshot.Cmyk) +
                                ": уровни не должны повторяться.");
                        }
                        if (!seen.Contains(snapshot.BaseChannels[channel]))
                            throw new InvalidOperationException("Канал " + ChannelName(channel, snapshot.Cmyk) +
                                ": среди уровней должно быть эталонное значение " + snapshot.BaseChannels[channel] + ".");
                        levels[channel] = (int[])specified.Clone();
                    }
                }
            }

            double size = snapshot.Compound ? snapshot.TileMm : snapshot.CellMm;
            double width = snapshot.Columns * size + (snapshot.Columns - 1) * snapshot.GapMm;
            double labelsPerRow = snapshot.Compound ? 2 : 1;
            double rowPitch = size + labelsPerRow * NumberBandMm + snapshot.GapMm;
            double height = snapshot.Rows * (size + labelsPerRow * NumberBandMm) + (snapshot.Rows - 1) * snapshot.GapMm;
            double required = 2 * MarginMm + AxisWidthMm + width +
                (snapshot.PrintLegend ? ColorProofGridLegendPlan.SideWidthMm : 0);
            if (!Finite(width) || !Finite(height) || !Finite(required))
                throw new InvalidOperationException("Габариты сетки слишком велики. Уменьшите размеры ячеек или промежутки.");
            if (required > snapshot.SheetWidthMm + 0.000001)
                throw new InvalidOperationException((snapshot.PrintLegend ? "Сетка с полями и легендой справа требует ширину " : "Сетка с полями требует ширину ") +
                    required.ToString("0.###", CultureInfo.CurrentCulture) + " мм. Увеличьте ширину листа или уменьшите сетку.");
            if (height > MaxGridHeightMm)
                throw new InvalidOperationException("Высота сетки цветопробы превышает 45 м. Уменьшите число строк или размер ячейки.");
            int quadrantCount = snapshot.Compound ? 4 : 1;
            int patchCount = checked(snapshot.Rows * snapshot.Columns * quadrantCount);
            if (patchCount > MaxPatches)
                throw new InvalidOperationException("В цветопробе допускается не больше " + MaxPatches + " плашек.");
            var patches = new List<ColorProofGridPatch>(patchCount);
            for (int row = 0; row < snapshot.Rows; row++)
            for (int column = 0; column < snapshot.Columns; column++)
            for (int quadrant = 0; quadrant < quadrantCount; quadrant++)
            {
                int[] values = (int[])snapshot.BaseChannels.Clone();
                values[snapshot.HorizontalChannel] = axisX[column];
                values[snapshot.VerticalChannel] = axisY[row];
                if (snapshot.Compound)
                {
                    if (snapshot.Cmyk)
                    {
                        int firstCount = levels[remaining[0]].Length;
                        values[remaining[0]] = levels[remaining[0]][quadrant % firstCount];
                        values[remaining[1]] = levels[remaining[1]][quadrant / firstCount];
                    }
                    else values[remaining[0]] = levels[remaining[0]][quadrant];
                }
                double patchSize = snapshot.Compound ? size / 2 : size;
                double x = column * (size + snapshot.GapMm) + (snapshot.Compound ? (quadrant % 2) * patchSize : 0);
                double y = row * rowPitch + (snapshot.Compound ? (quadrant / 2) * (patchSize + NumberBandMm) : 0);
                patches.Add(new ColorProofGridPatch(row, column, quadrant, values, x, y, patchSize, patchSize));
            }
            int referenceQuadrant = 0;
            if (snapshot.Compound)
                referenceQuadrant = snapshot.Cmyk
                    ? Array.IndexOf(levels[remaining[1]], snapshot.BaseChannels[remaining[1]]) * levels[remaining[0]].Length +
                        Array.IndexOf(levels[remaining[0]], snapshot.BaseChannels[remaining[0]])
                    : Array.IndexOf(levels[remaining[0]], snapshot.BaseChannels[remaining[0]]);
            return new ColorProofGridPlan {
                Options = snapshot,
                Patches = patches.AsReadOnly(),
                AxisX = axisX,
                AxisY = axisY,
                BaseChannels = (int[])snapshot.BaseChannels.Clone(),
                ExtraLevels = ColorProofGridOptions.CloneLevels(levels),
                ReferenceRow = Array.IndexOf(axisY, snapshot.BaseChannels[snapshot.VerticalChannel]),
                ReferenceColumn = Array.IndexOf(axisX, snapshot.BaseChannels[snapshot.HorizontalChannel]),
                ReferenceQuadrant = referenceQuadrant,
                WidthMm = width,
                HeightMm = height
            };
        }

        // Center on the reference while possible; shift by whole steps at channel bounds.
        // Even counts put the reference at count / 2 before any boundary shift.
        internal static int[] BuildAxis(int baseline, int count, int step, int limit)
            => Axis(baseline, count, step, limit, "Ось цветопробы");

        private static int[] Axis(int baseline, int count, int step, int limit, string title)
        {
            if (limit < 0 || baseline < 0 || baseline > limit || count < 1 || count > MaxAxisCount || step <= 0)
                throw new InvalidOperationException(title + ": некорректные эталон, число значений или шаг.");
            long lowerCapacity = baseline / step, upperCapacity = (limit - baseline) / step;
            if (lowerCapacity + upperCapacity + 1 < count)
                throw new InvalidOperationException(title + ": в диапазоне 0–" + limit + " при шаге " + step +
                    " не помещается " + count + " различных значений с эталоном " + baseline + ". Уменьшите шаг или число ячеек.");
            int below = (int)Math.Min(count / 2, lowerCapacity);
            if (count - 1 - below > upperCapacity) below = count - 1 - (int)upperCapacity;
            long start = (long)baseline - (long)below * step;
            var axis = new int[count];
            for (int index = 0; index < count; index++) axis[index] = checked((int)(start + (long)index * step));
            return axis;
        }

        private static bool AllowedLevelCount(int count) => count == 1 || count == 2 || count == 4;
        private static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
        private static void CheckValue(int value, int limit, string label)
        {
            if (value < 0 || value > limit)
                throw new InvalidOperationException(label + ": значение должно быть от 0 до " + limit + ".");
        }
        private static string ChannelName(int channel, bool cmyk) => (cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" })[channel];
    }
}
