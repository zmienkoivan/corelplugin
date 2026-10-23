using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    // Source geometry is immutable for the life of one preview window.
    // Reuse completed calculations when comparing rules or changing the budget.
    internal sealed class SportCalenderBaselineCache
    {
        private readonly Dictionary<string, SportSheetNesting.Result> _dense =
            new Dictionary<string, SportSheetNesting.Result>();
        private readonly Dictionary<string, SportSheetNesting.Result> _ordered =
            new Dictionary<string, SportSheetNesting.Result>();

        private static string Key(SportNestingSettings s) => String.Join("|", new[] {
            s.Width.ToString("R", CultureInfo.InvariantCulture), s.Length.ToString("R", CultureInfo.InvariantCulture),
            s.Gap.ToString("R", CultureInfo.InvariantCulture), ((int)s.Density).ToString(),
            s.Rotate.ToString(), s.Improve.ToString() });

        internal SportSheetNesting.Result Get(IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            string key = Key(s);
            lock (_dense) if (_dense.TryGetValue(key, out var cached)) return cached;
            var result = SportSheetNesting.Place(items, s.Width, s.Length, s.Gap, s.Density, s.Improve);
            SportNestingCancellation.Check();
            lock (_dense) { if (_dense.Count >= 8) _dense.Clear(); _dense[key] = result; }
            return result;
        }

        internal SportSheetNesting.Result Ordered(IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            string key = Key(s) + "|" + s.CalenderMode;
            lock (_ordered) if (_ordered.TryGetValue(key, out var cached)) return cached;
            var result = SportCalenderNesting.Place(items, s);
            SportNestingCancellation.Check();
            lock (_ordered) { if (_ordered.Count >= 32) _ordered.Clear(); _ordered[key] = result; }
            return result;
        }
    }

    internal static class SportCalenderNesting
    {
        internal const int Auto = 7;
        internal static readonly string[] Names = { "Плотно · прежний режим", "Пары + заполнение промежутков",
            "Пары + рукава по типам", "Компактные партии по 2", "Компактные партии по 3",
            "Компактно по типам и размерам", "Компактные комплекты", "Авто · в пределах допуска" };

        private sealed class Unit
        {
            internal SportSheetNesting.Item Item;
            internal IList<SportSheetNesting.Placement> Members;
        }

        internal static SportSheetNesting.Result Place(IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            if (s.CalenderMode < 1 || s.CalenderMode >= Auto)
                throw new InvalidOperationException("Выберите схему каландра.");
            if (items.Count == 0 || items.Any(i => String.IsNullOrWhiteSpace(i.Size) || i.Part < 0 || i.Part > 4 ||
                i.Width <= 0 || i.Height <= 0 || Double.IsNaN(i.Width + i.Height) || Double.IsInfinity(i.Width + i.Height)))
                throw new InvalidOperationException("У детали каландра не определены размер, тип или габариты.");
            // A body stays neck up. Only small parts may use source rotations.
            var source = items.Select(i => new SportSheetNesting.Item { Label = i.Label, Size = i.Size,
                Width = i.Width, Height = i.Height, Bands = i.Bands, SmallPart = i.SmallPart,
                Garment = i.Garment, Part = i.Part, Allow90 = i.Part >= 2 && i.Allow90 && s.Rotate,
                Allow180 = i.Part >= 2 && i.Allow180 && s.Rotate }).ToList();
            var sizes = Enumerable.Range(0, source.Count).GroupBy(i => source[i].Size, StringComparer.OrdinalIgnoreCase)
                .Select(size => size.GroupBy(i => source[i].Garment).OrderBy(kit => kit.Key)
                    .Select(kit => kit.OrderBy(i => Rank(source[i].Part)).ToList()).ToList()).ToList();
            if (sizes.SelectMany(size => size).Any(kit => kit.GroupBy(i => source[i].Part).Any(g => g.Count() != 1)))
                throw new InvalidOperationException("В комплекте каландра повторяется одна деталь.");
            SportSheetNesting.Result result;
            if (s.CalenderMode == 1 || s.CalenderMode == 2)
            {
                // Horizontal and vertical PAIRS are alternatives; the body
                // itself is never rotated. This also permits three narrow
                // bodies across the roll without reserving an entire row.
                var horizontal = Pairs(source, sizes, s, false);
                var vertical = Pairs(source, sizes, s, true);
                result = Shorter(horizontal, vertical);
            }
            else if (s.CalenderMode == 5)
            {
                var order = sizes.SelectMany(size => size.SelectMany(kit => kit)
                    .OrderBy(i => Rank(source[i].Part)).ThenBy(i => source[i].Garment)).ToList();
                result = SportSheetNesting.Place(source, s.Width, s.Length, s.Gap, s.Density, false, placementOrder: order);
            }
            else
            {
                int count = s.CalenderMode == 3 ? 2 : s.CalenderMode == 4 ? 3 : 1;
                var groups = new List<Unit>();
                foreach (var size in sizes)
                    for (int first = 0; first < size.Count; first += count)
                    {
                        SportNestingCancellation.Check();
                        var batch = size.Skip(first).Take(count).SelectMany(kit => kit).ToList();
                        if (count > 1) batch = batch.OrderBy(i => Rank(source[i].Part))
                            .ThenBy(i => source[i].Garment).ToList();
                        var inner = SportSheetNesting.Place(batch.Select(i => source[i]).ToList(),
                            s.Width, s.Length, s.Gap, s.Density, false,
                            placementOrder: Enumerable.Range(0, batch.Count).ToList());
                        if (inner.PageHeights.Count != 1)
                            throw new InvalidOperationException("Партия комплектов не помещается на одну страницу каландра. Выберите меньшую партию.");
                        groups.Add(Wrap(inner.Placements.Select(p => Copy(p, batch[p.Index], p.Left, p.Top)).ToList(), source, s));
                    }
                result = Pack(groups, s);
            }
            if (result.Placements.Count != source.Count || !new HashSet<int>(result.Placements.Select(p => p.Index))
                .SetEquals(Enumerable.Range(0, source.Count)))
                throw new InvalidOperationException("Схема каландра не сохранила все детали заказа.");
            if (result.Placements.Any(p => source[p.Index].Part < 2 && p.Rotation != 0))
                throw new InvalidOperationException("Корпус в упорядоченной схеме должен стоять горловиной вверх.");
            return result;
        }

        private static SportSheetNesting.Result Pairs(IList<SportSheetNesting.Item> items,
            IList<List<List<int>>> sizes, SportNestingSettings s, bool vertical)
        {
            var units = new List<Unit>(); double step = SportSheetNesting.GridStep(s.Width, s.Density);
            foreach (var kit in sizes.SelectMany(size => size))
            {
                SportNestingCancellation.Check();
                var bodies = kit.Where(i => items[i].Part < 2).ToList();
                if (bodies.Count == 0) continue;
                double height = bodies.Max(i => items[i].Height), left = 0, top = 0;
                var members = new List<SportSheetNesting.Placement>();
                foreach (int index in bodies)
                {
                    members.Add(SportPressTemplates.At(items, index, left,
                        vertical ? top : height - items[index].Height, 0, s.Width, s));
                    if (vertical) top += items[index].Height + s.Gap;
                    else left += (Math.Ceiling(items[index].Width / step) + Math.Ceiling(s.Gap / step)) * step;
                }
                var pair = Wrap(members, items, s);
                if (pair.Item.Width <= s.Width + .001 && pair.Item.Height <= s.Length + .001) units.Add(pair);
                else foreach (int index in bodies)
                    units.Add(Wrap(new[] { SportPressTemplates.At(items, index, 0, 0, 0, s.Width, s) }, items, s));
            }
            var pinned = units.Count == 0 ? null : Pack(units, s);
            var order = sizes.SelectMany(size => s.CalenderMode == 1 ? size.SelectMany(kit => kit) :
                size.SelectMany(kit => kit).OrderBy(i => Rank(items[i].Part)).ThenBy(i => items[i].Garment)).ToList();
            // Keep actual torso profiles pinned, rather than their enclosing
            // rectangles. Sleeves can occupy every remaining contour pocket.
            return SportSheetNesting.Place(items, s.Width, s.Length, s.Gap, s.Density, false,
                fixedLayout: pinned, preferGarmentPages: s.CalenderMode == 1, placementOrder: order);
        }

        private static Unit Wrap(IList<SportSheetNesting.Placement> members,
            IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            var first = items[members[0].Index]; double step = SportSheetNesting.GridStep(s.Width, s.Density);
            var bands = new List<SportContourNesting.Band>();
            if (s.Density != SportNestingMode.Rectangles)
                foreach (var p in members)
                    for (int column = 0; column < p.Profile.Top.Length; column++)
                    {
                        double top = p.Profile.Top[column], bottom = p.Profile.Bottom[column];
                        if (Double.IsNaN(top + bottom) || Double.IsInfinity(top + bottom) || bottom < top) continue;
                        bands.Add(new SportContourNesting.Band { Left = p.Left + column * step,
                            Right = p.Left + Math.Min(p.Width, (column + 1) * step),
                            Top = p.Top + top, Bottom = p.Top + bottom });
                    }
            bool single = members.Count == 1 && members[0].Rotation == 0;
            return new Unit { Members = members, Item = new SportSheetNesting.Item {
                Label = "Группа каландра · " + first.Label, Size = first.Size, Garment = first.Garment, Part = first.Part,
                SmallPart = members.All(p => items[p.Index].SmallPart),
                Width = members.Max(p => p.Left + p.Width), Height = members.Max(p => p.Top + p.Height),
                Bands = s.Density == SportNestingMode.Rectangles ? null : bands,
                Allow90 = single && first.Allow90, Allow180 = single && first.Allow180 } };
        }

        private static SportSheetNesting.Result Pack(IList<Unit> units, SportNestingSettings s)
        {
            var blocks = SportSheetNesting.Place(units.Select(unit => unit.Item).ToList(), s.Width, s.Length,
                s.Gap, s.Density, false, placementOrder: Enumerable.Range(0, units.Count).ToList());
            double step = SportSheetNesting.GridStep(s.Width, s.Density);
            var members = new List<SportSheetNesting.Placement>();
            foreach (var block in blocks.Placements)
            {
                SportNestingCancellation.Check();
                var unit = units[block.Index];
                foreach (var part in unit.Members)
                {
                    var placed = Copy(part, part.Index, block.Left + part.Left, block.Top + part.Top);
                    if (block.Rotation != 0)
                    {
                        if (unit.Members.Count != 1 || part.Rotation != 0)
                            throw new InvalidOperationException("Нельзя повернуть составную группу каландра.");
                        placed.Left = block.Left; placed.Top = block.Top;
                        placed.Width = block.Width; placed.Height = block.Height;
                        placed.Rotation = block.Rotation; placed.Profile = block.Profile;
                    }
                    placed.Page = block.Page; placed.Column = (int)Math.Round(placed.Left / step);
                    members.Add(placed);
                }
            }
            return new SportSheetNesting.Result { Placements = members, PageHeights = blocks.PageHeights.ToList() };
        }

        private static SportSheetNesting.Result Shorter(SportSheetNesting.Result a, SportSheetNesting.Result b) =>
            b.PageHeights.Sum() < a.PageHeights.Sum() - .001 ||
            (Math.Abs(a.PageHeights.Sum() - b.PageHeights.Sum()) <= .001 && b.PageHeights.Count < a.PageHeights.Count) ? b : a;

        private static SportSheetNesting.Placement Copy(SportSheetNesting.Placement p, int index, double left, double top) =>
            new SportSheetNesting.Placement { Index = index, Left = left, Top = top, Width = p.Width,
                Height = p.Height, Rotation = p.Rotation, Profile = p.Profile };
        private static int Rank(int part) => part == 1 ? 0 : part == 0 ? 1 : part;
    }
}
