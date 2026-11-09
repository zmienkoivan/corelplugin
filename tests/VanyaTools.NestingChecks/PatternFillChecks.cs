using System;
using System.Collections.Generic;
using System.Linq;
using VanyaTools.Native;

internal static class PatternFillChecks
{
    internal static void Motifs()
    {
        var options = new PatternFillOptions { SizeMm = 30, GapMm = 3 };
        var rows = PatternFillLayout.Build(82, 53, 3, options);
        Require(rows.Count == 6 && rows[0].Motif == 0 && rows[1].Motif == 1, "Regular repeats skip a motif.");
        Require(rows.All(p => p.Size == 30 && p.Angle == 0), "Uniform size or angle changed.");
        Require(rows[0].Center.X - rows[0].Size / 2 < 0, "Motifs are forced inside the cut line.");
        options.Mode = PatternFillMode.Staggered;
        var shifted = PatternFillLayout.Build(82, 53, 3, options);
        Require(shifted.Skip(3).Any(p => rows.All(q => Math.Abs(q.Center.X - p.Center.X) > 1)),
            "Alternating rows are not staggered.");
        Require(PatternFillLayout.Build(1, 95, 1, options).Count == 3, "Narrow details have an empty row.");

        options.Mode = PatternFillMode.Random;
        options.MinScale = .55; options.MaxScale = 1.3; options.Angle = 12; options.AngleVariation = 60;
        var scatter = PatternFillLayout.Build(900, 750, 3, options);
        Require(scatter.Count > 100 && scatter.All(p => p.Size >= 16.5 && p.Size <= 39 && p.Angle >= -48 && p.Angle <= 72),
            "Random transforms exceeded the specified ranges.");
        for (int i = 0; i < scatter.Count; i++)
            for (int j = i + 1; j < scatter.Count; j++)
            {
                var a = scatter[i]; var b = scatter[j];
                double separation = (a.Size + b.Size) / 2 + options.GapMm - 1e-8;
                Require(Math.Abs(a.Center.X - b.Center.X) >= separation || Math.Abs(a.Center.Y - b.Center.Y) >= separation,
                    "Random motifs overlap or violate the gap.");
            }
        var repeated = PatternFillLayout.Build(900, 750, 3, options);
        Require(scatter.Zip(repeated, (a, b) => a.Center.X == b.Center.X && a.Center.Y == b.Center.Y &&
            a.Size == b.Size && a.Angle == b.Angle && a.Motif == b.Motif).All(equal => equal), "The same variant changed.");
        options.Seed = 7919;
        Require(PatternFillLayout.Build(900, 750, 3, options)[0].Center.X != scatter[0].Center.X, "New variant did not change positions.");

        options.SizeMm = .5; options.GapMm = 0;
        try { PatternFillLayout.Build(1000, 1000, 1, options); }
        catch (InvalidOperationException) { return; }
        throw new Exception("Very dense patterns are not capped.");
    }

    internal static void Textures()
    {
        const double width = 325, height = 215;
        foreach (var mode in new[] { PatternFillMode.Crocodile, PatternFillMode.Pebbles })
            foreach (double angle in new[] { 0d, 38d, -90d })
            {
                var options = new PatternFillOptions { Mode = mode, SizeMm = 27, GapMm = 0, Angle = angle, Seed = 8123 };
                var cells = PatternFillLayout.Build(width, height, 0, options);
                Require(cells.Count > 65 && cells.All(c => c.Cell.Length >= 3), "Texture has missing or degenerate cells.");
                Require(cells.Any(c => c.Cell.Any(p => p.X < 0 || p.Y < 0 || p.X > width || p.Y > height)),
                    "Texture cells stop short of the contour edge.");
                for (double x = 1.1; x < width; x += 7.3)
                    for (double y = 1.6; y < height; y += 8.9)
                        Require(cells.Count(c => Contains(c.Cell, x, y)) == 1, "Base texture has a hole or overlap.");
                options.GapMm = 4;
                var spaced = PatternFillLayout.Build(width, height, 0, options);
                Require(spaced.Sum(c => Area(c.Cell)) < cells.Sum(c => Area(c.Cell)) * .95, "Gap does not open the texture.");
            }
    }

    internal static void Focus()
    {
        var hero = new PatternPlacement { Center = new PatternPoint(105, 80), Size = 72, Angle = 32 };
        var heroSize = new PatternPoint(90, 40);
        var transform = PatternTransform.Fit(heroSize, hero);
        var reserve = transform.Bounds(hero.Center, 7);
        Require(Math.Abs(Math.Max(transform.RotatedWidth, transform.RotatedHeight) - 72) < 1e-8 &&
            Math.Abs(reserve.Right - reserve.Left - transform.RotatedWidth - 14) < 1e-8,
            "The rotated hero or its physical clearance is incorrectly sized.");

        var options = new PatternFillOptions { Mode = PatternFillMode.Staggered, SizeMm = 25, GapMm = 2,
            AngleVariation = 50 };
        var items = PatternFillLayout.Build(210, 160, 2, options);
        var sizes = new[] { new PatternPoint(25, 25), new PatternPoint(15, 30) };
        int original = items.Count;
        PatternFillLayout.LeaveRoom(items, sizes, reserve);
        Require(items.Count > 0 && items.Count < original && items.All(p =>
            !PatternTransform.Fit(sizes[p.Motif], p).Bounds(p.Center).Overlaps(reserve)),
            "A background motif enters the reserved zone.");

        var corner = new PatternPlacement { Cell = new[] { new PatternPoint(0, 0),
            new PatternPoint(12, 0), new PatternPoint(0, 12) } };
        var touches = new PatternPlacement { Cell = new[] { new PatternPoint(9, 9),
            new PatternPoint(14, 9), new PatternPoint(9, 14) } };
        var cells = new List<PatternPlacement> { corner, touches };
        PatternFillLayout.LeaveRoom(cells, new PatternPoint[0], new PatternBox(8, 8, 13, 13));
        Require(cells.Count == 1 && ReferenceEquals(cells[0], corner),
            "Texture exclusion should remove only cells intersecting the reserved zone.");
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
