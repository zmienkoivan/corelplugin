using System;
using System.Linq;
using System.Threading;
using VanyaTools.Native;

public sealed class FakeTile
{
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int BytesPerLine { get; set; }
    public int BytesPerPixel { get; set; }
    public int BytesPerTile => _data == null ? 0 : _data.Length;
    public bool ReadOnly { get; set; }
    public bool RejectWrite, RejectRead;
    private byte[] _data;
    public byte[] PixelData { get { if (RejectRead) throw new InvalidOperationException("standalone PixelData getter failed"); return _data == null ? null : (byte[])_data.Clone(); } set { if (RejectWrite) throw new InvalidOperationException("write rejected"); _data = value; } }
    public FakeTile(int left, int top, int width, int height, int bpp, byte[] data, int stride = 0)
    { Left = left; Top = top; Width = width; Height = height; BytesPerPixel = bpp; BytesPerLine = stride == 0 ? width * bpp : stride; _data = data; }
}
public sealed class FakeTileAccess
{
    private readonly FakeTile[] _tiles;
    public FakeTileAccess(FakeTile[] tiles) { _tiles = tiles; }
    public FakeTile this[int index] => _tiles[index - 1];
}
public sealed class FakeTiles
{
    public int Count { get; }
    public FakeTileAccess Item { get; }
    public FakeTiles(params FakeTile[] tiles) { Count = tiles.Length; Item = new FakeTileAccess(tiles); }
}
public sealed class FakeImage
{
    public int Type { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool ReadOnly { get; set; }
    public FakeTiles Tiles { get; set; }
    public FakeImage(int type, int width, int height, params FakeTile[] tiles)
    { Type = type; Width = width; Height = height; Tiles = new FakeTiles(tiles); }
}
public sealed class FakeBitmap
{
    public int Mode => Image.Type;
    public FakeImage Image { get; set; }
    public FakeImage ImageAlpha { get; set; }
    public bool Transparent => ImageAlpha != null;
    public double ResolutionX { get; set; } = 300;
    public double ResolutionY { get; set; } = 400;
    public int Writes, OffsetX, OffsetY, OpaqueWrites;
    public void SetImageData(FakeImage image) { OpaqueWrites++; throw new InvalidOperationException("Native host requires a valid Alpha interface."); }
    public void SetImageData(FakeImage image, FakeImage alpha = null, int offsetX = 0, int offsetY = 0)
    { if (alpha == null) throw new InvalidOperationException("Native host requires a valid Alpha interface."); Materialize(image); Materialize(alpha); Image = image; ImageAlpha = alpha; OffsetX = offsetX; OffsetY = offsetY; Writes++; }
    private static void Materialize(FakeImage image) { for (int i = 1; i <= image.Tiles.Count; i++) image.Tiles.Item[i].RejectRead = false; }
}
public sealed class FakeColor { public int Mode = 5; public byte[] Samples; public int[] Cmyk; public FakeColor(params int[] cmyk) { Cmyk = cmyk; } }
public sealed class FakeApplication
{
    public FakeColor CreateCMYKColor(int c, int m, int y, int k) => new FakeColor(c, m, y, k);
    public FakeColor CreateRGBColor(int r, int g, int b) => new FakeColor { Mode = 4, Samples = new[] { (byte)b, (byte)g, (byte)r } };
    public FakeColor CreateGrayColor(int gray) => new FakeColor { Mode = 2, Samples = new[] { (byte)gray } };
}
public sealed class FakeDocument
{
    public int[] Order = { 3, 2, 1, 0 };
    public bool ReadOnly, RejectWrite, RejectStandaloneRead = true;
    public FakeApplication Application { get; } = new FakeApplication();
    public int Calls, OmittedFillCalls, ExplicitFillCalls, SmallColorSamples;
    public FakeImage CreateImage(int mode, int width, int height)
    { OmittedFillCalls++; throw new InvalidOperationException("Native host requires a valid FillColor interface."); }
    public FakeImage CreateImage(int mode, int width, int height, FakeColor color)
    {
        if (color == null) throw new InvalidOperationException("Explicit null interface argument is not a supported COM call.");
        if (color.Mode != mode) throw new InvalidOperationException("Fill model mismatch");
        ExplicitFillCalls++; if (width < 8 || height < 8) SmallColorSamples++;
        return MakeImage(mode, width, height, color);
    }
    private FakeImage MakeImage(int mode, int width, int height, FakeColor color)
    {
        Calls++;
        int channels = mode == 5 ? 4 : mode == 4 ? 3 : 1;
        var data = new byte[width * height * channels];
        if (color != null && mode == 5)
            for (int p = 0; p < width * height; p++)
                for (int channel = 0; channel < 4; channel++) data[p * 4 + Order[channel]] = (byte)(color.Cmyk[channel] * 255 / 100);
        else if (color != null)
            for (int p = 0; p < width * height; p++) Buffer.BlockCopy(color.Samples, 0, data, p * channels, channels);
        return new FakeImage(mode, width, height, new FakeTile(0, height - 1, width, height, channels, data)
            { ReadOnly = ReadOnly, RejectWrite = RejectWrite, RejectRead = RejectStandaloneRead && mode == 4 }) { ReadOnly = ReadOnly };
    }
}

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); _checks++; }
    private static void Reject(Action work, string message) { try { work(); } catch (InvalidOperationException) { _checks++; return; } throw new Exception(message); }
    [STAThread]
    private static int Main()
    {
        try
        {
            RoundTrips(); Validation(); CalibrationAndPreview(); ThreadGuard();
            Console.WriteLine("Native raster model checks passed: " + _checks + ". Fake Corel objects only."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static FakeBitmap Bitmap(int mode, int width, int height, byte[] nativeBytes, int bpp = 0)
    {
        int channels = mode == 5 ? 4 : mode == 4 ? 3 : 1;
        return new FakeBitmap { Image = new FakeImage(mode, width, height,
            new FakeTile(0, height - 1, width, height, bpp == 0 ? channels : bpp, nativeBytes)) };
    }
    private static void RoundTrips()
    {
        foreach (int mode in new[] { 2, 4, 5 })
        {
            int channels = NativeRasterBuffer.ChannelCount(mode);
            byte[] bottomUp = Enumerable.Range(0, 4 * channels).Select(value => (byte)(value * 13)).ToArray();
            var source = Bitmap(mode, 2, 2, bottomUp);
            source.ImageAlpha = new FakeImage(2, 2, 2, new FakeTile(0, 1, 2, 2, 1, new byte[] { 0, 255, 34, 202 }));
            NativeRasterBuffer buffer = NativeRasterModelIO.Capture(source);
            Check(buffer.Mode == mode && buffer.DpiX == 300 && buffer.DpiY == 400, "Model/DPI must remain native");
            Check(buffer.ColorBytes.Take(channels * 2).SequenceEqual(bottomUp.Skip(channels * 2)), "Raw source orientation is top-down");
            Check(buffer.AlphaBytes.SequenceEqual(new byte[] { 34, 202, 0, 255 }), "Alpha orientation preserved");
            var destination = Bitmap(4, 1, 1, new byte[3]);
            NativeRasterModelIO.Apply(new FakeDocument(), destination, buffer, -3, 4);
            NativeRasterBuffer copied = NativeRasterModelIO.Capture(destination);
            Check(copied.Mode == mode && copied.ColorBytes.SequenceEqual(buffer.ColorBytes), "All native color channels round-trip exactly");
            Check(copied.AlphaBytes.SequenceEqual(buffer.AlphaBytes), "All alpha bytes round-trip exactly");
            Check(destination.OffsetX == -3 && destination.OffsetY == 4 && source.Writes == 0, "Apply affects target only and passes offsets");
        }
        var padded = Bitmap(4, 1, 2, new byte[] { 1, 2, 3, 98, 255, 255, 255, 97 }, 4);
        NativeRasterBuffer rgb = NativeRasterModelIO.Capture(padded);
        Check(rgb.ColorBytes.SequenceEqual(new byte[] { 255, 255, 255, 1, 2, 3 }), "RGB tile padding is not a fourth channel");
        Check(rgb.AlphaBytes.All(value => value == 255), "Opaque source gets exact alpha255");
        var opaque = Bitmap(5, 1, 1, new byte[4]);
        var opaqueDocument = new FakeDocument();
        NativeRasterModelIO.Apply(opaqueDocument, opaque, rgb);
        Check(opaque.Mode == 4 && NativeRasterModelIO.Capture(opaque).AlphaBytes.All(value => value == 255) && opaque.OpaqueWrites == 0,
            "Opaque output supplies a real all-255 mask instead of a null or missing interface");
        Check(opaqueDocument.OmittedFillCalls == 0 && opaqueDocument.ExplicitFillCalls == 2, "Apply must supply valid same-model FillColor interfaces for both planes");
        NativeRasterModelIO.Apply(opaqueDocument, opaque, rgb, -2, 3);
        Check(opaque.OffsetX == -2 && opaque.OffsetY == 3 && NativeRasterModelIO.Capture(opaque).AlphaBytes.All(value => value == 255),
            "Opaque nonzero offsets preserve exact alpha using the same explicit-interface protocol");
    }
    private static void Validation()
    {
        var wrong = Bitmap(5, 1, 1, new byte[3]);
        Reject(() => NativeRasterModelIO.Capture(wrong), "Short CMYK pixels accepted");
        var missing = Bitmap(4, 2, 1, new byte[3]);
        missing.Image.Tiles = new FakeTiles(new FakeTile(0, 0, 1, 1, 3, new byte[3]));
        Reject(() => NativeRasterModelIO.Capture(missing), "Missing tile coverage accepted");
        var alpha = Bitmap(4, 1, 1, new byte[3]);
        alpha.ImageAlpha = new FakeImage(2, 2, 1, new FakeTile(0, 0, 2, 1, 1, new byte[2]));
        Reject(() => NativeRasterModelIO.Capture(alpha), "Mismatched alpha accepted");
        Reject(() => NativeRasterModelIO.Capture(Bitmap(4, 2, 1, new byte[6]), 1), "Pixel cap ignored");
        Reject(() => NativeRasterModelIO.Capture(Bitmap(6, 1, 1, new byte[1])), "Unsupported model converted silently");
        var target = Bitmap(4, 1, 1, new byte[3]);
        NativeRasterBuffer source = NativeRasterModelIO.Capture(target);
        Reject(() => NativeRasterModelIO.Apply(new FakeDocument { ReadOnly = true }, target, source), "Readonly image accepted");
        Reject(() => NativeRasterModelIO.Apply(new FakeDocument { RejectWrite = true }, target, source), "Failed tile write ignored");
        Check(target.Writes == 0, "No SetImageData until all images are complete");
        var negative = Bitmap(4, 1, 1, new byte[3]); negative.Image.Tiles.Item[1].BytesPerLine = -3;
        Reject(() => NativeRasterModelIO.Capture(negative), "Negative stride accepted");
    }
    private static void CalibrationAndPreview()
    {
        var document = new FakeDocument();
        int[] order = NativeRasterModelIO.CalibrateCmykOrder(document, new FakeApplication());
        Check(order.SequenceEqual(new[] { 3, 2, 1, 0 }) && document.Calls == 4, "CMYK mapping derives from four native primaries");
        Check(document.SmallColorSamples == 0 && document.ExplicitFillCalls == 4, "Calibration uses aligned non-null native color samples");
        var cmyk = new NativeRasterBuffer(2, 1, 5, 300, 300, new byte[] { 0, 0, 0, 0, 0, 0, 0, 255 }, new byte[] { 123, 255 }, order);
        byte[] preview = cmyk.ToPreviewBgra();
        Check(preview.SequenceEqual(new byte[] { 255, 255, 255, 123, 255, 255, 0, 255 }), "CMYK white exact255 and cyan mapping correct");
        Check(cmyk.IsPureWhite(0) && !cmyk.IsPureWhite(1), "Raw CMYK white test uses all-zero channels");
        var copied = cmyk.Copy(); copied.SetPureWhite(1);
        Check(copied.IsPureWhite(1) && !cmyk.IsPureWhite(1) && copied.CmykOrder.SequenceEqual(order), "Managed copy retains order without mutating source");
        var rgb = new NativeRasterBuffer(1, 1, 4, 300, 300, new byte[] { 253, 253, 253 }, new byte[] { 1 });
        Check(rgb.ToPreviewBgra().SequenceEqual(new byte[] { 253, 253, 253, 1 }), "RGB preview preserves exact bytes, without premature whitening");
        rgb.SetPureWhite(0); Check(rgb.IsPureWhite(0) && rgb.ColorBytes.All(value => value == 255), "RGB white setter is exact255");
        Reject(() => new NativeRasterBuffer(1, 1, 5, 300, 300, new byte[4], new byte[1], new[] { 0, 0, 2, 3 }), "Invalid channel permutation accepted");
        Reject(() => new NativeRasterBuffer(1, 1, 5, 300, 300, new byte[4], new byte[1]).ToPreviewBgra(), "Unknown CMYK order guessed");
        document.Order = new[] { 0, 0, 2, 3 };
        Reject(() => NativeRasterModelIO.CalibrateCmykOrder(document, new FakeApplication()), "Ambiguous native CMYK encoding accepted");
    }
    private static void ThreadGuard()
    {
        Exception caught = null;
        var thread = new Thread(() => { try { NativeRasterModelIO.Capture(null); } catch (Exception error) { caught = error; } });
        thread.SetApartmentState(ApartmentState.MTA); thread.Start(); thread.Join();
        Check(caught is InvalidOperationException && caught.Message.Contains("интерфейсе"), "MTA calls blocked before native access");
    }
}
