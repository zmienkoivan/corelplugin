using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal static class CorelApp
    {
        internal static int Gets;
        public static dynamic Get() { Gets++; throw new Exception("No live Corel in checks."); }
    }
    internal static class Log { public static void Info(string value) { } public static void Error(string value, Exception error) { } }
}

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception(message); }
    [STAThread]
    private static int Main()
    {
        try
        {
            Plans(); Success(); Failures();
            Console.WriteLine("Order frame capture checks passed: " + _checks + ". Fake Corel only; generated PNGs are temporary and removed.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Plans()
    {
        var small = OrderFrameCapturePlan.Build(2.54, 1.27);
        Check(small.Width == 40 && small.Height == 20 && small.Dpi == 400, "Small capture dimensions/DPI");
        foreach (var size in new[] { new[] { 946.346, 359.425 }, new[] { 1200.0, 1200.0 }, new[] { 10000.0, 100.0 }, new[] { 100.0, 10000.0 } })
        {
            var plan = OrderFrameCapturePlan.Build(size[0], size[1]);
            Check(plan.Width <= 9500 && plan.Height <= 9500 && plan.Width + plan.Height <= 9990 &&
                (long)plan.Width * plan.Height <= 24000000 && plan.Dpi >= 1 && plan.Dpi <= 400, "Plan exceeds frame limits");
        }
        foreach (double bad in new[] { 0.0, -1, Double.NaN, Double.PositiveInfinity, Double.MaxValue })
            Throws(() => OrderFrameCapturePlan.Build(bad, 1), "");
    }
    private static void Success()
    {
        var app = new FakeApp { Optimization = true };
        BitmapSource image = OrderFrameCapture.Capture(app, true);
        Check(image.IsFrozen && image.PixelWidth == 40 && image.PixelHeight == 20, "Frozen detached image not returned");
        Check(app.Document.Unit == 7 && app.Optimization, "Unit/optimization not restored");
        Check(app.Selection.Restores == 1 && app.Selection.IncludeOutline, "Selection restore/outline bounds missing");
        Check(app.Document.ExportCalls == 1 && app.Document.LastName is BStrWrapper && app.Document.LastFormat == 802 && app.Document.LastRange == 2,
            "ExportEx must receive BSTR, PNG and selection scope");
        Check(ReferenceEquals(app.Document.LastOptions, app.Options) && ReferenceEquals(app.Document.LastPalette, app.Palette), "Real option objects missing");
        Check(app.Options.SizeX == 40 && app.Options.SizeY == 20 && app.Options.ResolutionX == 400 && app.Options.ResolutionY == 400,
            "Native size/DPI options differ from plan");
        Check(app.Options.ImageType == 4 && app.Options.AntiAliasingType == 1 && !app.Options.Transparent && app.Options.Overwrite && app.Options.UseColorProfile,
            "RGB/AA/opaque/profile options changed");
        Check(!app.Document.OptimizationAtExport, "Native rendering must run with optimization off");
        Check(!File.Exists(app.Document.LastPath) && !Directory.Exists(Path.GetDirectoryName(app.Document.LastPath)), "Owned PNG workspace leaked");
        byte[] bytes = new byte[image.PixelWidth * image.PixelHeight * 4];
        new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0).CopyPixels(bytes, image.PixelWidth * 4, 0);
        Check(bytes[0] == 11 && bytes[1] == 22 && bytes[2] == 33 && bytes[3] == 255, "Decoded RGB values changed or file remained attached");
        var noRestore = new FakeApp(); OrderFrameCapture.Capture(noRestore, false);
        Check(noRestore.Selection.Restores == 0 && !noRestore.Optimization && noRestore.Document.Unit == 7, "Optional restore contract changed");
        var rounded = new FakeApp { Failure = "rounding" }; OrderFrameCapture.Capture(rounded, true);
        Check(rounded.Document.ExportCalls == 1, "One-pixel native rounding should remain valid");
    }
    private static void Failures()
    {
        foreach (string failure in new[] { "options", "export", "null-filter", "finish", "missing", "corrupt", "wrong-size", "oversized", "document-switch" })
        {
            var app = new FakeApp { Failure = failure, Optimization = true };
            Throws(() => OrderFrameCapture.Capture(app, true), "");
            Check(app.Document.Unit == 7 && app.Optimization, "State not restored after " + failure);
            Check(app.Document.LastPath == null || !File.Exists(app.Document.LastPath) && !Directory.Exists(Path.GetDirectoryName(app.Document.LastPath)),
                "Temp output leaked after " + failure);
            if (failure == "document-switch") Check(app.Selection.Restores == 0 && app.Other.Unit == 7, "Wrong document selection or units touched");
        }
        var empty = new FakeApp(); empty.Selection.Count = 0;
        Throws(() => OrderFrameCapture.Capture(empty, true), "Выделите");
        Check(empty.Document.ExportCalls == 0, "Empty selection exported");
        var invalid = new FakeApp(); invalid.Selection.Left = Double.NaN;
        Throws(() => OrderFrameCapture.Capture(invalid, true), "границы");
        Check(invalid.Document.ExportCalls == 0 && invalid.Document.Unit == 7, "Invalid bounds exported or units changed");
        int before = CorelApp.Gets;
        bool rejectedBeforeCorel = Task.Run(() => {
            try { OrderFrameCapture.Capture(true); } catch (InvalidOperationException) { return true; } return false;
        }).GetAwaiter().GetResult();
        Check(rejectedBeforeCorel && CorelApp.Gets == before, "Background call reached Corel before STA guard");
    }
    private static void Throws(Action action, string fragment)
    {
        try { action(); } catch (InvalidOperationException error) { Check(error.Message.Contains(fragment), "Wrong error: " + error.Message); return; }
        throw new Exception("Expected capture/planning rejection");
    }
}

public sealed class FakeApp
{
    public FakeApp() { Document = new FakeDocument(this); Other = new FakeDocument(this); ActiveDocument = Document; }
    public string Failure;
    public readonly FakeDocument Document, Other;
    public object ActiveDocument { get; set; }
    public FakeSelection Selection { get; } = new FakeSelection();
    public FakeSelection ActiveSelectionRange => Selection;
    public bool Optimization { get; set; }
    public FakeOptions Options;
    public FakePalette Palette;
    public object CreateStructExportOptions() { if (Failure == "options") throw new InvalidOperationException("Injected options error"); return Options = new FakeOptions(); }
    public object CreateStructPaletteOptions() => Palette = new FakePalette();
}
public sealed class FakeSelection
{
    public int Count { get; set; } = 49;
    public int Restores;
    public double Left = 1300;
    public bool IncludeOutline;
    public void GetBoundingBox(out double left, out double bottom, out double width, out double height, bool outline)
    { IncludeOutline = outline; left = Left; bottom = -200; width = 2.54; height = 1.27; }
    public void CreateSelection() { Restores++; }
    // These methods exist only as tripwires: the new path must not duplicate/group sources.
    public object Duplicate(double x, double y) => throw new InvalidOperationException("Source Duplicate called");
    public object Group() => throw new InvalidOperationException("Source Group called");
}
public sealed class FakeOptions
{
    public int SizeX { get; set; }
    public int SizeY { get; set; }
    public int ResolutionX { get; set; }
    public int ResolutionY { get; set; }
    public int ImageType { get; set; }
    public int AntiAliasingType { get; set; }
    public bool Transparent { get; set; }
    public bool Overwrite { get; set; }
    public bool UseColorProfile { get; set; }
}
public sealed class FakePalette { }
public sealed class FakeDocument
{
    private readonly FakeApp _app;
    public FakeDocument(FakeApp app) { _app = app; }
    public int Unit { get; set; } = 7;
    public int ExportCalls;
    public object LastName, LastOptions, LastPalette;
    public int LastFormat, LastRange;
    public string LastPath;
    public bool OptimizationAtExport;
    public object ExportEx(object fileName, int format, int range, FakeOptions options, FakePalette palette)
    {
        ExportCalls++; LastName = fileName; LastFormat = format; LastRange = range; LastOptions = options; LastPalette = palette;
        LastPath = Convert.ToString(((BStrWrapper)fileName).WrappedObject); OptimizationAtExport = _app.Optimization;
        if (_app.Failure == "export") throw new InvalidOperationException("Injected export error");
        return _app.Failure == "null-filter" ? null : new FakeFilter(_app, LastPath, options);
    }
    public void BeginCommandGroup(string name) => throw new Exception("Source mutation group called");
}
public sealed class FakeFilter
{
    private readonly FakeApp _app;
    private readonly string _path;
    private readonly FakeOptions _options;
    public FakeFilter(FakeApp app, string path, FakeOptions options) { _app = app; _path = path; _options = options; }
    public void Finish()
    {
        if (_app.Failure == "finish") throw new InvalidOperationException("Injected Finish error");
        if (_app.Failure == "missing") return;
        if (_app.Failure == "corrupt") { File.WriteAllText(_path, "not a PNG"); return; }
        int width = _app.Failure == "oversized" ? 9501 : _app.Failure == "wrong-size" ? _options.SizeX + 10 : _options.SizeX;
        int height = _app.Failure == "oversized" ? 1 : _app.Failure == "rounding" ? _options.SizeY + 1 : _options.SizeY;
        byte[] pixels = new byte[width * height * 3];
        for (int index = 0; index < pixels.Length; index += 3) { pixels[index] = 11; pixels[index + 1] = 22; pixels[index + 2] = 33; }
        var bitmap = BitmapSource.Create(width, height, _options.ResolutionX, _options.ResolutionY, PixelFormats.Bgr24, null, pixels, width * 3);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(_path)) encoder.Save(stream);
        if (_app.Failure == "document-switch") _app.ActiveDocument = _app.Other;
    }
}
