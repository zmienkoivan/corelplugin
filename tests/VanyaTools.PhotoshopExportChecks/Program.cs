using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal static class Log { public static void Info(string value) { } public static void Error(string value, Exception error) { } }
    // The actual native transfer has its own PrintSelectionChecks. Here the
    // boundary substitutes a normal document without invoking Corel/clipboard.
    internal sealed class PrintSelectionWorkspace : IDisposable
    {
        private readonly FakeApp _app;
        public static int Creates, Disposes;
        public object Document { get; }
        private PrintSelectionWorkspace(FakeApp app) { _app = app; Document = app.Transferred; app.ActiveDocument = Document; }
        public static PrintSelectionWorkspace Create(object app, object doc, object page, object range)
        { Creates++; return new PrintSelectionWorkspace((FakeApp)app); }
        public void Dispose() { Disposes++; _app.ActiveDocument = _app.Source; }
    }
}

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static void Throws(Action action, string message)
    { try { action(); } catch { _checks++; return; } throw new Exception("Expected rejection: " + message); }
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "Vanya-Photoshop-Checks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Plans(); Capture(root); Failures(root); Launcher(root);
            Console.WriteLine("Photoshop export checks passed: " + _checks + ". No Corel or Photoshop launched.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally
        {
            // Only exact files produced in this GUID directory; no recursive deletion.
            foreach (string file in Directory.GetFiles(root)) File.Delete(file);
            Directory.Delete(root, false);
        }
    }
    private static void Plans()
    {
        foreach (int dpi in new[] { 150, 300, 400, 600 })
        {
            var plan = PhotoshopExportPlan.Build(25.4, 12.7, dpi);
            Check(plan.Width == dpi && plan.Height == dpi / 2 && plan.Dpi == dpi, "Physical size or DPI changed");
        }
        foreach (double invalid in new[] { 0, -1, Double.NaN, Double.PositiveInfinity, Double.MaxValue, 100000.0 })
            Throws(() => PhotoshopExportPlan.Build(invalid, 20, 300), "invalid dimensions");
        Throws(() => PhotoshopExportPlan.Build(25.4, 12.7, 96), "unsupported DPI");
        Throws(() => PhotoshopExportPlan.Build(1000, 1000, 400), "pixel limit, no silent downscale");
    }
    private static void Capture(string root)
    {
        foreach (bool selection in new[] { true, false })
        {
            var app = new FakeApp { Optimization = true };
            string path = Path.Combine(root, selection ? "выделение с пробелами.png" : "страница.png");
            long estimate = 0;
            var plan = PhotoshopPngExport.Export(app, path, 300, selection, value => estimate = value);
            var doc = selection ? app.Transferred : app.Source;
            Check(plan.Width == 30 && plan.Height == 15 && estimate == 450, "Capture dimensions or memory check");
            Check(doc.LastRange == CorelConstants.CdrCurrentPage && doc.LastFormat == CorelConstants.CdrPng, "Wrong export scope/format");
            Check(doc.LastName is BStrWrapper && doc.Finishes == 1, "Native BSTR or filter Finish missing");
            Check(app.Options.Transparent && app.Options.UseColorProfile && app.Options.ImageType == CorelConstants.CdrRgbColorImage, "Transparency/RGB/document profile not requested");
            Check(app.Options.ResolutionX == 300 && app.Options.ResolutionY == 300 && !app.Options.Overwrite, "Export settings changed");
            Check(app.Source.Unit == 7 && app.Optimization && ReferenceEquals(app.ActiveDocument, app.Source), "Source context not restored");
            Check(app.Source.ExportCalls == (selection ? 0 : 1), "Focused selection exported in original document");
            using (var stream = File.OpenRead(path))
            {
                var image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                byte[] pixel = new byte[4]; image.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                Check(pixel[0] == 31 && pixel[1] == 65 && pixel[2] == 129 && pixel[3] == 127, "RGBA pixel changed");
            }
        }
        Check(PrintSelectionWorkspace.Creates == 1 && PrintSelectionWorkspace.Disposes == 1, "Workspace not limited to selection or not disposed");
    }
    private static void Failures(string root)
    {
        foreach (string mode in new[] { "export", "null-filter", "finish", "missing", "corrupt", "size", "dpi" })
        {
            var app = new FakeApp { Failure = mode, Optimization = true };
            string path = Path.Combine(root, "failed-" + mode + ".png");
            Throws(() => PhotoshopPngExport.Export(app, path, 300, true, null), mode);
            Check(!File.Exists(path), "Invalid output left behind: " + mode);
            Check(app.Source.Unit == 7 && app.Optimization && ReferenceEquals(app.ActiveDocument, app.Source), "Failure lost original context: " + mode);
        }
        string existing = Path.Combine(root, "existing.png"); File.WriteAllText(existing, "keep");
        Throws(() => PhotoshopPngExport.Export(new FakeApp(), existing, 300, true, null), "existing output");
        Check(File.ReadAllText(existing) == "keep", "Existing output overwritten/deleted");
        var rejected = new FakeApp();
        Throws(() => PhotoshopPngExport.Export(rejected, Path.Combine(root, "memory.png"), 300, true,
            _ => throw new InvalidOperationException("memory")), "memory budget");
        Check(rejected.Source.Unit == 7 && ReferenceEquals(rejected.ActiveDocument, rejected.Source) && rejected.Transferred.ExportCalls == 0, "Memory rejection changed document");
        var empty = new FakeApp(); empty.Selection.Count = 0;
        Throws(() => PhotoshopPngExport.Export(empty, Path.Combine(root, "empty.png"), 300, true, null), "empty selection");
    }
    private static void Launcher(string root)
    {
        string exe = Path.Combine(root, "Photoshop.exe"), png = Path.Combine(root, "выделение с пробелами.png");
        File.WriteAllText(exe, "stub, never execute");
        Check(PhotoshopLauncher.Find(exe) == exe, "Explicit Photoshop ignored");
        var start = PhotoshopLauncher.StartInfo(exe, png);
        Check(start.FileName == exe && start.Arguments == "\"" + png + "\"" && !start.UseShellExecute, "Quoted direct launch lost Unicode/spaces");
        Check(start.WorkingDirectory == root, "Working directory changed");
        Throws(() => PhotoshopLauncher.StartInfo(exe, Path.Combine(root, "missing.png")), "missing image");
        Throws(() => PhotoshopLauncher.StartInfo("", png), "missing exe");
        Throws(() => PhotoshopLauncher.NormalizeExecutable("Photoshop.exe"), "relative path");
        Throws(() => PhotoshopLauncher.NormalizeExecutable(Path.Combine(root, "cmd.exe")), "wrong exe");
        Throws(() => PhotoshopLauncher.NormalizeExecutable(exe + " --flag"), "command text");
    }
}

