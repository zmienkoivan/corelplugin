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

        internal bool HasVisiblePixels(CancellationToken cancellation)
        {
            for (int p = 3; p < _pixels.Length; p += 4)
            { if ((p & 65535) == 3) cancellation.ThrowIfCancellationRequested(); if (_pixels[p] != 0) return true; }
            return false;
        }

        internal DtfCorrectionPixels Correct(bool removeAlpha, int threshold, bool whiten, int tolerance, CancellationToken cancellation)
            => !removeAlpha && !whiten ? this : new DtfCorrectionPixels(Width, Height, DpiX, DpiY,
                DtfRasterOperations.Correct(_pixels, removeAlpha, threshold, whiten, tolerance, cancellation));
        internal DtfCorrectionPixels Correct(DtfCorrectionOptions options, CancellationToken cancellation)
        {
            var prep = options.Preparation(); prep.Validate();
            byte[] pixels = prep.Knockout ? (byte[])_pixels.Clone() : _pixels;
            DtfPrintPreparation.Knockout(pixels, 4, 4, null, pixels, 4, 3, Width, Height, prep, cancellation);
            if (options.RemoveSemiTransparent || options.WhitenNearWhite)
                pixels = DtfRasterOperations.Correct(pixels, options.RemoveSemiTransparent, options.Threshold,
                    options.WhitenNearWhite, options.NearWhiteTolerance, cancellation);
            if (prep.Halftone)
            {
                if (ReferenceEquals(pixels, _pixels)) pixels = (byte[])_pixels.Clone();
                DtfPrintPreparation.Halftone(pixels, 4, 3, Width, Height, DpiX, DpiY, prep, cancellation);
            }
            return ReferenceEquals(pixels, _pixels) ? this : new DtfCorrectionPixels(Width, Height, DpiX, DpiY, pixels);
        }
        internal DtfCorrectionPixels Prepare(DtfPrintPreparationOptions options, CancellationToken cancellation)
        {
            if (options == null || !options.Knockout && !options.Halftone) return this;
            byte[] pixels = (byte[])_pixels.Clone();
            DtfPrintPreparation.Knockout(pixels, 4, 4, null, pixels, 4, 3, Width, Height, options, cancellation);
            DtfPrintPreparation.Halftone(pixels, 4, 3, Width, Height, DpiX, DpiY, options, cancellation);
            return new DtfCorrectionPixels(Width, Height, DpiX, DpiY, pixels);
        }
        internal bool Sample(int x, int y, out Color color)
        {
            color = Colors.Black;
            if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
            int p = (y * Width + x) * 4;
            if (_pixels[p + 3] == 0) return false;
            color = Color.FromRgb(_pixels[p + 2], _pixels[p + 1], _pixels[p]); return true;
        }
        internal DtfCorrectionPixels Audit(bool semi, bool red, bool white, int tolerance,
            byte markerR, byte markerG, byte markerB, CancellationToken cancellation)
            => !semi && !white ? this : new DtfCorrectionPixels(Width, Height, DpiX, DpiY,
                DtfRasterOperations.Audit(_pixels, semi, red, white, tolerance, markerR, markerG, markerB, cancellation));

        internal ThinAuditResult AnalyzeThickness(ThinAuditOptions options, CancellationToken cancellation) =>
            ThinElementAudit.Analyze(_pixels, Width, Height, Width * 25.4 / DpiX, Height * 25.4 / DpiY, options, cancellation);

        internal DtfCorrectionPixels OverlayDiagnostic(byte[] overlay, CancellationToken cancellation)
        {
            if (overlay == null) return this;
            if (overlay.Length != _pixels.Length) throw new ArgumentException("Размер маски не совпадает с предпросмотром.");
            var pixels = (byte[])_pixels.Clone();
            for (int p = 0; p < pixels.Length; p += 4)
            {
                if ((p & 65535) == 0) cancellation.ThrowIfCancellationRequested();
                int alpha = overlay[p + 3]; if (alpha == 0) continue;
                for (int channel = 0; channel < 3; channel++) pixels[p + channel] = (byte)((pixels[p + channel] * (255 - alpha) + overlay[p + channel] * alpha + 127) / 255);
            }
            // Diagnostic colour never changes the captured transparency.
            return new DtfCorrectionPixels(Width, Height, DpiX, DpiY, pixels);
        }

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
