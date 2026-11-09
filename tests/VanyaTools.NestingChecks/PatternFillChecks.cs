using System;
using System.Collections.Generic;
using System.Linq;
using VanyaTools.Native;

internal static class PatternFillChecks
{
    internal static void Motifs()
    {
        var options = new PatternFillOptions { SizeMm = 30, GapMm = 3 };
        var motifs = Enumerable.Range(0, 3).Select(_ => new PatternFootprint(new PatternPoint(10, 10))).ToArray();
        var rows = PatternFillLayout.Build(82, 53, motifs, options);
        Require(rows.Count == 6 && rows[0].Motif == 0 && rows[1].Motif == 1, "Regular repeats skip a motif.");
        Require(rows.All(p => p.Size == 30 && p.Angle == 0), "Uniform size or angle changed.");
        Require(rows[0].Center.X - rows[0].Size / 2 < 0, "Motifs are forced inside the cut line.");
        options.Mode = PatternFillMode.Staggered;
        var shifted = PatternFillLayout.Build(82, 53, motifs, options);
        Require(shifted.Skip(3).Any(p => rows.All(q => Math.Abs(q.Center.X - p.Center.X) > 1)),
            "Alternating rows are not staggered.");
        Require(PatternFillLayout.Build(1, 95, motifs, options).Count == 3, "Narrow details have an empty row.");

        options.Mode = PatternFillMode.Random;
        options.MinScale = .55; options.MaxScale = 1.3; options.Angle = 12; options.AngleVariation = 60;
        var scatter = PatternFillLayout.Build(900, 750, motifs, options);
        Require(scatter.Count > 100 && scatter.All(p => p.Size >= 16.5 && p.Size <= 39 && p.Angle >= -48 && p.Angle <= 72),
            "Random transforms exceeded the specified ranges.");
        Require(scatter.Take(3).Select(p => p.Motif).Distinct().Count() == 3, "Scatter does not cycle the motif set.");
        for (int i = 0; i < scatter.Count; i++)
            for (int j = i + 1; j < scatter.Count; j++)
            {
                var a = scatter[i]; var b = scatter[j];
                double separation = (a.Size + b.Size) / 2 + options.GapMm - 1e-8;
                Require(Math.Abs(a.Center.X - b.Center.X) >= separation || Math.Abs(a.Center.Y - b.Center.Y) >= separation,
                    "Random motifs overlap or violate the gap.");
            }
        var repeated = PatternFillLayout.Build(900, 750, motifs, options);
        Require(scatter.Zip(repeated, (a, b) => a.Center.X == b.Center.X && a.Center.Y == b.Center.Y &&
            a.Size == b.Size && a.Angle == b.Angle && a.Motif == b.Motif).All(equal => equal), "The same variant changed.");
        options.Seed = 7919;
        Require(PatternFillLayout.Build(900, 750, motifs, options)[0].Center.X != scatter[0].Center.X, "New variant did not change positions.");

        options.SizeMm = .5; options.GapMm = 0;
        try { PatternFillLayout.Build(1000, 1000, motifs, options); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Very dense patterns are not capped.");
    }

    internal static void Textures()
    {
        const double width = 325, height = 215;
        var motifs = new[]
        {
            new PatternFootprint(new PatternPoint(10, 10)),
            new PatternFootprint(new PatternPoint(20, 20), new[] { new PatternPoint(0, 0),
                new PatternPoint(9, 0), new PatternPoint(0, 8), new PatternPoint(1, 1) }),
            new PatternFootprint(new PatternPoint(16, 8))
        };
        foreach (var mode in new[] { PatternFillMode.Crocodile, PatternFillMode.Pebbles })
            foreach (double angle in new[] { 0d, 38d, -90d })
            {
                var options = new PatternFillOptions { Mode = mode, SizeMm = 27, GapMm = 0, Angle = angle, Seed = 8123 };
                var cells = PatternFillLayout.TextureCells(width, height, options);
                Require(cells.Count > 65 && cells.All(c => c.Length >= 3), "Texture has missing or degenerate cells.");
                Require(cells.Any(c => c.Any(p => p.X < 0 || p.Y < 0 || p.X > width || p.Y > height)),
                    "Texture cells stop short of the contour edge.");
                for (double x = 1.1; x < width; x += 7.3)
                    for (double y = 1.6; y < height; y += 8.9)
                        Require(cells.Count(c => Contains(c, x, y)) == 1, "Base texture has a hole or overlap.");
                options.GapMm = 4;
                var spaced = PatternFillLayout.TextureCells(width, height, options);
                Require(spaced.Sum(Area) < cells.Sum(Area) * .95, "Gap does not open the texture.");
                options.AngleVariation = 25;
                var items = PatternFillLayout.Build(width, height, motifs, options);
                Require(items.Count == spaced.Count && items.Select(item => item.Motif).Distinct().Count() == motifs.Length,
                    "Texture did not place every motif across the cells.");
                for (int i = 0; i < items.Count; i++)
                {
                    var item = items[i];
                    var motif = motifs[item.Motif];
                    double scale = PatternTransform.Fit(motif.Size, item).Scale;
                    double radians = item.Angle * Math.PI / 180;
                    Require(motif.Hull.All(p => Contains(spaced[i],
                        item.Center.X + scale * (p.X * Math.Cos(radians) - p.Y * Math.Sin(radians)),
                        item.Center.Y + scale * (p.X * Math.Sin(radians) + p.Y * Math.Cos(radians)))),
                        "The visible motif extends beyond its cell.");
                }
            }
    }

    private static bool Contains(IReadOnlyList<PatternPoint> cell, double x, double y)
    {
        for (int i = 0; i < cell.Count; i++)
        {
            var a = cell[i]; var b = cell[(i + 1) % cell.Count];
            if ((b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X) < -1e-8) return false;
        }
        return true;
    }

    private static double Area(IReadOnlyList<PatternPoint> cell)
    {
        double area = 0;
        for (int i = 0; i < cell.Count; i++)
        {
            var a = cell[i]; var b = cell[(i + 1) % cell.Count];
            area += a.X * b.Y - b.X * a.Y;
        }
        return Math.Abs(area) / 2;
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
