using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace VanyaTools.Native
{
    // Bounded pages retain every occupied interval, including gaps above the
    // lowest placed pieces. Search never calls the Corel object model.
    internal static class SportSheetNesting
    {
        internal sealed class Item
        {
            public double Width, Height;
            public bool SmallPart, Allow90, Allow180;
            public IList<SportContourNesting.Band> Bands;
        }

        internal sealed class Placement
        {
            public int Index, Page;
            public double Left, Top, Width, Height, Rotation;
            internal int Column;
            internal SportContourNesting.Profile Profile;
        }

        internal sealed class Result
        {
            public IList<Placement> Placements;
            public IList<double> PageHeights;
            internal double Roughness;
        }

        private sealed class Variant
        {
            public double Width, Height, Rotation;
            public bool Rectangular;
            public SportContourNesting.Profile Profile;
        }

        private struct Interval
        {
            public double Top, Bottom;
        }

        private sealed class Page
        {
            public readonly List<Interval>[] Occupied;
            public readonly double[] MaxFreeHeight;
            public readonly double Limit;
            public readonly List<Placement> Placed = new List<Placement>();
            public double Height;
            public Page(int columns, double limit)
            {
                Occupied = new List<Interval>[columns];
                MaxFreeHeight = Enumerable.Repeat(limit, columns).ToArray(); Limit = limit;
            }
        }

        internal static Result Place(IList<Item> items, double width, double limit, double gap)
        {
            if (width <= 0 || limit <= 0 || gap < 0 || Double.IsNaN(width + limit + gap) ||
                Double.IsInfinity(width + limit + gap))
                throw new InvalidOperationException("Некорректные размеры страницы или зазор.");
            int columns = Math.Max(1, (int)Math.Ceiling(width / 2));
            double step = width / columns;
            int gapColumns = (int)Math.Ceiling(gap / step);
            var profiles = new Dictionary<IList<SportContourNesting.Band>, SportContourNesting.Profile[]>();
            var variants = new List<Variant>[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item.Width <= 0 || item.Height <= 0 || Double.IsNaN(item.Width + item.Height) ||
                    Double.IsInfinity(item.Width + item.Height))
                    throw new InvalidOperationException("У детали некорректный размер.");
                variants[i] = new List<Variant>();
                if (item.Bands != null)
                {
                    if (!profiles.TryGetValue(item.Bands, out SportContourNesting.Profile[] pair))
                    {
                        var source = new SportContourNesting.Item { Width = item.Width, Height = item.Height, Bands = item.Bands };
                        pair = new[] { SportContourNesting.MakeProfile(source, step, false),
                            SportContourNesting.MakeProfile(source, step, true) };
                        profiles.Add(item.Bands, pair);
                    }
                    variants[i].Add(new Variant { Width = item.Width, Height = item.Height, Profile = pair[0] });
                    if (item.Allow180) variants[i].Add(new Variant { Width = item.Width,
                        Height = item.Height, Rotation = 180, Profile = pair[1] });
                }
                else
                {
                    variants[i].Add(Rectangle(item.Width, item.Height, 0, step));
                    if (item.Allow90) variants[i].Add(Rectangle(item.Height, item.Width, 90, step));
                }
                variants[i].RemoveAll(v => v.Profile.Top.Length > columns || v.Height > limit + 0.000001);
                if (variants[i].Count == 0)
                    throw new InvalidOperationException("Деталь " + item.Width.ToString("0.#") + " × " +
                        item.Height.ToString("0.#") + " мм не помещается на страницу. Увеличьте её максимальную длину или ширину.");
            }

            Result best = null;
            for (int strategy = 0; strategy < 3; strategy++)
            {
                IEnumerable<int> order = Enumerable.Range(0, items.Count);
                if (strategy == 0) order = order.OrderBy(i => items[i].SmallPart)
                    .ThenByDescending(i => items[i].Height).ThenByDescending(i => items[i].Width);
                else if (strategy == 1) order = order.OrderBy(i => items[i].SmallPart)
                    .ThenByDescending(i => items[i].Width * items[i].Height).ThenByDescending(i => items[i].Height);
                else order = order.OrderByDescending(i => items[i].Width * items[i].Height)
                    .ThenByDescending(i => Math.Max(items[i].Width, items[i].Height));
                var pages = new List<Page>();
                foreach (int index in order)
                {
                    Placement selected = null;
                    int pageIndex = 0;
                    for (; pageIndex < pages.Count; pageIndex++)
                    {
                        selected = Find(pages[pageIndex], variants[index], columns, step, limit, gapColumns);
                        if (selected != null) break; // Fill earlier pages before creating another.
                    }
                    if (selected == null)
                    {
                        var page = new Page(columns, limit); pages.Add(page);
                        selected = Find(page, variants[index], columns, step, limit, gapColumns);
                        if (selected == null) throw new InvalidOperationException("Не удалось разместить деталь на пустой странице.");
                    }
                    selected.Index = index; selected.Page = pageIndex;
                    Page target = pages[pageIndex];
                    target.Placed.Add(selected);
                    target.Height = Math.Max(target.Height, selected.Top + selected.Height);
                    Occupy(target, selected, gap, gapColumns);
                }
                double roughness = pages.Sum(p => p.Occupied.Sum(cell =>
                {
                    double bottom = cell == null ? 0 : Math.Max(0, cell[cell.Count - 1].Bottom - gap);
                    return Math.Pow(p.Height - bottom, 2);
                }));
                var result = new Result { Placements = pages.SelectMany(p => p.Placed).ToList(),
                    PageHeights = pages.Select(p => Math.Max(10, p.Height)).ToList(), Roughness = roughness };
                if (Better(result, best)) best = result;
            }
            if (items.Any(item => item.Bands != null))
                best = ImproveContours(best, items, variants, columns, step, limit, gap, gapColumns);
            AlignBottomRow(best, gap, gapColumns);
            if (best.Placements.Count != items.Count || best.Placements.Select(p => p.Index).Distinct().Count() != items.Count ||
                best.PageHeights.Any(height => height > limit + 0.001))
                throw new InvalidOperationException("Нестинг вернул некорректное число деталей или длину страницы.");
            return best;
        }

        private static bool Better(Result candidate, Result current) => current == null ||
            candidate.PageHeights.Count < current.PageHeights.Count ||
            (candidate.PageHeights.Count == current.PageHeights.Count &&
            (candidate.PageHeights.Sum() < current.PageHeights.Sum() - 0.001 ||
            (Math.Abs(candidate.PageHeights.Sum() - current.PageHeights.Sum()) <= 0.001 && candidate.Roughness < current.Roughness - 0.001)));

        private static double Roughness(Page page, double gap) => page.Occupied.Sum(cell =>
        {
            double bottom = cell == null ? 0 : Math.Max(0, cell[cell.Count - 1].Bottom - gap);
            return Math.Pow(page.Height - bottom, 2);
        });

        private static bool BetterPage(Page candidate, Page current, double gap) => candidate != null &&
            (candidate.Height < current.Height - 0.001 ||
            (Math.Abs(candidate.Height - current.Height) <= 0.001 && Roughness(candidate, gap) < Roughness(current, gap) - 0.001));

        private static bool Expired(long deadline) => deadline != 0 && Stopwatch.GetTimestamp() >= deadline;

        private static Page RebuildPage(IEnumerable<Placement> placements, int columns, double limit, double gap, int gapColumns)
        {
            var page = new Page(columns, limit);
            foreach (var placement in placements)
            {
                var copy = new Placement { Index = placement.Index, Page = placement.Page, Column = placement.Column,
                    Left = placement.Left, Top = placement.Top, Width = placement.Width, Height = placement.Height,
                    Rotation = placement.Rotation, Profile = placement.Profile };
                page.Placed.Add(copy);
                page.Height = Math.Max(page.Height, copy.Top + copy.Height);
                Occupy(page, copy, gap, gapColumns);
            }
            return page;
        }

        private static Page PackPage(IList<int> order, IList<Variant>[] variants, int columns, double step,
            double heightLimit, double gap, int gapColumns, long deadline)
        {
            var page = new Page(columns, heightLimit);
            foreach (int index in order)
            {
                if (Expired(deadline)) return null;
                Placement placed = Find(page, variants[index], columns, step, heightLimit, gapColumns,
                    deadline, order.Count <= 24);
                if (placed == null) return null;
                placed.Index = index;
                page.Placed.Add(placed);
                page.Height = Math.Max(page.Height, placed.Top + placed.Height);
                Occupy(page, placed, gap, gapColumns);
            }
            return page;
        }

        private static Page ImprovePage(Page source, IList<Item> items, IList<Variant>[] variants,
            int columns, double step, double gap, int gapColumns, long deadline)
        {
            // A deferred torso can occupy a bottom side pocket instead of
            // forcing every sleeve below a central torso. Repack complete
            // permutations, so accepting a move never drops another piece.
            Page best = source;
            for (int round = 0; round < 2 && !Expired(deadline); round++)
            {
                Page before = best;
                var order = best.Placed.Select(p => p.Index).ToList();
                var positions = best.Placed.ToDictionary(p => p.Index);
                var priorities = order.OrderBy(index => items[index].SmallPart).ThenByDescending(index =>
                {
                    Placement placed = positions[index];
                    double sideSpace = Math.Max(0, Math.Min(placed.Left,
                        columns * step - placed.Left - placed.Width));
                    return sideSpace * placed.Width * placed.Height / (placed.Top + 100);
                }).ToList();
                // First try all deferrals, then earlier/later insertions.
                // The center blocker in an odd row gets a chance before a
                // large family of less useful insertion permutations.
                foreach (int phase in new[] { 0, 1 })
                foreach (int index in priorities)
                {
                    if (Expired(deadline)) return best;
                    int from = order.IndexOf(index);
                    IEnumerable<int> destinations = phase == 0 ? new[] { order.Count - 1 } : order.Count <= 24
                        ? Enumerable.Range(0, order.Count).OrderByDescending(to => Math.Abs(to - from))
                        : new[] { 0, Math.Min(order.Count - 1, from + 2), Math.Max(0, from - 2), order.Count / 2 };
                    foreach (int to in destinations)
                    {
                        if (to == from || (phase == 1 && to == order.Count - 1)) continue;
                        if (Expired(deadline)) return best;
                        var proposed = new List<int>(order);
                        proposed.RemoveAt(from); proposed.Insert(to, index);
                        Page candidate = PackPage(proposed, variants, columns, step, best.Height, gap, gapColumns, deadline);
                        if (BetterPage(candidate, best, gap)) best = candidate;
                    }
                }
                if (Object.ReferenceEquals(before, best)) break;
            }
            return best;
        }

        private static Result ImproveContours(Result original, IList<Item> items, IList<Variant>[] variants,
            int columns, double step, double limit, double gap, int gapColumns)
        {
            long started = Stopwatch.GetTimestamp();
            long deadline = started + 8 * Stopwatch.Frequency;
            long localDeadline = started + (original.PageHeights.Count == 1 ? 8 : 6) * Stopwatch.Frequency;
            var pages = Enumerable.Range(0, original.PageHeights.Count).Select(index =>
                RebuildPage(original.Placements.Where(p => p.Page == index), columns, limit, gap, gapColumns)).ToList();
            var targets = Enumerable.Range(0, pages.Count).Where(index => pages[index].Placed.Count <= 80)
                .OrderByDescending(index => pages[index].Occupied.Sum(cell =>
                    pages[index].Height - (cell == null ? 0 : cell.Sum(interval =>
                        Math.Max(0, Math.Min(pages[index].Height, interval.Bottom) - Math.Max(0, interval.Top)))))).ToList();
            for (int at = 0; at < targets.Count && !Expired(localDeadline); at++)
            {
                long now = Stopwatch.GetTimestamp();
                long pageDeadline = now + (localDeadline - now) / Math.Max(1, Math.Min(4, targets.Count - at));
                int index = targets[at];
                pages[index] = ImprovePage(pages[index], items, variants, columns, step, gap, gapColumns, pageDeadline);
            }
            // Compaction can open pockets that did not exist during the first
            // pass. Fill them from later sheets without extending earlier ones.
            for (int source = pages.Count - 1; source > 0 && !Expired(deadline); source--)
            {
                bool moved = false;
                foreach (var piece in pages[source].Placed.OrderBy(p => p.Width * p.Height).ToList())
                {
                    if (Expired(deadline)) break;
                    for (int destination = 0; destination < source && !Expired(deadline); destination++)
                    {
                        Placement placed = Find(pages[destination], variants[piece.Index], columns, step,
                            pages[destination].Height, gapColumns, deadline, true);
                        if (placed == null) continue;
                        placed.Index = piece.Index;
                        pages[destination].Placed.Add(placed);
                        Occupy(pages[destination], placed, gap, gapColumns);
                        pages[source].Placed.Remove(piece);
                        moved = true; break;
                    }
                }
                if (moved)
                {
                    pages[source] = RebuildPage(pages[source].Placed, columns, limit, gap, gapColumns);
                    if (pages[source].Placed.Count > 0 && !Expired(deadline))
                    {
                        Page compact = PackPage(pages[source].Placed.Select(p => p.Index).ToList(), variants,
                            columns, step, pages[source].Height, gap, gapColumns, deadline);
                        if (BetterPage(compact, pages[source], gap)) pages[source] = compact;
                    }
                }
            }
            pages.RemoveAll(page => page.Placed.Count == 0);
            for (int index = 0; index < pages.Count; index++)
                foreach (var placed in pages[index].Placed) placed.Page = index;
            var result = new Result { Placements = pages.SelectMany(page => page.Placed).ToList(),
                PageHeights = pages.Select(page => Math.Max(10, page.Height)).ToList(),
                Roughness = pages.Sum(page => Roughness(page, gap)) };
            bool accepted = Better(result, original);
            Log.Info("Sport nesting improvement: elapsed=" +
                ((Stopwatch.GetTimestamp() - started) * 1000 / Stopwatch.Frequency) +
                " ms, accepted=" + accepted + ", pages=" + original.PageHeights.Count + " -> " + result.PageHeights.Count +
                ", length=" + original.PageHeights.Sum().ToString("0.#") + " -> " + result.PageHeights.Sum().ToString("0.#") + " mm");
            return accepted ? result : original;
        }

        private static Variant Rectangle(double width, double height, double rotation, double step)
        {
            int count = Math.Max(1, (int)Math.Ceiling(width / step - 0.00000001));
            return new Variant { Width = width, Height = height, Rotation = rotation,
                Rectangular = true,
                Profile = new SportContourNesting.Profile { Top = new double[count],
                    Bottom = Enumerable.Repeat(height, count).ToArray() } };
        }

        private static Placement Find(Page page, IList<Variant> variants, int columns,
            double step, double limit, int gapColumns, long deadline = 0, bool dense = false)
        {
            Placement best = null;
            foreach (Variant variant in variants)
            {
                if (variant.Rectangular && !CouldFitRectangle(page, variant)) continue;
                int last = columns - variant.Profile.Top.Length;
                var candidates = new SortedSet<int> { 0, last };
                // Regular anchors explore contour interlocks; exact previous
                // edges keep narrow side strips and internal gaps discoverable.
                int stride = Math.Max(1, (int)Math.Floor((dense ? 2 : 8) / step));
                for (int column = 0; column <= last; column += stride) candidates.Add(column);
                foreach (Placement placed in page.Placed)
                    foreach (int anchor in new[] { placed.Column, placed.Column + placed.Profile.Top.Length + gapColumns,
                        placed.Column - variant.Profile.Top.Length - gapColumns,
                        placed.Column + placed.Profile.Top.Length - variant.Profile.Top.Length })
                        if (anchor >= 0 && anchor <= last) candidates.Add(anchor);
                foreach (int column in candidates)
                {
                    if (Expired(deadline)) return null;
                    double top = LowestTop(page, variant, column, limit, deadline);
                    if (Double.IsInfinity(top)) continue;
                    double bottom = top + variant.Height;
                    if (best == null || bottom < best.Top + best.Height - 0.000001 ||
                        (Math.Abs(bottom - best.Top - best.Height) <= 0.000001 &&
                        (top < best.Top - 0.000001 || (Math.Abs(top - best.Top) <= 0.000001 && column < best.Column))))
                        best = new Placement { Column = column, Left = column * step, Top = top,
                            Width = variant.Width, Height = variant.Height, Rotation = variant.Rotation, Profile = variant.Profile };
                }
            }
            return best;
        }

        private static bool CouldFitRectangle(Page page, Variant variant)
        {
            int run = 0;
            foreach (double free in page.MaxFreeHeight)
            {
                run = free + 0.000001 >= variant.Height ? run + 1 : 0;
                if (run >= variant.Profile.Top.Length) return true;
            }
            return false;
        }

        private static double LowestTop(Page page, Variant variant, int column, double limit, long deadline)
        {
            double top = 0;
            while (top + variant.Height <= limit + 0.000001)
            {
                if (Expired(deadline)) return Double.PositiveInfinity;
                double next = top;
                for (int j = 0; j < variant.Profile.Top.Length; j++)
                {
                    List<Interval> cell = page.Occupied[column + j];
                    if (cell == null) continue;
                    double from = top + variant.Profile.Top[j], to = top + variant.Profile.Bottom[j];
                    foreach (Interval interval in cell)
                    {
                        if (interval.Top >= to - 0.000001) break;
                        if (interval.Bottom > from + 0.000001)
                            next = Math.Max(next, interval.Bottom - variant.Profile.Top[j]);
                    }
                    if (next + variant.Height > limit + 0.000001) return Double.PositiveInfinity;
                }
                if (next <= top + 0.000001) return top;
                top = next;
            }
            return Double.PositiveInfinity;
        }

        private static void Occupy(Page page, Placement placed, double gap, int gapColumns)
        {
            for (int j = 0; j < placed.Profile.Top.Length; j++)
            {
                var interval = new Interval { Top = placed.Top + placed.Profile.Top[j] - gap,
                    Bottom = placed.Top + placed.Profile.Bottom[j] + gap };
                for (int column = Math.Max(0, placed.Column + j - gapColumns);
                    column <= Math.Min(page.Occupied.Length - 1, placed.Column + j + gapColumns); column++)
                {
                    List<Interval> cell = page.Occupied[column];
                    if (cell == null) page.Occupied[column] = cell = new List<Interval>();
                    Merge(cell, interval);
                }
            }
            for (int column = Math.Max(0, placed.Column - gapColumns);
                column <= Math.Min(page.Occupied.Length - 1, placed.Column + placed.Profile.Top.Length - 1 + gapColumns); column++)
            {
                double end = 0, largest = 0;
                foreach (Interval interval in page.Occupied[column])
                {
                    largest = Math.Max(largest, Math.Min(page.Limit, interval.Top) - end);
                    end = Math.Max(end, Math.Min(page.Limit, interval.Bottom));
                }
                page.MaxFreeHeight[column] = Math.Max(largest, page.Limit - end);
            }
        }

        private static void Merge(List<Interval> cell, Interval interval)
        {
            int index = 0;
            while (index < cell.Count && cell[index].Bottom < interval.Top - 0.000001) index++;
            while (index < cell.Count && cell[index].Top <= interval.Bottom + 0.000001)
            {
                interval.Top = Math.Min(interval.Top, cell[index].Top);
                interval.Bottom = Math.Max(interval.Bottom, cell[index].Bottom);
                cell.RemoveAt(index);
            }
            cell.Insert(index, interval);
        }

        private static void AlignBottomRow(Result result, double gap, int gapColumns)
        {
            foreach (Placement placed in result.Placements.OrderByDescending(p => p.Top + p.Height))
            {
                double bottom = result.PageHeights[placed.Page];
                double proposed = bottom - placed.Height;
                if (proposed <= placed.Top + 0.001 || bottom - placed.Top - placed.Height > placed.Height) continue;
                bool collision = result.Placements.Any(other => other != placed && other.Page == placed.Page &&
                    Collides(placed, proposed, other, gap, gapColumns));
                if (!collision) placed.Top = proposed;
            }
        }

        private static bool Collides(Placement a, double top, Placement b, double gap, int gapColumns)
        {
            if (a.Left + a.Width + gap <= b.Left + 0.000001 || b.Left + b.Width + gap <= a.Left + 0.000001 ||
                top + a.Height + gap <= b.Top + 0.000001 || b.Top + b.Height + gap <= top + 0.000001) return false;
            for (int j = 0; j < a.Profile.Top.Length; j++)
            {
                int center = a.Column + j - b.Column;
                for (int k = Math.Max(0, center - gapColumns); k <= Math.Min(b.Profile.Top.Length - 1, center + gapColumns); k++)
                    if (top + a.Profile.Bottom[j] > b.Top + b.Profile.Top[k] - gap + 0.000001 &&
                        top + a.Profile.Top[j] < b.Top + b.Profile.Bottom[k] + gap - 0.000001) return true;
            }
            return false;
        }
    }
}
