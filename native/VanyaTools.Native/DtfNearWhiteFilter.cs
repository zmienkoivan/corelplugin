using System;
using System.IO;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    // File adapter for the RGB correction pipeline. This does not preserve a
    // CMYK source model: capture/import must handle that separately.
    internal static class DtfNearWhiteFilter
    {
        internal static void Process(string sourcePath, string outputPath, int tolerance,
            CancellationToken cancellation = default(CancellationToken))
        {
            if (String.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Source image path is required.", nameof(sourcePath));
            if (String.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output image path is required.", nameof(outputPath));
            string source = Path.GetFullPath(sourcePath), output = Path.GetFullPath(outputPath);
            if (String.Equals(source, output, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Near-white correction must not overwrite its source image.", nameof(outputPath));
            cancellation.ThrowIfCancellationRequested();

            BitmapFrame decoded;
            using (var stream = File.OpenRead(source))
            {
                decoded = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                decoded.Freeze();
            }
            BitmapSource bgra = decoded.Format == PixelFormats.Bgra32 ? decoded :
                new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            int width = bgra.PixelWidth, height = bgra.PixelHeight, stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            bgra.CopyPixels(pixels, stride, 0);
            var mask = DtfNearWhitePixels.CreateMask(pixels, width, height, tolerance, cancellation);
            byte[] corrected = DtfNearWhitePixels.Whiten(pixels, mask, cancellation);
            var image = BitmapSource.Create(width, height, bgra.DpiX, bgra.DpiY,
                PixelFormats.Bgra32, null, corrected, stride);
            image.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));

            string directory = Path.GetDirectoryName(output);
            string temporary = Path.Combine(directory, ".vanya-near-white-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                cancellation.ThrowIfCancellationRequested();
                Directory.CreateDirectory(directory);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) encoder.Save(stream);
                cancellation.ThrowIfCancellationRequested();
                if (File.Exists(output)) File.Replace(temporary, output, null);
                else File.Move(temporary, output);
            }
            finally
            {
                // Delete only the exact file created by this invocation.
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
