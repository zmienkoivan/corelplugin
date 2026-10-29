using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    // Corel's tile buffers are bottom-up, with bottom-origin tile coordinates.
    // Preserve that established ordering; validate the whole canvas before returning it.
    internal static class NativeBitmapPixelReader
    {
        private const int MaximumTiles = 100000;

        private sealed class TileBounds
        {
            internal int Left, Right, Bottom, Top;
        }

        internal static BitmapSource Read(object bitmapObject, int captureDpi, int maxPixels, bool whiteBackground)
        {
            if (bitmapObject == null) throw new InvalidOperationException("CorelDRAW не вернул растр.");
            dynamic bitmap = bitmapObject;
            object colorObject = (object)bitmap.Image;
            if (colorObject == null) throw new InvalidOperationException("CorelDRAW не вернул цветовые пиксели растра.");
            dynamic color = colorObject;
            int width = Convert.ToInt32(color.Width), height = Convert.ToInt32(color.Height);
            long pixelCount = (long)width * height;
            if (width <= 0 || height <= 0 || pixelCount > maxPixels || pixelCount > Int32.MaxValue / 4)
                throw new InvalidOperationException("Размер выделения слишком велик для обработки. Выделите только нужную графику.");
            if (captureDpi <= 0) throw new InvalidOperationException("Некорректное разрешение захваченного растра.");

            object alphaObject = null;
            if (Convert.ToBoolean(bitmap.Transparent))
            {
                alphaObject = (object)bitmap.ImageAlpha;
                if (alphaObject == null) throw new InvalidOperationException("CorelDRAW не вернул альфа-канал растра.");
                dynamic alpha = alphaObject;
                int alphaWidth = Convert.ToInt32(alpha.Width), alphaHeight = Convert.ToInt32(alpha.Height);
                if (alphaWidth != width || alphaHeight != height)
                    throw new InvalidOperationException("Размер альфа-канала " + alphaWidth + "×" + alphaHeight +
                        " не совпадает с цветовым изображением " + width + "×" + height + ". Кадр не сохранён.");
            }

            byte[] pixels = new byte[checked((int)pixelCount * 4)];
            if (alphaObject == null)
                for (int index = 3; index < pixels.Length; index += 4) pixels[index] = 255;
            CopyTiles(colorObject, pixels, width, height, false);
            if (alphaObject != null) CopyTiles(alphaObject, pixels, width, height, true);
            if (whiteBackground && alphaObject != null)
                for (int index = 0; index < pixels.Length; index += 4)
                {
                    int alpha = pixels[index + 3];
                    if (alpha == 255) continue;
                    int white = 255 * (255 - alpha);
                    pixels[index] = (byte)((pixels[index] * alpha + white + 127) / 255);
                    pixels[index + 1] = (byte)((pixels[index + 1] * alpha + white + 127) / 255);
                    pixels[index + 2] = (byte)((pixels[index + 2] * alpha + white + 127) / 255);
                    pixels[index + 3] = 255;
                }
            var result = BitmapSource.Create(width, height, captureDpi, captureDpi,
                PixelFormats.Bgra32, null, pixels, checked(width * 4));
            result.Freeze();
            return result;
        }

        private static void CopyTiles(object imageObject, byte[] pixels, int width, int height, bool alpha)
        {
            string channel = alpha ? "альфа-канала" : "цветового изображения";
            dynamic image = imageObject, tiles = image.Tiles;
            int count = Convert.ToInt32(tiles.Count);
            if (count <= 0 || count > MaximumTiles)
                throw Failure(channel, "неподдерживаемое количество тайлов: " + count);
            var rectangles = new List<TileBounds>(count);
            for (int index = 1; index <= count; index++)
            {
                dynamic tile = tiles.Item[index];
                int left = Convert.ToInt32(tile.Left), top = Convert.ToInt32(tile.Top);
                int tileWidth = Convert.ToInt32(tile.Width), tileHeight = Convert.ToInt32(tile.Height);
                int stride = Convert.ToInt32(tile.BytesPerLine), bpp = Convert.ToInt32(tile.BytesPerPixel);
                string detail = "тайл " + index + " [X=" + left + ", Y=" + top + ", " + tileWidth + "×" + tileHeight + "]";
                if (stride < 0)
                    throw Failure(channel, detail + ": отрицательный шаг строк не поддерживается");
                long bottom = (long)top - tileHeight + 1, right = (long)left + tileWidth;
                if (tileWidth <= 0 || tileHeight <= 0 || left < 0 || bottom < 0 || right > width || top >= height)
                    throw Failure(channel, detail + ": границы выходят за размер " + width + "×" + height);
                if (bpp < (alpha ? 1 : 3) || stride <= 0 || (long)stride < (long)tileWidth * bpp)
                    throw Failure(channel, detail + ": неподдерживаемый формат пикселей или шаг строк");
                byte[] data = (byte[])tile.PixelData;
                long requiredBytes = (long)stride * (tileHeight - 1) + (long)tileWidth * bpp;
                if (data == null || data.LongLength < requiredBytes)
                    throw Failure(channel, detail + ": буфер пикселей неполный");
                rectangles.Add(new TileBounds { Left = left, Right = (int)right, Bottom = (int)bottom, Top = top + 1 });
                for (int y = 0; y < tileHeight; y++)
                {
                    int imageY = (int)bottom + y;
                    int destinationRow = checked((height - 1 - imageY) * width * 4);
                    int sourceRow = checked(y * stride);
                    for (int x = 0; x < tileWidth; x++)
                    {
                        int destination = checked(destinationRow + (left + x) * 4);
                        int source = checked(sourceRow + x * bpp);
                        if (alpha) pixels[destination + 3] = data[source];
                        else
                        {
                            pixels[destination] = data[source];
                            pixels[destination + 1] = data[source + 1];
                            pixels[destination + 2] = data[source + 2];
                        }
                    }
                }
            }
            ValidateCoverage(rectangles, width, height, channel);
        }

        private static void ValidateCoverage(IReadOnlyList<TileBounds> rectangles, int width, int height, string channel)
        {
            // Sweep only tile boundaries. Memory scales with tile count, not image pixel count.
            var events = new SortedDictionary<int, List<int>>();
            void Add(int y, int value)
            {
                if (!events.TryGetValue(y, out var values)) events.Add(y, values = new List<int>());
                values.Add(value);
            }
            for (int index = 0; index < rectangles.Count; index++)
            {
                Add(rectangles[index].Bottom, index + 1);
                Add(rectangles[index].Top, -(index + 1));
            }
            var active = new Dictionary<int, TileBounds>();
            int previous = 0;
            foreach (var boundary in events)
            {
                if (boundary.Key > previous) ValidateBand(active.Values, width, previous, boundary.Key, channel);
                foreach (int value in boundary.Value)
                    if (value > 0) active[value - 1] = rectangles[value - 1];
                    else active.Remove(-value - 1);
                previous = boundary.Key;
            }
            if (previous < height) ValidateBand(active.Values, width, previous, height, channel);
        }

        private static void ValidateBand(IEnumerable<TileBounds> active, int width, int bottom, int top, string channel)
        {
            int covered = 0;
            foreach (var interval in active.OrderBy(tile => tile.Left).ThenBy(tile => tile.Right))
            {
                if (interval.Left > covered)
                    throw Failure(channel, "не прочитана область X=" + covered + "…" + (interval.Left - 1) +
                        ", Y=" + bottom + "…" + (top - 1));
                covered = Math.Max(covered, interval.Right);
                if (covered == width) return;
            }
            if (covered < width)
                throw Failure(channel, "не прочитана область X=" + covered + "…" + (width - 1) +
                    ", Y=" + bottom + "…" + (top - 1));
        }

        private static InvalidOperationException Failure(string channel, string detail) => new InvalidOperationException(
            "Не удалось полностью прочитать пиксели " + channel + ": " + detail + ". Кадр не сохранён.");
    }
}
