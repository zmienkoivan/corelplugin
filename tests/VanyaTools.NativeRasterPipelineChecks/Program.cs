using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal static class CorelApp { internal static object Get() => throw new Exception("Corel must not be contacted by file-pipeline checks"); }
    internal static class Log { internal static void Info(string text) { } internal static void Error(string text, Exception error) { } }
    internal sealed class DtfCorrectionOptions
    {
        internal bool RemoveSemiTransparent, SmoothStairs, Outline, WhitenNearWhite;
        internal int Threshold = 130, StairsScale = 1, NearWhiteTolerance = 6;
        internal double RadiusMm = 0, ExpansionMm = 25.4 / 400;
        internal Color Color = Colors.White;
    }
    internal sealed class DtfCorrectionResult
    { internal string OutputPath; internal Tuple<double, double> OffsetMm = Tuple.Create(0.0, 0.0); }
}

internal static class Program
{
    private static int _checks;
    private static string _workspace;
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); _checks++; }
    private static void Reject(Action action, string message)
    {
        try { action(); }
        catch (Exception error) when (error is IOException || error is InvalidDataException || error is InvalidOperationException || error is ArgumentException)
        { _checks++; return; }
        throw new Exception(message);
    }
    private static void Canceled(Action action)
    { try { action(); } catch (OperationCanceledException) { _checks++; return; } throw new Exception("Canceled work was not stopped"); }
    private static string PathFor(string name) => Path.Combine(_workspace, name);
    private static string Hash(string path) { using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path)) return Convert.ToBase64String(sha.ComputeHash(stream)); }
    private static NativeRasterBuffer Read(string preview) => NativeRasterFiles.Read(preview + NativeRasterFiles.Suffix, preview);
    private static NativeRasterBuffer Fixture(int mode)
    {
        byte[] alpha = { 255, 255, 255, 128, 0, 130 };
        byte[] colors = mode == 4 ? new byte[] { 255,255,255, 253,253,253, 70,40,10, 254,254,254, 253,253,253, 255,255,255 } :
            mode == 2 ? new byte[] { 255,253,248,254,253,255 } :
            new byte[] { 0,0,0,0, 2,0,2,2, 20,3,7,50, 0,1,0,0, 2,0,2,2, 0,0,0,0 };
        return new NativeRasterBuffer(3, 2, mode, 400, 400, colors, alpha, mode == 5 ? new[] { 2,0,3,1 } : null);
    }
    private static byte[] Preview(string path, out int width, out int height)
    {
        BitmapSource image;
        using (var stream = File.OpenRead(path)) { image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad); image.Freeze(); }
        if (image.Format != PixelFormats.Bgra32) image = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        width = image.PixelWidth; height = image.PixelHeight;
        var data = new byte[checked(width * height * 4)]; image.CopyPixels(data, width * 4, 0); return data;
    }
    private static void CheckWhitePreview(string path, int pixel)
    {
        byte[] data = Preview(path, out _, out _);
        Check(data[pixel * 4] == 255 && data[pixel * 4 + 1] == 255 && data[pixel * 4 + 2] == 255, "Visible source white must remain exactly RGB 255 in PNG preview");
    }
    private static void EqualBuffer(NativeRasterBuffer expected, NativeRasterBuffer actual)
    {
        Check(expected.Width == actual.Width && expected.Height == actual.Height && expected.Mode == actual.Mode, "Native size and model round-trip");
        Check(expected.DpiX == actual.DpiX && expected.DpiY == actual.DpiY, "Native DPI round-trip");
        Check(expected.Colors.SequenceEqual(actual.Colors) && expected.Alpha.SequenceEqual(actual.Alpha), "Every native channel and alpha sample round-trips");
        Check(expected.CmykOrder == null ? actual.CmykOrder == null : expected.CmykOrder.SequenceEqual(actual.CmykOrder), "CMYK order round-trips without permutation");
    }

    private static void FileAndFilterPipeline(int mode)
    {
        var source = Fixture(mode); byte[] colors = (byte[])source.Colors.Clone(), alpha = (byte[])source.Alpha.Clone();
        string input = PathFor("source-" + mode + ".png"); NativeRasterCorrection.Save(input, source, CancellationToken.None);
        string pngHash = Hash(input), nativeHash = Hash(input + NativeRasterFiles.Suffix);
        EqualBuffer(source, Read(input)); CheckWhitePreview(input, 0);
        string copy = PathFor("copy-" + mode + ".png");
        var pass = NativeRasterCorrection.Process(input, copy, new DtfCorrectionOptions(), CancellationToken.None);
        EqualBuffer(source, Read(copy)); Check(pass.OutputPath == copy && pass.OffsetMm.Item1 == 0 && pass.OffsetMm.Item2 == 0, "No-op processing preserves placement");
        CheckWhitePreview(copy, 0);

        string whitened = PathFor("whitened-" + mode + ".png");
        NativeRasterCorrection.Process(input, whitened, new DtfCorrectionOptions { WhitenNearWhite = true }, CancellationToken.None);
        var white = Read(whitened);
        Check(white.Mode == mode && white.IsPureWhite(0) && white.IsPureWhite(1) && white.IsPureWhite(3), "Whitening uses exact white in the original model");
        Check(white.Alpha.SequenceEqual(alpha), "Whitening preserves every alpha sample");
        Check(white.Colors.Skip(2 * source.Channels).Take(source.Channels).SequenceEqual(colors.Skip(2 * source.Channels).Take(source.Channels)), "Non-near-white native color is untouched");
        Check(white.Colors.Skip(4 * source.Channels).Take(source.Channels).SequenceEqual(colors.Skip(4 * source.Channels).Take(source.Channels)), "Invisible near-white color is untouched");
        CheckWhitePreview(whitened, 1); CheckWhitePreview(whitened, 3);

        string threshold = PathFor("threshold-" + mode + ".png");
        NativeRasterCorrection.Process(input, threshold, new DtfCorrectionOptions { RemoveSemiTransparent = true, Threshold = 130 }, CancellationToken.None);
        var opaque = Read(threshold);
        Check(opaque.Mode == mode && opaque.Colors.SequenceEqual(colors), "Alpha removal does not rewrite color samples or model");
        Check(opaque.Alpha.SequenceEqual(new byte[] {255,255,255,0,0,255}), "Alpha threshold remains inclusive and binary");
        CheckWhitePreview(threshold, 0);

        string outlined = PathFor("outlined-" + mode + ".png");
        var outlineResult = NativeRasterCorrection.Process(input, outlined, new DtfCorrectionOptions { Outline = true, Color = Colors.Red }, CancellationToken.None);
        var outline = Read(outlined); int ox = (int)Math.Round(outlineResult.OffsetMm.Item1 * 400 / 25.4), oy = (int)Math.Round(outlineResult.OffsetMm.Item2 * 400 / 25.4);
        Check(outline.Mode == mode && outline.Width > source.Width && outline.Height > source.Height, "Outline grows a raster in its source model");
        for (int p = 0; p < source.Alpha.Length; p++) if (alpha[p] != 0)
        {
            int target = (p / source.Width + oy) * outline.Width + p % source.Width + ox;
            Check(outline.Alpha[target] == alpha[p] && outline.Colors.Skip(target * source.Channels).Take(source.Channels).SequenceEqual(colors.Skip(p * source.Channels).Take(source.Channels)),
                "Outline preserves visible source color and alpha samples at the reported origin");
        }
        CheckWhitePreview(outlined, oy * outline.Width + ox);
        byte[] expectedRed = mode == 4 ? new byte[] {0,0,255} : mode == 2 ? new byte[] {76} : new byte[] {255,0,0,255};
        Check(Enumerable.Range(0, outline.Alpha.Length).Any(p => outline.Alpha[p] != 0 &&
            (p % outline.Width < ox || p / outline.Width < oy || p % outline.Width >= ox + 3 || p / outline.Width >= oy + 2) &&
            outline.Colors.Skip(p * source.Channels).Take(source.Channels).SequenceEqual(expectedRed)), "New outline color is encoded into native channels, including calibrated CMYK order");
        string solidPath = PathFor("solid-" + mode + ".png"), smoothPath = PathFor("smooth-" + mode + ".png");
        var solid = source.Copy();
        for (int p = 0; p < solid.Alpha.Length; p++) { solid.SetPureWhite(p); solid.Alpha[p] = 255; }
        NativeRasterCorrection.Save(solidPath, solid, CancellationToken.None);
        NativeRasterCorrection.Process(solidPath, smoothPath, new DtfCorrectionOptions { SmoothStairs = true, StairsScale = 2 }, CancellationToken.None);
        var smooth = Read(smoothPath);
        Check(smooth.Mode == mode && Enumerable.Range(0, smooth.Alpha.Length).All(p => smooth.Alpha[p] == 0 || smooth.IsPureWhite(p)),
            "Smoothing a white raster cannot turn native white into near white");
        CheckWhitePreview(smoothPath, Enumerable.Range(0, smooth.Alpha.Length).First(p => smooth.Alpha[p] != 0));
        Check(Hash(input) == pngHash && Hash(input + NativeRasterFiles.Suffix) == nativeHash && source.Colors.SequenceEqual(colors) && source.Alpha.SequenceEqual(alpha),
            "Processing never mutates source files or source buffer");
    }

    private static void IntegrityAndCancellation()
    {
        string source = PathFor("integrity.png"); NativeRasterCorrection.Save(source, Fixture(5), CancellationToken.None);
        byte[] original = File.ReadAllBytes(source + NativeRasterFiles.Suffix); string originalPngHash = Hash(source), originalNativeHash = Hash(source + NativeRasterFiles.Suffix);
        string wrong = PathFor("different-preview.png"); File.WriteAllBytes(wrong, File.ReadAllBytes(source).Concat(new byte[] { 1 }).ToArray());
        Reject(() => NativeRasterFiles.Read(source + NativeRasterFiles.Suffix, wrong), "Mismatched PNG must not use another raster's channels");
        foreach (int count in new[] {0, 1, 7, 36, original.Length / 2})
        {
            string path = PathFor("truncated-" + count); File.WriteAllBytes(path, original.Take(count).ToArray());
            Reject(() => NativeRasterFiles.Read(path, source), "Truncated native metadata or payload must fail closed");
        }
        byte[] corrupted = (byte[])original.Clone(); corrupted[0] ^= 1;
        string bad = PathFor("bad-magic"); File.WriteAllBytes(bad, corrupted);
        Reject(() => NativeRasterFiles.Read(bad, source), "Corrupted header must fail");
        corrupted = (byte[])original.Clone(); Buffer.BlockCopy(BitConverter.GetBytes(401.0), 0, corrupted, 21, 8);
        bad = PathFor("changed-metadata"); File.WriteAllBytes(bad, corrupted);
        Reject(() => NativeRasterFiles.Read(bad, source), "Native checksum binds DPI metadata as well as samples");
        int payload;
        using (var memory = new MemoryStream(original)) using (var reader = new BinaryReader(memory))
        { reader.ReadBytes(9 + 4 * 3 + 8 * 2); int order = reader.ReadInt32(); reader.ReadBytes(order * 4 + 64); payload = (int)memory.Position; }
        byte[] unpacked;
        using (var memory = new MemoryStream(original, payload, original.Length - payload))
        using (var deflate = new DeflateStream(memory, CompressionMode.Decompress))
        using (var expanded = new MemoryStream()) { deflate.CopyTo(expanded); unpacked = expanded.ToArray(); }
        unpacked[0] ^= 1;
        string tampered = PathFor("changed-native-samples");
        using (var memory = File.Create(tampered))
        { memory.Write(original, 0, payload); using (var deflate = new DeflateStream(memory, CompressionLevel.Fastest)) deflate.Write(unpacked, 0, unpacked.Length); }
        Reject(() => NativeRasterFiles.Read(tampered, source), "Valid recompressed but changed CMYK bytes must fail checksum validation");
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Canceled(() => NativeRasterFiles.Read("does-not-exist", "does-not-exist", cancel.Token));
            Canceled(() => NativeRasterFiles.Write(PathFor("cancel.native"), "does-not-exist", Fixture(4), cancel.Token));
            Canceled(() => NativeRasterCorrection.Process(source, PathFor("cancel.png"), new DtfCorrectionOptions(), cancel.Token));
            Canceled(() => NativeRasterCorrection.Save(source, Fixture(4), cancel.Token));
        }
        Check(Hash(source) == originalPngHash && Hash(source + NativeRasterFiles.Suffix) == originalNativeHash, "Invalid and canceled work cannot alter the source pair");
    }

    private static void AtomicOverwrite()
    {
        string target = PathFor("overwrite.png"); var before = Fixture(5); NativeRasterCorrection.Save(target, before, CancellationToken.None);
        string previewHash = Hash(target), nativeHash = Hash(target + NativeRasterFiles.Suffix);
        var replacement = Fixture(4);
        using (var locked = File.Open(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => NativeRasterCorrection.Save(target, replacement, CancellationToken.None), "Locked PNG overwrite must fail");
        Check(Hash(target) == previewHash && Hash(target + NativeRasterFiles.Suffix) == nativeHash, "Locked PNG leaves the prior pair intact");
        using (var locked = File.Open(target + NativeRasterFiles.Suffix, FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => NativeRasterCorrection.Save(target, replacement, CancellationToken.None), "Locked native overwrite must fail");
        Check(Hash(target) == previewHash && Hash(target + NativeRasterFiles.Suffix) == nativeHash, "Second rename failure restores the previous PNG and native pair");
        EqualBuffer(before, Read(target));
        NativeRasterCorrection.Save(target, replacement, CancellationToken.None); EqualBuffer(replacement, Read(target));
        Check(!Directory.GetFiles(_workspace).Any(path => path.EndsWith(".tmp") || path.EndsWith(".bak")), "Successful commit and rollback remove only their owned staging files");
    }

    private static void LongParentPaths()
    {
        int parentLength = Math.Max(190, _workspace.Length + 12);
        string parent = Path.Combine(_workspace, new string('l', parentLength - _workspace.Length - 1));
        Directory.CreateDirectory(parent);
        string source = Path.Combine(parent, "processed-output.png"), result = Path.Combine(parent, "processed-result.png");
        Check(source.Length + NativeRasterFiles.Suffix.Length < 260, "Long test final filenames stay within classic MAX_PATH");
        var fixture = Fixture(5); NativeRasterCorrection.Save(source, fixture, CancellationToken.None); EqualBuffer(fixture, Read(source));
        string before = Hash(source), beforeNative = Hash(source + NativeRasterFiles.Suffix);
        NativeRasterCorrection.Process(source, result, new DtfCorrectionOptions { WhitenNearWhite = true }, CancellationToken.None);
        Check(Read(result).Mode == 5 && Read(result).IsPureWhite(1), "Processing stages PNG and native files successfully under a long parent");
        Check(Hash(source) == before && Hash(source + NativeRasterFiles.Suffix) == beforeNative, "Long-path processing preserves its source pair");
        NativeRasterCorrection.Save(result, Fixture(4), CancellationToken.None); EqualBuffer(Fixture(4), Read(result));
        Check(!Directory.GetFiles(parent).Any(path => path.EndsWith(".tmp") || path.EndsWith(".bak")), "Long-path writes and overwrite leave no staging or backup files");
    }

    [STAThread]
    private static int Main()
    {
        string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        _workspace = Path.Combine(parent, "Vanya-NativeRasterPipelineChecks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        try
        {
            foreach (int mode in new[] { 4, 5, 2 }) FileAndFilterPipeline(mode);
            IntegrityAndCancellation(); AtomicOverwrite(); LongParentPaths();
            Console.WriteLine("Native raster pipeline checks passed: " + _checks + ". Real PNG/native pairs; no Corel or network."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            if (Directory.Exists(_workspace) && String.Equals(Path.GetDirectoryName(Path.GetFullPath(_workspace)), parent, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(_workspace).StartsWith("Vanya-NativeRasterPipelineChecks-", StringComparison.Ordinal)) Directory.Delete(_workspace, true);
        }
    }
}
