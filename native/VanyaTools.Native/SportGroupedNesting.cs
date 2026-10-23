using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal static class SportGroupedNesting
    {
        private sealed class Unit
        {
            internal SportSheetNesting.Item Item;
            internal IList<SportSheetNesting.Placement> Members;
        }

        internal static SportSheetNesting.Result Place(IList<SportSheetNesting.Item> items, double width,
            double limit, double gap, SportNestingMode density, SportGroupingMode grouping, bool improve = true)
        {
            if (!Enum.IsDefined(typeof(SportGroupingMode), grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
            if (grouping == SportGroupingMode.Free)
                return SportSheetNesting.Place(items, width, limit, gap, density, improve);
            var indexed = Enumerable.Range(0, items.Count).ToList();
            var units = new List<Unit>();
            double step = SportSheetNesting.GridStep(width, density);
            foreach (var garment in indexed.Where(index => grouping == SportGroupingMode.Garment || items[index].Part < 2)
                .GroupBy(index => items[index].Garment))
            {
                SportNestingCancellation.Check();
                var indices = garment.OrderBy(index => items[index].Part).ToList();
                var inside = SportSheetNesting.Place(indices.Select(index => items[index]).ToList(),
                    width, limit, gap, density, false);
                if (inside.PageHeights.Count != 1)
                    throw new InvalidOperationException((grouping == SportGroupingMode.Garment ? "Комплект №" : "Пара перед/спина №") +
                        garment.Key + " не помещается целиком на один лист. Увеличьте ширину/длину страницы или выберите свободную раскладку.");
                var bands = new List<SportContourNesting.Band>();
                foreach (var placed in inside.Placements)
                {
                    placed.Index = indices[placed.Index];
                    for (int column = 0; column < placed.Profile.Top.Length; column++)
                        bands.Add(new SportContourNesting.Band { Left = placed.Left + column * step,
                            Right = placed.Left + Math.Min(placed.Width, (column + 1) * step),
                            Top = placed.Top + placed.Profile.Top[column], Bottom = placed.Top + placed.Profile.Bottom[column] });
                }
                units.Add(new Unit { Members = inside.Placements,
                    Item = new SportSheetNesting.Item { Width = inside.Placements.Max(part => part.Left + part.Width),
                        Label = (grouping == SportGroupingMode.Garment ? "Комплект №" : "Пара перед/спина №") + garment.Key,
                        Height = inside.PageHeights[0], Bands = bands } });
            }
            if (units.Count == 0) return SportSheetNesting.Place(items, width, limit, gap, density, improve);
            var outside = SportSheetNesting.Place(units.Select(unit => unit.Item).ToList(), width, limit, gap, density, improve);
            var result = new SportSheetNesting.Result { PageHeights = outside.PageHeights.ToList(),
                Placements = new List<SportSheetNesting.Placement>() };
            foreach (var block in outside.Placements)
                foreach (var member in units[block.Index].Members)
                    result.Placements.Add(new SportSheetNesting.Placement { Index = member.Index, Page = block.Page,
                        Left = block.Left + member.Left, Top = block.Top + member.Top,
                        Column = (int)Math.Round((block.Left + member.Left) / step),
                        Width = member.Width, Height = member.Height, Rotation = member.Rotation, Profile = member.Profile });

            if (grouping == SportGroupingMode.FrontBackPairs)
            {
                // Keep torso pairs pinned, but use each actual member contour
                // as an obstacle. Sleeves/neck can fill pockets on every page,
                // including spaces hidden by a pair's combined envelope.
                if (indexed.Any(index => items[index].Part >= 2))
                    result = SportSheetNesting.Place(items, width, limit, gap, density, fixedLayout: result);
            }
            if (result.Placements.Count != items.Count || result.Placements.Select(part => part.Index).Distinct().Count() != items.Count ||
                result.PageHeights.Any(height => height > limit + 0.001) || result.Placements.Any(part =>
                    part.Left < -0.001 || part.Left + part.Width > width + 0.001 || part.Top < -0.001 ||
                    part.Top + part.Height > result.PageHeights[part.Page] + 0.001))
                throw new InvalidOperationException("Не удалось сохранить все детали внутри групповой раскладки.");
            return result;
        }
    }
}
