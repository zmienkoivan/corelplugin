using System;
using System.Threading;

namespace VanyaTools.Native
{
    // The AI sheet uses a reserved magenta matte. Binary keying alone leaves
    // opaque magenta/foreground mixtures on antialiased outer and inner edges.
    internal static class PatternFillerMatte
    {
        internal static void Remove(byte[] pixels, int width, int height, CancellationToken cancellation)
        {
            int count = checked(width * height);
            if (width <= 0 || height <= 0 || pixels == null || pixels.Length != checked(count * 4))
                throw new ArgumentException("Invalid filler bitmap.");
            var distance = new byte[count];
            var histograms = new[] { new int[256], new int[256], new int[256] };
            int samples = 0;
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int p = y * width + x, i = p * 4;
                    int blueDistance = 255 - pixels[i], redDistance = 255 - pixels[i + 2], green = pixels[i + 1];
                    bool matte = blueDistance * blueDistance + redDistance * redDistance + green * green <= 40 * 40;
                    if (matte && pixels[i + 3] >= 250)
                    {
                        for (int c = 0; c < 3; c++) histograms[c][pixels[i + c]]++;
                        samples++;
                    }
                    if (pixels[i + 3] <= 8 || matte)
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                    else distance[p] = 4;
                }
            }
            // The model may add colored specks to otherwise flat magenta. Remove
            // only matte-like pixels away from real ink; keep mixed edge pixels
            // near an opaque drawing so the unmatting pass can preserve coverage.
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int p = y * width + x, i = p * 4;
                    if (pixels[i + 3] == 0 || pixels[i + 1] > 90 ||
                        Math.Min(pixels[i], pixels[i + 2]) - pixels[i + 1] < 90 ||
                        NearInk(pixels, width, height, x, y)) continue;
                    pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                    distance[p] = 0;
                }
            }
            var background = new int[3];
            for (int c = 0; c < 3; c++)
            {
                background[c] = c == 1 ? 0 : 255;
                for (int value = 0, sum = 0; samples > 0 && value < 256; value++)
                {
                    sum += histograms[c][value];
                    if (sum > samples / 2) { background[c] = value; break; }
                }
            }

            // Capped Chebyshev distance to the removed background. Colors beyond
            // this narrow band are immutable and provide local foreground samples.
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int p = y * width + x, value = distance[p];
                    if (x > 0) value = Math.Min(value, distance[p - 1] + 1);
                    if (y > 0)
                    {
                        value = Math.Min(value, distance[p - width] + 1);
                        if (x > 0) value = Math.Min(value, distance[p - width - 1] + 1);
                        if (x + 1 < width) value = Math.Min(value, distance[p - width + 1] + 1);
                    }
                    distance[p] = (byte)value;
                }
            }
            for (int y = height - 1; y >= 0; y--)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = width - 1; x >= 0; x--)
                {
                    int p = y * width + x, value = distance[p];
                    if (x + 1 < width) value = Math.Min(value, distance[p + 1] + 1);
                    if (y + 1 < height)
                    {
                        value = Math.Min(value, distance[p + width] + 1);
                        if (x > 0) value = Math.Min(value, distance[p + width - 1] + 1);
                        if (x + 1 < width) value = Math.Min(value, distance[p + width + 1] + 1);
                    }
                    distance[p] = (byte)value;
                }
            }
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int p = y * width + x, i = p * 4;
                    if (distance[p] == 0 || distance[p] >= 4 || pixels[i + 3] < 250) continue;
                    int foreground = NearestCore(pixels, distance, width, height, x, y);
                    if (foreground < 0) continue;
                    double numerator = 0, denominator = 0;
                    for (int c = 0; c < 3; c++)
                    {
                        double direction = background[c] - pixels[foreground + c];
                        numerator += (pixels[i + c] - pixels[foreground + c]) * direction;
                        denominator += direction * direction;
                    }
                    if (denominator < 400) continue;
                    double mattePart = numerator / denominator;
                    bool matteLike = pixels[i + 1] <= 90 && Math.Min(pixels[i], pixels[i + 2]) - pixels[i + 1] >= 90;
                    if (mattePart >= .98 && matteLike)
                    {
                        pixels[i] = pixels[i + 1] = pixels[i + 2] = pixels[i + 3] = 0;
                        continue;
                    }
                    if (mattePart <= .015 || mattePart >= .98) continue;
                    bool matches = true;
                    for (int c = 0; c < 3; c++)
                    {
                        double expected = pixels[foreground + c] * (1 - mattePart) + background[c] * mattePart;
                        if (Math.Abs(pixels[i + c] - expected) > 12) matches = false;
                    }
                    if (!matches && !matteLike) continue;
                    double alpha = 1 - mattePart;
                    for (int c = 0; c < 3; c++)
                        pixels[i + c] = (byte)Math.Max(0, Math.Min(255,
                            Math.Round(matteLike || !matches ? pixels[foreground + c] : (pixels[i + c] - background[c] * mattePart) / alpha)));
                    pixels[i + 3] = (byte)Math.Max(1, Math.Round(pixels[i + 3] * alpha));
                }
            }
        }

        private static bool NearInk(byte[] pixels, int width, int height, int x, int y)
        {
            for (int dy = -3; dy <= 3; dy++)
                for (int dx = -3; dx <= 3; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int i = (ny * width + nx) * 4;
                    if (pixels[i + 3] >= 250 &&
                        (pixels[i + 1] > 90 || Math.Min(pixels[i], pixels[i + 2]) - pixels[i + 1] < 90)) return true;
                }
            return false;
        }

        private static int NearestCore(byte[] pixels, byte[] distance, int width, int height, int x, int y)
        {
            int best = -1, bestDistance = int.MaxValue;
            int dark = -1, darkChroma = int.MaxValue, darkDistance = int.MaxValue;
            for (int dy = -6; dy <= 6; dy++)
                for (int dx = -6; dx <= 6; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    int squared = dx * dx + dy * dy;
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    int p = ny * width + nx;
                    int i = p * 4;
                    if (distance[p] == 0 || pixels[i + 3] < 250) continue;
                    int chroma = Math.Max(0, Math.Min(pixels[i], pixels[i + 2]) - pixels[i + 1]);
                    if (squared <= 36 && Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) <= 90 &&
                        (chroma < darkChroma || chroma == darkChroma && squared < darkDistance))
                    { dark = i; darkChroma = chroma; darkDistance = squared; }
                    // Thin neutral outlines need not have a four-pixel-deep core.
                    if (distance[p] < 4 && Math.Min(pixels[i], pixels[i + 2]) - pixels[i + 1] > 2) continue;
                    if (squared < bestDistance) { best = p * 4; bestDistance = squared; }
                }
            return dark >= 0 ? dark : best;
        }
    }
}
