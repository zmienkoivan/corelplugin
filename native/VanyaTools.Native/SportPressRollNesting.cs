using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    // Press-sized rectangles are movable batches, not indivisible garment kits.
    // Rebuild small batches from existing pieces to use the roll's side strips.
    internal static class SportPressRollNesting
    {
        private sealed class Sheet
        {
            internal IList<SportSheetNesting.Placement> Members;
            internal double Width, Height;
        }

        internal static SportNestingChoice Place(IList<SportSheetNesting.Item> items,
            SportSheetNesting.Result inside, double innerWidth, double innerHeight, SportNestingSettings settings)
        {
            var original = Enumerable.Range(0, inside.PageHeights.Count).Select(page =>
                MakeSheet(inside.Placements.Where(p => p.Page == page).ToList(), settings)).ToList();
            SportNestingChoice best = Arrange(original, items, settings);
            double originalLength = best.Layout.PageHeights.Sum();
            double gap = Math.Max(1, settings.Gap);
            double grid = SportSheetNesting.GridStep(settings.Width, SportNestingMode.Rectangles);
            // Include the actual residual widths, with room for the outer grid's
            // rounding. Each narrow batch retains its own four press margins.
            var widths = FreeWidths(best).Select(width => width - gap - 2 * grid)
                .Where(width => width > settings.Left + settings.Right + 10 &&
                    width < innerWidth + settings.Left + settings.Right - grid)
                .Select(width => Math.Floor(width / grid) * grid)
                .Distinct().OrderByDescending(width => width).Take(settings.Improve ? 10 : 5).ToList();
            foreach (double width in widths)
            {
                SportNestingCancellation.Check();
                double narrowWidth = width - settings.Left - settings.Right;
                var subsets = new HashSet<string>();
                // Try sleeves-only sheets, attached small pieces, then also
                // individual bodies. Pair grouping may yield when it saves roll.
                for (int pass = 0; pass < 3; pass++)
                {
                    SportNestingCancellation.Check();
                    try
                    {
                        var retained = new List<Sheet>();
                        var extracted = new List<int>();
                        foreach (var sheet in original)
                        {
                            bool onlySmall = sheet.Members.All(p => items[p.Index].SmallPart);
                            var keep = new List<SportSheetNesting.Placement>();
                            foreach (var member in sheet.Members)
                            {
                                var item = items[member.Index];
                                bool eligible = (pass == 2 || item.SmallPart) && (pass != 0 || onlySmall) &&
                                    Fits(item, narrowWidth, innerHeight);
                                if (eligible) extracted.Add(member.Index);
                                else keep.Add(member);
                            }
                            if (keep.Count > 0) retained.Add(MakeSheet(keep, settings));
                        }
                        if (extracted.Count == 0 || !subsets.Add(String.Join(",", extracted.OrderBy(index => index)))) continue;
                        foreach (var size in extracted.GroupBy(index => items[index].Size, StringComparer.OrdinalIgnoreCase))
                        {
                            SportNestingCancellation.Check();
                            var indices = size.ToList();
                            var packed = SportSheetNesting.Place(indices.Select(index => items[index]).ToList(),
                                narrowWidth, innerHeight, settings.Gap, settings.Density, false, pressOrder: true);
                            foreach (int page in Enumerable.Range(0, packed.PageHeights.Count))
                            {
                                var members = packed.Placements.Where(p => p.Page == page)
                                    .Select(p => Copy(p, indices[p.Index], p.Left, p.Top)).ToList();
                                retained.Add(MakeSheet(members, settings));
                            }
                        }
                        var candidate = Arrange(retained, items, settings);
                        if (Better(candidate, best)) best = candidate;
                    }
                    catch (InvalidOperationException error)
                    { Log.Info("Press side-strip candidate rejected: " + error.Message); }
                }
            }
            Log.Info("Press roll side-strip search: " + originalLength.ToString("0.#") + " -> " +
                best.Layout.PageHeights.Sum().ToString("0.#") + " mm, sheets=" + original.Count +
                " -> " + best.Panels.Count + ", strip widths=" + widths.Count);
            return best;
        }

        private static IEnumerable<double> FreeWidths(SportNestingChoice choice)
        {
            foreach (var page in choice.Panels.GroupBy(panel => panel.Page))
            {
                var edges = page.SelectMany(panel => new[] { panel.Top, panel.Top + panel.Height })
                    .Distinct().OrderBy(y => y).ToList();
                for (int at = 1; at < edges.Count; at++)
                {
                    SportNestingCancellation.Check();
                    double y = (edges[at - 1] + edges[at]) / 2, right = 0;
                    foreach (var panel in page.Where(panel => panel.Top < y && panel.Top + panel.Height > y)
                        .OrderBy(panel => panel.Left))
                    {
                        if (panel.Left > right) yield return panel.Left - right;
                        right = Math.Max(right, panel.Left + panel.Width);
                    }
                    if (right < choice.Settings.Width) yield return choice.Settings.Width - right;
                }
            }
        }

        private static bool Fits(SportSheetNesting.Item item, double width, double height) =>
            (item.Width <= width + .000001 && item.Height <= height + .000001) ||
            (item.Allow90 && item.Height <= width + .000001 && item.Width <= height + .000001);

        private static Sheet MakeSheet(IList<SportSheetNesting.Placement> members, SportNestingSettings settings)
        {
            double left = members.Min(p => p.Left), top = members.Min(p => p.Top);
            return new Sheet {
                Width = members.Max(p => p.Left + p.Width) - left + settings.Left + settings.Right,
                Height = members.Max(p => p.Top + p.Height) - top + settings.Top + settings.Bottom,
                Members = members.Select(p => Copy(p, p.Index, p.Left - left, p.Top - top)).ToList() };
        }

        private static SportNestingChoice Arrange(IList<Sheet> sheets, IList<SportSheetNesting.Item> items,
            SportNestingSettings settings)
        {
            var blocks = sheets.Select((sheet, index) => new SportSheetNesting.Item {
                Label = "Лист для пресса №" + (index + 1), Width = sheet.Width, Height = sheet.Height }).ToList();
            var outside = SportSheetNesting.Place(blocks, settings.Width, settings.Length,
                Math.Max(1, settings.Gap), SportNestingMode.Rectangles, false);
            var choice = new SportNestingChoice { Settings = settings,
                Layout = new SportSheetNesting.Result { PageHeights = outside.PageHeights.ToList(),
                    Placements = new List<SportSheetNesting.Placement>() } };
            foreach (var block in outside.Placements)
            {
                SportNestingCancellation.Check();
                var sheet = sheets[block.Index];
                if (sheet.Members.Select(p => items[p.Index].Size).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1)
                    throw new InvalidOperationException("В отрезной лист попали разные размеры.");
                choice.Panels.Add(new SportPressPanel { Page = block.Page, Left = block.Left,
                    Top = block.Top, Width = block.Width, Height = block.Height });
                foreach (var member in sheet.Members)
                {
                    if (member.Left < -.001 || member.Top < -.001 ||
                        member.Left + member.Width > sheet.Width - settings.Left - settings.Right + .01 ||
                        member.Top + member.Height > sheet.Height - settings.Top - settings.Bottom + .01)
                        throw new InvalidOperationException("Деталь вышла за поля отрезного листа.");
                    var placed = Copy(member, member.Index, block.Left + settings.Left + member.Left,
                        block.Top + settings.Top + member.Top);
                    placed.Page = block.Page;
                    choice.Layout.Placements.Add(placed);
                }
            }
            SportNestingPreview.Validate(choice, items.Count);
            return choice;
        }

        internal static bool Better(SportNestingChoice candidate, SportNestingChoice current)
        {
            if (current == null) return true;
            double delta = candidate.Layout.PageHeights.Sum() - current.Layout.PageHeights.Sum();
            // Fewer press transfers cannot justify leaving a useful strip empty.
            // At equal roll length retain the variant with fewer separate sheets.
            if (Math.Abs(delta) > .001) return delta < 0;
            if (candidate.Panels.Count != current.Panels.Count) return candidate.Panels.Count < current.Panels.Count;
            return candidate.Layout.PageHeights.Count < current.Layout.PageHeights.Count;
        }

        private static SportSheetNesting.Placement Copy(SportSheetNesting.Placement source, int index,
            double left, double top) => new SportSheetNesting.Placement { Index = index, Page = source.Page,
                Left = left, Top = top, Width = source.Width, Height = source.Height,
                Rotation = source.Rotation, Column = source.Column, Profile = source.Profile };
    }
}
