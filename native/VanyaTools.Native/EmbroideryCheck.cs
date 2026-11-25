using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    // Advisory raster checks only. They cannot validate stitch density,
    // underlay, spelling or the stitch program produced by a digitizer.
    internal static class EmbroideryCheck
    {
        internal static bool[] Mask(BitmapSource source)
        {
            var image = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = image.PixelWidth, h = image.PixelHeight; byte[] pixels = new byte[w * h * 4]; image.CopyPixels(pixels, w * 4, 0);
            var corners = new[] { 0, w - 1, (h - 1) * w, h * w - 1 };
            byte[] bg = Enumerable.Range(0, 3).Select(c => corners.Select(p => pixels[p * 4 + c]).OrderBy(v => v).ElementAt(1)).ToArray();
            var mask = new bool[w * h];
            for (int p = 0; p < mask.Length; p++) mask[p] = pixels[p * 4 + 3] > 127 &&
                (Math.Abs(pixels[p * 4] - bg[0]) > 28 || Math.Abs(pixels[p * 4 + 1] - bg[1]) > 28 || Math.Abs(pixels[p * 4 + 2] - bg[2]) > 28);
            return mask;
        }
        internal static Int32Rect Bounds(bool[] mask, int w, int h)
        {
            int left = w, top = h, right = -1, bottom = -1;
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (mask[y * w + x]) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
            return right < left ? new Int32Rect() : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
        }
        internal static IEnumerable<string> Analyze(BitmapSource source, double mmPerPixel, EmbroideryRequest request, bool mono)
        {
            double scale = Math.Min(1, 640d / Math.Max(source.PixelWidth, source.PixelHeight));
            BitmapSource image = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0); int w = bgra.PixelWidth, h = bgra.PixelHeight;
            var mask = Mask(image); double pxMm = mmPerPixel / scale;
            byte[] data = new byte[w * h * 4]; bgra.CopyPixels(data, w * 4, 0);
            var colors = new Dictionary<int, int>(); int total = 0;
            for (int p = 0; p < mask.Length; p++) if (mask[p])
            { int key = (data[p * 4] / 64) | ((data[p * 4 + 1] / 64) << 2) | ((data[p * 4 + 2] / 64) << 4); colors[key] = colors.TryGetValue(key, out int count) ? count + 1 : 1; total++; }
            int significant = colors.Values.Count(n => n > Math.Max(6, total * .025));
            if (significant > (mono ? 1 : request.Colors)) yield return "обнаружены лишние цветовые области или фактура. Проверьте лимит " + (mono ? 1 : request.Colors) + " цветов ниток.";
            int small = Components(mask, w, h, true, request.MinStrokeMm / pxMm);
            if (small > 0) yield return "есть мелкие отдельные детали меньше " + EmbroideryRequest.F(request.MinStrokeMm) + " мм (" + small + "). Укрупните или объедините их при оцифровке.";
            int gaps = Components(mask, w, h, false, request.MinGapMm / pxMm);
            if (gaps > 0) yield return "есть узкие замкнутые просветы меньше " + EmbroideryRequest.F(request.MinGapMm) + " мм (" + gaps + "). Проверьте отверстия и буквы.";
        }
        private static int Components(bool[] mask, int w, int h, bool foreground, double limit)
        {
            var seen = new bool[mask.Length]; var queue = new Queue<int>(); int small = 0;
            for (int start = 0; start < mask.Length; start++)
            {
                if (seen[start] || mask[start] != foreground) continue;
                queue.Enqueue(start); seen[start] = true; int area = 0, left = w, top = h, right = 0, bottom = 0; bool edge = false;
                while (queue.Count > 0)
                {
                    int p = queue.Dequeue(), x = p % w, y = p / w; area++;
                    left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    edge |= x == 0 || y == 0 || x == w - 1 || y == h - 1;
                    if (x > 0) Add(p - 1); if (x < w - 1) Add(p + 1); if (y > 0) Add(p - w); if (y < h - 1) Add(p + w);
                }
                if (area >= 4 && (foreground || !edge) && Math.Min(right - left + 1, bottom - top + 1) < limit) small++;
            }
            return small;
            void Add(int p) { if (!seen[p] && mask[p] == foreground) { seen[p] = true; queue.Enqueue(p); } }
        }
    }
}
