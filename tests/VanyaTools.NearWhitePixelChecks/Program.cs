using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); _checks++; }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { _checks++; return; }
        throw new InvalidOperationException(name);
    }
    private static bool Pixel(byte[] bytes, int index, params byte[] expected) =>
        bytes.Skip(index * 4).Take(4).SequenceEqual(expected);

    [STAThread]
    private static int Main()
    {
        try
        {
            byte[] input = {
                255,255,255,255, // Exact white must not be highlighted or rewritten.
                253,253,253,255, // User's example.
                249,249,249,255, // Inclusive tolerance boundary.
                248,254,254,255, // One channel exceeds tolerance.
                255,250,254,127, // Slightly tinted near-white; alpha is independent.
                253,253,253,0,   // Hidden RGB in a fully transparent pixel stays untouched.
                0,0,0,255,
                255,255,255,1
            };
            byte[] original = (byte[])input.Clone();
            var mask = DtfNearWhitePixels.CreateMask(input, 4, 2, 6);
            Check(mask.Width == 4 && mask.Height == 2 && mask.Count == 3, "Mask geometry and candidate count");
            Check(!mask.IsMarked(0) && mask.IsMarked(1) && mask.IsMarked(2), "Exact white excluded; near-white inclusive boundary");
            Check(!mask.IsMarked(3) && mask.IsMarked(4), "Every RGB channel must fall within the tolerance");
            Check(!mask.IsMarked(5) && !mask.IsMarked(6) && !mask.IsMarked(7), "Transparent, dark and pure-white pixels excluded");
            Check(input.SequenceEqual(original), "Candidate diagnosis does not mutate source");
            Check(DtfNearWhitePixels.CreateMask(input, 4, 2, 0).Count == 0, "Zero tolerance finds no almost-white colors");
            Check(DtfNearWhitePixels.CreateMask(input, 4, 2, 255).Count == 5, "Maximum tolerance still excludes pure white and transparent pixels");

            byte[] whitened = DtfNearWhitePixels.Whiten(input, mask);
            Check(!ReferenceEquals(whitened, input) && input.SequenceEqual(original), "Whitening returns independent data");
            Check(Pixel(whitened, 1, 255,255,255,255) && Pixel(whitened, 2, 255,255,255,255), "Candidate RGB becomes exactly 255");
            Check(Pixel(whitened, 4, 255,255,255,127), "Whitening retains semitransparency exactly");
            Check(Pixel(whitened, 3, 248,254,254,255) && Pixel(whitened, 5, 253,253,253,0), "Non-candidates and hidden RGB stay unchanged");
            Check(DtfNearWhitePixels.CreateMask(whitened, 4, 2, 6).Count == 0, "Fixed pixels no longer count as almost white");
            Check(Enumerable.Range(0, 8).All(index => input[index * 4 + 3] == whitened[index * 4 + 3]), "Every alpha byte survives whitening");
            Check(DtfNearWhitePixels.Whiten(whitened, mask).SequenceEqual(whitened), "Whitening is idempotent with the same mask");

            byte[] overlay = DtfNearWhitePixels.Highlight(input, mask);
            Check(!ReferenceEquals(overlay, input) && input.SequenceEqual(original), "Highlight cannot overwrite the source or exported pixels");
            Check(Pixel(overlay, 1, 210,126,254,255), "Default marker is an exact half-opacity BGRA pink blend");
            Check(Pixel(overlay, 0, 255,255,255,255) && Pixel(overlay, 5, 253,253,253,0), "Overlay ignores pure white and fully transparent pixels");
            Check(Enumerable.Range(0, 8).All(index => input[index * 4 + 3] == overlay[index * 4 + 3]), "Overlay keeps all alpha bytes unchanged");
            Check(DtfNearWhitePixels.Highlight(input, mask, 10,20,30,0).SequenceEqual(input), "Zero-opacity diagnostic changes no pixels");
            Check(Pixel(DtfNearWhitePixels.Highlight(input, mask, 10,20,30,255), 4, 30,20,10,127), "Custom marker channel order and opacity");
            Check(DtfNearWhitePixels.Whiten(input, mask).SequenceEqual(whitened), "Preview overlay cannot contaminate a later export correction");

            Throws<ArgumentNullException>(() => DtfNearWhitePixels.CreateMask(null, 1, 1, 6), "Null buffer rejected");
            Throws<ArgumentOutOfRangeException>(() => DtfNearWhitePixels.CreateMask(input, 0, 2, 6), "Empty geometry rejected");
            Throws<ArgumentOutOfRangeException>(() => DtfNearWhitePixels.CreateMask(input, Int32.MaxValue, 2, 6), "Oversized geometry rejected before allocation");
            Throws<ArgumentException>(() => DtfNearWhitePixels.CreateMask(input, 1, 1, 6), "Mismatched buffer rejected");
            Throws<ArgumentOutOfRangeException>(() => DtfNearWhitePixels.CreateMask(input, 4, 2, -1), "Negative tolerance rejected");
            Throws<ArgumentOutOfRangeException>(() => DtfNearWhitePixels.CreateMask(input, 4, 2, 256), "Overlarge tolerance rejected");
            Throws<ArgumentNullException>(() => DtfNearWhitePixels.Whiten(input, null), "Null mask rejected");
            Throws<ArgumentException>(() => DtfNearWhitePixels.Highlight(new byte[4], mask), "Mismatched highlight geometry rejected");
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Throws<OperationCanceledException>(() => DtfNearWhitePixels.CreateMask(input, 4, 2, 6, cancelled.Token), "Diagnosis observes cancellation");
                Throws<OperationCanceledException>(() => DtfNearWhitePixels.Whiten(input, mask, cancelled.Token), "Whitening observes cancellation");
                Throws<OperationCanceledException>(() => DtfNearWhitePixels.Highlight(input, mask, cancellation: cancelled.Token), "Overlay observes cancellation");
            }
            PngRoundTrip();
            Console.WriteLine("Passed " + _checks + " near-white pixel and PNG checks. No Corel or UI.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void PngRoundTrip()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "Vanya-NearWhiteChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        string source = Path.Combine(workspace, "source.png"), output = Path.Combine(workspace, "result.png");
        string cancelledOutput = Path.Combine(workspace, "cancelled.png"), bgrSource = Path.Combine(workspace, "bgr.png");
        string[] owned = { source, output, cancelledOutput, bgrSource };
        try
        {
            byte[] pixels = { 255,255,255,255, 253,253,253,255, 248,254,254,255,
                12,34,56,255, 253,253,253,0, 250,251,252,127 };
            SavePng(source, BitmapSource.Create(3, 2, 300, 150, PixelFormats.Bgra32, null, pixels, 12));
            byte[] sourceBytes = File.ReadAllBytes(source);
            BitmapSource decoded = ReadPng(source);
            DtfNearWhiteFilter.Process(source, output, 6);
            BitmapSource result = ReadPng(output);
            byte[] actual = Bgra(result);
            Check(result.PixelWidth == 3 && result.PixelHeight == 2, "PNG correction preserves image dimensions");
            Check(Math.Abs(result.DpiX - decoded.DpiX) < .001 && Math.Abs(result.DpiY - decoded.DpiY) < .001,
                "PNG correction preserves decoded X/Y resolution");
            Check(Pixel(actual, 0, 255,255,255,255) && Pixel(actual, 1, 255,255,255,255), "PNG white remains exactly 255 and almost-white is corrected");
            Check(Pixel(actual, 2, 248,254,254,255) && Pixel(actual, 3, 12,34,56,255), "PNG round trip preserves unmarked colors exactly");
            Check(Pixel(actual, 4, 253,253,253,0) && Pixel(actual, 5, 255,255,255,127), "PNG round trip preserves alpha and transparent hidden RGB");
            Check(File.ReadAllBytes(source).SequenceEqual(sourceBytes), "File correction does not mutate source image bytes");
            Check(DtfNearWhitePixels.CreateMask(actual, 3, 2, 6).Count == 0, "Exported PNG contains correction rather than pink diagnostic overlay");
            DtfNearWhiteFilter.Process(source, output, 6);
            Check(Bgra(ReadPng(output)).SequenceEqual(actual), "Replacing an existing PNG output preserves the same correction");
            Throws<ArgumentException>(() => DtfNearWhiteFilter.Process(source, source, 6), "Source/output alias is rejected before writing");
            Check(File.ReadAllBytes(source).SequenceEqual(sourceBytes), "Rejected alias cannot alter source image");

            byte[] sentinel = { 1,2,3,4 };
            File.WriteAllBytes(cancelledOutput, sentinel);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                Throws<OperationCanceledException>(() => DtfNearWhiteFilter.Process(source, cancelledOutput, 6, cancelled.Token), "File processing observes cancellation");
            }
            Check(File.ReadAllBytes(cancelledOutput).SequenceEqual(sentinel), "Cancelled filter keeps any previous output intact");

            SavePng(bgrSource, BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgr24, null,
                new byte[] { 255,255,255, 253,253,253 }, 6));
            DtfNearWhiteFilter.Process(bgrSource, output, 6);
            Check(Bgra(ReadPng(output)).SequenceEqual(new byte[] { 255,255,255,255, 255,255,255,255 }),
                "Opaque RGB PNG source remains exactly white with an opaque alpha channel");
            Check(Directory.GetFiles(workspace).All(path => owned.Contains(path)), "Completed file filter leaves no staging files");
        }
        finally
        {
            foreach (string path in owned) if (File.Exists(path)) File.Delete(path);
            // Remove only this invocation's known, now-empty directory.
            if (Directory.Exists(workspace)) Directory.Delete(workspace, false);
        }
    }

    private static void SavePng(string path, BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path)) encoder.Save(stream);
    }
    private static BitmapSource ReadPng(string path)
    {
        using (var stream = File.OpenRead(path))
        {
            var bitmap = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            bitmap.Freeze(); return bitmap;
        }
    }
    private static byte[] Bgra(BitmapSource source)
    {
        BitmapSource bitmap = source.Format == PixelFormats.Bgra32 ? source :
            new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels;
    }
}
