using System;
using System.Linq;
using System.Threading;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static readonly int[] Order = { 2, 0, 3, 1 };
    private static void Check(bool condition, string name)
    { if (!condition) throw new InvalidOperationException(name); _checks++; }
    private static void Throws<T>(Action action, string name) where T : Exception
    {
        try { action(); } catch (T) { _checks++; return; }
        throw new InvalidOperationException(name);
    }
    private static bool Pixel(NativeRasterBuffer image, int x, int y, params byte[] expected) =>
        image.Colors.Skip((y * image.Width + x) * image.Channels).Take(image.Channels).SequenceEqual(expected);
    private static NativeRasterBuffer Uniform(int width, int height, int mode, byte alpha, params byte[] channels)
    {
        int count = width * height;
        var colors = new byte[count * channels.Length];
        for (int pixel = 0; pixel < count; pixel++) Buffer.BlockCopy(channels, 0, colors, pixel * channels.Length, channels.Length);
        return new NativeRasterBuffer(width, height, mode, 254, 127, colors,
            Enumerable.Repeat(alpha, count).ToArray(), mode == 5 ? Order : null);
    }
    private static NativeRasterBuffer Run(NativeRasterBuffer source, NativeRasterFilterOptions options) => NativeRasterFilters.Process(source, options).Buffer;
    private static byte[] Cmyk(byte c, byte m, byte y, byte k)
    {
        var values = new byte[4]; values[Order[0]] = c; values[Order[1]] = m; values[Order[2]] = y; values[Order[3]] = k; return values;
    }

    private static int Main()
    {
        try
        {
            NoOpAndThreshold(); Whitening(); Smoothing(); OutlineAndCrop(); Validation();
            Console.WriteLine("Passed " + _checks + " native raster filter checks. Raw RGB/CMYK/gray arrays; no Corel or UI.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void NoOpAndThreshold()
    {
        var source = new NativeRasterBuffer(4, 1, 5, 300, 200,
            new byte[] { 0,0,0,0, 11,22,33,44, 5,6,7,8, 255,250,245,240 }, new byte[] { 255,129,130,0 }, Order);
        byte[] originalColors = (byte[])source.Colors.Clone(), originalAlpha = (byte[])source.Alpha.Clone();
        var none = NativeRasterFilters.Process(source, new NativeRasterFilterOptions());
        Check(none.Buffer.Mode == 5 && none.Buffer.Colors.SequenceEqual(source.Colors) && none.Buffer.Alpha.SequenceEqual(source.Alpha), "No-op preserves every native CMYK/alpha byte");
        Check(!ReferenceEquals(none.Buffer, source) && !ReferenceEquals(none.Buffer.Colors, source.Colors) && !ReferenceEquals(none.Buffer.Alpha, source.Alpha), "No-op owns detached result planes");
        Check(none.OffsetMm.Item1 == 0 && none.OffsetMm.Item2 == 0 && none.Buffer.DpiX == 300 && none.Buffer.DpiY == 200, "No-op preserves size, DPI and physical placement");
        Check(none.Buffer.CmykOrder.SequenceEqual(Order), "No-op retains calibrated CMYK order");
        var threshold = NativeRasterFilters.Threshold(source, 130);
        Check(threshold.Alpha.SequenceEqual(new byte[] { 255,0,255,0 }), "Alpha threshold includes its boundary and preserves full transparency");
        Check(threshold.Colors.SequenceEqual(originalColors), "Alpha threshold cannot rewrite color channels, including removed or hidden pixels");
        Check(threshold.IsPureWhite(0) && Pixel(threshold, 0, 0, 0,0,0,0), "CMYK zero white stays zero after alpha correction");
        Check(source.Colors.SequenceEqual(originalColors) && source.Alpha.SequenceEqual(originalAlpha), "Alpha correction does not mutate source planes");
        Check(NativeRasterFilters.Threshold(source, 0).Alpha.SequenceEqual(new byte[] {255,255,255,0}), "Zero alpha never becomes visible at threshold zero");
        Check(NativeRasterFilters.Threshold(source, 255).Alpha.SequenceEqual(new byte[] {255,0,0,0}), "Maximum threshold keeps only opaque pixels");
        var rgb = Uniform(2, 1, 4, 127, 255,255,255);
        Check(NativeRasterFilters.Threshold(rgb, 100).Colors.All(value => value == 255), "RGB white remains exactly 255 without rounding");
        var gray = Uniform(2, 1, 2, 127, 255);
        Check(NativeRasterFilters.Threshold(gray, 128).Mode == 2 && NativeRasterFilters.Threshold(gray, 128).Colors.All(value => value == 255), "Grayscale model and white are preserved");
    }

    private static void Whitening()
    {
        byte[] colors = Cmyk(0,0,0,0).Concat(Cmyk(0,0,0,2)).Concat(Cmyk(20,0,0,0)).Concat(Cmyk(0,0,0,2)).ToArray();
        var cmyk = new NativeRasterBuffer(4, 1, 5, 300, 300, colors, new byte[] {255,127,255,0}, Order);
        var result = Run(cmyk, new NativeRasterFilterOptions { WhitenNearWhite = true, NearWhiteTolerance = 6 });
        Check(result.Mode == 5 && result.CmykOrder.SequenceEqual(Order), "Whitening retains CMYK model and nonidentity channel order");
        Check(result.IsPureWhite(0) && result.IsPureWhite(1), "Almost-white becomes native CMYK zero, never RGB bytes");
        Check(result.Alpha.SequenceEqual(cmyk.Alpha), "CMYK whitening keeps alpha exactly");
        Check(Pixel(result, 2, 0, Cmyk(20,0,0,0)) && Pixel(result, 3, 0, Cmyk(0,0,0,2)), "Whitening keeps colored and fully transparent raw pixels");
        Check(cmyk.Colors.SequenceEqual(colors) && !cmyk.IsPureWhite(1), "Whitening never mutates captured native colors");
        var rgb = new NativeRasterBuffer(3, 1, 4, 96, 96, new byte[] {255,255,255, 253,253,253, 248,254,254}, new byte[] {255,127,255});
        var rgbResult = Run(rgb, new NativeRasterFilterOptions { WhitenNearWhite = true });
        Check(Pixel(rgbResult, 0, 0, 255,255,255) && Pixel(rgbResult, 1, 0, 255,255,255) && Pixel(rgbResult, 2, 0, 248,254,254), "RGB whitening obeys inclusive per-channel tolerance");
        var gray = new NativeRasterBuffer(3, 1, 2, 96, 96, new byte[] {255,253,248}, new byte[] {255,127,255});
        var grayResult = Run(gray, new NativeRasterFilterOptions { WhitenNearWhite = true });
        Check(grayResult.Mode == 2 && grayResult.Colors.SequenceEqual(new byte[] {255,255,248}), "Gray whitening stays one native channel");
        Check(Run(rgb, new NativeRasterFilterOptions { WhitenNearWhite = true, NearWhiteTolerance = 0 }).Colors.SequenceEqual(rgb.Colors), "Zero tolerance changes no native colors");
        var supplied = new NativeRasterFilterOptions { WhitenNearWhite = true,
            NearWhitePreviewBgra = value => new byte[] {255,255,255,255, 253,253,253,127, 248,254,254,255} };
        Check(Run(rgb, supplied).Colors.SequenceEqual(rgbResult.Colors), "Supplied pure managed preview gives identical whitening");
    }

    private static void Smoothing()
    {
        var options = new NativeRasterFilterOptions { SmoothStairs = true, StairsScale = 2 };
        var white = Uniform(3, 3, 5, 255, 0,0,0,0);
        var whiteResult = Run(white, options);
        Check(whiteResult.Mode == 5 && whiteResult.Colors.All(value => value == 0), "CMYK white stays exactly zero across Gaussian coverage and interpolation");
        Check(whiteResult.CmykOrder.SequenceEqual(Order), "Smoothing carries the calibrated CMYK order");
        Check(whiteResult.Width == 6 && whiteResult.Height == 6 && whiteResult.DpiX == white.DpiX * 2 && whiteResult.DpiY == white.DpiY * 2, "Stair supersampling scales DPI with pixel dimensions");
        Check(whiteResult.Width / whiteResult.DpiX == white.Width / white.DpiX && whiteResult.Height / whiteResult.DpiY == white.Height / white.DpiY, "Stair smoothing preserves physical dimensions");
        Check(whiteResult.Alpha.Any(value => value > 0 && value < 255), "Preserving-alpha smoothing does not threshold its new soft edge");
        var rgbWhite = Uniform(3, 3, 4, 255, 255,255,255);
        Check(Run(rgbWhite, options).Colors.All(value => value == 255), "RGB white stays exactly 255 during weighted color interpolation");
        var solid = Uniform(3, 3, 5, 255, 9,19,29,39);
        var solidResult = Run(solid, options);
        Check(Enumerable.Range(0, solidResult.Alpha.Length).All(pixel => Pixel(solidResult, pixel % solidResult.Width, pixel / solidResult.Width, 9,19,29,39)), "Each native CMYK channel interpolates independently without a color conversion");
        Check(solid.Colors.SequenceEqual(Uniform(3, 3, 5, 255, 9,19,29,39).Colors), "Smoothing leaves source color planes unchanged");

        var pair = new NativeRasterBuffer(2, 1, 5, 100, 100,
            new byte[] { 0,255,0,100, 200,55,50,0 }, new byte[] {255,127}, Order);
        var pairResult = Run(pair, options);
        Check(pairResult.Colors[4] == 28, "Interpolated native ink uses alpha-weighted samples, not hidden/matted color");
        Check(pairResult.Mode == 5 && pairResult.CmykOrder.SequenceEqual(Order), "Alpha-weighted interpolation retains its native CMYK metadata");
        var binary = Run(Uniform(3, 3, 4, 200, 10,20,30), new NativeRasterFilterOptions { RemoveSemiTransparent = true, Threshold = 130, SmoothStairs = true, StairsScale = 2 });
        Check(binary.Alpha.All(value => value == 0 || value == 255), "Optional alpha threshold retains binary semantics through stair smoothing");
        var budget = Run(rgbWhite, new NativeRasterFilterOptions { SmoothStairs = true, StairsScale = 4, MaximumOutputPixels = 9 });
        Check(budget.Width == 3 && budget.Height == 3, "Output budget prevents an unnecessary supersampling allocation");
    }

    private static void OutlineAndCrop()
    {
        var source = Uniform(1, 1, 5, 127, 10,20,30,40);
        var outlined = NativeRasterFilters.Process(source, new NativeRasterFilterOptions { Outline = true,
            RadiusMm = 0, ExpansionMm = .1, OutlineColor = new byte[] {1,2,3,4} });
        var image = outlined.Buffer;
        Check(image.Width == 7 && image.Height == 7 && image.Mode == 5, "Outline grows a native-model canvas with the established two-pixel safety margin");
        Check(Math.Abs(outlined.OffsetMm.Item1 - .3) < 1e-9 && Math.Abs(outlined.OffsetMm.Item2 - .6) < 1e-9, "Outline placement uses independent X/Y DPI");
        Check(Pixel(image, 3, 3, 10,20,30,40) && image.Alpha[3 * image.Width + 3] == 127, "Outline preserves existing native color and semitransparent alpha exactly");
        Check(Pixel(image, 2, 3, 1,2,3,4) && image.Alpha[3 * image.Width + 2] == 127, "Only the exterior ring gets the supplied native fill channels");
        Check(image.CmykOrder.SequenceEqual(Order) && source.CmykOrder.SequenceEqual(Order), "Outline carries the calibrated native channel order");
        Check(source.Width == 1 && source.Alpha[0] == 127 && Pixel(source, 0, 0, 10,20,30,40), "Outline does not mutate the captured source");
        var defaultWhite = Run(Uniform(1, 1, 5, 255, 0,0,0,0), new NativeRasterFilterOptions { Outline = true, RadiusMm = .1, ExpansionMm = .1 });
        Check(defaultWhite.Colors.All(value => value == 0), "Default CMYK white outline remains ink-free zero in every channel");
        var rgbWhite = Run(Uniform(1, 1, 4, 255, 255,255,255), new NativeRasterFilterOptions { Outline = true, RadiusMm = .1, ExpansionMm = .1 });
        Check(Enumerable.Range(0, rgbWhite.Alpha.Length).Where(pixel => rgbWhite.Alpha[pixel] != 0).All(pixel => Pixel(rgbWhite, pixel % rgbWhite.Width, pixel / rgbWhite.Width, 255,255,255)), "Default RGB white outline remains exactly 255 at visible pixels");
        var threshold = Run(Uniform(1, 1, 5, 200, 0,0,0,0), new NativeRasterFilterOptions { RemoveSemiTransparent = true, Outline = true, RadiusMm = .1, ExpansionMm = .1, Threshold = 130 });
        Check(threshold.Alpha.All(value => value == 0 || value == 255), "The optional final threshold includes newly generated outline pixels");

        var padded = Uniform(8, 8, 5, 0, 7,17,27,37); padded.Alpha[3 * 8 + 4] = 255;
        var crop = NativeRasterFilters.Trim(padded, 2, out int left, out int top);
        Check(left == 2 && top == 1 && crop.Width == 5 && crop.Height == 5, "Visible bounds crop uses the established two-pixel safety margin");
        Check(Pixel(crop, 2, 2, 7,17,27,37) && crop.Alpha[2 * 5 + 2] == 255, "Cropping copies exact source color and alpha samples");
        Check(crop.Mode == 5 && crop.CmykOrder.SequenceEqual(Order) && crop.DpiX == padded.DpiX && crop.DpiY == padded.DpiY, "Cropping preserves model, calibration and DPI");
        var empty = Uniform(4, 5, 4, 0, 3,4,5);
        Check(ReferenceEquals(NativeRasterFilters.Trim(empty, 2, out _, out _), empty), "Fully transparent image retains its original canvas");
    }

    private static void Validation()
    {
        var source = Uniform(1, 1, 5, 127, 0,0,0,0);
        Throws<ArgumentOutOfRangeException>(() => NativeRasterFilters.Threshold(source, -1), "Invalid alpha threshold is rejected");
        Throws<ArgumentException>(() => Run(source, new NativeRasterFilterOptions { Outline = true, OutlineColor = new byte[3] }), "Outline fill must match native channel count");
        Throws<InvalidOperationException>(() => Run(source, new NativeRasterFilterOptions { Outline = true, ExpansionMm = .1, MaximumOutputPixels = 1 }), "Oversized outline is rejected before canvas allocation");
        Throws<InvalidOperationException>(() => Run(source, new NativeRasterFilterOptions { Outline = true, RadiusMm = 2 }), "Excessive smoothing radius is rejected");
        Throws<ArgumentOutOfRangeException>(() => Run(source, new NativeRasterFilterOptions { Outline = true, ExpansionMm = Double.NaN }), "Nonfinite expansion is rejected");
        var wrongMask = DtfNearWhitePixels.CreateMask(new byte[] {253,253,253,255, 253,253,253,255}, 2, 1, 6);
        Throws<ArgumentException>(() => NativeRasterFilters.Whiten(source, wrongMask), "Mismatched whitening geometry is rejected");
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            Throws<OperationCanceledException>(() => NativeRasterFilters.Process(source, new NativeRasterFilterOptions { Outline = true }, cancellation.Token), "Pipeline cancellation is observed before native-plane allocation");
            Throws<OperationCanceledException>(() => NativeRasterFilters.Threshold(source, 130, cancellation.Token), "Threshold cancellation is observed");
        }
        Check(source.IsPureWhite(0) && source.Alpha[0] == 127, "Failures and cancellation keep captured source untouched");
    }
}
