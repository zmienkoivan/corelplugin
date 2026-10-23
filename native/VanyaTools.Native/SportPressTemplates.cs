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

    // Build torso sheets only. Sleeves are fitted to the roll afterwards,
    // without reserving full-width sleeve sheets in front of the bodies.
    internal static class SportPressTemplates
    {
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
            foreach (var kit in Enumerable.Range(0, items.Count).GroupBy(i => items[i].Garment))
            {
                SportNestingCancellation.Check();
                var parts = kit.GroupBy(i => items[i].Part).ToDictionary(g => g.Key, g => g.ToList());
                if (parts.Values.Any(group => group.Count != 1) || !parts.ContainsKey(0) || !parts.ContainsKey(1)) continue;
                int front = parts[0][0], back = parts[1][0];
                double pairWidth = items[back].Width + settings.Gap + items[front].Width;
                double pairHeight = Math.Max(items[back].Height, items[front].Height);
                if (Fits(pairWidth, pairHeight, smallWidth, smallHeight))
                {
                    bodies.Add(new[] {
                        At(items, back, 0, pairHeight - items[back].Height, 0, smallWidth, settings),
                        At(items, front, items[back].Width + settings.Gap, pairHeight - items[front].Height, 0, smallWidth, settings) });
                    plan.PairedGarments++;
                    continue;
                }
                var frontSheet = Large(items, front, largeWidth, largeHeight, settings);
                var backSheet = Large(items, back, largeWidth, largeHeight, settings);
                if (frontSheet == null || backSheet == null) continue;
                large.Add(frontSheet); large.Add(backSheet); plan.LargeGarments++;
            }
            foreach (var sheet in bodies) Add(plan, sheet, false);
            foreach (var sheet in large) Add(plan, sheet, false);
            return plan;
        }

        private static IList<SportSheetNesting.Placement> Large(IList<SportSheetNesting.Item> items,
            int body, double width, double height, SportNestingSettings settings)
        {
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

        internal static SportSheetNesting.Placement At(IList<SportSheetNesting.Item> items, int index,
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
