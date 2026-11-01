using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal static class CorelConstants { internal const int CdrMillimeter = 3, CdrRgbColorImage = 4, CdrNormalAntiAliasing = 1; }
    internal static class CorelApp { internal static dynamic Get() => throw new Exception("Live Corel forbidden in checks"); }
    internal static class Log { internal static void Info(string value) { } internal static void Error(string value, Exception error) { } }
    internal static class NativeRasterCorrection { internal static string CaptureIfBitmap(Action<long> memoryCheck) => null; }
    internal static class RasterCaptureMemory { internal static void EnsureAvailable(long pixels) { } }
    internal static class DockerUiWatchdog { internal static IDisposable Trace(System.Windows.Threading.Dispatcher dispatcher, string text) => null; }
    internal static class OrderFrameCapture { internal static System.Windows.Media.Imaging.BitmapSource Capture(bool restore) => throw new Exception("Capture forbidden"); }
    internal static class NativeBitmapPixelReader { internal static System.Windows.Media.Imaging.BitmapSource Read(object bitmap, int dpi, int pixels, bool white) => throw new Exception("Capture forbidden"); }
}
internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string text) { _checks++; if (!condition) throw new Exception(text); }
    private static byte[] Image(int w, int h, Func<int, int, bool> ink, byte alpha = 255)
    { var pixels = new byte[w * h * 4]; for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (ink(x, y)) pixels[(y * w + x) * 4 + 3] = alpha; return pixels; }
    private static ThinAuditResult Run(byte[] pixels, int w = 100, int h = 100, ThinAuditOptions options = null, double width = 10, double height = 10)
        => ThinElementAudit.Analyze(pixels, w, h, width, height, options ?? new ThinAuditOptions(), CancellationToken.None);
    private static int Kind(ThinAuditResult result, int x = 50, int y = 50, int width = 100)
    { int at = (y * width + x) * 4; return result.Overlay[at + 3] == 0 ? 0 : result.Overlay[at + 2] == 30 ? 3 : result.Overlay[at + 1] == 210 ? 2 : 1; }
    private static int Main()
    {
        try
        {
            var dtf = ThinAuditOptions.Preset(false); var uv = ThinAuditOptions.Preset(true);
            Check(dtf.MinimumMm == .6 && dtf.SafeMm == .8 && dtf.DotMm == 1 && uv.MinimumMm == .4 && uv.SafeMm == .6 && uv.DotMm == .8, "Preset thresholds are wrong");
            foreach (bool vertical in new[] { false, true })
            {
                Check(Kind(Run(Image(100, 100, (x,y) => (vertical ? x : y) >= 49 && (vertical ? x : y) <= 51 && (vertical ? y : x) > 10 && (vertical ? y : x) < 90))) == 1, "Thin line not red");
                Check(Kind(Run(Image(100, 100, (x,y) => (vertical ? x : y) >= 47 && (vertical ? x : y) <= 53 && (vertical ? y : x) > 10 && (vertical ? y : x) < 90))) == 2, "Borderline line not yellow");
                Check(Kind(Run(Image(100, 100, (x,y) => (vertical ? x : y) >= 46 && (vertical ? x : y) <= 54 && (vertical ? y : x) > 10 && (vertical ? y : x) < 90))) == 3, "Near-safe line not green");
            }
            Check(Kind(Run(Image(100, 100, (x,y) => Math.Abs(x-y) / Math.Sqrt(2) <= 1.3 && x > 10 && x < 90))) == 1, "45 degree thin stroke missed");
            Check(Kind(Run(Image(100, 100, (x,y) => Math.Abs(x-y) / Math.Sqrt(2) <= 3.4 && x > 10 && x < 90))) == 2, "45 degree borderline stroke wrong");
            var broad = Run(Image(100, 100, (x,y) => x > 10 && x < 90 && y > 10 && y < 90));
            Check(Kind(broad) == 0 && broad.Red < broad.Foreground * .05, "Audit marked every thick outside edge red");
            var smallDot = Run(Image(100,100,(x,y) => (x-50)*(x-50)+(y-50)*(y-50) <= 16));
            Check(smallDot.SmallDots == 1 && Kind(smallDot) == 1, "Compact dot under 1 mm missed");
            var uvDot = Run(Image(100,100,(x,y) => (x-50)*(x-50)+(y-50)*(y-50) <= 16), options: uv);
            Check(uvDot.SmallDots == 0, "UV dot still uses DTF threshold");
            Check(Run(Image(100,100,(x,y) => true, 100)).Foreground == 0, "Alpha cutoff ignored");
            Check(Run(new byte[100 * 100 * 4]).Red == 0, "Empty image reported warnings");
            var opaque = Enumerable.Repeat((byte)255, 100 * 100 * 4).ToArray();
            for (int y = 0; y < 100; y++) for (int x = 49; x <= 51; x++) { int p = (y*100+x)*4; opaque[p] = opaque[p+1] = opaque[p+2] = 0; }
            Check(Kind(Run(opaque, options: new ThinAuditOptions { IgnoreWhite = true })) == 1, "White-background option did not expose line");
            var original = (byte[])opaque.Clone(); Run(opaque); Check(original.SequenceEqual(opaque), "Audit changed source pixels");
            var anisotropic = Image(100,200,(x,y) => y >= 94 && y <= 107 && x > 10 && x < 90);
            Check(Kind(Run(anisotropic,100,200),50,100) == 2, "Physical anisotropic pixel dimensions ignored");
            bool canceled = false; var token = new CancellationTokenSource(); token.Cancel();
            try { ThinElementAudit.Analyze(opaque,100,100,10,10,dtf,token.Token); } catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Canceled audit kept running");
            bool low = false; try { Run(opaque, width:100); } catch (InvalidOperationException) { low = true; } Check(low, "Low-resolution analysis silently accepted");
            bool invalid = false; try { new ThinAuditOptions { SafeMm = .1 }.Validate(); } catch (InvalidOperationException) { invalid = true; } Check(invalid, "Invalid thresholds accepted");
            Reference();
            CaptureBounds();
            Console.WriteLine("Thin-element checks passed: " + _checks + ". Synthetic masks and fake Corel geometry; no live Corel."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Reference()
    {
        var app = new FakeApp(); ThinElementReference.Insert(app, false, ThinAuditOptions.Preset(false));
        var shapes = app.ActiveDocument.ActiveLayer.Items;
        Check(app.ActiveDocument.Unit == 9 && app.ActiveDocument.Groups == 0 && app.Range.Grouped, "Reference did not restore units/end group");
        var dots = shapes.Where(s => s.Kind == "ellipse").ToArray(); Check(dots.Length == 10, "Wrong circle count");
        for (int i = 0; i < dots.Length; i++) Check(Math.Abs(dots[i].Width - (i+1)/10d) < 1e-10 && dots[i].Fill.Color[3] == 100 && dots[i].Outline.None, "Circle scale/black/outline wrong");
        var diagonal = shapes.Where(s => s.Angle == 45).ToArray();
        Check(diagonal.Length == 10 && diagonal.Select(s => s.Height).SequenceEqual(ThinElementReference.Values()), "Diagonal thickness changed");
        Check(shapes.Any(s => s.Kind == "rect" && s.Width == 10 && s.Height == .2), "10 mm calibration missing");
        Check(shapes.Any(s => (s.Text ?? "").Contains("ДТФ")) && app.Range.Selection, "Reference labels/group selection missing");
        app = new FakeApp(); app.ActiveDocument.ActiveLayer.FailAt = 7;
        bool failed = false; try { ThinElementReference.Insert(app, true, ThinAuditOptions.Preset(true)); } catch (InvalidOperationException) { failed = true; }
        Check(failed && app.ActiveDocument.Unit == 9 && app.ActiveDocument.Groups == 0 && app.ActiveDocument.ActiveLayer.Items.All(s => s.Deleted), "Failed reference left partial artwork or changed units");
    }
    private static void CaptureBounds()
    {
        var method = typeof(AiSelectionCapture).GetMethod("ChooseDpi", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        var document = new FakeDoc(); var line = new FakeBounds();
        Check((int)method.Invoke(null,new object[]{document,line,400,7000,24000000,0,true}) == 400 && line.OutlineRequested, "Audit ignored a horizontal line's physical outline");
        Check(document.Unit == 9, "Outline measurement did not restore units");
        bool rejected = false;
        try { method.Invoke(null,new object[]{document,line,400,7000,24000000,0,false}); }
        catch (System.Reflection.TargetInvocationException error) { rejected = error.InnerException is InvalidOperationException; }
        Check(rejected && document.Unit == 9, "Default capture behavior changed or failed measurement left units changed");
        line.StrokeWidth = 1000;
        Check((int)method.Invoke(null,new object[]{document,line,400,7000,24000000,0,true}) < 400, "Audit pixel/side cap ignored outline bounds");
    }
}
public sealed class FakeBounds
{
    public double SizeWidth => 20; public double SizeHeight => 0; public double StrokeWidth = .6; public bool OutlineRequested;
    public void GetBoundingBox(out double left,out double bottom,out double width,out double height,bool outline)
    { OutlineRequested = outline; left=0; bottom=0; width=20; height=StrokeWidth; }
}
public sealed class FakeApp
{
    public FakeDoc ActiveDocument { get; } = new FakeDoc(); public FakeRange ActiveSelectionRange { get; } = new FakeRange();
    public FakeRange Range; public FakeRange CreateShapeRange() => Range = new FakeRange();
}
public sealed class FakeDoc
{
    public int Unit { get; set; } = 9; public int Groups;
    public FakePage ActivePage { get; } = new FakePage(); public FakeLayer ActiveLayer { get; } = new FakeLayer();
    public void BeginCommandGroup(string text) { Groups++; } public void EndCommandGroup() { Groups--; }
}
public sealed class FakePage { public double LeftX => 20; public double TopY => 250; }
public sealed class FakeRange
{
    public int Count => 0; public bool Grouped, Selection; public string Name { get; set; }
    public void Add(object shape) { } public FakeRange Group() { Grouped = true; return this; } public void CreateSelection() { Selection = true; }
}
public sealed class FakeLayer
{
    public List<FakeShape> Items = new List<FakeShape>(); public int FailAt;
    private FakeShape Add(FakeShape shape) { if (FailAt > 0 && Items.Count == FailAt) throw new InvalidOperationException("Synthetic create failure"); Items.Add(shape); return shape; }
    public FakeShape CreateArtisticTextWide(double x,double y,string text,int a,int b,string font,float points) => Add(new FakeShape { Kind="text", Text=text, LeftX=x, TopY=y });
    public FakeShape CreateRectangle2(double x,double y,double w,double h) => Add(new FakeShape { Kind="rect", Width=w, Height=h, LeftX=x, TopY=y+h });
    public FakeShape CreateEllipse2(double x,double y,double rx,double ry) => Add(new FakeShape { Kind="ellipse", Width=rx*2, Height=ry*2, LeftX=x-rx, TopY=y+ry });
}
public sealed class FakeShape
{
    public string Kind, Text; public double Width,Height,Angle; public bool Deleted;
    public double LeftX { get; set; } public double TopY { get; set; }
    public FakeFill Fill { get; } = new FakeFill(); public FakeOutline Outline { get; } = new FakeOutline();
    public void Move(double x,double y) { LeftX+=x; TopY+=y; } public void Rotate(double angle) { Angle=angle; } public void Delete() { Deleted=true; }
}
public sealed class FakeFill
{
    public FakeFill UniformColor => this; public int[] Color;
    public void CMYKAssign(int c,int m,int y,int k) { Color = new[]{c,m,y,k}; }
}
public sealed class FakeOutline { public bool None; public void SetNoOutline() { None=true; } }
