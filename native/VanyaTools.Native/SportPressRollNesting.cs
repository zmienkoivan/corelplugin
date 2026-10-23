using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    // Torso sheets are placed first. Template sleeves fill the remaining
    // rectangles as single-column stacks; free mode retains its own search.
    internal static partial class SportPressRollNesting
    {
        private sealed class Sheet
        {
            internal IList<SportSheetNesting.Placement> Members;
            internal double Width, Height;
            internal bool Locked, SleeveRows, SleeveStack, PairedSleeves;
        }

        internal static SportNestingChoice Place(IList<SportSheetNesting.Item> items,
            SportPressPlan plan, double innerWidth, double innerHeight, SportNestingSettings settings)
        {
            var inside = plan.Layout;
            var original = Enumerable.Range(0, inside.PageHeights.Count).Select(page =>
            {
                var sheet = MakeSheet(inside.Placements.Where(p => p.Page == page).ToList(), settings);
                sheet.Locked = plan.LockedPages.Contains(page); sheet.SleeveRows = plan.SleeveRowPages.Contains(page);
                return sheet;
            }).ToList();
            if (settings.PressGrouping != 2) return PlaceBodyFirst(original, items, settings, plan);
            SportNestingChoice best = Arrange(original, items, settings, plan);
            double originalLength = best.Layout.PageHeights.Sum();
            double gap = Math.Max(1, settings.Gap);
            double grid = SportSheetNesting.GridStep(settings.Width, SportNestingMode.Rectangles);
            // Include the actual residual widths, with room for the outer grid's
            // rounding. Each narrow batch retains its own four press margins.
            var widths = FreeWidths(best).Select(width => Math.Min(width - gap - 2 * grid,
                    innerWidth + settings.Left + settings.Right - 2 * grid))
                .Where(width => width > settings.Left + settings.Right + 10)
                .Select(width => Math.Floor(width / grid) * grid)
                .Distinct().OrderByDescending(width => width).Take(settings.Improve ? 10 : 5).ToList();
            foreach (double width in widths)
            {
                SportNestingCancellation.Check();
                double narrowWidth = width - settings.Left - settings.Right;
                double narrowHeight = plan.LockedPages.Count == 0 ? innerHeight :
                    Math.Min(settings.Length, 1200) - settings.Top - settings.Bottom;
                var movableLargeArms = new HashSet<int>(plan.LargeArmIndices
                    .GroupBy(index => items[index].Garment)
                    .Where(kit => SportPressTemplates.LargeSleevesFit(kit.ToList(), items, narrowWidth, narrowHeight, settings))
                    .SelectMany(kit => kit));
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
                            if (sheet.Locked && !sheet.Members.Any(member => plan.LargeArmIndices.Contains(member.Index)))
                            { retained.Add(sheet); continue; }
                            bool onlySmall = sheet.Members.All(p => items[p.Index].SmallPart);
                            var keep = new List<SportSheetNesting.Placement>();
                            foreach (var member in sheet.Members)
                            {
                                var item = items[member.Index];
                                bool eligible = plan.LargeArmIndices.Contains(member.Index) ?
                                    pass > 0 && movableLargeArms.Contains(member.Index) : !sheet.Locked &&
                                    (pass == 2 || item.SmallPart) && (pass != 0 || onlySmall) && Fits(item, narrowWidth, narrowHeight);
                                if (eligible) extracted.Add(member.Index);
                                else keep.Add(member);
                            }
                            if (keep.Count > 0)
                            {
                                var kept = MakeSheet(keep, settings); kept.Locked = sheet.Locked; kept.SleeveRows = sheet.SleeveRows;
                                retained.Add(kept);
                            }
                        }
                        if (extracted.Count == 0 || !subsets.Add(String.Join(",", extracted.OrderBy(index => index)))) continue;
                        var largeArms = extracted.Where(index => plan.LargeArmIndices.Contains(index)).ToList();
                        foreach (var members in SportPressTemplates.LargeSleeveSheets(largeArms, items, narrowWidth, narrowHeight, settings))
                        {
                            var sheet = MakeSheet(members, settings); sheet.Locked = true; retained.Add(sheet);
                        }
                        foreach (var size in extracted.Where(index => !plan.LargeArmIndices.Contains(index))
                            .GroupBy(index => items[index].Size, StringComparer.OrdinalIgnoreCase))
                        {
                            SportNestingCancellation.Check();
                            var indices = size.ToList();
                            var packed = SportSheetNesting.Place(indices.Select(index => items[index]).ToList(),
                                narrowWidth, narrowHeight, settings.Gap, settings.Density, false, pressOrder: true);
                            foreach (int page in Enumerable.Range(0, packed.PageHeights.Count))
                            {
                                var members = packed.Placements.Where(p => p.Page == page)
                                    .Select(p => Copy(p, indices[p.Index], p.Left, p.Top)).ToList();
                                retained.Add(MakeSheet(members, settings));
                            }
                        }
                        var candidate = Arrange(retained, items, settings, plan);
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
            SportNestingSettings settings, SportPressPlan plan, SportSheetNesting.Result fixedOutside = null)
        {
            var blocks = sheets.Select((sheet, index) => new SportSheetNesting.Item {
                Label = "Лист для пресса №" + (index + 1), Width = sheet.Width, Height = sheet.Height }).ToList();
            var outside = fixedOutside ?? SportSheetNesting.Place(blocks, settings.Width, settings.Length,
                Math.Max(1, settings.Gap), SportNestingMode.Rectangles, false,
                placementOrder: plan.LockedPages.Count == 0 ? null : Enumerable.Range(0, sheets.Count).ToList());
            var choice = new SportNestingChoice { Settings = settings, PairedGarments = plan.PairedGarments,
                LargeGarments = plan.LargeGarments,
                Layout = new SportSheetNesting.Result { PageHeights = outside.PageHeights.ToList(),
                    Placements = new List<SportSheetNesting.Placement>() } };
            foreach (var block in outside.Placements)
            {
                SportNestingCancellation.Check();
                var sheet = sheets[block.Index];
                double shortSide = settings.Press == 1 ? 800 : 1000;
                if (!((sheet.Width <= 1200 + .01 && sheet.Height <= shortSide + .01) ||
                    (sheet.Width <= shortSide + .01 && sheet.Height <= 1200 + .01)))
                    throw new InvalidOperationException("Отрезной лист превышает размеры плиты пресса.");
                if (sheet.SleeveRows && sheet.Members.Any(p => items[p.Index].Part != 2 && items[p.Index].Part != 3))
                    throw new InvalidOperationException("В лист рядов рукавов попала другая деталь.");
                if (sheet.SleeveRows)
                    foreach (var row in sheet.Members.GroupBy(p => items[p.Index].Garment))
                    {
                        if (row.Count() > 2 || row.Select(p => items[p.Index].Part).Distinct().Count() != row.Count() ||
                            row.Select(p => items[p.Index].Size).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1 ||
                            row.Max(p => p.Top + p.Height) - row.Min(p => p.Top + p.Height) > .01)
                            throw new InvalidOperationException("Нарушена строка рукавов одного комплекта.");
                        var left = row.FirstOrDefault(p => items[p.Index].Part == 2);
                        var right = row.FirstOrDefault(p => items[p.Index].Part == 3);
                        if (left != null && right != null && left.Left + left.Width + settings.Gap > right.Left + .01)
                            throw new InvalidOperationException("Левый и правый рукава поменялись колонками.");
                    }
                if (sheet.SleeveStack)
                {
                    var stack = sheet.Members.OrderBy(p => p.Top).ThenBy(p => p.Left).ToList();
                    bool completePairs = stack.GroupBy(p => items[p.Index].Garment).All(kit => kit.Count() == 2 &&
                        kit.Select(p => items[p.Index].Part).OrderBy(part => part).SequenceEqual(new[] { 2, 3 }));
                    if (stack.Count > 4 || stack.Any(p => items[p.Index].Part != 2 && items[p.Index].Part != 3) ||
                        (sheet.PairedSleeves ? stack.Count != 2 || !completePairs :
                            stack.Select(p => items[p.Index].Part).Distinct().Count() != 1) ||
                        (!sheet.PairedSleeves && stack.Max(p => p.Left) - stack.Min(p => p.Left) > .01) ||
                        stack.Select(p => p.Rotation).Distinct().Count() != 1 ||
                        !stack.Select(p => items[p.Index].Garment).SequenceEqual(stack.Select(p => items[p.Index].Garment).OrderBy(i => i)) ||
                        stack.Any(p => p.Rotation != 0 && (!settings.Rotate || !items[p.Index].Allow90)))
                        throw new InvalidOperationException("Нарушена стопка рукавов для пресса.");
                    for (int i = 0; i < stack.Count; i++)
                        for (int j = 0; j < i; j++)
                            if (!Separated(stack[i].Left, stack[i].Top, stack[i].Width, stack[i].Height,
                                stack[j].Left, stack[j].Top, stack[j].Width, stack[j].Height, settings.Gap))
                                throw new InvalidOperationException("Рукава на листе пересекаются.");
                }
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
            for (int i = 0; i < choice.Panels.Count; i++)
            {
                var panel = choice.Panels[i];
                if (panel.Page < 0 || panel.Page >= choice.Layout.PageHeights.Count || panel.Left < -.01 || panel.Top < -.01 ||
                    panel.Left + panel.Width > settings.Width + .01 ||
                    panel.Top + panel.Height > choice.Layout.PageHeights[panel.Page] + .01)
                    throw new InvalidOperationException("Отрезной лист вышел за границы рулона.");
                foreach (var previous in choice.Panels.Take(i).Where(p => p.Page == panel.Page))
                    if (!Separated(panel.Left, panel.Top, panel.Width, panel.Height,
                        previous.Left, previous.Top, previous.Width, previous.Height, Math.Max(1, settings.Gap)))
                        throw new InvalidOperationException("Отрезные листы пересекаются или нарушен зазор между ними.");
            }
            SportNestingPreview.Validate(choice, items.Count);
            return choice;
        }

        internal static bool Better(SportNestingChoice candidate, SportNestingChoice current)
        {
            if (current == null) return true;
            int candidateTemplates = candidate.PairedGarments + candidate.LargeGarments;
            int currentTemplates = current.PairedGarments + current.LargeGarments;
            if (candidateTemplates != currentTemplates) return candidateTemplates > currentTemplates;
            if (candidate.PairedGarments != current.PairedGarments) return candidate.PairedGarments > current.PairedGarments;
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
