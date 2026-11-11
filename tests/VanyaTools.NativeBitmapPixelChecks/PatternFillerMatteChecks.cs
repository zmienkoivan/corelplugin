using System;
using System.Linq;
using System.Threading;
using VanyaTools.Native;

internal static class PatternFillerMatteChecks
{
    internal static void Run()
    {
        const int width = 220, height = 100;
        var pixels = new byte[width * height * 4];
        var expected = new byte[pixels.Length];
        int outer = 0, inner = 0, purple = 0;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                double r = Math.Sqrt((x - 48.3) * (x - 48.3) + (y - 49.7) * (y - 49.7));
                double edge = Math.Min(36.2 - r, r - 20.6);
                double alpha = Math.Max(0, Math.Min(1, edge + .5));
                bool gold = r > 26 && r < 30;
                int[] color = gold ? new[] { 80, 185, 230 } : new[] { 0, 0, 0 };
                double purpleR = Math.Sqrt((x - 158.2) * (x - 158.2) + (y - 49.4) * (y - 49.4));
                if (purpleR < 31.5)
                {
                    alpha = Math.Max(0, Math.Min(1, 31.5 - purpleR));
                    color = new[] { 170, 60, 135 };
                }
                int i = (y * width + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    expected[i + c] = (byte)color[c];
                    pixels[i + c] = (byte)Math.Round(alpha * color[c] + (1 - alpha) * (c == 1 ? 0 : 255));
                }
                expected[i + 3] = (byte)Math.Round(alpha * 255);
                pixels[i + 3] = 255;
            }
        var before = (byte[])pixels.Clone();
        PatternFillerMatte.Remove(pixels, width, height, CancellationToken.None);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4, a = expected[i + 3];
                if (a == 255)
                    Require(Enumerable.Range(0, 4).All(c => pixels[i + c] == expected[i + c]),
                        "Opaque gold/black/purple artwork changed.");
                if (a < 80) continue; // Strong matte colors are keyed out completely.
                if (a == 255) continue;
                for (int c = 0; c < 4; c++)
                    Require(Math.Abs(pixels[i + c] - expected[i + c]) <= 4,
                        "Antialiased edge retains matte or loses coverage at " + x + "," + y);
                if (x > 110) purple++;
                else if (Math.Sqrt((x - 48.3) * (x - 48.3) + (y - 49.7) * (y - 49.7)) > 28) outer++;
                else inner++;
            }
        Require(outer > 40 && inner > 30 && purple > 40, "Missing outer, inner or genuine purple edge samples.");
        Require(pixels[3] == 0 && pixels[(50 * width + 48) * 4 + 3] == 0,
            "Sheet background or ring hole remained opaque.");
        var once = (byte[])pixels.Clone();
        PatternFillerMatte.Remove(pixels, width, height, CancellationToken.None);
        Require(pixels.SequenceEqual(once), "Matte cleanup is not idempotent.");

        var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try { PatternFillerMatte.Remove(before, width, height, cancellation.Token); }
        catch (OperationCanceledException) { Console.WriteLine("Filler matte checks passed. Outer and inner edges, genuine purple and cancellation."); return; }
        throw new Exception("Matte cleanup ignored cancellation.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new Exception(message); }
}
