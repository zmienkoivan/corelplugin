using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using VanyaTools.Native;

internal static class Program
{
    private static int _failed;
    private static void Main()
    {
        Check("Pairs fill side pockets without moving torsos", PairPocket);
        Check("Sleeves return to earlier pair pages", EarlierPairs);
        Check("Sleeve fits inside a torso bounding box without overlap", ContourPairPocket);
        Check("Full contour finds a narrow sleeve pocket", NarrowPocket);
        Check("Rectangles keep their bounding boxes", RectangleMode);
        Check("Printing retains allowed 90-degree rotation", PrintingRotation);
        Check("All-small order works in pair mode", AllSmall);
        Check("Whole garments stay on one page", Garments);
        Check("Varied orders preserve gaps, parts and cutting rotations", VariedOrders);
        Check("Tapered contours preserve gaps in all density modes", TaperedOrders);
        Check("Contour improvement keeps a valid result", Improvement);
        Console.WriteLine(_failed == 0 ? "All nesting checks passed." : _failed + " nesting checks failed.");
        Environment.ExitCode = _failed == 0 ? 0 : 1;
    }
    private static void Check(string name, Action test)
    {
        var clock = Stopwatch.StartNew();
        try { test(); Console.WriteLine("PASS " + name + " (" + clock.ElapsedMilliseconds + " ms)"); }
        catch (Exception error) { _failed++; Console.WriteLine("FAIL " + name + ": " + error.Message); }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static SportSheetNesting.Item Rect(double w, double h, int g = 0, int part = 0) =>
        new SportSheetNesting.Item { Width = w, Height = h, Garment = g, Part = part, SmallPart = part >= 2, Allow180 = true };
    private static List<SportSheetNesting.Item> PocketItems() => new List<SportSheetNesting.Item> {
        new SportSheetNesting.Item { Width = 200, Height = 200,
            Bands = new List<SportContourNesting.Band> {
                new SportContourNesting.Band { Left = 0, Right = 92, Top = 0, Bottom = 200 },
                new SportContourNesting.Band { Left = 92, Right = 136, Top = 0, Bottom = 80 },
                new SportContourNesting.Band { Left = 136, Right = 200, Top = 0, Bottom = 200 } } }, Rect(40, 100, 0, 2) };
    private static void PairPocket()
    {
        var items = new List<SportSheetNesting.Item> { Rect(70, 100, 0, 0), Rect(70, 100, 0, 1), Rect(40, 50, 0, 2) };
        var torso = SportGroupedNesting.Place(items.Take(2).ToList(), 200, 400, 4, SportNestingMode.FullContour, SportGroupingMode.FrontBackPairs);
        var result = SportGroupedNesting.Place(items, 200, 400, 4, SportNestingMode.FullContour, SportGroupingMode.FrontBackPairs);
        Validate(items, result, 200, 400, 4);
        Require(result.PageHeights.Sum() <= 100.001, "Sleeve appended below a free pocket; height=" + result.PageHeights.Sum());
        foreach (var original in torso.Placements)
        {
            var p = result.Placements.Single(part => part.Index == original.Index);
            Require(p.Left == original.Left && p.Top == original.Top && p.Page == original.Page && p.Rotation == original.Rotation,
                "A torso pair moved or broke apart.");
        }
    }
    private static void EarlierPairs()
    {
        var items = new List<SportSheetNesting.Item>();
        for (int g = 0; g < 3; g++) { items.Add(Rect(70, 100, g, 0)); items.Add(Rect(70, 100, g, 1)); items.Add(Rect(40, 70, g, 2)); }
        var result = SportGroupedNesting.Place(items, 200, 210, 4, SportNestingMode.FullContour, SportGroupingMode.FrontBackPairs);
        Validate(items, result, 200, 210, 4);
        Require(result.Placements.Any(p => items[p.Index].SmallPart && p.Page == 0), "No sleeve returned to the first page.");
        Require(result.PageHeights.Sum() <= 310, "Pair pages extended unnecessarily: " + result.PageHeights.Sum());
    }
    private static void NarrowPocket()
    {
        var items = PocketItems();
        var result = SportSheetNesting.Place(items, 200, 400, 0, SportNestingMode.FullContour, false);
        Validate(items, result, 200, 400, 0);
        Require(result.PageHeights.Sum() <= 200.001, "Narrow pocket missed; height=" + result.PageHeights.Sum());
        Require(Math.Abs(result.Placements.Single(p => p.Index == 1).Left - 94) < .001, "Wrong pocket anchor.");
    }
    private static void ContourPairPocket()
    {
        var items = new List<SportSheetNesting.Item>();
        for (int part = 0; part < 2; part++)
            items.Add(new SportSheetNesting.Item { Width = 100, Height = 200, Part = part,
                Bands = new List<SportContourNesting.Band> {
                    new SportContourNesting.Band { Left = 0, Right = 30, Top = 0, Bottom = 200 },
                    new SportContourNesting.Band { Left = 30, Right = 100, Top = 0, Bottom = 80 } } });
        items.Add(Rect(50, 100, 0, 2));
        var result = SportGroupedNesting.Place(items, 220, 400, 2, SportNestingMode.FullContour, SportGroupingMode.FrontBackPairs);
        Validate(items, result, 220, 400, 2);
        Require(result.PageHeights.Sum() <= 200.001, "Contour pocket below the torso arm was not used.");
        var sleeve = result.Placements.Single(p => p.Index == 2);
        Require(result.Placements.Where(p => p.Index < 2).Any(p => sleeve.Left >= p.Left &&
            sleeve.Left + sleeve.Width <= p.Left + p.Width && sleeve.Top >= p.Top &&
            sleeve.Top + sleeve.Height <= p.Top + p.Height), "Sleeve was not placed inside the available contour pocket.");
    }
    private static void RectangleMode()
    {
        var items = PocketItems();
        var result = SportSheetNesting.Place(items, 200, 400, 0, SportNestingMode.Rectangles, false);
        Validate(items, result, 200, 400, 0);
        Require(result.PageHeights.Sum() >= 300, "Rectangle mode used a contour-only pocket.");
    }
    private static void PrintingRotation()
    {
        var item = Rect(120, 40); item.Allow90 = true;
        var items = new List<SportSheetNesting.Item> { item };
        var result = SportSheetNesting.Place(items, 80, 200, 2, SportNestingMode.FullContour, false);
        Validate(items, result, 80, 200, 2);
        Require(result.Placements.Single().Rotation == 90, "Allowed print rotation was lost.");
    }
    private static void AllSmall()
    {
        var items = new List<SportSheetNesting.Item> { Rect(30, 40, 0, 2), Rect(40, 50, 0, 3) };
        Validate(items, SportGroupedNesting.Place(items, 200, 200, 4, SportNestingMode.FullContour, SportGroupingMode.FrontBackPairs), 200, 200, 4);
    }
    private static void Garments()
    {
        var items = new List<SportSheetNesting.Item>();
        for (int g = 0; g < 2; g++) for (int p = 0; p < 5; p++) items.Add(Rect(p < 2 ? 50 : 25, p < 2 ? 60 : 25, g, p));
        var result = SportGroupedNesting.Place(items, 200, 200, 4, SportNestingMode.FullContour, SportGroupingMode.Garment);
        Validate(items, result, 200, 200, 4);
        foreach (var kit in result.Placements.GroupBy(p => items[p.Index].Garment))
            Require(kit.Select(p => p.Page).Distinct().Count() == 1, "A garment was split between pages.");
    }
    private static void VariedOrders()
    {
        var random = new Random(5151);
        foreach (SportNestingMode mode in Enum.GetValues(typeof(SportNestingMode)))
        foreach (double gap in new[] { 0.0, 2.0, 7.0 })
        {
            var items = Enumerable.Range(0, 30).Select(i => Rect(random.Next(20, 100), random.Next(20, 120), i / 5, i % 5)).ToList();
            Validate(items, SportSheetNesting.Place(items, 220, 300, gap, mode, false), 220, 300, gap, mode);
            Validate(items, SportGroupedNesting.Place(items, 220, 300, gap, mode, SportGroupingMode.FrontBackPairs), 220, 300, gap, mode);
        }
    }
    private static void Improvement()
    {
        var items = PocketItems(); items[0].Allow180 = true;
        Validate(items, SportSheetNesting.Place(items, 200, 400, 2), 200, 400, 2);
    }
    private static void TaperedOrders()
    {
        var random = new Random(5152);
        foreach (SportNestingMode mode in Enum.GetValues(typeof(SportNestingMode)))
        foreach (double gap in new[] { 0.0, 2.0, 7.0 })
        {
            var items = new List<SportSheetNesting.Item>();
            for (int i = 0; i < 25; i++)
            {
                var item = Rect(random.Next(35, 100), random.Next(50, 120), i / 5, i % 5);
                var bands = new List<SportContourNesting.Band>();
                var a = new SportContourNesting.Point(0, item.Height * .35);
                var b = new SportContourNesting.Point(item.Width * .5, 0);
                var c = new SportContourNesting.Point(item.Width, item.Height * .35);
                var d = new SportContourNesting.Point(item.Width * .8, item.Height);
                var e = new SportContourNesting.Point(item.Width * .2, item.Height);
                SportContourNesting.AddLine(bands, a, b); SportContourNesting.AddLine(bands, b, c);
                SportContourNesting.AddCubic(bands, c, new SportContourNesting.Point(item.Width * .9, item.Height * .6),
                    new SportContourNesting.Point(item.Width * .85, item.Height * .9), d);
                SportContourNesting.AddLine(bands, d, e); SportContourNesting.AddLine(bands, e, a);
                item.Bands = bands; items.Add(item);
            }
            Validate(items, SportSheetNesting.Place(items, 220, 300, gap, mode, false), 220, 300, gap, mode);
            Validate(items, SportGroupedNesting.Place(items, 220, 300, gap, mode, SportGroupingMode.FrontBackPairs), 220, 300, gap, mode);
        }
    }
    private static void Validate(IList<SportSheetNesting.Item> items, SportSheetNesting.Result result, double width, double limit,
        double gap, SportNestingMode mode = SportNestingMode.FullContour)
    {
        Require(result.Placements.Count == items.Count && result.Placements.Select(p => p.Index).Distinct().Count() == items.Count,
            "Missing or duplicated parts.");
        foreach (var p in result.Placements)
        {
            Require(p.Index >= 0 && p.Index < items.Count && p.Page >= 0 && p.Page < result.PageHeights.Count, "Invalid part/page.");
            Require(p.Left >= -.001 && p.Top >= -.001 && p.Left + p.Width <= width + .001 &&
                p.Top + p.Height <= result.PageHeights[p.Page] + .001 && result.PageHeights[p.Page] <= limit + .001, "Out of sheet bounds.");
            Require(p.Rotation == 0 || p.Rotation == 180 && items[p.Index].Allow180 || p.Rotation == 90 && items[p.Index].Allow90,
                "Forbidden rotation.");
        }
        double pitch = SportSheetNesting.GridStep(width, mode);
        // Independent physical interval checks, without the packer's column
        // indices or collision helper. Profile cells cover the source contour.
        for (int a = 0; a < result.Placements.Count; a++)
        for (int b = a + 1; b < result.Placements.Count; b++)
        {
            var one = result.Placements[a]; var two = result.Placements[b];
            if (one.Page != two.Page || one.Left + one.Width + gap <= two.Left + .001 || two.Left + two.Width + gap <= one.Left + .001 ||
                one.Top + one.Height + gap <= two.Top + .001 || two.Top + two.Height + gap <= one.Top + .001) continue;
            for (int j = 0; j < one.Profile.Top.Length; j++)
            for (int k = 0; k < two.Profile.Top.Length; k++)
            {
                double leftA = one.Left + j * pitch, rightA = one.Left + Math.Min(one.Width, (j + 1) * pitch);
                double leftB = two.Left + k * pitch, rightB = two.Left + Math.Min(two.Width, (k + 1) * pitch);
                if (rightA + gap <= leftB + .001 || rightB + gap <= leftA + .001) continue;
                Require(one.Top + one.Profile.Bottom[j] + gap <= two.Top + two.Profile.Top[k] + .001 ||
                    two.Top + two.Profile.Bottom[k] + gap <= one.Top + one.Profile.Top[j] + .001,
                    "Overlap or insufficient gap: " + one.Index + "/" + two.Index);
            }
        }
    }
}
namespace VanyaTools.Native { internal static class Log { internal static void Info(string message) { } } }
