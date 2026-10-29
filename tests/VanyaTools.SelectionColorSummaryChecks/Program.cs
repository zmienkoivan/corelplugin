using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static SelectionColorSummaryResult Run(FakeApp app) => new SelectionColorSummaryService().InsertSummary(app);
    private static void Reject(Action action, string fragment)
    { try { action(); throw new Exception("Expected rejection: " + fragment); } catch (InvalidOperationException error) { Check(error.Message.Contains(fragment), error.Message); } }

    [STAThread]
    private static int Main()
    {
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("ru-RU");
            VectorAndPlacement(); TraversalAndText(); SpotsAndModels(); RasterAndAlpha(); LimitsAndWarnings(); RollbackAndGuards();
            Console.WriteLine("Selection color summary checks passed: " + _checks + ". No live Corel or exports."); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void VectorAndPlacement()
    {
        var cmyk = FakeColor.Cmyk(10, 20, 30, 40); var rgb = FakeColor.Rgb(100, 120, 140);
        var a = new FakeShape(1, new FakeFill(cmyk)) { Outline = new FakeOutline(rgb) };
        var b = new FakeShape(2, new FakeFill(cmyk)); var app = new FakeApp(a, b);
        var result = Run(app); var text = app.Document.ActiveLayer.Created[0];
        var usage = app.Document.ActiveLayer.Created[1];
        Check(result.ColorCount == 2 && result.ApproximateRasterCount == 0 && !result.Incomplete, "Exact dedup failed");
        Check(result.Text.Contains("CMYK C10 M20 Y30 K40") && result.Text.Contains("RGB R100 G120 B140"), "Native recipes missing");
        Check(text.LeftX == 10 && text.TopY == 15, "Summary not anchored 5mm below left edge");
        Check(text.Points == 14 && text.Value == result.Text, "Not14pt multiline text");
        Check(result.Text.Split('\n').Length == 2 && !result.Text.Contains("Цвета выделения") && !result.Text.Contains("заливка") && !result.Text.Contains("контур"),
            "Primary list must contain one formula per line without header or role annotations");
        Check(app.Document.ActiveLayer.Created.Count == 2 && usage.Value == result.UsageText && usage.Points == 14,
            "Role information must be a second independent 14pt text object");
        Check(usage.LeftX == text.LeftX && Math.Abs(usage.TopY - (text.BottomY - 2)) < 1e-9, "Role block must be 2mm below measured formula bounds");
        Check(usage.Value.Contains("CMYK C10 M20 Y30 K40 — заливка") && usage.Value.Contains("RGB R100 G120 B140 — контур"),
            "Fill and outline roles do not match source colors");
        Check(app.Document.Unit == 2 && app.Document.Begins == 1 && app.Document.Ends == 1, "Units or Undo group not restored");
        Check(a.Fill.UniformColor == cmyk && cmyk.Conversions == 0 && rgb.Conversions == 0, "Source colors changed");
        Check(app.Selection.Count == 2 && app.Selection[1] == a, "Source selection changed");
        Check(app.Exports == 0 && app.Copies == 0, "Unexpected export or source copy");
        usage.Delete();
        Check(usage.Deleted && !text.Deleted && app.Selection[1] == a, "Deleting role block must leave formulas and source artwork independent");
        var shared = FakeColor.Rgb(1, 2, 3);
        var both = Run(new FakeApp(new FakeShape(3, new FakeFill(shared)) { Outline = new FakeOutline(shared) }));
        Check(both.ColorCount == 1 && both.UsageText.Contains("RGB R1 G2 B3 — заливка и контур"), "Same color used by both roles lost a role or duplicated its formula");
    }

    private static void TraversalAndText()
    {
        var red = FakeColor.Rgb(255, 0, 0); var blue = FakeColor.Rgb(0, 0, 255); var green = FakeColor.Rgb(0, 255, 0);
        var gradient = new FakeFill { Type = 2, Fountain = new FakeFountain(red, blue, green) };
        var curve = new FakeShape(5, gradient);
        var group = new FakeShape(6, null) { Type = 7, Shapes = new FakeCollection(curve) };
        var frame = new FakeShape(7, new FakeFill(red)) { PowerClip = new FakeClip(group) };
        group.PowerClip = new FakeClip(frame); // Native cycles and selecting a descendant twice must not duplicate it.
        var text = new FakeShape(8, null) { Type = 6, Text = new FakeText(new FakeCharacter(new FakeFill(red)),
            new FakeCharacter(new FakeFill(green), new FakeOutline(FakeColor.Cmyk(0, 0, 0, 100)))) };
        var first = Run(new FakeApp(group, curve, text));
        var second = Run(new FakeApp(text, curve, group));
        Check(first.ColorCount == 4 && !first.Incomplete, "Nested group/PowerClip/text colors lost");
        Check(first.Text == second.Text, "Output depends on selection order");
        Check(first.UsageText == second.UsageText && first.UsageText.Contains("Градиенты: перечислены цвета узлов"), "Gradient limitation or stable role order lost");
        Check(first.Text.Contains("C0 M0 Y0 K100"), "Character outline missing");
    }

    private static void SpotsAndModels()
    {
        var spot = FakeColor.Spot("PANTONE 123 C", "paletteA", 12, 100);
        var tint = FakeColor.Spot("PANTONE 123 C", "paletteA", 12, 50);
        var other = FakeColor.Spot("PANTONE 123 C", "paletteB", 12, 100);
        var lab = new FakeColor { Type = 12, LabLuminance = 255, LabComponentA = -15, LabComponentB = 6 };
        var gray = new FakeColor { Type = 9, Gray = 128 };
        var app = new FakeApp(new FakeShape(10, new FakeFill(spot)), new FakeShape(11, new FakeFill(tint)),
            new FakeShape(12, new FakeFill(other)), new FakeShape(13, new FakeFill(lab)), new FakeShape(14, new FakeFill(gray)));
        var result = Run(app);
        Check(result.ColorCount == 5, "Spot palettes/tints collapsed");
        Check(result.Text.Contains("PANTONE 123 C · 50%") && result.Text.Contains("Lab эквивалент по Corel"), "Spot identity/equivalent unclear");
        Check(result.Text.Contains("Lab L*100 a*-15 b*6") && result.Text.Contains("Gray 128"), "Lab scale/native Gray wrong");
        Check(spot.Conversions == 0 && tint.Conversions == 0 && other.Conversions == 0 && spot.Type == 25, "Source spot converted");
    }

    private static void RasterAndAlpha()
    {
        var red = FakeColor.Rgb(255, 0, 0); var blue = FakeColor.Rgb(0, 0, 255);
        var image = new FakeImage(24, 24, (x, y) => x < 16 ? red : blue);
        var alpha = new FakeImage(24, 24, (x, y) => new FakeColor { Type = 9, Gray = x < 16 ? 255 : 0 });
        var raster = new FakeShape(20, null) { Type = 5, Bitmap = new FakeBitmap(image, alpha) };
        var app = new FakeApp(raster); var result = Run(app);
        Check(result.ColorCount == 0 && result.ApproximateRasterCount == 1, "Transparent pixels counted as colors");
        Check(result.UsageText.Contains("основной цвет растра, приблизительно") && result.Text == "≈ RGB R255 G0 B0", "Raster sampling inaccurate or unlabeled");
        Check(image.Reads == 96 && alpha.Reads == 144, "Raster sample size unexpectedly unbounded");
        Check(app.Exports == 0 && app.Copies == 0 && image.Mutations == 0, "Raster sampling mutated/exported sources");
        var brokenAlpha = new FakeImage(24, 24, (x, y) => { throw new InvalidOperationException("Alpha unavailable"); });
        var broken = new FakeShape(21, null) { Type = 5, Bitmap = new FakeBitmap(image, brokenAlpha) };
        var vector = new FakeShape(22, new FakeFill(red));
        var incomplete = Run(new FakeApp(vector, broken));
        Check(incomplete.Incomplete && incomplete.ApproximateRasterCount == 0 && incomplete.UsageText.Contains("Не удалось прочитать основные цвета растра"), "Broken alpha silently treated as opaque");
        var pixels = new FakeImage(30, 30, (x, y) => FakeColor.Rgb((x * 19) % 256, (y * 19) % 256, (x * y) % 256));
        var many = Run(new FakeApp(new FakeShape(23, null) { Type = 5, Bitmap = new FakeBitmap(pixels) }));
        Check(many.ApproximateRasterCount == 8 && many.UsageText.Contains("Показаны 8 основных групп"), "Raster dominant colors not capped");
        Check(pixels.Reads == 144, "Large raster sampled too many pixels");
        var monochrome = new FakeImage(24, 24, (x, y) => new FakeColor { Type = 8, BW = x < 12 });
        Check(Run(new FakeApp(new FakeShape(24, null) { Type = 5, Bitmap = new FakeBitmap(monochrome) })).ApproximateRasterCount == 2,
            "Monochrome black and white merged into one bucket");
    }

    private static void LimitsAndWarnings()
    {
        var shapes = Enumerable.Range(1, 30).Select(i => (object)new FakeShape(100 + i, new FakeFill(FakeColor.Rgb(i, 0, 0)))).ToArray();
        var result = Run(new FakeApp(shapes));
        Check(result.ColorCount == 30 && result.UsageText.Contains("Ещё 6 векторных цветов"), "Vector display limit is silent or wrong");
        Check(result.Text.Split('\n').Count(line => line.StartsWith("RGB ")) == 24, "Vector list is not compact");
        var unsupported = new FakeShape(150, new FakeFill { Type = 3 });
        var known = new FakeShape(151, new FakeFill(FakeColor.Rgb(50, 60, 70)));
        var partial = Run(new FakeApp(unsupported, known));
        Check(partial.Incomplete && partial.UsageText.Contains("Сложные заливки не включены"), "Unsupported fill silently ignored");
        var chars = Enumerable.Range(0, 5001).Select(_ => (object)new FakeCharacter(new FakeFill(FakeColor.Rgb(1, 2, 3)))).ToArray();
        var longText = new FakeShape(152, null) { Type = 6, Text = new FakeText(chars) };
        Check(Run(new FakeApp(longText)).UsageText.Contains("часть символов не проверена"), "Text safety cap not disclosed");
        var duplicates = Enumerable.Range(0, 10001).Select(i => (object)new FakeShape(1000 + i, new FakeFill(FakeColor.Rgb(1, 2, 3)))).ToArray();
        var capped = Run(new FakeApp(duplicates));
        Check(capped.Incomplete && capped.UsageText.Contains("часть выделения не проверена"), "Object safety cap not disclosed");
        var good = new FakeShape(30000, new FakeFill(FakeColor.Rgb(100, 1, 2)));
        var bad = new BrokenShape(); var nested = new FakeShape(30001, null) { Type = 7, Shapes = new FakeCollection(bad, good) };
        var partialGroup = Run(new FakeApp(nested));
        Check(partialGroup.ColorCount == 1 && partialGroup.Incomplete, "One bad child prevented reading the remaining group");
    }

    private static void RollbackAndGuards()
    {
        var app = new FakeApp(new FakeShape(201, new FakeFill(FakeColor.Rgb(1, 2, 3))));
        app.Document.ActiveLayer.FailMove = true;
        Reject(() => Run(app), "Move failed");
        Check(app.Document.ActiveLayer.Created.Single().Deleted && app.Document.Unit == 2 && !app.Document.Dirty, "Failed insertion not rolled back");
        Check(app.Document.Begins == 1 && app.Document.Ends == 1, "Failed insertion left Undo group open");
        var empty = new FakeApp(); Reject(() => Run(empty), "Выделите объекты"); Check(empty.Document.Begins == 0, "Empty selection mutated document");
        var change = new FakeApp(new FakeShape(202, new FakeFill(FakeColor.Rgb(1, 2, 3))));
        change.Selection[1].Effects.OnRead = () => change.ActiveDocument = new FakeDoc();
        Reject(() => Run(change), "Активный документ изменился"); Check(change.Document.ActiveLayer.Created.Count == 0, "Inserted into changed CDR");
        var fail = new FakeApp(new FakeShape(203, new FakeFill { Type = 4 }));
        Reject(() => Run(fail), "не удалось прочитать цвета"); Check(fail.Document.Unit == 2 && fail.Document.Begins == 0 && !fail.Document.Dirty, "Read failure changed document");
        var endFail = new FakeApp(new FakeShape(204, new FakeFill(FakeColor.Rgb(1, 2, 3)))); endFail.Document.FailFirstEnd = true;
        Reject(() => Run(endFail), "End failed");
        Check(endFail.Document.ActiveLayer.Created.Count == 2 && endFail.Document.ActiveLayer.Created.All(value => value.Deleted) && endFail.Document.Unit == 2 && !endFail.Document.Dirty,
            "EndCommandGroup failure left summary/source state behind");
        var secondCreate = new FakeApp(new FakeShape(206, new FakeFill(FakeColor.Rgb(1, 2, 3))));
        secondCreate.Document.ActiveLayer.FailCreateAt = 2;
        Reject(() => Run(secondCreate), "Create failed");
        Check(secondCreate.Document.ActiveLayer.Created.Single().Deleted && secondCreate.Document.Unit == 2 && !secondCreate.Document.Dirty,
            "Secondary text creation failure did not remove the primary owned text");
        var secondMove = new FakeApp(new FakeShape(207, new FakeFill(FakeColor.Rgb(1, 2, 3))));
        secondMove.Document.ActiveLayer.FailMoveAt = 2;
        Reject(() => Run(secondMove), "Move failed");
        Check(secondMove.Document.ActiveLayer.Created.Count == 2 && secondMove.Document.ActiveLayer.Created.All(value => value.Deleted) && !secondMove.Document.Dirty,
            "Secondary placement failure did not remove both owned texts");
        var between = new FakeApp(new FakeShape(208, new FakeFill(FakeColor.Rgb(1, 2, 3))));
        between.Document.ActiveLayer.OnCreate = index => { if (index == 1) between.ActiveDocument = new FakeDoc(); };
        Reject(() => Run(between), "Активный документ изменился");
        Check(between.Document.ActiveLayer.Created.Single().Deleted && !between.Document.Dirty && between.Document.Unit == 2,
            "Document change between texts left the primary text behind");
        var deleteFail = new FakeApp(new FakeShape(205, new FakeFill(FakeColor.Rgb(1, 2, 3))));
        deleteFail.Document.ActiveLayer.FailMove = deleteFail.Document.ActiveLayer.FailDelete = true;
        Reject(() => Run(deleteFail), "Move failed");
        Check(deleteFail.Document.Dirty && deleteFail.Document.Unit == 2 && !deleteFail.Document.ActiveLayer.Created.Single().Deleted,
            "Failed cleanup incorrectly marked a modified document clean");
        Exception wrongThread = null;
        var worker = new System.Threading.Thread(() => { try { new SelectionColorSummaryService().InsertSummary(); } catch (Exception error) { wrongThread = error; } });
        worker.Start(); worker.Join();
        Check(wrongThread is InvalidOperationException, "Background call reached Corel before the STA guard");
    }
}

