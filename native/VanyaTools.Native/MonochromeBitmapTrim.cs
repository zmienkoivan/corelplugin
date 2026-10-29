using System;

namespace VanyaTools.Native
{
    // The native one-bit tile packing differs from the byte-per-channel images.
    // Read the native Pixel[x,y] predicate instead of guessing a bit/row order.
    // Coordinates remain bottom-origin throughout; no bitmap is rendered or converted.
    internal static class MonochromeBitmapTrim
    {
        internal readonly struct Bounds
        {
            internal Bounds(int left, int bottom, int right, int top)
            { Left = left; Bottom = bottom; Right = right; Top = top; }
            internal int Left { get; }
            internal int Bottom { get; }
            internal int Right { get; }
            internal int Top { get; }
        }

        internal static bool Visible(bool whitePixel, bool whitePaintVisible, bool blackPaintVisible, int alpha)
        {
            return alpha > 10 && (whitePixel ? whitePaintVisible : blackPaintVisible);
        }

        // Find an exact boundary, including isolated one-pixel features. Never use
        // a coarse grid, which could silently cut off small marks or thin text.
        // Once an occupied boundary row/column is found its interior is not scanned.
        internal static bool FindBounds(int width, int height, Func<int, int, bool> content, out Bounds bounds)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (content == null) throw new ArgumentNullException(nameof(content));
            bounds = default;
            int bottom = 0;
            while (bottom < height && !Row(content, bottom, width)) bottom++;
            if (bottom == height) return false;

            int top = height - 1;
            while (top > bottom && !Row(content, top, width)) top--;
            int left = 0;
            while (left < width && !Column(content, left, bottom, top)) left++;
            int right = width - 1;
            while (right > left && !Column(content, right, bottom, top)) right--;
            bounds = new Bounds(left, bottom, right, top);
            return true;
        }

        private static bool Row(Func<int, int, bool> content, int y, int width)
        {
            for (int x = 0; x < width; x++) if (content(x, y)) return true;
            return false;
        }

        private static bool Column(Func<int, int, bool> content, int x, int bottom, int top)
        {
            for (int y = bottom; y <= top; y++) if (content(x, y)) return true;
            return false;
        }
    }
}
