using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    // Press sheets are independent batches. Different garment sizes never share
    // a batch; only the final rectangular sheets are nested together on the roll.
    internal static class SportPressNesting
    {
        internal static SportSheetNesting.Result Place(IList<SportSheetNesting.Item> items,
            double width, double height, SportNestingSettings settings)
        {
            if (items.Any(item => String.IsNullOrWhiteSpace(item.Size)))
                throw new InvalidOperationException("У детали не определён размер; нельзя безопасно собрать листы пресса по размерам.");
            var result = Empty();
            foreach (var size in Enumerable.Range(0, items.Count)
                .GroupBy(index => items[index].Size ?? "", StringComparer.OrdinalIgnoreCase))
            {
                SportNestingCancellation.Check();
                var indices = size.ToList();
                var sameSize = indices.Select(index => items[index]).ToList();
                SportSheetNesting.Result layout;
                if (settings.PressGrouping == 2)
                {
                    layout = Pack(sameSize, width, height, settings, settings.Improve);
                }
                else
                {
                    var pinned = Empty();
                    foreach (var garment in Enumerable.Range(0, sameSize.Count).GroupBy(i => sameSize[i].Garment))
                    {
                        SportNestingCancellation.Check();
                        var kit = garment.ToList();
                        if (settings.PressGrouping == 0)
                        {
                            var complete = TrySingleSheet(kit.Select(i => sameSize[i]).ToList(), width, height, settings);
                            if (complete != null) { Append(pinned, complete, kit); continue; }
                        }
                        // A complete kit is optional. A torso pair can occupy
                        // its own sheet; when even that cannot fit, keep every
                        // torso on a smaller sheet and pool the sleeves below.
                        var torsos = kit.Where(i => sameSize[i].Part < 2).ToList();
                        if (torsos.Count == 0) continue;
                        var bodies = Pack(torsos.Select(i => sameSize[i]).ToList(), width, height, settings, false);
                        Append(pinned, bodies, torsos);
                    }
                    // Fixed torso/kit placements are real contour obstacles.
                    // Remaining sleeves and necks first fill their side pockets,
                    // then new sheets, exclusively within this same size.
                    layout = SportSheetNesting.Place(sameSize, width, height, settings.Gap,
                        settings.Density, settings.Improve, fixedLayout: pinned.Placements.Count == 0 ? null : pinned,
                        preferGarmentPages: settings.PressGrouping == 0);
                }
                Append(result, layout, indices);
            }
            if (result.Placements.Count != items.Count || result.Placements.Select(p => p.Index).Distinct().Count() != items.Count)
                throw new InvalidOperationException("Не удалось сохранить все детали в листах пресса.");
            return result;
        }

        private static SportSheetNesting.Result TrySingleSheet(IList<SportSheetNesting.Item> items,
            double width, double height, SportNestingSettings settings)
        {
            try
            {
                var packed = Pack(items, width, height, settings, false);
                return packed.PageHeights.Count == 1 ? packed : null;
            }
            catch (InvalidOperationException) { return null; }
        }

        private static SportSheetNesting.Result Pack(IList<SportSheetNesting.Item> items,
            double width, double height, SportNestingSettings settings, bool improve) =>
            SportSheetNesting.Place(items, width, height, settings.Gap, settings.Density, improve);

        private static SportSheetNesting.Result Empty() => new SportSheetNesting.Result {
            Placements = new List<SportSheetNesting.Placement>(), PageHeights = new List<double>() };

        private static void Append(SportSheetNesting.Result target, SportSheetNesting.Result source, IList<int> indices)
        {
            int firstPage = target.PageHeights.Count;
            foreach (double pageHeight in source.PageHeights) target.PageHeights.Add(pageHeight);
            foreach (var member in source.Placements)
            {
                SportNestingCancellation.Check();
                target.Placements.Add(new SportSheetNesting.Placement { Index = indices[member.Index],
                    Page = firstPage + member.Page, Left = member.Left, Top = member.Top,
                    Width = member.Width, Height = member.Height, Rotation = member.Rotation,
                    Column = member.Column, Profile = member.Profile });
            }
        }
    }
}
