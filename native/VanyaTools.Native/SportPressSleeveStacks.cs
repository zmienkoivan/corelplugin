using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal static partial class SportPressRollNesting
    {
        private sealed class FreeRectangle
        {
            internal int Page;
            internal double Left, Top, Width, Height;
        }

        private sealed class SleeveBatch
        {
            internal IList<int> Indices;
            internal int Rotation;
            internal int CompletePairs;
            internal bool PairRows;
            internal double Width, Height, Area;
            internal FreeRectangle Slot;
        }

        private static SportNestingChoice PlaceBodyFirst(IList<Sheet> original,
            IList<SportSheetNesting.Item> items, SportNestingSettings settings, SportPressPlan plan)
        {
            // Keep every torso pair's internal positions. Remove small pieces
            // before the outer search so no sleeve sheet can displace a torso.
            var sheets = original.Select(sheet => sheet.Members.Where(p => items[p.Index].Part < 2).ToList())
                .Where(members => members.Count > 0).Select(members => MakeSheet(members, settings))
                .OrderByDescending(sheet => sheet.Width).ThenByDescending(sheet => sheet.Height).ToList();
            var blocks = sheets.Select((sheet, index) => new SportSheetNesting.Item {
                Label = "Лист корпусов №" + (index + 1), Width = sheet.Width, Height = sheet.Height }).ToList();
            var outside = sheets.Count == 0 ? new SportSheetNesting.Result {
                Placements = new List<SportSheetNesting.Placement>(), PageHeights = new List<double>() } :
                SportSheetNesting.Place(blocks, settings.Width, settings.Length, Math.Max(1, settings.Gap),
                    SportNestingMode.Rectangles, false, placementOrder: Enumerable.Range(0, sheets.Count).ToList());
            double bodyLength = outside.PageHeights.Sum();
            var remaining = new HashSet<int>(Enumerable.Range(0, items.Count)
                .Where(index => items[index].Part == 2 || items[index].Part == 3));
            int sideSleeves = 0, sideStacks = 0;

            // Search actual free rectangles, including their height. A stack
            // may cross several adjacent torso rows when the strip stays free.
            while (remaining.Count > 0)
            {
                SportNestingCancellation.Check();
                SleeveBatch best = null;
                var available = remaining.OrderBy(index => items[index].Garment).ThenBy(index => index).ToList();
                foreach (var slot in FreeRectangles(outside, settings, false))
                {
                    var candidate = BestStack(available, items, slot, settings);
                    if (BetterStack(candidate, best)) best = candidate;
                }
                if (best == null) break;
                AddStack(best, sheets, outside, items, settings);
                foreach (int index in best.Indices) remaining.Remove(index);
                sideSleeves += best.Indices.Count; sideStacks++;
            }

            // When no side pocket remains, retain the same 2-4 sleeve column
            // format. Its whole rectangle is fitted around the fixed bodies;
            // a final single sleeve is allowed instead of dropping a part.
            while (remaining.Count > 0)
            {
                SportNestingCancellation.Check();
                var empty = new FreeRectangle { Width = settings.Width,
                    Height = Math.Min(settings.Length, 1200) };
                var batch = BestStack(remaining.OrderBy(index => items[index].Garment).ThenBy(index => index), items, empty, settings);
                if (batch == null)
                    throw new InvalidOperationException(items[remaining.OrderBy(index => index).First()].Label +
                        ": рукав с полями не помещается на лист пресса.");
                batch.Slot = FindRectangle(outside, batch.Width, batch.Height, settings);
                AddStack(batch, sheets, outside, items, settings);
                foreach (int index in batch.Indices) remaining.Remove(index);
            }

            // Necks and other non-sleeve details retain their original
            // same-size groups, and are placed only after all torso sheets.
            foreach (var members in original.Select(sheet => sheet.Members.Where(p => items[p.Index].Part >= 2 &&
                items[p.Index].Part != 2 && items[p.Index].Part != 3).ToList()).Where(group => group.Count > 0))
            {
                SportNestingCancellation.Check();
                var sheet = MakeSheet(members, settings);
                AddSheet(sheet, FindRectangle(outside, sheet.Width, sheet.Height, settings), sheets, outside);
            }
            var choice = Arrange(sheets, items, settings, plan, outside);
            Log.Info("Press body-first stacks: torso sheets=" + blocks.Count + ", side sleeves=" + sideSleeves +
                ", side stacks=" + sideStacks + ", total sheets=" + sheets.Count + ", torso length=" +
                bodyLength.ToString("0.#") + ", final length=" + outside.PageHeights.Sum().ToString("0.#") + " mm");
            return choice;
        }

        private static SleeveBatch BestStack(IEnumerable<int> indices, IList<SportSheetNesting.Item> items,
            FreeRectangle slot, SportNestingSettings settings)
        {
            var available = indices.ToList();
            SleeveBatch best = BestPairs(available, items, slot, settings, out HashSet<int> paired);
            foreach (int part in new[] { 2, 3 })
                foreach (var size in available.Where(i => items[i].Part == part && !paired.Contains(i))
                    .GroupBy(i => items[i].Size, StringComparer.OrdinalIgnoreCase))
                foreach (int rotation in settings.Rotate ? new[] { 0, -90 } : new[] { 0 })
                {
                    var selected = new List<int>(); double width = 0, height = 0, area = 0;
                    foreach (int index in size)
                    {
                        SportNestingCancellation.Check();
                        var item = items[index];
                        if (rotation != 0 && !item.Allow90) continue;
                        double w = rotation == 0 ? item.Width : item.Height;
                        double h = rotation == 0 ? item.Height : item.Width;
                        double nextWidth = Math.Max(width, w);
                        double nextHeight = height + (selected.Count == 0 ? 0 : settings.Gap) + h;
                        double outerWidth = nextWidth + settings.Left + settings.Right;
                        double outerHeight = nextHeight + settings.Top + settings.Bottom;
                        if (outerWidth > slot.Width + .001 || outerHeight > slot.Height + .001 ||
                            !FitsPress(outerWidth, outerHeight, settings)) continue;
                        selected.Add(index); width = nextWidth; height = nextHeight; area += item.Width * item.Height;
                        var candidate = new SleeveBatch { Indices = selected.ToList(), Rotation = rotation,
                            Width = outerWidth, Height = outerHeight, Area = area, Slot = slot };
                        if (BetterStack(candidate, best)) best = candidate;
                        if (selected.Count == 4) break;
                    }
                }
            return best;
        }

        private static SleeveBatch BestPairs(IList<int> indices, IList<SportSheetNesting.Item> items,
            FreeRectangle slot, SportNestingSettings settings, out HashSet<int> protectedPairs)
        {
            protectedPairs = new HashSet<int>(); SleeveBatch best = null;
            foreach (var size in indices.GroupBy(i => items[i].Size, StringComparer.OrdinalIgnoreCase))
            {
                var pairs = size.GroupBy(i => items[i].Garment).Where(kit => kit.Count() == 2 &&
                    kit.Select(i => items[i].Part).OrderBy(part => part).SequenceEqual(new[] { 2, 3 }))
                    .OrderBy(kit => kit.Key).Select(kit => kit.OrderBy(i => items[i].Part).ToList()).ToList();
                foreach (int rotation in settings.Rotate ? new[] { 0, -90 } : new[] { 0 })
                    foreach (bool rows in new[] { false, true })
                    {
                        var selected = new List<int>();
                        foreach (var pair in pairs)
                        {
                            SportNestingCancellation.Check();
                            if (rotation != 0 && pair.Any(i => !items[i].Allow90)) continue;
                            var single = PairBatch(pair, rows, rotation, items, settings);
                            if (single.Width <= settings.Width + .001 && single.Height <= settings.Length + .001 &&
                                FitsPress(single.Width, single.Height, settings))
                                foreach (int index in pair) protectedPairs.Add(index);
                            var next = PairBatch(selected.Concat(pair).ToList(), rows, rotation, items, settings);
                            if (next.Width > slot.Width + .001 || next.Height > slot.Height + .001 ||
                                !FitsPress(next.Width, next.Height, settings)) continue;
                            selected.AddRange(pair); next.Slot = slot;
                            if (BetterStack(next, best)) best = next;
                            if (selected.Count == 4) break;
                        }
                    }
            }
            return best;
        }

        private static SleeveBatch PairBatch(IList<int> indices, bool rows, int rotation,
            IList<SportSheetNesting.Item> items, SportNestingSettings settings)
        {
            double W(int i) => rotation == 0 ? items[i].Width : items[i].Height;
            double H(int i) => rotation == 0 ? items[i].Height : items[i].Width;
            double width = 0, height = 0;
            for (int at = 0; at < indices.Count; at += 2)
            {
                int left = indices[at], right = indices[at + 1];
                width = Math.Max(width, rows ? W(left) + settings.Gap + W(right) : Math.Max(W(left), W(right)));
                height += (at == 0 ? 0 : settings.Gap) +
                    (rows ? Math.Max(H(left), H(right)) : H(left) + settings.Gap + H(right));
            }
            return new SleeveBatch { Indices = indices.ToList(), CompletePairs = indices.Count / 2, PairRows = rows,
                Rotation = rotation, Width = width + settings.Left + settings.Right,
                Height = height + settings.Top + settings.Bottom, Area = indices.Sum(i => items[i].Width * items[i].Height) };
        }

        private static bool BetterStack(SleeveBatch candidate, SleeveBatch current)
        {
            if (candidate == null) return false;
            if (current == null) return true;
            if (candidate.CompletePairs != current.CompletePairs) return candidate.CompletePairs > current.CompletePairs;
            if (candidate.Indices.Count != current.Indices.Count) return candidate.Indices.Count > current.Indices.Count;
            if ((candidate.Rotation == 0) != (current.Rotation == 0)) return candidate.Rotation == 0;
            if (Math.Abs(candidate.Area - current.Area) > .001) return candidate.Area > current.Area;
            if (candidate.Slot.Page != current.Slot.Page) return candidate.Slot.Page < current.Slot.Page;
            if (Math.Abs(candidate.Slot.Top - current.Slot.Top) > .001) return candidate.Slot.Top < current.Slot.Top;
            return candidate.Slot.Left < current.Slot.Left - .001;
        }

        private static bool FitsPress(double width, double height, SportNestingSettings settings)
        {
            double shortSide = settings.Press == 1 ? 800 : 1000;
            double cap = settings.PressSheetWidth > 0 ? Math.Min(shortSide, settings.PressSheetWidth) : shortSide;
            return (width <= cap + .001 && height <= 1200 + .001) ||
                (width <= 1200 + .001 && height <= shortSide + .001);
        }

        private static void AddStack(SleeveBatch batch, IList<Sheet> sheets, SportSheetNesting.Result outside,
            IList<SportSheetNesting.Item> items, SportNestingSettings settings)
        {
            var members = new List<SportSheetNesting.Placement>(); double top = 0;
            for (int at = 0; at < batch.Indices.Count; at++)
            {
                int index = batch.Indices[at];
                if (batch.PairRows)
                {
                    int right = batch.Indices[++at];
                    var a = SportPressTemplates.At(items, index, 0, top, batch.Rotation, settings.Width, settings);
                    var b = SportPressTemplates.At(items, right, a.Width + settings.Gap, top, batch.Rotation, settings.Width, settings);
                    double height = Math.Max(a.Height, b.Height);
                    a.Top += height - a.Height; b.Top += height - b.Height;
                    members.Add(a); members.Add(b); top += height + settings.Gap;
                    continue;
                }
                var member = SportPressTemplates.At(items, index, 0, top, batch.Rotation, settings.Width, settings);
                members.Add(member); top += member.Height + settings.Gap;
            }
            var sheet = MakeSheet(members, settings); sheet.SleeveStack = true; sheet.PairedSleeves = batch.CompletePairs > 0;
            AddSheet(sheet, batch.Slot, sheets, outside);
        }

        private static void AddSheet(Sheet sheet, FreeRectangle slot, IList<Sheet> sheets,
            SportSheetNesting.Result outside)
        {
            if (slot.Page == outside.PageHeights.Count) outside.PageHeights.Add(0);
            if (slot.Page < 0 || slot.Page >= outside.PageHeights.Count ||
                sheet.Width > slot.Width + .001 || sheet.Height > slot.Height + .001)
                throw new InvalidOperationException("Лист не помещается в найденный свободный прямоугольник.");
            outside.Placements.Add(new SportSheetNesting.Placement { Index = sheets.Count, Page = slot.Page,
                Left = slot.Left, Top = slot.Top, Width = sheet.Width, Height = sheet.Height });
            outside.PageHeights[slot.Page] = Math.Max(outside.PageHeights[slot.Page], slot.Top + sheet.Height);
            sheets.Add(sheet);
        }

        private static FreeRectangle FindRectangle(SportSheetNesting.Result outside, double width, double height,
            SportNestingSettings settings)
        {
            FreeRectangle best = null; double bestGrowth = Double.PositiveInfinity;
            foreach (var slot in FreeRectangles(outside, settings, true))
            {
                if (width > slot.Width + .001 || height > slot.Height + .001) continue;
                double growth = Math.Max(0, slot.Top + height - outside.PageHeights[slot.Page]);
                if (best == null || growth < bestGrowth - .001 || (Math.Abs(growth - bestGrowth) <= .001 &&
                    (slot.Page < best.Page || (slot.Page == best.Page &&
                    (slot.Top < best.Top - .001 || (Math.Abs(slot.Top - best.Top) <= .001 && slot.Left < best.Left))))))
                { best = slot; bestGrowth = growth; }
            }
            if (best != null) return best;
            if (width > settings.Width + .001 || height > settings.Length + .001)
                throw new InvalidOperationException("Лист с мелкими деталями превышает размеры рулона.");
            return new FreeRectangle { Page = outside.PageHeights.Count, Width = settings.Width, Height = settings.Length };
        }

        // For each possible top/bottom edge, intersect the free horizontal
        // intervals. Gaps belong to the obstacle, press margins to the sheet.
        // This finds a continuous strip across several torso sheets, unlike a
        // width-only search that cannot tell whether a 3-4 sleeve stack fits.
        private static IEnumerable<FreeRectangle> FreeRectangles(SportSheetNesting.Result outside,
            SportNestingSettings settings, bool extend)
        {
            double gap = Math.Max(1, settings.Gap);
            for (int page = 0; page < outside.PageHeights.Count; page++)
            {
                SportNestingCancellation.Check();
                double limit = extend ? settings.Length : outside.PageHeights[page];
                var occupied = outside.Placements.Where(p => p.Page == page).ToList();
                var tops = occupied.Select(p => p.Top + p.Height + gap).Concat(new[] { 0.0 })
                    .Where(y => y < limit - .001).Distinct().OrderBy(y => y);
                foreach (double top in tops)
                {
                    double bottomLimit = Math.Min(limit, top + 1200);
                    var bottoms = occupied.Select(p => p.Top - gap).Concat(new[] { bottomLimit })
                        .Where(y => y > top + .001 && y <= bottomLimit + .001).Distinct().OrderByDescending(y => y);
                    foreach (double bottom in bottoms)
                    {
                        SportNestingCancellation.Check();
                        double right = 0;
                        foreach (var obstacle in occupied.Where(p => p.Top - gap < bottom - .001 &&
                            p.Top + p.Height + gap > top + .001).OrderBy(p => p.Left))
                        {
                            double left = Math.Max(0, obstacle.Left - gap);
                            if (left > right + .001) yield return new FreeRectangle {
                                Page = page, Left = right, Top = top, Width = left - right, Height = bottom - top };
                            right = Math.Max(right, obstacle.Left + obstacle.Width + gap);
                        }
                        if (right < settings.Width - .001) yield return new FreeRectangle {
                            Page = page, Left = right, Top = top, Width = settings.Width - right, Height = bottom - top };
                    }
                }
            }
        }

        private static bool Separated(double left, double top, double width, double height,
            double otherLeft, double otherTop, double otherWidth, double otherHeight, double gap) =>
            left + width + gap <= otherLeft + .01 || otherLeft + otherWidth + gap <= left + .01 ||
            top + height + gap <= otherTop + .01 || otherTop + otherHeight + gap <= top + .01;
    }
}
