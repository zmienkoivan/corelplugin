using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    /// <summary>A plain managed snapshot. No decoder or Freezable crosses a thread boundary.</summary>
    internal sealed class DtfCorrectionPixels
    {
        internal readonly int Width, Height;
        internal readonly double DpiX, DpiY;
        private readonly byte[] _pixels;

        private DtfCorrectionPixels(int width, int height, double dpiX, double dpiY, byte[] pixels)
        { Width = width; Height = height; DpiX = dpiX; DpiY = dpiY; _pixels = pixels; }

        internal static DtfCorrectionPixels Capture(BitmapSource source)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source :
                new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = checked(bgra.PixelWidth * 4);
            var pixels = new byte[checked(stride * bgra.PixelHeight)];
            bgra.CopyPixels(pixels, stride, 0);
            return new DtfCorrectionPixels(bgra.PixelWidth, bgra.PixelHeight, bgra.DpiX, bgra.DpiY, pixels);
        }

        internal BitmapSource Bitmap()
        {
            var bitmap = BitmapSource.Create(Width, Height, DpiX, DpiY, PixelFormats.Bgra32, null,
                _pixels, checked(Width * 4));
            bitmap.Freeze(); return bitmap;
        }

        internal DtfCorrectionPixels Correct(bool removeAlpha, int threshold, bool whiten, int tolerance, CancellationToken cancellation)
            => !removeAlpha && !whiten ? this : new DtfCorrectionPixels(Width, Height, DpiX, DpiY,
                DtfRasterOperations.Correct(_pixels, removeAlpha, threshold, whiten, tolerance, cancellation));
        internal DtfCorrectionPixels Audit(bool semi, bool red, bool white, int tolerance,
            byte markerR, byte markerG, byte markerB, CancellationToken cancellation)
            => !semi && !white ? this : new DtfCorrectionPixels(Width, Height, DpiX, DpiY,
                DtfRasterOperations.Audit(_pixels, semi, red, white, tolerance, markerR, markerG, markerB, cancellation));

        internal void WritePng(Stream stream, CancellationToken cancellation)
            => FastRasterPng.Write(stream, _pixels, Width, Height, DpiX, DpiY, cancellation);

        internal DtfCorrectionPixels Crop(Int32Rect region, CancellationToken cancellation)
        {
            int stride = checked(region.Width * 4);
            var pixels = new byte[checked(stride * region.Height)];
            for (int y = 0; y < region.Height; y++)
            {
                if ((y & 127) == 0) cancellation.ThrowIfCancellationRequested();
                Buffer.BlockCopy(_pixels, checked(((region.Y + y) * Width + region.X) * 4), pixels, y * stride, stride);
            }
            return new DtfCorrectionPixels(region.Width, region.Height, DpiX, DpiY, pixels);
        }
    }
}
