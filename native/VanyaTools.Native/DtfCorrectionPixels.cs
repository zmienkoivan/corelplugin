using System;
using System.Collections;
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

        internal static DtfCorrectionPixels Capture(BitmapSource source, double? dpiX = null, double? dpiY = null)
        {
            BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source :
                new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int stride = checked(bgra.PixelWidth * 4);
            var pixels = new byte[checked(stride * bgra.PixelHeight)];
            bgra.CopyPixels(pixels, stride, 0);
            return new DtfCorrectionPixels(bgra.PixelWidth, bgra.PixelHeight, dpiX ?? bgra.DpiX, dpiY ?? bgra.DpiY, pixels);
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
            if(options.Resample300) {
                var prepared=Resample300(cancellation);var filters=options.Copy();filters.Resample300=false;
                return prepared.Correct(filters,cancellation);
            }
            var prep = options.Preparation(); prep.Validate();
            byte[] pixels = prep.Knockout ? (byte[])_pixels.Clone() : _pixels;
            var knockoutMask = prep.Knockout ? Mask(options.KnockoutMask) : null;
            DtfPrintPreparation.Knockout(pixels, 4, 4, null, pixels, 4, 3, Width, Height, prep, cancellation, knockoutMask);
            if (options.RemoveSemiTransparent || options.WhitenNearWhite)
                pixels = DtfRasterOperations.Correct(pixels, options.RemoveSemiTransparent, options.Threshold,
                    options.WhitenNearWhite, options.NearWhiteTolerance, cancellation,
                    options.RemoveSemiTransparent ? Mask(options.AlphaMask) : null,
                    options.WhitenNearWhite ? Mask(options.WhiteMask) : null);
            if (prep.Halftone)
            {
                if (ReferenceEquals(pixels, _pixels)) pixels = (byte[])_pixels.Clone();
                var halftoneMask = Mask(options.HalftoneMask);
                DtfPrintPreparation.Boost(pixels, 4, 4, null, pixels, 4, 3, Width, Height, prep, cancellation, halftoneMask);
                DtfPrintPreparation.Halftone(pixels, 4, 3, Width, Height, DpiX, DpiY, prep, cancellation, halftoneMask);
            }
            return ReferenceEquals(pixels, _pixels) ? this : new DtfCorrectionPixels(Width, Height, DpiX, DpiY, pixels);

            BitArray Mask(RasterProtectionMask mask) => mask?.Rasterize(Width, Height,
                DpiX, DpiY, options.GridOriginXmm, options.GridOriginYmm, cancellation);
        }
        internal DtfCorrectionPixels Resample300(CancellationToken cancellation)
        {
            var plan=DtfResamplePlan.Create(Width,Height,DpiX,DpiY);if(!plan.Needed)return this;
            var pixels=new byte[checked(plan.Width*plan.Height*4)];
            DtfRasterResample.Run(_pixels,3,4,_pixels,4,3,Width,Height,pixels,4,pixels,4,3,plan,cancellation);
            return new DtfCorrectionPixels(plan.Width,plan.Height,plan.DpiX,plan.DpiY,pixels);
        }
        internal DtfCorrectionPixels Prepare(DtfPrintPreparationOptions options, CancellationToken cancellation)
        {
            if (options == null || !options.Knockout && !options.Halftone) return this;
            byte[] pixels = (byte[])_pixels.Clone();
            DtfPrintPreparation.Knockout(pixels, 4, 4, null, pixels, 4, 3, Width, Height, options, cancellation);
            DtfPrintPreparation.Boost(pixels, 4, 4, null, pixels, 4, 3, Width, Height, options, cancellation);
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

        internal DtfCorrectionPixels RepairStairs(int strength, RasterProtectionMask protection, CancellationToken cancellation)
        {
            var protectedPixels = protection?.Rasterize(Width, Height, DpiX, DpiY, 0, 0, cancellation);
            return new DtfCorrectionPixels(Width, Height, DpiX, DpiY,
                StairCoverage.RepairBgra(_pixels, Width, Height, strength, cancellation, protectedPixels));
        }

        internal int CountStairs(int strength, CancellationToken cancellation)
        {
            StairCoverage.Detect(_pixels, Width, Height, 4, 3, strength, cancellation, out int elbows);
            return elbows;
        }

        internal DtfCorrectionPixels AuditStairs(bool enabled, int strength, CancellationToken cancellation, out int elbows)
        {
            elbows = 0;
            if (!enabled) return this;
            var marked = StairCoverage.Detect(_pixels, Width, Height, 4, 3, strength, cancellation, out elbows);
            if (elbows == 0) return this;
            var result = (byte[])_pixels.Clone();
            for (int p = 0; p < marked.Length; p++)
            {
                if ((p & 4095) == 0) cancellation.ThrowIfCancellationRequested();
                int i = p * 4;
                if (marked[p] == 0 || result[i + 3] <= 32) continue;
                result[i] = (byte)(result[i] / 4);
                result[i + 1] = (byte)((result[i + 1] + 3 * 119) / 4);
                result[i + 2] = (byte)((result[i + 2] + 3 * 255) / 4);
            }
            return new DtfCorrectionPixels(Width, Height, DpiX, DpiY, result);
        }

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
