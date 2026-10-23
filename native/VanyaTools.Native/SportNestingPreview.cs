using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    // These snapshots contain managed geometry only. Background search never owns Corel objects.
    internal sealed class SportNestingSettings
    {
        internal double Width, Length, Gap;
        internal SportNestingMode Density;
        internal SportGroupingMode Grouping;
        internal bool Rotate, Improve, Borders = true;
        internal int Press;
        internal double Left = 20, Right = 20, Top = 20, Bottom = 20;

        internal static SportNestingSettings From(SportLayoutOptions options, bool cutting) =>
            new SportNestingSettings { Width = options.SheetWidthMm, Length = options.MaxPageLengthMm,
                Gap = options.GapMm, Density = options.NestingMode, Grouping = options.GroupingMode,
                Rotate = cutting ? options.CutAllow180 : options.RotateSleeves,
                Press = cutting ? 0 : options.PressMode, Left = options.PressLeftMm,
                Right = options.PressRightMm, Top = options.PressTopMm, Bottom = options.PressBottomMm,
                Borders = options.PressBorders };

        internal void Apply(SportLayoutOptions options, bool cutting)
        {
            options.SheetWidthMm = Width; options.MaxPageLengthMm = Length; options.GapMm = Gap;
            options.NestingMode = Density; options.GroupingMode = Grouping;
            if (cutting) options.CutAllow180 = Rotate;
            else
            {
                options.RotateSleeves = Rotate; options.PressMode = Press;
                options.PressLeftMm = Left; options.PressRightMm = Right;
                options.PressTopMm = Top; options.PressBottomMm = Bottom; options.PressBorders = Borders;
            }
        }
    }

    internal sealed class SportPressPanel
    {
        internal int Page;
        internal double Left, Top, Width, Height;
    }

    internal sealed class SportNestingChoice
    {
        internal SportNestingSettings Settings;
        internal SportSheetNesting.Result Layout;
        internal IList<SportPressPanel> Panels = new List<SportPressPanel>();
        internal double Seconds;
        public override string ToString() => Layout.PageHeights.Count + " стр. · " +
            (Layout.PageHeights.Sum() / 1000).ToString("0.###") + " м · " +
            new[] { "Прямоугольники", "Упрощённые", "Полный контур" }[(int)Settings.Density] +
            " · " + new[] { "свободно", "пары", "комплект" }[(int)Settings.Grouping] +
            (Settings.Improve ? " · уплотнение" : " · быстро") +
            (Settings.Press == 0 ? "" : " · " + Panels.Count + " листов пресса");
    }

    internal static class SportNestingPreview
    {
        internal static SportNestingChoice Calculate(IList<SportSheetNesting.Item> source,
            SportNestingSettings settings)
        {
            var items = source.Select(item => new SportSheetNesting.Item { Width = item.Width, Height = item.Height,
                Label = item.Label,
                Bands = item.Bands, Garment = item.Garment, Part = item.Part, SmallPart = item.SmallPart,
                Allow90 = item.Allow90 && settings.Rotate, Allow180 = item.Allow180 && settings.Rotate }).ToList();
            if (settings.Press == 0)
                return new SportNestingChoice { Settings = settings, Layout = SportGroupedNesting.Place(items,
                    settings.Width, settings.Length, settings.Gap, settings.Density, settings.Grouping, settings.Improve) };

            SportNestingChoice best = null;
            Exception failure = null;
            // Both orientations of the physical press sheet are considered, without rotating whole garments.
            foreach (bool landscape in new[] { true, false })
            {
                SportNestingCancellation.Check();
                try
                {
                    double pressWidth = landscape ? 1200 : settings.Press == 1 ? 800 : 1000;
                    double pressHeight = landscape ? settings.Press == 1 ? 800 : 1000 : 1200;
                    double innerWidth = Math.Min(settings.Width, pressWidth) - settings.Left - settings.Right;
                    double innerHeight = Math.Min(settings.Length, pressHeight) - settings.Top - settings.Bottom;
                    if (innerWidth <= 0 || innerHeight <= 0)
                        throw new InvalidOperationException("Поля не оставляют рабочей зоны для деталей.");
                    var inside = SportGroupedNesting.Place(items, innerWidth, innerHeight, settings.Gap,
                        settings.Density, settings.Grouping, settings.Improve);
                    var members = Enumerable.Range(0, inside.PageHeights.Count)
                        .Select(page => inside.Placements.Where(p => p.Page == page).ToList()).ToList();
                    var blocks = members.Select((parts, index) => new SportSheetNesting.Item {
                        Label = "Лист для пресса №" + (index + 1),
                        Width = parts.Max(p => p.Left + p.Width) + settings.Left + settings.Right,
                        Height = parts.Max(p => p.Top + p.Height) + settings.Top + settings.Bottom }).ToList();
                    // Complete rectangular sheets can share a roll row but never interlock.
                    var outside = SportSheetNesting.Place(blocks, settings.Width, settings.Length,
                        Math.Max(1, settings.Gap), SportNestingMode.Rectangles, false);
                    var choice = new SportNestingChoice { Settings = settings,
                        Layout = new SportSheetNesting.Result { PageHeights = outside.PageHeights.ToList(),
                            Placements = new List<SportSheetNesting.Placement>() } };
                    foreach (var block in outside.Placements)
                    {
                        choice.Panels.Add(new SportPressPanel { Page = block.Page, Left = block.Left,
                            Top = block.Top, Width = block.Width, Height = block.Height });
                        foreach (var member in members[block.Index])
                            choice.Layout.Placements.Add(new SportSheetNesting.Placement { Index = member.Index,
                                Page = block.Page, Left = block.Left + settings.Left + member.Left,
                                Top = block.Top + settings.Top + member.Top, Width = member.Width,
                                Height = member.Height, Rotation = member.Rotation, Profile = member.Profile });
                    }
                    Validate(choice, items.Count);
                    // A press run values fewer transfers first, then paper use.
                    if (best == null || choice.Panels.Count < best.Panels.Count ||
                        (choice.Panels.Count == best.Panels.Count &&
                         choice.Layout.PageHeights.Count < best.Layout.PageHeights.Count) ||
                        (choice.Panels.Count == best.Panels.Count &&
                         choice.Layout.PageHeights.Count == best.Layout.PageHeights.Count &&
                         choice.Layout.PageHeights.Sum() < best.Layout.PageHeights.Sum() - .001)) best = choice;
                }
                catch (InvalidOperationException error) { failure = error; }
            }
            return best ?? throw new InvalidOperationException("Не удалось уложить детали в полезную зону пресса. " +
                "Попробуйте свободную группировку или уменьшите поля. " + failure?.Message, failure);
        }

        internal static void Validate(SportNestingChoice choice, int count)
        {
            var result = choice.Layout;
            if (result == null || result.Placements.Count != count || result.PageHeights.Count == 0 ||
                result.Placements.Select(p => p.Index).Distinct().Count() != count ||
                result.PageHeights.Any(h => Double.IsNaN(h) || Double.IsInfinity(h) || h <= 0 || h > choice.Settings.Length + .01) ||
                result.Placements.Any(p => p.Index < 0 || p.Index >= count || p.Page < 0 || p.Page >= result.PageHeights.Count ||
                    Double.IsNaN(p.Left + p.Top + p.Width + p.Height + p.Rotation) ||
                    Double.IsInfinity(p.Left + p.Top + p.Width + p.Height + p.Rotation) ||
                    p.Width <= 0 || p.Height <= 0 || p.Left < -.01 || p.Top < -.01 ||
                    p.Left + p.Width > choice.Settings.Width + .01 || p.Top + p.Height > result.PageHeights[p.Page] + .01))
                throw new InvalidOperationException("Вариант раскладки не прошёл проверку границ и количества деталей.");
        }
    }
}
