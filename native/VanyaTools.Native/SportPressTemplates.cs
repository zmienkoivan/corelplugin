using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal sealed class SportPressPlan
    {
        internal SportSheetNesting.Result Layout = new SportSheetNesting.Result {
            Placements = new List<SportSheetNesting.Placement>(), PageHeights = new List<double>() };
        internal HashSet<int> LockedPages = new HashSet<int>();
        internal HashSet<int> SleeveRowPages = new HashSet<int>();
        internal HashSet<int> LargeArmIndices = new HashSet<int>();
        internal int PairedGarments, LargeGarments;
    }

    // Small pairs and sleeve rows are immutable. Large sleeves may move as a
    // garment pair to the separate narrow strip explicitly requested by the user.
    internal static class SportPressTemplates
    {
        private sealed class Row
        {
            internal int Left = -1, Right = -1;
            internal double LeftWidth, RightWidth, Height;
        }

        internal static SportPressPlan Create(IList<SportSheetNesting.Item> items, SportNestingSettings settings)
        {
            var plan = new SportPressPlan();
            if (settings.PressGrouping == 2) return plan;
            double shortSide = settings.Press == 1 ? 800 : 1000;
            double cap = settings.PressSheetWidth > 0 ? settings.PressSheetWidth : 1200;
            double smallWidth = Math.Min(settings.Width, 1200) - settings.Left - settings.Right;
            double smallHeight = Math.Min(settings.Length, shortSide) - settings.Top - settings.Bottom;
            double largeWidth = Math.Min(settings.Width, Math.Min(cap, shortSide)) - settings.Left - settings.Right;
            double largeHeight = Math.Min(settings.Length, 1200) - settings.Top - settings.Bottom;
            var bodies = new List<IList<SportSheetNesting.Placement>>();
            var large = new List<IList<SportSheetNesting.Placement>>();
            var rows = new List<Row>();
            foreach (var kit in Enumerable.Range(0, items.Count).GroupBy(i => items[i].Garment))
            {
                SportNestingCancellation.Check();
                var parts = kit.GroupBy(i => items[i].Part).ToDictionary(g => g.Key, g => g.ToList());
                if (parts.Values.Any(group => group.Count != 1) || !parts.ContainsKey(0) || !parts.ContainsKey(1)) continue;
                int front = parts[0][0], back = parts[1][0];
                int left = parts.ContainsKey(2) ? parts[2][0] : -1, right = parts.ContainsKey(3) ? parts[3][0] : -1;
                double pairWidth = items[back].Width + settings.Gap + items[front].Width;
                double pairHeight = Math.Max(items[back].Height, items[front].Height);
                if (Fits(pairWidth, pairHeight, smallWidth, smallHeight))
                {
                    bodies.Add(new[] {
                        At(items, back, 0, pairHeight - items[back].Height, 0, smallWidth, settings),
                        At(items, front, items[back].Width + settings.Gap, pairHeight - items[front].Height, 0, smallWidth, settings) });
                    plan.PairedGarments++;
                    var row = new Row { Left = left, Right = right,
                        LeftWidth = left < 0 ? 0 : items[left].Width, RightWidth = right < 0 ? 0 : items[right].Width,
                        Height = Math.Max(left < 0 ? 0 : items[left].Height, right < 0 ? 0 : items[right].Height) };
                    if ((left >= 0 || right >= 0) && Fits(row.LeftWidth + row.RightWidth +
                        (left >= 0 && right >= 0 ? settings.Gap : 0), row.Height, smallWidth, smallHeight)) rows.Add(row);
                    continue;
                }
                // Portrait large sheets: front + right sleeve, back + left.
                var frontSheet = Large(items, front, right, largeWidth, largeHeight, settings);
                var backSheet = Large(items, back, left, largeWidth, largeHeight, settings);
                if (frontSheet == null || backSheet == null) continue;
                large.Add(frontSheet); large.Add(backSheet); plan.LargeGarments++;
                if (left >= 0) plan.LargeArmIndices.Add(left);
                if (right >= 0) plan.LargeArmIndices.Add(right);
            }
            AddSleeveSheets(plan, rows, items, smallWidth, smallHeight, settings);
            foreach (var sheet in bodies) Add(plan, sheet, false);
            foreach (var sheet in large) Add(plan, sheet, false);
            var placed = new HashSet<int>(plan.Layout.Placements.Select(p => p.Index));
            var loose = plan.LargeArmIndices.Where(index => !placed.Contains(index))
                .GroupBy(index => items[index].Garment).Where(kit => LargeSleevesFit(kit.ToList(), items,
                    largeWidth, largeHeight, settings)).SelectMany(kit => kit).ToList();
            foreach (var sheet in LargeSleeveSheets(loose, items, largeWidth, largeHeight, settings))
                Add(plan, sheet, false);
            return plan;
        }

        private static IList<SportSheetNesting.Placement> Large(IList<SportSheetNesting.Item> items,
            int body, int sleeve, double width, double height, SportNestingSettings settings)
        {
            if (sleeve < 0)
            {
                if (Fits(items[body].Width, items[body].Height, width, height))
                    return new[] { At(items, body, 0, 0, 0, width, settings) };
                return null;
            }
            double sideWidth = items[body].Height + settings.Gap + items[sleeve].Height;
            double sideHeight = Math.Max(items[body].Width, items[sleeve].Width);
            if (Fits(sideHeight, sideWidth, width, height))
                return new[] {
                    At(items, body, (sideHeight - items[body].Width) / 2,
                        items[sleeve].Height + settings.Gap, 0, width, settings),
                    At(items, sleeve, (sideHeight - items[sleeve].Width) / 2, 0, 0, width, settings) };
            if (Fits(items[body].Width, items[body].Height, width, height))
                return new[] { At(items, body, 0, 0, 0, width, settings) };
            return null;
        }

        internal static bool LargeSleevesFit(IList<int> pair, IList<SportSheetNesting.Item> items,
            double width, double height, SportNestingSettings settings) => settings.Rotate &&
            pair.All(index => items[index].Allow90) && Fits(pair.Max(index => items[index].Height),
                pair.Sum(index => items[index].Width) + settings.Gap * (pair.Count - 1), width, height);

        internal static IList<IList<SportSheetNesting.Placement>> LargeSleeveSheets(IList<int> indices,
            IList<SportSheetNesting.Item> items, double width, double height, SportNestingSettings settings)
        {
            var sheets = new List<IList<SportSheetNesting.Placement>>();
            foreach (var size in indices.GroupBy(index => items[index].Size, StringComparer.OrdinalIgnoreCase))
            {
                var batch = new List<SportSheetNesting.Placement>(); double top = 0;
                foreach (var kit in size.GroupBy(index => items[index].Garment))
                {
                    SportNestingCancellation.Check();
                    var pair = kit.OrderBy(index => items[index].Part == 3 ? 0 : 1).ToList();
                    double pairHeight = pair.Sum(index => items[index].Width) + settings.Gap * (pair.Count - 1);
                    if (batch.Count > 0 && top + pairHeight > height + .000001)
                    { sheets.Add(batch); batch = new List<SportSheetNesting.Placement>(); top = 0; }
                    foreach (int index in pair)
                    { batch.Add(At(items, index, 0, top, -90, width, settings)); top += items[index].Width + settings.Gap; }
                }
                if (batch.Count > 0) sheets.Add(batch);
            }
            return sheets;
        }

        private static void AddSleeveSheets(SportPressPlan plan, IList<Row> rows,
            IList<SportSheetNesting.Item> items, double width, double height, SportNestingSettings settings)
        {
            var batch = new List<Row>(); double usedHeight = 0, leftWidth = 0, rightWidth = 0;
            foreach (var row in rows)
            {
                SportNestingCancellation.Check();
                double nextLeft = Math.Max(leftWidth, row.LeftWidth), nextRight = Math.Max(rightWidth, row.RightWidth);
                double nextHeight = usedHeight + (batch.Count == 0 ? 0 : settings.Gap) + row.Height;
                if (batch.Count > 0 && !Fits(nextLeft + nextRight +
                    (nextLeft > 0 && nextRight > 0 ? settings.Gap : 0), nextHeight, width, height))
                {
                    FlushRows(plan, batch, leftWidth, items, width, settings);
                    batch.Clear(); usedHeight = 0; leftWidth = 0; rightWidth = 0;
                }
                if (batch.Count > 0) usedHeight += settings.Gap;
                batch.Add(row); usedHeight += row.Height;
                leftWidth = Math.Max(leftWidth, row.LeftWidth); rightWidth = Math.Max(rightWidth, row.RightWidth);
            }
            if (batch.Count > 0) FlushRows(plan, batch, leftWidth, items, width, settings);
        }

        private static void FlushRows(SportPressPlan plan, IList<Row> rows, double leftWidth,
            IList<SportSheetNesting.Item> items, double width, SportNestingSettings settings)
        {
            var members = new List<SportSheetNesting.Placement>(); double top = 0;
            foreach (var row in rows)
            {
                if (row.Left >= 0) members.Add(At(items, row.Left, 0, top + row.Height - items[row.Left].Height, 0, width, settings));
                if (row.Right >= 0) members.Add(At(items, row.Right, leftWidth > 0 ? leftWidth + settings.Gap : 0,
                    top + row.Height - items[row.Right].Height, 0, width, settings));
                top += row.Height + settings.Gap;
            }
            Add(plan, members, true);
        }

        private static void Add(SportPressPlan plan, IList<SportSheetNesting.Placement> members, bool sleeveRows)
        {
            int page = plan.Layout.PageHeights.Count;
            plan.Layout.PageHeights.Add(members.Max(p => p.Top + p.Height));
            foreach (var member in members) { member.Page = page; plan.Layout.Placements.Add(member); }
            plan.LockedPages.Add(page);
            if (sleeveRows) plan.SleeveRowPages.Add(page);
        }

        private static bool Fits(double w, double h, double width, double height) =>
            w > 0 && h > 0 && w <= width + .000001 && h <= height + .000001;

        private static SportSheetNesting.Placement At(IList<SportSheetNesting.Item> items, int index,
            double left, double top, int rotation, double width, SportNestingSettings settings)
        {
            var item = items[index]; int angle = (rotation + 360) % 360;
            double w = rotation == 0 ? item.Width : item.Height, h = rotation == 0 ? item.Height : item.Width;
            double step = SportSheetNesting.GridStep(width, settings.Density);
            var profile = item.Bands != null && settings.Density != SportNestingMode.Rectangles ?
                SportSheetNesting.Profile(new SportContourNesting.Item { Width = item.Width, Height = item.Height,
                    Bands = item.Bands }, step, angle, settings.Density) : new SportContourNesting.Profile {
                        Top = new double[Math.Max(1, (int)Math.Ceiling(w / step))],
                        Bottom = Enumerable.Repeat(h, Math.Max(1, (int)Math.Ceiling(w / step))).ToArray() };
            return new SportSheetNesting.Placement { Index = index, Left = left, Top = top,
                Width = w, Height = h, Rotation = rotation, Profile = profile };
        }
    }
}
