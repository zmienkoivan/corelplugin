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
        internal int PressGrouping, PressOrientation = 1;
        internal double PressSheetWidth = 750;
        internal double Left = 20, Right = 20, Top = 20, Bottom = 20;

        internal static SportNestingSettings From(SportLayoutOptions options, bool cutting) =>
            new SportNestingSettings { Width = options.SheetWidthMm, Length = options.MaxPageLengthMm,
                Gap = options.GapMm, Density = options.NestingMode, Grouping = options.GroupingMode,
                Rotate = cutting ? options.CutAllow180 : options.RotateSleeves,
                Press = cutting ? 0 : options.PressMode, Left = options.PressLeftMm,
                Right = options.PressRightMm, Top = options.PressTopMm, Bottom = options.PressBottomMm,
                Borders = options.PressBorders, PressGrouping = options.PressGrouping,
                PressOrientation = options.PressOrientation, PressSheetWidth = options.PressSheetWidthMm };

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
                options.PressGrouping = PressGrouping; options.PressOrientation = PressOrientation;
                options.PressSheetWidthMm = PressSheetWidth;
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
            " · " + (Settings.Press == 0 ? new[] { "свободно", "пары", "комплект" }[(int)Settings.Grouping] :
                new[] { "комплект → пары", "пары + рукава", "по размерам" }[Settings.PressGrouping]) +
            (Settings.Improve ? " · уплотнение" : " · быстро") +
            (Settings.Press == 0 ? "" : " · " + Panels.Count + " листов пресса");
    }

    internal static class SportNestingPreview
    {
        internal static SportNestingChoice Calculate(IList<SportSheetNesting.Item> source,
            SportNestingSettings settings)
        {
            var items = source.Select(item => new SportSheetNesting.Item { Width = item.Width, Height = item.Height,
                Label = item.Label, Size = item.Size,
                Bands = item.Bands, Garment = item.Garment, Part = item.Part, SmallPart = item.SmallPart,
                Allow90 = item.Allow90 && settings.Rotate, Allow180 = item.Allow180 && settings.Rotate }).ToList();
            if (settings.Press == 0)
                return new SportNestingChoice { Settings = settings, Layout = SportGroupedNesting.Place(items,
                    settings.Width, settings.Length, settings.Gap, settings.Density, settings.Grouping, settings.Improve) };

            SportNestingChoice best = null;
            Exception failure = null;
            // Sheet orientation does not rotate the printed artwork.
            foreach (bool landscape in settings.PressOrientation == 1 ? new[] { false } :
                settings.PressOrientation == 2 ? new[] { true } : new[] { true, false })
            {
                SportNestingCancellation.Check();
                try
                {
                    double pressWidth = landscape ? 1200 : settings.Press == 1 ? 800 : 1000;
                    double pressHeight = landscape ? settings.Press == 1 ? 800 : 1000 : 1200;
                    double sheetWidth = Math.Min(settings.Width, pressWidth);
                    if (settings.PressSheetWidth > 0) sheetWidth = Math.Min(sheetWidth, settings.PressSheetWidth);
                    double innerWidth = sheetWidth - settings.Left - settings.Right;
                    double innerHeight = Math.Min(settings.Length, pressHeight) - settings.Top - settings.Bottom;
                    if (innerWidth <= 0 || innerHeight <= 0)
                        throw new InvalidOperationException("Поля не оставляют рабочей зоны для деталей.");
                    var inside = SportPressNesting.Place(items, innerWidth, innerHeight, settings);
                    var choice = SportPressRollNesting.Place(items, inside, innerWidth, innerHeight, settings);
                    if (SportPressRollNesting.Better(choice, best)) best = choice;
                }
                catch (InvalidOperationException error) { failure = error; }
            }
            return best ?? throw new InvalidOperationException("Не удалось уложить детали в полезную зону пресса. " +
                "Увеличьте ширину листа, смените ориентацию или уменьшите поля. " + failure?.Message, failure);
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
