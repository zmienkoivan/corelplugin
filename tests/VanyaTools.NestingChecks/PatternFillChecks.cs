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


    internal static void MirrorVariants()
    {
        var originals = Enumerable.Range(0, 7).Select(i => new PatternFootprint(new PatternPoint(10 + i, 12))).ToArray();
        var variants = new List<PatternFootprint>();
        foreach (var source in originals)
        {
            variants.Add(source);
            if (Array.IndexOf(originals, source) % 2 == 1)
                variants.Add(new PatternFootprint(source.Size,
                    source.Hull.Select(p => new PatternPoint(-p.X, p.Y)), source));
        }
        foreach (PatternFillMode mode in Enum.GetValues(typeof(PatternFillMode)))
            foreach (bool repeat in new[] { false, true })
            {
                var options = new PatternFillOptions { Mode = mode, SizeMm = 30, GapMm = 3,
                    MinScale = .8, MaxScale = 1, Seed = 8123, AngleVariation = 25,
                    RepeatWidthMm = repeat ? 300 : 0, RepeatHeightMm = repeat ? 250 : 0 };
                var before = PatternFillLayout.Build(650, 450, originals, options);
                var after = PatternFillLayout.Build(650, 450, variants, options);
                string label = mode + (repeat ? " repeat" : "");
                Require(before.Count == after.Count, label + ": mirrors changed the number of placements.");
                for (int i = 0; i < before.Count; i++)
                {
                    var a = before[i]; var b = after[i];
                    Require(ReferenceEquals(originals[a.Motif], variants[b.Motif].Family),
                        label + ": mirrored copies changed source order/frequency.");
                    Require(DistanceSquared(a.Center, b.Center) < 1e-12 &&
                        Math.Abs(a.Angle - b.Angle) < 1e-8 && Math.Abs(a.Size - b.Size) < 1e-8,
                        label + ": mirror selection changed symmetric geometry.");
                }
                Require(after.Any(p => !ReferenceEquals(variants[p.Motif], variants[p.Motif].Family)),
                    label + ": enabled mirror variants are never used.");
                Require(after.Any(p => Array.IndexOf(originals, variants[p.Motif]) % 2 == 1 &&
                    ReferenceEquals(variants[p.Motif], variants[p.Motif].Family)),
                    label + ": enabling mirrors removed the original orientation.");
            }

        var triangle = new PatternFootprint(new PatternPoint(12, 12), new[] {
            new PatternPoint(-6, -6), new PatternPoint(6, -6), new PatternPoint(-5, 6) });
        var triangleMirror = new PatternFootprint(triangle.Size,
            triangle.Hull.Select(p => new PatternPoint(-p.X, p.Y)), triangle);
        var shapes = new[] { triangle, triangleMirror, originals[0], originals[2] };
        foreach (var mode in new[] { PatternFillMode.Crocodile, PatternFillMode.Pebbles })
        {
            var items = PatternFillLayout.Build(300, 220, shapes,
                new PatternFillOptions { Mode = mode, SizeMm = 30, GapMm = 3, Seed = 8123 });
            AssertGap(items.Select(p => Hull(p, shapes[p.Motif])).ToArray(), 3);
        }
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
                var items = PatternFillLayout.Build(width, height, motifs, options, out PatternLayoutGuides guides);
                Require(items.Count == spaced.Count && items.Select(item => item.Motif).Distinct().Count() == motifs.Length,
                    "Texture did not place every motif across the cells.");
                if (mode == PatternFillMode.Pebbles)
                {
                    Require(guides.RepeatLines == null && spaced.Zip(guides.Cells, (a, b) => a.SequenceEqual(b)).All(equal => equal) &&
                        spaced.Count == guides.Cells.Count, "The preview guides differ from the actual fitting cells.");
                    AssertGap(items.Select(item => Hull(item, motifs[item.Motif])).ToArray(), options.GapMm);
                    continue;
                }
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

    internal static void OrganicVariety()
    {
        const double width = 550, height = 710;
        var motifs = Enumerable.Range(0, 7).Select(i => new PatternFootprint(new PatternPoint(8 + i, 9))).ToArray();
        foreach (var mode in new[] { PatternFillMode.Crocodile, PatternFillMode.Pebbles })
        {
            var options = new PatternFillOptions { Mode = mode, SizeMm = 22, GapMm = 2, Seed = 42 };
            var items = PatternFillLayout.Build(width, height, motifs, options);
            Require(items.Count > 500, "The random seeds leave the detail sparse.");
            var blocks = items.Take(7 * 10).Select(p => p.Motif).ToArray();
            Require(items.Select(p => p.Motif).Distinct().Count() == motifs.Length, "An organic variant loses part of the motif set.");
            var counts = items.GroupBy(item => item.Motif).Select(group => group.Count()).ToArray();
            Require(counts.Max() - counts.Min() < 10, "The neighbor rule starves some motifs.");
            Require(Enumerable.Range(0, 10).Select(i => String.Join(",", blocks.Skip(i * 7).Take(7))).Distinct().Count() > 8,
                "The same motif sequence repeats over the detail.");
            var same = PatternFillLayout.Build(width, height, motifs, options);
            Require(items.Count == same.Count && items.Zip(same, (a, b) =>
                a.Center.X == b.Center.X && a.Center.Y == b.Center.Y && a.Motif == b.Motif && a.Size == b.Size).All(equal => equal),
                "An organic variant changes during preview.");
            options.Seed++;
            var another = PatternFillLayout.Build(width, height, motifs, options);
            Require(items[0].Center.X != another[0].Center.X && items[0].Center.Y != another[0].Center.Y,
                "A new organic variant did not change the positions.");
        }
    }

    internal static void PebbleOrientationAndNeighbors()
    {
        var cell = new[] { new PatternPoint(0, 0), new PatternPoint(8, 0),
            new PatternPoint(8, 36), new PatternPoint(0, 36) };
        var horizontal = new PatternFootprint(new PatternPoint(30, 6));
        var options = new PatternFillOptions { SizeMm = 30, GapMm = 0 };
        foreach (var mode in new[] { PatternFillMode.Pebbles, PatternFillMode.Crocodile })
        {
            options.Mode = mode;
            var fitted = PatternFillLayout.PlaceInCells(new List<PatternPoint[]> { cell }, new[] { horizontal }, options).Single();
            Require(PatternTransform.Fit(horizontal.Size, fitted).Scale > 1.18 && Math.Abs(Math.Abs(fitted.Angle) - 90) < 3,
                "A long horizontal motif was not turned to fill a vertical cell.");
            Require(Hull(fitted, horizontal).All(p => Contains(cell, p.X, p.Y)), "Auto-rotation crossed a fitting boundary.");
        }

        options.Mode = PatternFillMode.Pebbles;
        const double pitch = 24;
        var motifs = Enumerable.Range(0, 7).Select(i => new PatternFootprint(new PatternPoint(6 + i * 2, 11))).ToArray();
        options.SizeMm = pitch;
        options.GapMm = 3;
        options.Seed = 8123;
        VerifyNeighbors();
        options.RepeatWidthMm = 135;
        options.RepeatHeightMm = 125;
        VerifyNeighbors();

        void VerifyNeighbors()
        {
            var items = PatternFillLayout.Build(475, 365, motifs, options);
            var centers = items.Select(item => At(item, motifs[item.Motif], motifs[item.Motif].Center)).ToArray();
            int near = 0, repeats = 0;
            for (int i = 0; i < items.Count; i++)
                for (int j = i + 1; j < items.Count; j++)
                {
                    if (DistanceSquared(centers[i], centers[j]) > 1.65 * pitch * pitch) continue;
                    near++;
                    if (items[i].Motif == items[j].Motif) repeats++;
                }
            Require(near > 100 && repeats < near * .04,
                "The same motif appears too often among nearby cells, including at repeat seams.");
        }
    }

    internal static void PebbleRepeat()
    {
        const double width = 475, height = 365, periodX = 110, periodY = 95, gap = 3.5;
        var motifs = new[] { new PatternFootprint(new PatternPoint(15, 15)),
            new PatternFootprint(new PatternPoint(23, 12)),
            new PatternFootprint(new PatternPoint(20, 20), new[] { new PatternPoint(-5, -6),
                new PatternPoint(9, 2), new PatternPoint(1, 8) }) };
        foreach (PatternFillMode mode in Enum.GetValues(typeof(PatternFillMode)))
        foreach (double angle in new[] { 0d, 31d })
        {
            var options = new PatternFillOptions { Mode = mode, SizeMm = 26, GapMm = gap,
                RepeatWidthMm = periodX, RepeatHeightMm = periodY, Angle = angle, AngleVariation = 31, Seed = 8123 };
            var items = PatternFillLayout.Build(width, height, motifs, options, out PatternLayoutGuides guides);
            Require(items.Count > 80 && (options.Texture ? guides.Cells.Count > 150 : guides.Cells == null),
                "The periodic module leaves empty parts of the frame or has incorrect cell guides.");
            var hulls = items.Select(item => Hull(item, motifs[item.Motif])).ToArray();
            AssertGap(hulls, gap);
            double radians = angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
            Require(CountMatches(periodX * cos, periodX * sin) > 35 && CountMatches(-periodY * sin, periodY * cos) > 35,
                "The positions, sizes and motifs differ between copies of the repeat.");
            var lines = guides.RepeatLines.Select(line => line.Select(ToModule).ToArray()).ToArray();
            Require(lines.Count(line => Math.Abs(line[0].X - line[1].X) < 1e-8) > 2 &&
                lines.Count(line => Math.Abs(line[0].Y - line[1].Y) < 1e-8) > 2 &&
                lines.All(line => OnPeriod(line[0].X, line[1].X, periodX) || OnPeriod(line[0].Y, line[1].Y, periodY)),
                "The dashed guides are not aligned with the tile boundaries.");
            if (mode == PatternFillMode.Pebbles)
                Require(hulls.Select(hull => hull.Select(ToModule).ToArray()).Any(hull =>
                    hull.Min(p => p.X) < periodX && hull.Max(p => p.X) > periodX),
                    "Pebble motifs must carry across the seams of the repeat.");

            int CountMatches(double dx, double dy) => items.Count(item => items.Any(other =>
                other.Motif == item.Motif && other.Angle == item.Angle && other.Size == item.Size &&
                Math.Abs(item.Center.X + dx - other.Center.X) < 1e-8 && Math.Abs(item.Center.Y + dy - other.Center.Y) < 1e-8));

            PatternPoint ToModule(PatternPoint point)
            {
                double x = point.X - width / 2, y = point.Y - height / 2;
                return new PatternPoint(x * cos + y * sin + periodX / 2, -x * sin + y * cos + periodY / 2);
            }

            bool OnPeriod(double first, double second, double period) =>
                Math.Abs(first - second) < 1e-8 && Math.Abs(first - Math.Round(first / period) * period) < 1e-8;
        }

        var small = new PatternFillOptions { Mode = PatternFillMode.Pebbles, SizeMm = 22, GapMm = 6,
            RepeatWidthMm = 22, RepeatHeightMm = 22, Seed = 124 };
        var repeated = PatternFillLayout.Build(110, 132, motifs.Take(1).ToArray(), small);
        Require(repeated.Count > 20, "A one-cell repeat did not tile the frame.");
        AssertGap(repeated.Select(item => Hull(item, motifs[0])).ToArray(), 6);
    }

    internal static void BottleProfile()
    {
        foreach (var size in new[] { (70d, 70d, 95d), (61d, 82d, 102d), (82d, 61d, 102d), (70.000001d, 70d, 95d), (100d, 15d, 12d) })
        {
            var profile = new PatternBottle(size.Item1, size.Item2, size.Item3);
            Require(Math.Abs(profile.CircumferenceAt(0) - Math.PI * size.Item2) < 1e-8 &&
                Math.Abs(profile.CircumferenceAt(profile.Slant) - Math.PI * size.Item1) < 1e-8,
                "The two circumference lengths do not match the bottle diameters.");
            var left = profile.Map(0, 0);
            var right = profile.Map(profile.Width, 0);
            Require(Math.Abs(Math.Sqrt(DistanceSquared(left, profile.Map(0, profile.Slant))) - profile.Slant) < 1e-8 &&
                Math.Abs(Math.Sqrt(DistanceSquared(right, profile.Map(profile.Width, profile.Slant))) - profile.Slant) < 1e-8,
                "The two seam edges have different lengths.");
            double rotation = (profile.RotationAt(profile.Width) - profile.RotationAt(0)) * Math.PI / 180;
            foreach (double height in new[] { 0d, profile.Slant * .37, profile.Slant })
            {
                var point = profile.Map(profile.Width * .21, height);
                var projected = profile.Project(point);
                Require(Math.Abs(projected.X - profile.Width * .21) < 1e-8 && Math.Abs(projected.Y - height) < 1e-8,
                    "The pointer position did not map back to the surface.");
                var copy = profile.Map(profile.Width * 1.21, height);
                Require(DistanceSquared(profile.Repeat(point, 1), copy) < 1e-16 &&
                    DistanceSquared(profile.Repeat(copy, -1), point) < 1e-16 &&
                    DistanceSquared(profile.Repeat(point, 0), point) < 1e-16,
                    "Moving a whole motif between periods changes its physical position.");
                double x = point.X - left.X, y = point.Y - left.Y;
                var rotated = new PatternPoint(right.X + x * Math.Cos(rotation) - y * Math.Sin(rotation),
                    right.Y + x * Math.Sin(rotation) + y * Math.Cos(rotation));
                Require(DistanceSquared(rotated, copy) < 1e-16,
                    "The geometry across the two sides of the seam cannot align.");
            }
            var edge = profile.Outline().Last();
            Require(DistanceSquared(edge.End, profile.Map(0, profile.Slant)) < 1e-16,
                "The outline does not finish at the top of the first seam edge.");
        }
    }

    internal static void OrganicSpacing()
    {
        var metrics = SpacingMetrics();
        Require(metrics.CenterVariation < .100 && metrics.Void95 < 6.5,
            "Pebble centers or outline gaps are too uneven.");
        Require(metrics.Repeats < .025, "Crocodile motifs repeat along nearby diagonals.");

        var motif = new PatternFootprint(new PatternPoint(10, 10));
        var small = new PatternPlacement { Size = 4, Center = new PatternPoint(0, 0) };
        var neighbor = new PatternPlacement { Size = 10, Center = new PatternPoint(15, 0) };
        var items = new List<PatternPlacement> { small, neighbor,
            new PatternPlacement { Size = 10, Center = new PatternPoint(0, 9) },
            new PatternPlacement { Size = 10, Center = new PatternPoint(0, -9) } };
        PatternPebbleSpacing.Balance(items, new[] { motif }, 2);
        Require(small.Size == 4 && neighbor.Size > 12 &&
            neighbor.Center.X == 15 && neighbor.Center.Y == 0,
            "A larger neighbor does not fill the space left by the small motif.");
        AssertGap(items.Select(item => Hull(item, motif)).ToArray(), 2);
    }

    private static (double CenterVariation, double Void95, double Repeats) SpacingMetrics()
    {
        const double width = 325, height = 215, pitch = 27, gap = 4;
        var motifs = new[]
        {
            new PatternFootprint(new PatternPoint(10, 10)),
            new PatternFootprint(new PatternPoint(18, 8)),
            new PatternFootprint(new PatternPoint(5, 20)),
            new PatternFootprint(new PatternPoint(25, 20), new[] { new PatternPoint(-12, -8),
                new PatternPoint(9, -8), new PatternPoint(4, 9) }),
            new PatternFootprint(new PatternPoint(12, 11), Enumerable.Range(0, 24).Select(i =>
                new PatternPoint(6 * Math.Cos(i * Math.PI / 12), 5.5 * Math.Sin(i * Math.PI / 12)))),
            new PatternFootprint(new PatternPoint(7, 22)),
            new PatternFootprint(new PatternPoint(12, 12), new[] { new PatternPoint(2, -5), new PatternPoint(5, -3),
                new PatternPoint(2, 4), new PatternPoint(-4, 0) })
        };
        var distances = new List<double>();
        var voids = new List<double>();
        int diagonalNeighbors = 0, sameDiagonals = 0;
        foreach (int seed in new[] { 42, 8123, 69127 })
        {
            var options = new PatternFillOptions { Mode = PatternFillMode.Pebbles, SizeMm = pitch, GapMm = gap,
                Angle = 12, AngleVariation = 25, Seed = seed };
            var items = PatternFillLayout.Build(width, height, motifs, options);
            var hulls = items.Select(item => Hull(item, motifs[item.Motif])).ToArray();
            AssertGap(hulls, gap);
            var centers = items.Select(item => At(item, motifs[item.Motif], motifs[item.Motif].Center)).ToArray();
            for (int i = 0; i < centers.Length; i++)
            {
                var p = centers[i];
                if (p.X < pitch || p.X > width - pitch || p.Y < pitch || p.Y > height - pitch) continue;
                distances.Add(Math.Sqrt(centers.Where((_, index) => index != i).Min(q => DistanceSquared(p, q))));
            }
            for (double y = 1.5; y < height; y += 5.3)
                for (double x = 1.3; x < width; x += 5.1)
                {
                    var p = new PatternPoint(x, y);
                    voids.Add(Math.Sqrt(hulls.Min(hull => Contains(hull, p.X, p.Y) ? 0 : DistanceToEdges(p, hull))));
                }

            options.Mode = PatternFillMode.Crocodile;
            items = PatternFillLayout.Build(width, height, motifs, options);
            centers = items.Select(item => At(item, motifs[item.Motif], motifs[item.Motif].Center)).ToArray();
            for (int i = 0; i < centers.Length; i++)
                for (int j = i + 1; j < centers.Length; j++)
                {
                    double dx = Math.Abs(centers[i].X - centers[j].X), dy = Math.Abs(centers[i].Y - centers[j].Y);
                    if (dx < .25 * pitch || dy < .25 * pitch || dx * dx + dy * dy >= 2.1 * pitch * pitch) continue;
                    diagonalNeighbors++;
                    if (items[i].Motif == items[j].Motif) sameDiagonals++;
                }
        }
        double mean = distances.Average();
        return (Math.Sqrt(distances.Average(d => (d - mean) * (d - mean))) / mean,
            voids.OrderBy(d => d).ElementAt((int)(voids.Count * .95)),
            sameDiagonals / (double)diagonalNeighbors);
    }

    private static void AssertGap(PatternPoint[][] hulls, double gap)
    {
        for (int i = 0; i < hulls.Length; i++)
            for (int j = i + 1; j < hulls.Length; j++)
            {
                var a = hulls[i]; var b = hulls[j];
                if (a.Min(p => p.X) - b.Max(p => p.X) >= gap || b.Min(p => p.X) - a.Max(p => p.X) >= gap ||
                    a.Min(p => p.Y) - b.Max(p => p.Y) >= gap || b.Min(p => p.Y) - a.Max(p => p.Y) >= gap) continue;
                Require(Separated(a, b) || Separated(b, a), "Motif hulls overlap.");
                double minimum = Math.Min(a.Min(p => DistanceToEdges(p, b)), b.Min(p => DistanceToEdges(p, a)));
                double tolerance = Math.Max(0, gap - 1e-6);
                Require(minimum >= tolerance * tolerance, "Motif edges violate the minimum gap.");
            }
    }

    private static bool Separated(PatternPoint[] a, PatternPoint[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            var start = a[i]; var end = a[(i + 1) % a.Length];
            double nx = end.Y - start.Y, ny = start.X - end.X;
            if (b.All(p => nx * (p.X - start.X) + ny * (p.Y - start.Y) >= -1e-8)) return true;
        }
        return false;
    }

    private static PatternPoint[] Hull(PatternPlacement item, PatternFootprint motif) =>
        motif.Hull.Select(point => At(item, motif, point)).ToArray();

    private static PatternPoint At(PatternPlacement item, PatternFootprint motif, PatternPoint point)
    {
        double scale = PatternTransform.Fit(motif.Size, item).Scale;
        double radians = item.Angle * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        return new PatternPoint(item.Center.X + scale * (point.X * cos - point.Y * sin),
            item.Center.Y + scale * (point.X * sin + point.Y * cos));
    }

    private static double DistanceSquared(PatternPoint a, PatternPoint b)
    {
        double x = a.X - b.X, y = a.Y - b.Y;
        return x * x + y * y;
    }

    private static double DistanceToEdges(PatternPoint point, PatternPoint[] hull)
    {
        double closest = double.PositiveInfinity;
        for (int i = 0; i < hull.Length; i++)
        {
            var a = hull[i]; var b = hull[(i + 1) % hull.Length];
            double x = b.X - a.X, y = b.Y - a.Y;
            double fraction = ((point.X - a.X) * x + (point.Y - a.Y) * y) / (x * x + y * y);
            closest = Math.Min(closest, DistanceSquared(point, PatternPoint.Between(a, b, Math.Max(0, Math.Min(1, fraction)))));
        }
        return closest;
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