public sealed class FakeApp
{
    public FakeDoc Document = new FakeDoc(); public object ActiveDocument { get; set; }
    public FakeSelection Selection; public object ActiveSelectionRange => Selection;
    public int Exports, Copies;
    public FakeApp(params object[] shapes) { ActiveDocument = Document; Selection = new FakeSelection(shapes); }
    public FakeColor CreateColor() => new FakeColor();
}
public sealed class FakeDoc
{
    public int Unit { get; set; } = 2; public bool Dirty { get; set; }
    public FakeLayer ActiveLayer { get; } = new FakeLayer(); public int Begins, Ends; public bool FailFirstEnd;
    public void BeginCommandGroup(string value) { Begins++; }
    public void EndCommandGroup() { Ends++; if (FailFirstEnd && Ends == 1) throw new InvalidOperationException("End failed"); }
}
public class FakeCollection
{
    protected readonly object[] Data; public int Count => Data.Length;
    public object this[int index] => Data[index - 1];
    public FakeCollection(params object[] data) { Data = data; }
}
public sealed class FakeSelection : FakeCollection
{
    public FakeSelection(params object[] data) : base(data) { }
    public new FakeShape this[int index] => (FakeShape)base[index];
    public void GetBoundingBox(out double left, out double bottom, out double width, out double height, bool outline)
    { left = 10; bottom = 20; width = 200; height = 300; }
}
public sealed class FakeShape
{
    public int StaticID { get; } public int Type { get; set; } = 3;
    public FakeFill Fill { get; } public FakeOutline Outline { get; set; } = new FakeOutline();
    public FakeCollection Shapes { get; set; } = new FakeCollection(); public FakeClip PowerClip { get; set; }
    public FakeText Text { get; set; } public FakeBitmap Bitmap { get; set; }
    public FakeEffects Effects { get; } = new FakeEffects(); public double SizeWidth => 100; public double SizeHeight => 100;
    public FakeShape(int id, FakeFill fill) { StaticID = id; Fill = fill ?? new FakeFill(); }
}
public sealed class FakeEffects { public Action OnRead; public int Count { get { OnRead?.Invoke(); return 0; } } }
public sealed class BrokenShape { public int StaticID => throw new InvalidOperationException("Stale source shape"); }
public sealed class FakeClip { public FakeCollection Shapes { get; } public FakeClip(params object[] shapes) { Shapes = new FakeCollection(shapes); } }
public sealed class FakeFill
{
    public int Type { get; set; } public FakeColor UniformColor { get; } public FakeFountain Fountain { get; set; }
    public FakeFill(FakeColor color = null) { UniformColor = color ?? new FakeColor(); Type = color == null ? 0 : 1; }
}
public sealed class FakeOutline
{
    public int Type { get; private set; } public FakeColor Color { get; }
    public FakeOutline(FakeColor color = null) { Color = color ?? new FakeColor(); Type = color == null ? 0 : 1; }
    public void SetNoOutline() { Type = 0; }
}
public sealed class FakeFountain
{
    public FakeColor StartColor { get; } public FakeColor EndColor { get; } public FakeCollection Colors { get; }
    public FakeFountain(FakeColor start, FakeColor end, params FakeColor[] stops)
    { StartColor = start; EndColor = end; Colors = new FakeCollection(stops.Select(color => (object)new FakeStop { Color = color }).ToArray()); }
}
public sealed class FakeStop { public FakeColor Color { get; set; } }
public sealed class FakeText
{ public FakeText Story => this; public FakeCollection Characters { get; } public FakeText(params object[] characters) { Characters = new FakeCollection(characters); } }
public sealed class FakeCharacter
{ public FakeFill Fill { get; } public FakeOutline Outline { get; } public FakeCharacter(FakeFill fill, FakeOutline outline = null) { Fill = fill; Outline = outline ?? new FakeOutline(); } }
public sealed class FakeBitmap
{
    public FakeImage Image { get; } public FakeImage ImageAlpha { get; } public bool Transparent => ImageAlpha != null;
    public FakeBitmap(FakeImage image, FakeImage alpha = null) { Image = image; ImageAlpha = alpha; }
}
public sealed class FakeImage
{
    public int Width { get; } public int Height { get; } public int Reads, Mutations;
    private readonly Func<int, int, FakeColor> _pixel;
    public FakeImage(int width, int height, Func<int, int, FakeColor> pixel) { Width = width; Height = height; _pixel = pixel; }
    [IndexerName("Pixel")] public FakeColor this[int x, int y] { get { Reads++; return _pixel(x, y); } }
}
public sealed class FakeColor
{
    public int Type { get; set; } = 5; public bool IsSpot { get; set; } public string SpotColorName { get; set; } = "";
    public string Name => SpotColorName; public string PaletteIdentifier { get; set; } = ""; public int SpotColorID { get; set; } public int Tint { get; set; } = 100;
    public int CMYKCyan { get; set; } public int CMYKMagenta { get; set; } public int CMYKYellow { get; set; } public int CMYKBlack { get; set; }
    public int RGBRed { get; set; } public int RGBGreen { get; set; } public int RGBBlue { get; set; } public int Gray { get; set; }
    public int LabLuminance { get; set; } public int LabComponentA { get; set; } public int LabComponentB { get; set; }
    public bool BW { get; set; }
    public int Conversions;
    public static FakeColor Cmyk(int c, int m, int y, int k) => new FakeColor { Type = 2, CMYKCyan = c, CMYKMagenta = m, CMYKYellow = y, CMYKBlack = k };
    public static FakeColor Rgb(int r, int g, int b) => new FakeColor { Type = 5, RGBRed = r, RGBGreen = g, RGBBlue = b };
    public static FakeColor Spot(string name, string palette, int id, int tint) => new FakeColor { Type = 25, IsSpot = true, SpotColorName = name, PaletteIdentifier = palette, SpotColorID = id, Tint = tint };
    public void CopyAssign(FakeColor source) { Type = source.Type; IsSpot = source.IsSpot; SpotColorName = source.SpotColorName; Tint = source.Tint; }
    public void ConvertToLab() { Type = 12; LabLuminance = 128; LabComponentA = 10; LabComponentB = 20; Conversions++; }
    public void CMYKAssign(int c, int m, int y, int k) { Type = 2; CMYKCyan = c; CMYKMagenta = m; CMYKYellow = y; CMYKBlack = k; }
}
public sealed class FakeLayer
{
    public readonly List<FakeInsertedText> Created = new List<FakeInsertedText>(); public bool FailMove, FailDelete;
    public int FailCreateAt, FailMoveAt;
    public Action<int> OnCreate;
    public FakeInsertedText CreateArtisticTextWide(double x, double y, string text, int a, int b, string font, float points)
    {
        int index = Created.Count + 1;
        if (index == FailCreateAt) throw new InvalidOperationException("Create failed");
        var shape = new FakeInsertedText(text, points) { FailMove = FailMove || index == FailMoveAt, FailDelete = FailDelete };
        Created.Add(shape); OnCreate?.Invoke(index); return shape;
    }
}
public sealed class FakeInsertedText
{
    public string Value { get; } public float Points { get; } public string Name { get; set; }
    public double LeftX { get; private set; } = 99; public double TopY { get; private set; } = 88;
    public double BottomY => TopY - Value.Split('\n').Length * Points * 25.4 / 72;
    public FakeFill Fill { get; } = new FakeFill(FakeColor.Rgb(0, 0, 0)); public FakeOutline Outline { get; } = new FakeOutline();
    public bool Deleted, FailMove, FailDelete;
    public FakeInsertedText(string value, float points) { Value = value; Points = points; }
    public void Move(double x, double y) { if (FailMove) throw new InvalidOperationException("Move failed"); LeftX += x; TopY += y; }
    public void Delete() { if (FailDelete) throw new InvalidOperationException("Delete failed"); Deleted = true; }
}
namespace VanyaTools.Native
{
    internal static class CorelApp { public static object Get() => throw new Exception("Live Corel must not be called in checks."); }
    internal static class Log { public static void Error(string message, Exception error) { } }
}
