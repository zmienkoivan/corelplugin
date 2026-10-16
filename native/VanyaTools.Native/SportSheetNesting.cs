using System;
using System.Collections.Generic;
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
                if (best == null || result.PageHeights.Count < best.PageHeights.Count ||
                    (result.PageHeights.Count == best.PageHeights.Count &&
                    (result.PageHeights.Sum() < best.PageHeights.Sum() - 0.001 ||
                    (Math.Abs(result.PageHeights.Sum() - best.PageHeights.Sum()) <= 0.001 && result.Roughness < best.Roughness))))
                    best = result;
            }
            AlignBottomRow(best, gap, gapColumns);
            return best;
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
            double step, double limit, int gapColumns)
        {
            Placement best = null;
            foreach (Variant variant in variants)
            {
                if (variant.Rectangular && !CouldFitRectangle(page, variant)) continue;
                int last = columns - variant.Profile.Top.Length;
                var candidates = new SortedSet<int> { 0, last };
                // Regular anchors explore contour interlocks; exact previous
                // edges keep narrow side strips and internal gaps discoverable.
                int stride = Math.Max(1, (int)Math.Floor(8 / step));
                for (int column = 0; column <= last; column += stride) candidates.Add(column);
                foreach (Placement placed in page.Placed)
                    foreach (int anchor in new[] { placed.Column, placed.Column + placed.Profile.Top.Length + gapColumns,
                        placed.Column - variant.Profile.Top.Length - gapColumns,
                        placed.Column + placed.Profile.Top.Length - variant.Profile.Top.Length })
                        if (anchor >= 0 && anchor <= last) candidates.Add(anchor);
                foreach (int column in candidates)
                {
                    double top = LowestTop(page, variant, column, limit);
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

        private static double LowestTop(Page page, Variant variant, int column, double limit)
        {
            double top = 0;
            while (top + variant.Height <= limit + 0.000001)
            {
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
