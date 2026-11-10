using System;
using System.Collections;
using System.Threading;

namespace VanyaTools.Native
{
    internal static class StairCoverage
    {
        internal const int DefaultStrength = 2;

        // A staircase contains consecutive convex and concave elbows with the
        // same inward diagonal. A single square corner or isolated dot does not.
        internal static byte[] Detect(byte[] source, int width, int height, int stride, int offset,
            int strength, CancellationToken cancellation, out int elbows)
        {
            int count = checked(width * height);
            var corners = new byte[count];
            int span = Math.Max(1, Math.Min(3, strength)) + 1;
            for (int y = 0; y < height - 1; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width - 1; x++)
                {
                    int i = y * width + x, p = i * stride + offset, row = width * stride;
                    int a = source[p], b = source[p + stride], c = source[p + row], d = source[p + row + stride];
                    if (Soft(a) || Soft(b) || Soft(c) || Soft(d)) continue;
                    int bits = (a >= 128 ? 1 : 0) | (b >= 128 ? 2 : 0) |
                        (c >= 128 ? 4 : 0) | (d >= 128 ? 8 : 0);
                    switch (bits)
                    {
                        case 1: corners[i] = 1; break;
                        case 2: corners[i] = 2; break;
                        case 4: corners[i] = 3; break;
                        case 8: corners[i] = 4; break;
                        case 7: corners[i] = 8; break;
                        case 11: corners[i] = 7; break;
                        case 13: corners[i] = 6; break;
                        case 14: corners[i] = 5; break;
                    }
                }
            }

            var marked = new byte[count];
            elbows = 0;
            for (int y = 0; y < height - 1; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width - 1; x++)
                {
                    int p = y * width + x, corner = corners[p];
                    if (corner == 0 || !Paired(x, y, (byte)(9 - corner))) continue;
                    marked[p] = marked[p + 1] = marked[p + width] = marked[p + width + 1] = 1;
                    elbows++;
                }
            }
            return marked;

            bool Paired(int x, int y, byte other)
            {
                for (int j = Math.Max(0, y - span); j <= Math.Min(height - 2, y + span); j++)
                    for (int i = Math.Max(0, x - span); i <= Math.Min(width - 2, x + span); i++)
                        if (Math.Abs(i - x) + Math.Abs(j - y) <= span && corners[j * width + i] == other) return true;
                return false;
            }

            bool Soft(int value) => value > 32 && value < 223;
        }

        internal static byte[] Repair(byte[] source, int width, int height, int stride, int offset,
            int strength, CancellationToken cancellation, BitArray protection = null)
        {
            int radius = Math.Max(1, Math.Min(3, strength));
            var mask = Detect(source, width, height, stride, offset, radius, cancellation, out _);
            // Update only the two-pixel strip around a detected elbow. Sample the
            // unchanged input, so results don't depend on scan order.
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    int original = Alpha(i);
                    if (mask[i] == 0 || protection?[i] == true || original >= 223 && !Supported(x, y))
                    {
                        mask[i] = (byte)original;
                        continue;
                    }

                    int coverage = 0, weight = 0;
                    for (int j = Math.Max(0, y - radius); j <= Math.Min(height - 1, y + radius); j++)
                        for (int k = Math.Max(0, x - radius); k <= Math.Min(width - 1, x + radius); k++)
                        {
                            int w = (radius + 1 - Math.Abs(j - y)) * (radius + 1 - Math.Abs(k - x));
                            coverage += Alpha(j * width + k) * w; weight += w;
                        }
                    // Limit feathering to the corner itself while replacing its
                    // jagged sampling with coverage estimated across nearby runs.
                    int corrected = Math.Max(0, Math.Min(255, (coverage + weight / 2) / weight * 2 - 128));
                    mask[i] = (byte)((corrected * (radius + 1) + original + (radius + 2) / 2) / (radius + 2));
                }
            }
            return mask;

            int Alpha(int pixel) => source[pixel * stride + offset];

            bool Supported(int x, int y)
            {
                for (int top = Math.Max(0, y - 1); top <= Math.Min(height - 2, y); top++)
                    for (int left = Math.Max(0, x - 1); left <= Math.Min(width - 2, x); left++)
                    {
                        int p = top * width + left;
                        if (Alpha(p) >= 128 && Alpha(p + 1) >= 128 && Alpha(p + width) >= 128 && Alpha(p + width + 1) >= 128)
                            return true;
                    }
                return false;
            }
        }

        internal static byte[] RepairBgra(byte[] source, int width, int height, int strength,
            CancellationToken cancellation, BitArray protection = null)
        {
            var alpha = Repair(source, width, height, 4, 3, strength, cancellation, protection);
            var result = (byte[])source.Clone();
            for (int p = 0; p < alpha.Length; p++)
            {
                if ((p & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int index = p * 4;
                if (alpha[p] > source[index + 3] && source[index + 3] <= 32)
                {
                    int nearest = NearestVisible(source, width, height, 4, 3, p % width, p / width);
                    if (nearest < 0) continue;
                    Buffer.BlockCopy(source, nearest * 4, result, index, 3);
                }
                result[index + 3] = alpha[p];
            }
            return result;
        }

        internal static int NearestVisible(byte[] source, int width, int height, int stride, int offset, int x, int y)
        {
            int found = -1, distance = Int32.MaxValue;
            for (int j = Math.Max(0, y - 2); j <= Math.Min(height - 1, y + 2); j++)
                for (int i = Math.Max(0, x - 2); i <= Math.Min(width - 1, x + 2); i++)
                {
                    int pixel = j * width + i, square = (i - x) * (i - x) + (j - y) * (j - y);
                    if (source[pixel * stride + offset] < 128 || square >= distance) continue;
                    found = pixel; distance = square;
                }
            return found;
        }
    }
}
