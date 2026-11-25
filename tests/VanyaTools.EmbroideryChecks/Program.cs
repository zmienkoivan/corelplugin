using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VanyaTools.Native;

internal static class Program
{
    private static int checks;
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message) { try { action(); } catch (ArgumentException) { checks++; return; } catch (InvalidOperationException) { checks++; return; } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Private).GetValue(value);
    private static object Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Private).Invoke(value, args);
    private static void Render(FrameworkElement element, int width, string path)
    {
        element.Measure(new Size(width, 1600)); element.Arrange(new Rect(0, 0, width, 1600)); element.UpdateLayout();
        var image = new RenderTargetBitmap(width, 1600, 96, 96, PixelFormats.Pbgra32); var backdrop = new DrawingVisual();
        using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, 1600)); image.Render(backdrop); image.Render(element); EmbroideryImages.Save(image, path);
    }
    private static void Pump(Task task)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        { var frame = new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false)); Dispatcher.PushFrame(frame); Thread.Sleep(5); }
        Check(task.IsCompleted, "UI task timed out"); task.GetAwaiter().GetResult();
    }
    [STAThread] private static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        string root = Path.Combine(Directory.GetCurrentDirectory(), "release", "embroidery-checks-" + Guid.NewGuid().ToString("N").Substring(0, 8)); Directory.CreateDirectory(root);
        try { Requests(); string sheet = Sheet(root, 1025); Images(root, sheet); Recovery(root, sheet); Ui(root, sheet); Insertion(root, sheet); Console.WriteLine("Embroidery checks passed: " + checks + ". Offline WPF/images, injected workers and fake Corel; no AI charges or customer documents."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { Directory.Delete(root, true); }
    }
    private static EmbroideryRequest Request() => new EmbroideryRequest { Description = "Знак с буквой А", ExactText = "А" };
    private static void Requests()
    {
        foreach (string model in new[] { "google/nano-banana-pro", "google/nano-banana-2" }) foreach (int fabric in Enumerable.Range(0, 5)) foreach (int thread in new[] { 40, 60 })
        {
            var r = Request(); r.Model = model; r.Fabric = fabric; r.Thread = thread; var input = r.Input("replicate-file:example.png");
            Check((string)input["resolution"] == "4K" && (string)input["aspect_ratio"] == "1:1" && (string)input["output_format"] == "png", "Not one fixed 4K square sheet");
            Check(((string[])input["image_input"]).Length == 1 && input.ContainsKey("allow_fallback_model") == (model.EndsWith("pro")), "Reference or fallback contract changed");
            string prompt = r.Prompt(); Check(new[] { "TOP LEFT", "TOP RIGHT", "BOTTOM LEFT", "BOTTOM RIGHT", "tatami", "7 mm", "60 mm", "80 mm", "ONE-THREAD-COLOUR", "№" }.Take(9).All(prompt.Contains), "Missing paired outputs or technology");
        }
        Check(!Request().Input(null).ContainsKey("image_input"), "Text-only logo requires an image");
        Reject(() => new EmbroideryRequest().Validate(false), "Empty brief accepted");
        new EmbroideryRequest().Validate(true); checks++;
        foreach (string field in new[] { "WidthMm", "HeightMm", "MinStrokeMm", "MinGapMm", "MinTextMm", "HoopWidthMm", "HoopHeightMm", "HoopMarginMm" })
            foreach (double invalid in new[] { Double.NaN, Double.PositiveInfinity, -1d, 1001d })
            { var r = Request(); typeof(EmbroideryRequest).GetProperty(field).SetValue(r, invalid); Reject(() => r.Validate(false), "Invalid dimension " + field); }
        var hoop = Request(); hoop.HeightMm = 199; Reject(() => hoop.Validate(false), "Hoop margin ignored");
        var color = Request(); color.FabricColor = "#GGGGGG"; Reject(() => color.Validate(false), "Invalid fabric color accepted");
        var limit = Request(); limit.Colors = 13; Reject(() => limit.Validate(false), "Unlimited thread palette accepted");
    }
    private static string Sheet(string root, int size)
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(216, 216, 216)), null, new Rect(0, 0, size, size));
            for (int i = 0; i < 4; i++)
            {
                int x = i % 2 * (size / 2), y = i / 2 * (size / 2);
                dc.DrawRectangle(i == 0 ? Brushes.Red : i == 1 ? Brushes.Green : i == 2 ? Brushes.Blue : Brushes.Gold, null, new Rect(x + 110, y + 90, 280, 320));
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(216, 216, 216)), null, new Rect(x + 210, y + 190, 60, 60));
            }
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze();
        string file = Path.Combine(root, "sheet-" + size + ".png"); EmbroideryImages.Save(bitmap, file); return file;
    }
    private static EmbroideryJob Job(string root) => new EmbroideryStore(Path.Combine(root, Guid.NewGuid().ToString("N"))).Create(Request(), null);
    private static EmbroideryResult Prepared(string root, string sheet)
    { var job = Job(root); File.Copy(sheet, job.RawPath); return EmbroideryImages.Prepare(job, CancellationToken.None); }
    private static void Images(string root, string sheet)
    {
        var result = Prepared(root, sheet); Check(result.Frames.Length == 4 && result.Frames.All(f => File.Exists(f.Path) && f.CanInsert), "Quadrants missing");
        Check(result.Notes.Any(n => n.Contains("1025")), "Sub-4K response not reported");
        for (int i = 0; i < 4; i++)
        {
            var f = result.Frames[i]; var image = EmbroideryImages.Load(f.Path); var pixels = new byte[4]; image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2 + 70, 1, 1), pixels, 4, 0);
            Color expected = new[] { Colors.Red, Colors.Green, Colors.Blue, Colors.Gold }[i];
            Check(pixels[0] == expected.B && pixels[1] == expected.G && pixels[2] == expected.R && pixels[3] == 255, "Quadrants swapped or pixels altered");
            Check(f.Preview.IsFrozen && f.WidthMm > 0 && f.HeightMm > 0, "Unsafe preview or physical size");
            if (i % 2 == 0) Check(f.WidthMm <= 80.001 && f.HeightMm <= 60.001 && Math.Abs(image.DpiX - image.DpiY) < .01, "Flat logo stretched or outside mm box");
            Check(Math.Abs(image.PixelWidth / image.DpiX * 25.4 - f.WidthMm) < .02, "PNG physical size differs from import");
        }
        Check(EmbroideryImages.Load(result.Frames[1].Path).PixelWidth == 513 && EmbroideryImages.Load(result.Frames[2].Path).PixelHeight < 513, "Odd quadrants cropped incorrectly");
        using (File.Open(result.Frames[0].Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { checks++; }
        var wrong = Job(root); var visual = BitmapSource.Create(800, 600, 96, 96, PixelFormats.Bgra32, null, new byte[800 * 600 * 4], 3200); EmbroideryImages.Save(visual, wrong.RawPath);
        Reject(() => EmbroideryImages.Prepare(wrong, CancellationToken.None), "Non-square layout accepted"); Check(File.Exists(wrong.RawPath), "Bad paid sheet deleted");
        var design = new DrawingVisual(); using (var dc = design.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 200, 200)); dc.DrawRectangle(Brushes.Black, null, new Rect(20, 20, 120, 150));
            dc.DrawRectangle(Brushes.White, null, new Rect(60, 60, 3, 30)); dc.DrawRectangle(Brushes.Red, null, new Rect(170, 40, 3, 3));
        }
        var small = new RenderTargetBitmap(200, 200, 96, 96, PixelFormats.Pbgra32); small.Render(design);
        var notes = EmbroideryCheck.Analyze(small, .1, Request(), true).ToArray();
        Check(notes.Any(n => n.Contains("мелкие")) && notes.Any(n => n.Contains("просветы")), "Small details or narrow counters missed");
        var fourK = Prepared(root, Sheet(root, 4096));
        Check(!fourK.Notes.Any(n => n.Contains("Запрошено 4K")) && fourK.Frames.All(f => f.Preview.IsFrozen), "Real 4096 px sheet rejected");
    }
    private static void Recovery(string root, string sheet)
    {
        var store = new EmbroideryStore(Path.Combine(root, "history")); var request = Request(); var reference = EmbroideryImages.Load(sheet); var job = store.Create(request, reference); request.WidthMm = 10;
        Check(job.Request.WidthMm == 80 && store.Last().HasReference, "Saved job not immutable or reference lost");
        Check(!File.ReadAllText(Path.Combine(job.DirectoryPath, "job.json")).Contains("token"), "API key stored");
        int calls = 0;
        EmbroideryWorker fail = (model, key, input, output, progress, cancel, resume) => { calls++; Check(!resume, "First request resumed"); throw new InvalidOperationException("Synthetic 500"); };
        Reject(() => EmbroideryService.Run(job, "test-key", false, null, CancellationToken.None, fail), "Synthetic network failure swallowed");
        Check(File.Exists(job.StartedPath) && calls == 1, "Ambiguous request marker missing");
        Reject(() => EmbroideryService.Run(job, "test-key", false, null, CancellationToken.None, fail), "Duplicate paid submission allowed"); Check(calls == 1, "Worker called twice");
        EmbroideryWorker resumeWorker = (model, key, input, output, progress, cancel, resume) =>
        {
            calls++; string upload = ((string[])input["image_input"])[0].Substring("replicate-file:".Length);
            Check(resume && upload != job.ReferencePath && File.ReadAllBytes(upload).SequenceEqual(File.ReadAllBytes(job.ReferencePath)), "Resume changed reference or exposed saved original to worker cleanup");
            File.Copy(sheet, output); File.Delete(upload);
        };
        var result = EmbroideryService.Run(store.Last(), "test-key", true, null, CancellationToken.None, resumeWorker); Check(calls == 2 && result.Frames.Length == 4 && store.Last().HasReference, "Resume failed or history reference deleted");
        EmbroideryService.Run(store.Last(), "", true, null, CancellationToken.None, fail); Check(calls == 2, "Paid sheet sent for generation again");
        var bad = Job(root); File.WriteAllBytes(bad.RawPath, new byte[] { 1, 2, 3 });
        for (int i = 0; i < 2; i++)
        {
            try { EmbroideryService.Run(bad, "", true, null, CancellationToken.None, fail); throw new Exception("Invalid image accepted"); }
            catch (Exception error) when (error is FileFormatException || error is NotSupportedException) { checks++; }
        }
        Check(calls == 2 && File.Exists(bad.RawPath), "Local decoding failure submitted another paid request or deleted raw answer");
        var canceled = Job(root); using (var cts = new CancellationTokenSource())
        {
            EmbroideryWorker cancelWorker = (m, k, input, output, p, ct, re) => { File.Copy(sheet, output); cts.Cancel(); };
            try { EmbroideryService.Run(canceled, "key", false, null, cts.Token, cancelWorker); throw new Exception("Cancellation ignored"); } catch (OperationCanceledException) { checks++; }
            Check(File.Exists(canceled.RawPath), "Cancellation discarded paid image");
        }
        File.WriteAllText(Path.Combine(store.Root, "last.txt"), "../../outside"); Reject(() => store.Last(), "Unsafe history pointer accepted");
    }
    private static void Ui(string root, string sheet)
    {
        var store = new EmbroideryStore(Path.Combine(root, "ui")); int confirmations = 0, sends = 0, resumes = 0;
        var tab = new EmbroideryTab(() => "test-key", () => sheet, (_, __) => { }, _ => { }, store,
            (r, image) => { confirmations++; return true; }, (job, key, resume, progress, cancel) =>
            {
                if (!resume) { sends++; File.WriteAllText(job.StartedPath, "test"); throw new InvalidOperationException("Synthetic lost response"); }
                resumes++; File.Copy(sheet, job.RawPath); return EmbroideryImages.Prepare(job, cancel);
            });
        tab.GetType().GetField("_restored", Private).SetValue(tab, true);
        Field<TextBox>(tab, "_description").Text = "Нашивка с А";
        Pump((Task)Call(tab, "Generate"));
        Check(confirmations == 1 && sends == 1 && (string)Field<Button>(tab, "_generate").Content == "Продолжить запрос", "UI offers another generation after failure");
        Check(!Field<StackPanel>(tab, "_settings").IsEnabled && Field<Button>(tab, "_generate").IsEnabled, "Pending request can be edited or cannot resume");
        Pump((Task)Call(tab, "Generate")); Check(confirmations == 1 && sends == 1 && resumes == 1, "Retry created another paid request");
        var cards = Field<StackPanel>(tab, "_cards"); Check(cards.Children.OfType<Image>().Count() == 4, "Four result cards missing");
        string render = Environment.GetEnvironmentVariable("VANYA_EMBROIDERY_RENDER");
        foreach (int width in new[] { 360, 450 })
        {
            using (var host = new HwndSource(new HwndSourceParameters("Offline embroidery check") { Width = width, Height = 1600, WindowStyle = 0 }))
            {
                host.RootVisual = tab; tab.Measure(new Size(width, 1600)); tab.Arrange(new Rect(0, 0, width, 1600)); tab.UpdateLayout();
                Check(tab.ActualWidth == width && !Double.IsNaN(tab.ActualHeight), "Narrow panel layout failed");
                if (!String.IsNullOrEmpty(render)) { Directory.CreateDirectory(render); Render(tab, width, Path.Combine(render, "embroidery-results-" + width + ".png")); }
                host.RootVisual = null;
            }
        }
        Call(tab, "Fresh"); Check(Field<StackPanel>(tab, "_settings").IsEnabled && File.Exists(store.Last().RawPath), "New task erased history or settings locked");
        if (!String.IsNullOrEmpty(render)) using (var host = new HwndSource(new HwndSourceParameters("Offline embroidery settings") { Width = 360, Height = 1600, WindowStyle = 0 }))
        { host.RootVisual = tab; Render(tab, 360, Path.Combine(render, "embroidery-settings.png")); host.RootVisual = null; }
        Pump((Task)Call(tab, "Restore")); Check(sends == 1 && resumes == 1 && Field<StackPanel>(tab, "_cards").Children.OfType<Image>().Count() == 4, "Opening last result invoked paid worker");
    }
    private static void Insertion(string root, string sheet)
    {
        var frame = Prepared(root, sheet).Frames[0];
        foreach (bool failure in new[] { false, true })
        {
            var app = new FakeApp { Failure = failure }; CorelApp.Current = app;
            if (failure) Reject(() => EmbroideryCorel.Insert(frame), "Wrong native dimensions accepted"); else EmbroideryCorel.Insert(frame);
            Check(app.ActiveDocument.Unit == 9 && !app.Optimization && app.ActiveDocument.OpenGroups == 0, "Native state leaked");
            Check(app.ActiveDocument.ActivePage.Layer.Deleted == failure && app.ActiveSelectionRange.OriginalAlive, "Rollback or original source changed");
        }
        CorelApp.Current = null;
    }
}
namespace VanyaTools.Native
{
    internal static class CorelApp { internal static object Current; internal static dynamic Get() => Current ?? throw new Exception("Live Corel forbidden in offline checks"); }
    internal static class DockerTheme { internal static Button Primary(Button button) => button; }
    internal static class Log { internal static void Error(string message, Exception error) { } }
    internal static class ReplicateWorkerClient { internal static void Run(string m, string t, Dictionary<string, object> i, string o, Action<string> p, CancellationToken c, bool r) => throw new Exception("Network forbidden"); }
}
public sealed class FakeApp
{
    public bool Optimization, Failure; public FakeDoc ActiveDocument = new FakeDoc(); public FakeSelection ActiveSelectionRange = new FakeSelection();
    public object CreateStructImportOptions() { ActiveDocument.ActivePage.Failure = Failure; return new object(); } public void Refresh() { }
}
public sealed class FakeDoc { public int Unit = 9, OpenGroups; public FakePage ActivePage = new FakePage(); public void BeginCommandGroup(string name) => OpenGroups++; public void EndCommandGroup() => OpenGroups--; }
public sealed class FakeSelection { public int Count = 1; public double RightX = 30, TopY = 100; public bool OriginalAlive = true; }
public sealed class FakePage { public double SizeWidth = 200, SizeHeight = 150; public bool Failure; public FakeLayer Layer; public FakeLayer CreateLayer(string n) => Layer = new FakeLayer(this); }
public sealed class FakeLayer
{
    private readonly FakePage page; public bool Visible, Editable, Printable, Deleted; public FakeShapes Shapes;
    public FakeLayer(FakePage p) { page = p; Shapes = new FakeShapes(); }
    public FakeFilter ImportEx(BStrWrapper path, int format, object options) { Shapes.Image = new FakeImage { Failure = page.Failure }; return new FakeFilter(); } public void Delete() => Deleted = true;
}
public sealed class FakeFilter { public void Finish() { } }
public sealed class FakeShapes { public FakeImage Image; public int Count => Image == null ? 0 : 1; public FakeImage this[int index] => Image; }
public sealed class FakeImage
{
    public bool Failure; public int Type = 5; public double LeftX, TopY, W, H; public string Name;
    public void SetSize(double w, double h) { W = w + (Failure ? 1 : 0); H = h; } public void Move(double x, double y) { LeftX += x; TopY += y; }
    public void GetBoundingBox(out double x, out double y, out double w, out double h, bool outline) { x = LeftX; y = TopY - H; w = W; h = H; } public void CreateSelection() { }
}
