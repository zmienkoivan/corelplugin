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
            double limit, double gap, SportNestingMode density, SportGroupingMode grouping)
        {
            if (!Enum.IsDefined(typeof(SportGroupingMode), grouping)) throw new ArgumentOutOfRangeException(nameof(grouping));
            if (grouping == SportGroupingMode.Free)
                return SportSheetNesting.Place(items, width, limit, gap, density);
            var indexed = Enumerable.Range(0, items.Count).ToList();
            var units = new List<Unit>();
            foreach (var garment in indexed.Where(index => grouping == SportGroupingMode.Garment || items[index].Part < 2)
                .GroupBy(index => items[index].Garment))
            {
                var indices = garment.OrderBy(index => items[index].Part).ToList();
                var inside = SportSheetNesting.Place(indices.Select(index => items[index]).ToList(),
                    width, limit, gap, density, false);
                if (inside.PageHeights.Count != 1)
                    throw new InvalidOperationException((grouping == SportGroupingMode.Garment ? "Комплект №" : "Пара перед/спина №") +
                        garment.Key + " не помещается целиком на один лист. Увеличьте ширину/длину страницы или выберите свободную раскладку.");
                var bands = new List<SportContourNesting.Band>();
                double step = SportSheetNesting.GridStep(width, density);
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
                        Height = inside.PageHeights[0], Bands = bands } });
            }
            if (units.Count == 0) return SportSheetNesting.Place(items, width, limit, gap, density);
            var outside = SportSheetNesting.Place(units.Select(unit => unit.Item).ToList(), width, limit, gap, density);
            var result = new SportSheetNesting.Result { PageHeights = outside.PageHeights.ToList(),
                Placements = new List<SportSheetNesting.Placement>() };
            foreach (var block in outside.Placements)
                foreach (var member in units[block.Index].Members)
                    result.Placements.Add(new SportSheetNesting.Placement { Index = member.Index, Page = block.Page,
                        Left = block.Left + member.Left, Top = block.Top + member.Top,
                        Width = member.Width, Height = member.Height, Rotation = member.Rotation, Profile = member.Profile });

            if (grouping == SportGroupingMode.FrontBackPairs)
            {
                // Finish every torso pair first. Sleeves/neck start below the
                // pairs, using the last page's remaining length before new pages.
                var rest = indexed.Where(index => items[index].Part >= 2).ToList();
                if (rest.Count > 0)
                {
                    int previousPages = result.PageHeights.Count;
                    double top = result.PageHeights[previousPages - 1] + gap;
                    double remaining = limit - top;
                    bool append = remaining >= 10;
                    var tail = SportSheetNesting.Place(rest.Select(index => items[index]).ToList(),
                        width, limit, gap, density, true, append ? (double?)remaining : null);
                    if (append)
                    {
                        bool firstUsed = tail.Placements.Any(part => part.Page == 0);
                        if (firstUsed) result.PageHeights[previousPages - 1] = top + tail.PageHeights[0];
                        foreach (double height in tail.PageHeights.Skip(1)) result.PageHeights.Add(height);
                    }
                    else foreach (double height in tail.PageHeights) result.PageHeights.Add(height);
                    foreach (var part in tail.Placements)
                        result.Placements.Add(new SportSheetNesting.Placement { Index = rest[part.Index],
                            Page = append ? previousPages - 1 + part.Page : previousPages + part.Page,
                            Left = part.Left, Top = part.Top + (append && part.Page == 0 ? top : 0),
                            Width = part.Width, Height = part.Height, Rotation = part.Rotation, Profile = part.Profile });
                }
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
