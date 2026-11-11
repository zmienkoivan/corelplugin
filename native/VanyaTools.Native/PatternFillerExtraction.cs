using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class PatternFillerResult
    {
        internal BitmapSource Sheet;
        internal BitmapSource[] Cells = new BitmapSource[4];
        internal bool[] Representative = new bool[4];
        internal string[] Names = new string[4];
        internal int Count => Cells.Count(image => image != null);
    }

    internal static class PatternFillerExtraction
    {
        private sealed class Component
        {
            internal int Area, Left, Right, Top, Bottom;
            internal bool Cut(int width, int height) => Left == 0 || Top == 0 || Right == width - 1 || Bottom == height - 1;
        }

        internal static PatternFillerResult Extract(BitmapSource sheet, PatternFillerPlan plan, CancellationToken cancellation)
        {
            int width = sheet.PixelWidth, height = sheet.PixelHeight;
            if (width < 32 || height < 32 || (long)width * height > 16000000)
                throw new System.IO.InvalidDataException("ИИ вернул лист неподходящего размера.");
            var converted = new FormatConvertedBitmap(sheet, PixelFormats.Bgra32, null, 0);
            var pixels = new byte[checked(width * height * 4)];
            converted.CopyPixels(pixels, width * 4, 0);
            PatternFillerMatte.Remove(pixels, width, height, cancellation);
            var result = new PatternFillerResult { Sheet = sheet };
            for (int index = 0; index < 4; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                result.Names[index] = String.IsNullOrWhiteSpace(plan?.Elements[index]) ? "Фигура " + (index + 1) : plan.Elements[index];
                int x0 = width * (index % 2) / 2, y0 = height * (index / 2) / 2;
                int w = width * (index % 2 + 1) / 2 - x0, h = height * (index / 2 + 1) / 2 - y0;
                var components = Components(pixels, width, x0, y0, w, h, cancellation);
                int minimum = Math.Max(8, w * h / 200000);
                var complete = components.Where(c => c.Area >= minimum && !c.Cut(w, h)).ToArray();
                // Antialiasing can spill a one-pixel strip from the next cell.
                var cut = components.Where(c => c.Cut(w, h) && c.Right - c.Left >= 5 && c.Bottom - c.Top >= 5).ToArray();
                if (complete.Length == 0) continue;
                int largest = complete.Max(c => c.Area);
                bool tiled = cut.Any(c => c.Area >= Math.Max(minimum, largest / 30));
                Component bounds;
                if (tiled)
                {
                    // A repeated cell may contain complete motifs as well as cropped
                    // copies at its edges. Recover one whole central copy.
                    if (largest < cut.Max(c => c.Area) * .15) continue;
                    bounds = complete.Where(c => c.Area >= largest * .55)
                        .OrderBy(c => Math.Pow((c.Left + c.Right - w) / (double)w, 2) +
                            Math.Pow((c.Top + c.Bottom - h) / (double)h, 2)).First();
                    result.Representative[index] = true;
                }
                else
                {
                    // Preserve disconnected parts of a single ornament (rays, dots,
                    // petals). Tiny detached edge noise must not discard the cell.
                    bounds = new Component { Left = complete.Min(c => c.Left), Right = complete.Max(c => c.Right),
                        Top = complete.Min(c => c.Top), Bottom = complete.Max(c => c.Bottom) };
                }
                int left = Math.Max(0, bounds.Left - 2), top = Math.Max(0, bounds.Top - 2);
                int right = Math.Min(w - 1, bounds.Right + 2), bottom = Math.Min(h - 1, bounds.Bottom + 2);
                int spriteWidth = right - left + 1, spriteHeight = bottom - top + 1;
                if (spriteWidth < 8 || spriteHeight < 8) continue;
                var sprite = new byte[spriteWidth * spriteHeight * 4];
                for (int y = 0; y < spriteHeight; y++)
                    Buffer.BlockCopy(pixels, ((y0 + top + y) * width + x0 + left) * 4,
                        sprite, y * spriteWidth * 4, spriteWidth * 4);
                var image = BitmapSource.Create(spriteWidth, spriteHeight, 96, 96, PixelFormats.Bgra32,
                    null, sprite, spriteWidth * 4);
                image.Freeze(); result.Cells[index] = image;
            }
            return result;
        }

        private static List<Component> Components(byte[] pixels, int strideWidth, int x0, int y0, int width, int height,
            CancellationToken cancellation)
        {
            int count = width * height;
            var visited = new bool[count];
            var queue = new int[count];
            var result = new List<Component>();
            int minimum = Math.Max(8, count / 200000);
            for (int y = 0; y < height; y++)
            {
                cancellation.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                {
                    int start = y * width + x;
                    if (visited[start] || pixels[((y0 + y) * strideWidth + x0 + x) * 4 + 3] <= 8) continue;
                    var component = new Component { Left = x, Right = x, Top = y, Bottom = y };
                    int first = 0, last = 1; queue[0] = start; visited[start] = true;
                    while (first < last)
                    {
                        if ((first & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                        int p = queue[first++], px = p % width, py = p / width;
                        component.Area++;
                        component.Left = Math.Min(component.Left, px); component.Right = Math.Max(component.Right, px);
                        component.Top = Math.Min(component.Top, py); component.Bottom = Math.Max(component.Bottom, py);
                        for (int dy = -1; dy <= 1; dy++)
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                int nx = px + dx, ny = py + dy;
                                if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                                int q = ny * width + nx;
                                if (visited[q] || pixels[((y0 + ny) * strideWidth + x0 + nx) * 4 + 3] <= 8) continue;
                                visited[q] = true; queue[last++] = q;
                            }
                    }
                    if (component.Area >= minimum) result.Add(component);
                }
            }
            return result;
        }
    }
}
