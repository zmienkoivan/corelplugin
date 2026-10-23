using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace VanyaTools.Native
{
    // One cache per preview window and its immutable source geometry. Completed
    // results only; changing an order rule reuses the same dense comparison.
    internal sealed class SportCalenderBaselineCache
    {
        private readonly Dictionary<string, SportSheetNesting.Result> _results =
            new Dictionary<string, SportSheetNesting.Result>();
        internal SportSheetNesting.Result Get(IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            string key = String.Join("|", new[] { s.Width.ToString("R", CultureInfo.InvariantCulture),
                s.Length.ToString("R", CultureInfo.InvariantCulture), s.Gap.ToString("R", CultureInfo.InvariantCulture),
                ((int)s.Density).ToString(), s.Rotate.ToString(), s.Improve.ToString() });
            lock (_results) if (_results.TryGetValue(key, out var cached)) return cached;
            var result = SportSheetNesting.Place(items, s.Width, s.Length, s.Gap, s.Density, s.Improve);
            SportNestingCancellation.Check();
            lock (_results)
            {
                if (_results.Count >= 8) _results.Clear();
                _results[key] = result;
            }
            return result;
        }
    }

    internal static class SportCalenderNesting
    {
        internal static readonly string[] Names = { "Плотно · прежний режим", "Пары + свои рукава сбоку",
            "Пары корпусов → рукава", "Партии по 2 комплекта", "Партии по 3 комплекта",
            "Ряды по типам одного размера", "Комплект за комплектом" };

        private sealed class Unit
        {
            internal IList<SportSheetNesting.Placement> Parts;
            internal double Width, Height;
        }

        private sealed class Builder
        {
            internal readonly SportSheetNesting.Result Result = new SportSheetNesting.Result {
                Placements = new List<SportSheetNesting.Placement>(), PageHeights = new List<double>() };
            private readonly SportNestingSettings _settings;
            internal Builder(SportNestingSettings settings) { _settings = settings; }
            internal void Add(IEnumerable<Unit> rows)
            {
                foreach (var row in rows)
                {
                    SportNestingCancellation.Check();
                    if (row.Width > _settings.Width + .001 || row.Height > _settings.Length + .001)
                        throw new InvalidOperationException("Ряд деталей не помещается в ширину/длину страницы каландра.");
                    int page = Result.PageHeights.Count - 1;
                    double top = page < 0 ? 0 : Result.PageHeights[page] + _settings.Gap;
                    if (page < 0 || top + row.Height > _settings.Length + .001)
                    { page++; Result.PageHeights.Add(0); top = 0; }
                    foreach (var part in row.Parts)
                    {
                        var copy = Copy(part, part.Left, top + part.Top); copy.Page = page;
                        Result.Placements.Add(copy);
                    }
                    Result.PageHeights[page] = top + row.Height;
                }
            }
        }

        internal static SportSheetNesting.Result Place(IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            if (s.CalenderMode < 1 || s.CalenderMode >= Names.Length)
                throw new InvalidOperationException("Выберите схему каландра.");
            if (items.Any(i => String.IsNullOrWhiteSpace(i.Size) || i.Part < 0 || i.Part > 4 ||
                i.Width <= 0 || i.Height <= 0 || Double.IsNaN(i.Width + i.Height) || Double.IsInfinity(i.Width + i.Height)))
                throw new InvalidOperationException("У детали каландра не определены размер, тип или габариты.");
            var result = new Builder(s);
            // Sizes retain the first occurrence in the order table. Within
            // each size, garment numbers are always ascending, never area-sorted.
            foreach (var size in Enumerable.Range(0, items.Count).GroupBy(i => items[i].Size, StringComparer.OrdinalIgnoreCase))
            {
                SportNestingCancellation.Check();
                var kits = size.GroupBy(i => items[i].Garment).OrderBy(g => g.Key)
                    .Select(g => g.OrderBy(i => Rank(items[i].Part)).ToList()).ToList();
                if (kits.Any(kit => kit.GroupBy(i => items[i].Part).Any(g => g.Count() != 1)))
                    throw new InvalidOperationException("В комплекте каландра повторяется одна деталь.");
                if (s.CalenderMode == 1) SidePairs(result, kits, items, s);
                else if (s.CalenderMode == 2)
                {
                    result.Add(Rows(kits.SelectMany(kit => BodyRows(kit, items, s)), s));
                    result.Add(Rows(kits.SelectMany(kit => SmallRows(kit, items, s)), s));
                }
                else if (s.CalenderMode == 3 || s.CalenderMode == 4)
                {
                    int count = s.CalenderMode == 3 ? 2 : 3;
                    for (int first = 0; first < kits.Count; first += count)
                    {
                        var batch = kits.Skip(first).Take(count).ToList();
                        foreach (int type in new[] { 1, 0 })
                            result.Add(Rows(batch.SelectMany(kit => kit.Where(i => items[i].Part == type))
                                .Select(i => Single(i, items, s)), s));
                        result.Add(Rows(batch.SelectMany(kit => SmallRows(kit, items, s)), s));
                    }
                }
                else if (s.CalenderMode == 5)
                    foreach (int type in new[] { 1, 0, 2, 3, 4 })
                        result.Add(Rows(kits.SelectMany(kit => kit.Where(i => items[i].Part == type))
                            .Select(i => Single(i, items, s)), s));
                else
                {
                    foreach (var kit in kits)
                    {
                        var rows = BodyRows(kit, items, s).Concat(SmallRows(kit, items, s)).ToList();
                        // Starting the next kit never backfills the previous
                        // one's band. A long kit may continue onto the next page.
                        result.Add(rows);
                    }
                }
            }
            var layout = result.Result;
            if (layout.Placements.Count != items.Count ||
                !new HashSet<int>(layout.Placements.Select(p => p.Index)).SetEquals(Enumerable.Range(0, items.Count)))
                throw new InvalidOperationException("Схема каландра не сохранила все детали заказа.");
            foreach (var page in layout.Placements.GroupBy(p => p.Page))
            {
                var ordered = page.OrderBy(p => p.Top).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    var a = ordered[i];
                    if (items[a.Index].Part < 2 && a.Rotation != 0)
                        throw new InvalidOperationException("Корпус в упорядоченной схеме должен стоять горловиной вверх.");
                    for (int j = i + 1; j < ordered.Count && ordered[j].Top < a.Top + a.Height + s.Gap - .001; j++)
                    {
                        var b = ordered[j];
                        if (a.Left + a.Width + s.Gap > b.Left + .001 && b.Left + b.Width + s.Gap > a.Left + .001)
                            throw new InvalidOperationException("В упорядоченной схеме пересекаются детали или нарушен зазор.");
                    }
                }
            }
            return layout;
        }

        private static void SidePairs(Builder result, IList<List<int>> kits,
            IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            var bodies = new List<Unit>(); var deferred = new List<int>();
            foreach (var kit in kits)
            {
                var rows = BodyRows(kit, items, s).ToList();
                var small = kit.Where(i => items[i].Part >= 2).ToList();
                bool attached = false;
                for (int at = 0; at < rows.Count && !attached && small.Count > 0; at++)
                {
                    var side = SideStack(small, rows[at], items, s);
                    var used = small;
                    if (side == null)
                    {
                        used = small.Where(i => items[i].Part != 4).ToList();
                        side = SideStack(used, rows[at], items, s);
                    }
                    if (side == null) continue;
                    var body = rows[at];
                    rows[at] = Make(body.Parts.Concat(side.Parts.Select(p => Copy(p, body.Width + s.Gap + p.Left, p.Top))));
                    deferred.AddRange(small.Except(used)); attached = true;
                }
                bodies.AddRange(rows);
                if (!attached) deferred.AddRange(small);
            }
            result.Add(Rows(bodies, s));
            result.Add(Rows(deferred.GroupBy(i => items[i].Garment).OrderBy(g => g.Key)
                .SelectMany(kit => SmallRows(kit.ToList(), items, s)), s));
        }

        private static Unit SideStack(IList<int> indices, Unit body,
            IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            if (indices.Count == 0) return null;
            Unit best = null;
            foreach (int rotation in s.Rotate ? new[] { 0, -90 } : new[] { 0 })
            {
                if (rotation != 0 && indices.Any(i => !items[i].Allow90)) continue;
                var members = new List<SportSheetNesting.Placement>(); double top = 0;
                foreach (int index in indices.OrderBy(i => Rank(items[i].Part)))
                {
                    var part = SportPressTemplates.At(items, index, 0, top, rotation, s.Width, s);
                    members.Add(part); top += part.Height + s.Gap;
                }
                var candidate = Make(members);
                if (body.Width + s.Gap + candidate.Width > s.Width + .001 || candidate.Height > body.Height + .001) continue;
                if (best == null || candidate.Height < best.Height - .001) best = candidate;
            }
            return best;
        }

        private static IEnumerable<Unit> BodyRows(IList<int> kit, IList<SportSheetNesting.Item> items, SportNestingSettings s) =>
            Rows(kit.Where(i => items[i].Part < 2).OrderBy(i => Rank(items[i].Part)).Select(i => Single(i, items, s)), s);

        private static IEnumerable<Unit> SmallRows(IList<int> kit, IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            var small = kit.Where(i => items[i].Part >= 2).OrderBy(i => Rank(items[i].Part)).ToList();
            var rows = Rows(small.Select(i => Single(i, items, s)), s).ToList();
            if (rows.Count == 0) return rows;
            double height = rows.Sum(row => row.Height) + s.Gap * (rows.Count - 1);
            // Keep a selected sleeve pair and collar as one little unit when
            // possible; the row builder can put two such units side by side.
            if (height > s.Length + .001) return rows;
            var members = new List<SportSheetNesting.Placement>(); double top = 0;
            foreach (var row in rows)
            {
                members.AddRange(row.Parts.Select(p => Copy(p, p.Left, top + p.Top)));
                top += row.Height + s.Gap;
            }
            return new[] { Make(members) };
        }

        private static Unit Single(int index, IList<SportSheetNesting.Item> items, SportNestingSettings s)
        {
            var item = items[index]; int rotation = 0;
            if (item.Width > s.Width + .001 || item.Height > s.Length + .001)
            {
                if (item.Part < 2 || !item.Allow90 || !s.Rotate || item.Height > s.Width + .001 || item.Width > s.Length + .001)
                    throw new InvalidOperationException(item.Label + ": " + item.Width.ToString("0.#") + " × " +
                        item.Height.ToString("0.#") + " мм не помещается в схему каландра.");
                rotation = -90;
            }
            return Make(new[] { SportPressTemplates.At(items, index, 0, 0, rotation, s.Width, s) });
        }

        private static IEnumerable<Unit> Rows(IEnumerable<Unit> units, SportNestingSettings s)
        {
            var row = new List<Unit>(); double width = 0;
            foreach (var unit in units)
            {
                SportNestingCancellation.Check();
                if (row.Count > 0 && width + s.Gap + unit.Width > s.Width + .001)
                { yield return Row(row, s.Gap); row.Clear(); width = 0; }
                if (row.Count > 0) width += s.Gap;
                row.Add(unit); width += unit.Width;
            }
            if (row.Count > 0) yield return Row(row, s.Gap);
        }

        private static Unit Row(IList<Unit> units, double gap)
        {
            double height = units.Max(unit => unit.Height), left = 0;
            var members = new List<SportSheetNesting.Placement>();
            foreach (var unit in units)
            {
                foreach (var part in unit.Parts) members.Add(Copy(part, left + part.Left, height - unit.Height + part.Top));
                left += unit.Width + gap;
            }
            return Make(members);
        }

        private static Unit Make(IEnumerable<SportSheetNesting.Placement> parts)
        {
            var list = parts.ToList();
            return new Unit { Parts = list, Width = list.Max(p => p.Left + p.Width), Height = list.Max(p => p.Top + p.Height) };
        }

        private static SportSheetNesting.Placement Copy(SportSheetNesting.Placement p, double left, double top) =>
            new SportSheetNesting.Placement { Index = p.Index, Left = left, Top = top, Width = p.Width,
                Height = p.Height, Rotation = p.Rotation, Profile = p.Profile };
        private static int Rank(int part) => part == 1 ? 0 : part == 0 ? 1 : part;
    }
}