public sealed class FakeApp
{
    public FakeApp() { Source = new FakeDocument(this); Transferred = new FakeDocument(this); ActiveDocument = Source; }
    public string Failure;
    public FakeDocument Source, Transferred;
    public object ActiveDocument { get; set; }
    public FakeSelection Selection { get; } = new FakeSelection();
    public FakeSelection ActiveSelectionRange => Selection;
    public bool Optimization { get; set; }
    public FakeOptions Options;
    public object CreateStructExportOptions() => Options = new FakeOptions();
    public object CreateStructPaletteOptions() => new object();
}
public sealed class FakeSelection
{
    public int Count { get; set; } = 2;
    public void GetBoundingBox(out double x, out double y, out double width, out double height, bool outline)
    { x = 100; y = -200; width = 2.54; height = 1.27; if (!outline) throw new Exception("Outline excluded"); }
}
public sealed class FakePage { public double SizeWidth => 2.54; public double SizeHeight => 1.27; }
public sealed class FakeOptions
{
    public int SizeX { get; set; } public int SizeY { get; set; }
    public int ResolutionX { get; set; } public int ResolutionY { get; set; }
    public int ImageType { get; set; } public int AntiAliasingType { get; set; }
    public bool Transparent { get; set; } public bool Overwrite { get; set; } public bool UseColorProfile { get; set; }
}
public sealed class FakeDocument
{
    private readonly FakeApp _app;
    public FakeDocument(FakeApp app) { _app = app; }
    public int Unit { get; set; } = 7;
    public FakePage ActivePage { get; } = new FakePage();
    public int ExportCalls, Finishes, LastRange, LastFormat;
    public object LastName;
    public object ExportEx(object name, int format, int range, FakeOptions options, object palette)
    {
        ExportCalls++; LastName = name; LastRange = range; LastFormat = format;
        if (_app.Failure == "export") throw new Exception("Injected export error");
        return _app.Failure == "null-filter" ? null : new FakeFilter(_app, this, Convert.ToString(((BStrWrapper)name).WrappedObject), options);
    }
}
public sealed class FakeFilter
{
    private readonly FakeApp _app; private readonly FakeDocument _doc; private readonly string _path; private readonly FakeOptions _options;
    public FakeFilter(FakeApp app, FakeDocument doc, string path, FakeOptions options) { _app = app; _doc = doc; _path = path; _options = options; }
    public void Finish()
    {
        _doc.Finishes++;
        if (_app.Failure == "finish") throw new Exception("Injected filter error");
        if (_app.Failure == "missing") return;
        if (_app.Failure == "corrupt") { File.WriteAllText(_path, "broken"); return; }
        int width = _options.SizeX + (_app.Failure == "size" ? 10 : 0), height = _options.SizeY;
        int dpi = _app.Failure == "dpi" ? 96 : _options.ResolutionX;
        byte[] pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 31; pixels[i + 1] = 65; pixels[i + 2] = 129; pixels[i + 3] = 127; }
        var image = BitmapSource.Create(width, height, dpi, dpi, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(_path)) encoder.Save(stream);
    }
}
