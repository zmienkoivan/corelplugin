using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal sealed class ColorProofGridSeriesOptions
    {
        public ColorProofGridOptions Grid = new ColorProofGridOptions();
        public int Channel = 2;
        public int Count = 5;
        public int Step = 1;
        // Explicit integer channel offsets from the original reference; null selects automatic values.
        public int[] Offsets;

        public ColorProofGridSeriesOptions Copy()
        {
            var copy = (ColorProofGridSeriesOptions)MemberwiseClone();
            copy.Grid = Grid == null ? null : Grid.Copy();
            copy.Offsets = Offsets == null ? null : (int[])Offsets.Clone();
            return copy;
        }

        public ColorProofGridSeriesOptions Clone() => Copy();
    }

    // A complete series is validated before any renderer can create Corel pages.
    internal sealed class ColorProofGridSeriesPlan
    {
        public const int MaxPages = 9;
        public const int MaxTotalPatchCount = 10000;

        public ColorProofGridSeriesOptions Options { get; private set; }
        public IReadOnlyList<ColorProofGridPlan> Pages { get; private set; }
        public int[] OriginalBaseChannels { get; private set; }
        public int[] ChannelValues { get; private set; }
        public int[] Offsets { get; private set; }
        public int TotalPatchCount { get; private set; }
        public int ReferencePageIndex { get; private set; }

        public static ColorProofGridSeriesPlan Build(ColorProofGridSeriesOptions options)
        {
            if (options == null) throw new InvalidOperationException("Не заданы параметры серии цветопроб.");
            ColorProofGridSeriesOptions snapshot = options.Copy();
            ColorProofGridOptions grid = snapshot.Grid;
            if (grid == null) throw new InvalidOperationException("Не заданы параметры сетки для серии цветопроб.");
            int channelCount = grid.Cmyk ? 4 : 3, limit = grid.Cmyk ? 100 : 255;
            if (grid.BaseChannels == null || grid.BaseChannels.Length != channelCount)
                throw new InvalidOperationException("Для эталона серии " + (grid.Cmyk ? "CMYK нужны четыре канала." : "RGB нужны три канала."));
            int[] original = (int[])grid.BaseChannels.Clone();
            for (int channel = 0; channel < channelCount; channel++)
                if (original[channel] < 0 || original[channel] > limit)
                    throw new InvalidOperationException("Эталон: значение канала " + ChannelName(channel, grid.Cmyk) +
                        " должно быть от 0 до " + limit + ".");
            if (grid.HorizontalChannel < 0 || grid.HorizontalChannel >= channelCount ||
                grid.VerticalChannel < 0 || grid.VerticalChannel >= channelCount ||
                grid.HorizontalChannel == grid.VerticalChannel)
                throw new InvalidOperationException("Выберите два разных допустимых канала осей сетки.");
            if (snapshot.Channel < 0 || snapshot.Channel >= channelCount)
                throw new InvalidOperationException("Выберите допустимый канал для изменения между листами.");
            if (snapshot.Channel == grid.HorizontalChannel || snapshot.Channel == grid.VerticalChannel)
                throw new InvalidOperationException("Канал серии должен отличаться от горизонтальной и вертикальной осей.");
            if (grid.Compound && !grid.Cmyk)
                throw new InvalidOperationException("Составная серия поддерживается только для CMYK. Для RGB выберите обычные плашки.");

            int[] values, offsets;
            int baseline = original[snapshot.Channel];
            if (snapshot.Offsets == null)
            {
                if (snapshot.Count < 1 || snapshot.Count > MaxPages)
                    throw new InvalidOperationException("Число листов серии цветопроб: от 1 до " + MaxPages + ".");
                if (snapshot.Step <= 0)
                    throw new InvalidOperationException("Шаг канала серии должен быть положительным целым числом.");
                values = ColorProofGridPlan.BuildAxis(baseline, snapshot.Count, snapshot.Step, limit);
                offsets = new int[values.Length];
                for (int index = 0; index < values.Length; index++) offsets[index] = values[index] - baseline;
            }
            else
            {
                offsets = (int[])snapshot.Offsets.Clone();
                if (offsets.Length < 1 || offsets.Length > MaxPages)
                    throw new InvalidOperationException("Задайте от 1 до " + MaxPages + " смещений канала серии.");
                values = new int[offsets.Length];
                var unique = new HashSet<int>();
                bool reference = false;
                for (int index = 0; index < offsets.Length; index++)
                {
                    int offset = offsets[index];
                    if (!unique.Add(offset))
                        throw new InvalidOperationException("Смещения канала серии не должны повторяться.");
                    reference |= offset == 0;
                    long value = (long)baseline + offset;
                    if (value < 0 || value > limit)
                        throw new InvalidOperationException("Смещение " + offset + " для канала " + ChannelName(snapshot.Channel, grid.Cmyk) +
                            " выводит значение за диапазон 0–" + limit + ". Измените смещения; значения не обрезаются.");
                    values[index] = (int)value;
                }
                if (!reference)
                    throw new InvalidOperationException("Среди смещений канала серии должен быть 0 — лист исходного эталона.");
            }

            int otherChannel = -1;
            int[] otherLevels = null;
            if (grid.Compound)
            {
                if (grid.ExtraLevels != null && grid.ExtraLevels.Length != channelCount)
                    throw new InvalidOperationException("Уровни дополнительных каналов должны быть заданы по индексам CMYK.");
                if (grid.ExtraLevels != null && (grid.ExtraLevels[grid.HorizontalChannel] != null ||
                    grid.ExtraLevels[grid.VerticalChannel] != null))
                    throw new InvalidOperationException("Для каналов осей отдельные уровни не задаются.");
                for (int channel = 0; channel < channelCount; channel++)
                    if (channel != grid.HorizontalChannel && channel != grid.VerticalChannel && channel != snapshot.Channel)
                        otherChannel = channel;
                int[] specified = grid.ExtraLevels == null ? null : grid.ExtraLevels[otherChannel];
                if (specified == null)
                {
                    if (grid.ExtraStep <= 0)
                        throw new InvalidOperationException("Шаг дополнительного канала должен быть положительным целым числом.");
                    otherLevels = ColorProofGridPlan.BuildAxis(original[otherChannel], 4, grid.ExtraStep, limit);
                }
                else
                {
                    if (specified.Length != 4)
                        throw new InvalidOperationException("В составной серии канал " + ChannelName(otherChannel, grid.Cmyk) +
                            " должен содержать четыре различных уровня, включая исходный эталон.");
                    var unique = new HashSet<int>();
                    foreach (int value in specified)
                    {
                        if (value < 0 || value > limit)
                            throw new InvalidOperationException("Канал " + ChannelName(otherChannel, grid.Cmyk) +
                                ": значения уровней должны быть от 0 до " + limit + ".");
                        if (!unique.Add(value))
                            throw new InvalidOperationException("Канал " + ChannelName(otherChannel, grid.Cmyk) + ": уровни не должны повторяться.");
                    }
                    if (!unique.Contains(original[otherChannel]))
                        throw new InvalidOperationException("Канал " + ChannelName(otherChannel, grid.Cmyk) +
                            ": среди уровней должно быть исходное эталонное значение " + original[otherChannel] + ".");
                    otherLevels = (int[])specified.Clone();
                }
            }

            var pages = new List<ColorProofGridPlan>(values.Length);
            int total = 0;
            for (int index = 0; index < values.Length; index++)
            {
                ColorProofGridOptions pageOptions = grid.Copy();
                pageOptions.BaseChannels = (int[])original.Clone();
                pageOptions.BaseChannels[snapshot.Channel] = values[index];
                if (grid.Compound)
                {
                    // The series channel is fixed on this page, regardless of previously entered levels.
                    pageOptions.ExtraLevels = new int[channelCount][];
                    pageOptions.ExtraLevels[snapshot.Channel] = new[] { values[index] };
                    pageOptions.ExtraLevels[otherChannel] = (int[])otherLevels.Clone();
                }
                ColorProofGridPlan page;
                try { page = ColorProofGridPlan.Build(pageOptions); }
                catch (InvalidOperationException error)
                { throw new InvalidOperationException("Лист серии " + (index + 1) + ": " + error.Message, error); }
                total = checked(total + page.Patches.Count);
                if (total > MaxTotalPatchCount)
                    throw new InvalidOperationException("Серия содержит больше " + MaxTotalPatchCount +
                        " плашек. Уменьшите число листов, строк или столбцов.");
                pages.Add(page);
            }
            return new ColorProofGridSeriesPlan {
                Options = snapshot,
                Pages = pages.AsReadOnly(),
                OriginalBaseChannels = (int[])original.Clone(),
                ChannelValues = (int[])values.Clone(),
                Offsets = (int[])offsets.Clone(),
                TotalPatchCount = total,
                ReferencePageIndex = Array.IndexOf(offsets, 0)
            };
        }

        private static string ChannelName(int channel, bool cmyk)
            => (cmyk ? new[] { "C", "M", "Y", "K" } : new[] { "R", "G", "B" })[channel];
    }
}
