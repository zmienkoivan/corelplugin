using System;
using System.Collections.Generic;
using System.Linq;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool condition, string message) { _checks++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidOperationException error) { Check(error.Message.Contains("PDF не отправлен"), "Unclear failure"); return; } throw new Exception(message); }
    private static UvShape Cut(string name, double left, double top) => new UvShape(name, left, top - 10, 10, 10);
    private static UvPage Page(params UvShape[] shapes) => new UvPage(shapes);
    private static int Sort(UvPage page) => UvCutContourOrder.Sort(new UvApp(page), page);
    private static IEnumerable<UvShape> Painting(UvPage page) => page.CutLayer.Shapes.AsEnumerable().Reverse();
    private static void Main()
    {
        try
        {
            Buttons(); CopiedPacks(); Rows(); SnakeRows(); Compound(); KeepArtwork(); Shapes(); Failures(); BatchCost();
            Console.WriteLine("UV cut order checks passed: " + _checks + ". Synthetic groups/paths only; no Corel, customer CDR or RIP.");
        }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
    }
    private static void Buttons()
    {
        for (int i = 0; i < 5; i++) Check(UvCutContourOrder.ForButton(i) == (i == 4), "Wrong print button sorted");
    }
    private static void CopiedPacks()
    {
        // Copy order is by image across packs, with duplicate names: NOT spatial order.
        var groups = new List<UvShape>();
        for (int image = 0; image < 3; image++)
        {
            var copies = new List<UvShape>();
            for (int row = 2; row >= 0; row--) for (int col = 3; col >= 0; col--)
                copies.Add(Cut("same-name", col * 30, 300 - row * 70 - image * 20));
            groups.Add(UvShape.Group(UvShape.Group(copies.ToArray())));
        }
        var page = Page(groups.ToArray());
        Check(Sort(page) == 36, "Missing or duplicated copied cut");
        var paths = Painting(page).ToArray();
        Check(paths.Select(p => p.StaticID).Distinct().Count() == 36, "Duplicate path identity");
        for (int row = 0; row < 9; row++)
        {
            var band = paths.Skip(row * 4).Take(4).ToArray();
            Check(band.All(p => p.Top == band[0].Top), "Several rows interleaved");
            Check(band.Select(p => p.Left).SequenceEqual(row % 2 == 0 ? new[] { 0d, 30, 60, 90 } : new[] { 90d, 60, 30, 0 }), "Row is not serpentine");
            if (row > 0) Check(band[0].Top < paths[row * 4 - 1].Top, "Rows not top-to-bottom");
        }
        Check(page.CutLayer.Visible && page.CutLayer.Printable && page.CutLayer.Editable, "Unprintable cut layer");
        Check(paths.All(p => p.Outline.Color.SpotColorName == "CUT" && p.Outline.Color.IsSpot && p.Outline.Width == .1 && p.Outline.Overprint),
            "Spot/outline properties changed");
    }
    private static void Rows()
    {
        var page = Page(Cut("right", 50, 102), Cut("left", -10, 100), Cut("next", 0, 85));
        Sort(page);
        Check(Painting(page).Select(p => p.Name).SequenceEqual(new[] { "left", "right", "next" }), "Near-aligned row or negative coordinates");
        var a = Cut("A", 30, 100); a.Bottom = 50; a.Height = 50;
        var b = Cut("B", 20, 75); b.Bottom = 25; b.Height = 50;
        var c = Cut("C", 10, 50); c.Bottom = 0; c.Height = 50;
        page = Page(a, b, c);
        Sort(page);
        Check(Painting(page).Select(p => p.Name).SequenceEqual(new[] { "B", "A", "C" }), "Rows chained beyond anchor tolerance");
    }
    private static void SnakeRows()
    {
        var shapes = new List<UvShape>();
        // Five different top edges used to become five short return passes.
        for (int row = 0; row < 3; row++) for (int col = 4; col >= 0; col--)
        {
            var shape = Cut(row + ":" + col, col * 40, 300 - row * 80 + col * 5);
            shape.Bottom = shape.Top - 35; shape.Height = 35; shapes.Add(shape);
        }
        var page = Page(shapes.ToArray()); Sort(page);
        var expected = new List<string>();
        for (int row = 0; row < 3; row++) foreach (int col in row % 2 == 0 ? Enumerable.Range(0, 5) : Enumerable.Range(0, 5).Reverse()) expected.Add(row + ":" + col);
        Check(Painting(page).Select(p => p.Name).SequenceEqual(expected), "Unequal sticker heights split a row or added an empty return");
        Check(Painting(page).All(p => p.Height == 35), "Row grouping moved or resized stickers");
    }
    private static void Compound()
    {
        var high = Cut("high", 10, 100); var low = Cut("low", 10, 10);
        var compound = UvShape.Compound(high, low);
        var middle = Cut("middle", 10, 50);
        var page = Page(compound, middle);
        Check(Sort(page) == 3, "Compound paths not individually sorted");
        Check(Painting(page).Select(p => p.Name).SequenceEqual(new[] { "high", "middle", "low" }), "Compound paths left adjacent");
        Check(compound.Deleted && Painting(page).All(p => p.Curve.SubPaths.Count == 1), "Old compound or multicut survived");
    }
    private static void KeepArtwork()
    {
        var bitmap = Cut("bitmap", 0, 100); bitmap.Type = 5;
        var process = Cut("magenta", 0, 100); process.Outline.Color.IsSpot = false;
        var white = Cut("white-layer", 0, 100); white.Outline.Color.SpotColorName = "White";
        var filled = Cut("filled", 0, 100); filled.Fill.Type = 1;
        var marks = Cut("registration", 0, 100); marks.Outline.Color.IsSpot = false;
        var page = Page(bitmap, process, white, filled, marks);
        var before = page.Layers[0].Shapes.Select(p => p.StaticID).ToArray();
        Check(Sort(page) == 0 && page.CutLayer == null, "Artwork treated as cut");
        Check(page.Layers[0].Shapes.Select(p => p.StaticID).SequenceEqual(before), "Artwork order changed without cuts");
        var cut = Cut("cut", 0, 30); page = Page(bitmap, UvShape.Group(cut), process, marks, white);
        var artworkLayer = page.Layers[0];
        Sort(page);
        Check(artworkLayer.Shapes.Where(p => p.Type != 7).Select(p => p.Name).SequenceEqual(new[] { "bitmap", "magenta", "registration", "white-layer" }), "Artwork reordered");
        Check(!bitmap.Deleted && !process.Deleted && !marks.Deleted && !white.Deleted, "Artwork deleted");
        page = Page(Cut("nonprinting", 0, 30)); page.Layers[0].Printable = false;
        Check(Sort(page) == 0, "Nonprinting layer exported");
    }
    private static void Shapes()
    {
        var rectangle = Cut("rectangle", 0, 50); rectangle.Type = 1;
        var alias = Cut("alias", 0, 20); alias.Outline.Color.SpotColorName = "CutContour";
        var line = Cut("open-line", 0, 10); line.Width = 0;
        var page = Page(line, alias, rectangle);
        Check(Sort(page) == 3 && rectangle.Type == 3, "Rectangle/alias/open path not supported");
        Check(Painting(page).Select(p => p.Name).SequenceEqual(new[] { "rectangle", "alias", "open-line" }), "Shape positions changed");
    }
    private static void Failures()
    {
        foreach (string failure in new[] { "shift", "scale", "length", "color", "tint", "pen", "fill", "drop", "order", "order-shift", "batch-ignore", "batch-partial" })
        {
            var page = Page(UvShape.Compound(Cut("A", 30, 100), Cut("B", 0, 30)), Cut("C", 15, 60));
            page.Failure = failure;
            Reject(() => Sort(page), "Accepted changed geometry/color/count/order: " + failure);
        }
        var clipped = Cut("inside-clip", 0, 30); var frame = Cut("frame", 0, 50); frame.Outline.Color.IsSpot = false;
        frame.PowerClip = new UvClip(clipped); var clipPage = Page(frame);
        Reject(() => Sort(clipPage), "CUT extracted from PowerClip lost clipping");
        Check(clipPage.CutLayer == null && !clipped.Deleted, "Clipping error mutated page before validation");
        var bad = Cut("bad", Double.NaN, 30); var badPage = Page(bad);
        Reject(() => Sort(badPage), "NaN accepted");
        var cycle = UvShape.Group(); cycle.Shapes.Add(cycle); var cyclePage = Page(); cyclePage.Layers[0].Shapes.Add(cycle);
        Check(Sort(cyclePage) == 0, "Repeated group references looped");
    }
    private static void BatchCost()
    {
        var paths = Enumerable.Range(0, 1240).Select(i => Cut("same", i % 40 * 20, 3000 - i / 40 * 20)).ToArray();
        var page = Page(UvShape.Group(paths));
        Check(Sort(page) == paths.Length, "Large sheet lost contours");
        Check(page.BatchMoves == 1 && page.IndividualMoves == 0, "Per-contour native transfers returned");
        Check(paths.All(p => p.BoundsReads == 2 && p.LengthReads == 2), "Repeated geometry/length calculations returned");
        Console.WriteLine("Synthetic 1240-path sheet: native batch moves=" + page.BatchMoves + ", individual moves=" + page.IndividualMoves +
            ", bounding-box reads=" + paths.Sum(p => p.BoundsReads) + ", length reads=" + paths.Sum(p => p.LengthReads));
        var top = Cut("top", 0, 100); var bottom = Cut("bottom", 0, 50);
        page = Page(bottom, top); Sort(page);
        Check(page.OrderMoves == 0, "Already ordered paths unnecessarily restacked");
    }
}

public sealed class UvApp
{
    private readonly UvPage _page;
    public UvApp(UvPage page) { _page = page; }
    public UvRange CreateShapeRange() => new UvRange(_page);
}
public sealed class UvRange
{
    private readonly UvPage _page;
    private readonly List<UvShape> _items = new List<UvShape>();
    public UvRange(UvPage page) { _page = page; }
    public int Count => _items.Count;
    public void Add(object value) { _items.Add((UvShape)value); }
    public void MoveToLayer(UvLayer target)
    {
        _page.BatchMoves++;
        if (_page.Failure == "batch-ignore") return;
        var move = _page.Failure == "batch-partial" ? _items.Take(1) : _items.AsEnumerable();
        foreach (var shape in move.Reverse()) shape.MoveInternal(target);
    }
}
public sealed class UvPage
{
    public int BatchMoves, IndividualMoves, OrderMoves;
    public string Failure;
    public List<UvLayer> Layers = new List<UvLayer>();
    public UvLayer CutLayer;
    public UvPage(IEnumerable<UvShape> shapes) { var layer = new UvLayer(this); Layers.Add(layer); foreach (var s in shapes) layer.Add(s); }
    public UvLayer CreateLayer(string name) { CutLayer = new UvLayer(this); Layers.Insert(0, CutLayer); return CutLayer; }
}
public sealed class UvLayer
{
    public readonly UvPage Page;
    public bool Printable = true, Visible = true, Editable = true;
    public List<UvShape> Shapes = new List<UvShape>();
    public UvLayer(UvPage page) { Page = page; }
    public void Add(UvShape shape) { Shapes.Add(shape); shape.Attach(this, Shapes); }
}
public sealed class UvFill { public int Type; }
public sealed class UvColor { public bool IsSpot = true; public string SpotColorName = "CUT"; public double Tint = 100; }
public sealed class UvOutline { public int Type = 1; public UvColor Color = new UvColor(); public double Width = .1; public bool Overprint = true; }
public sealed class UvClip { public List<UvShape> Shapes; public UvClip(params UvShape[] shapes) { Shapes = shapes.ToList(); } }
public sealed class UvCurve
{
    private readonly UvShape _shape;
    public UvCurve(UvShape shape) { _shape = shape; }
    public List<int> SubPaths => Enumerable.Range(0, _shape.Parts?.Count ?? 1).ToList();
    public double Length { get { _shape.LengthReads++; return _shape.Parts?.Sum(p => p.Curve.Length) ?? _shape.PathLength; } }
}
public sealed class UvShape
{
    private static int _id;
    public int StaticID = ++_id, Type = 3;
    public string Name; public double Left, Bottom, Width, Height; public double Top => Bottom + Height;
    public UvOutline Outline = new UvOutline(); public UvFill Fill = new UvFill();
    public UvClip PowerClip;
    public UvLayer Layer; public List<UvShape> Parent, Shapes = new List<UvShape>(), Parts;
    public bool Deleted;
    public int BoundsReads, LengthReads;
    public double PathLength = 40;
    public UvCurve Curve => Type == 3 ? new UvCurve(this) : throw new InvalidOperationException("Not a curve");
    public UvShape(string name, double left, double bottom, double width, double height) { Name = name; Left = left; Bottom = bottom; Width = width; Height = height; }
    public static UvShape Group(params UvShape[] shapes) => new UvShape("group", 0, 0, 1, 1) { Type = 7, Shapes = shapes.ToList() };
    public static UvShape Compound(params UvShape[] paths)
    {
        double left = paths.Min(p => p.Left), bottom = paths.Min(p => p.Bottom);
        return new UvShape("compound", left, bottom, paths.Max(p => p.Left + p.Width) - left, paths.Max(p => p.Top) - bottom) { Parts = paths.ToList() };
    }
    public void Attach(UvLayer layer, List<UvShape> parent)
    {
        Layer = layer; Parent = parent;
        foreach (var child in Shapes) child.Attach(layer, Shapes);
        if (PowerClip != null) foreach (var child in PowerClip.Shapes) child.Attach(layer, PowerClip.Shapes);
    }
    public void GetBoundingBox(out double x, out double y, out double w, out double h, bool outline) { BoundsReads++; x = Left; y = Bottom; w = Width; h = Height; }
    public void ConvertToCurves() { Type = 3; }
    public void MoveToLayer(UvLayer target)
    { target.Page.IndividualMoves++; MoveInternal(target); }
    public void MoveInternal(UvLayer target)
    {
        Parent.Remove(this); target.Shapes.Insert(0, this); Attach(target, target.Shapes);
        switch (target.Page.Failure)
        {
            case "shift": Left += 1; break;
            case "scale": Width += 1; break;
            case "length": PathLength += 1; break;
            case "color": Outline.Color.SpotColorName = "White"; break;
            case "tint": Outline.Color.Tint = 80; break;
            case "pen": Outline.Width = .3; break;
            case "fill": Fill.Type = 1; break;
        }
    }
    public IEnumerable<UvShape> BreakApartEx()
    {
        Parent.Remove(this); Deleted = true;
        var paths = Parts.ToArray();
        foreach (var p in paths) { p.Outline = Outline; Layer.Add(p); }
        return Layer.Page.Failure == "drop" ? paths.Take(1) : paths;
    }
    public void OrderToFront()
    {
        Layer.Page.OrderMoves++;
        if (Layer.Page.Failure == "order") return;
        if (Layer.Page.Failure == "order-shift") Left += 1;
        Parent.Remove(this); Parent.Insert(0, this);
    }
}
namespace VanyaTools.Native
{
    internal static class Log { internal static void Info(string text) { } }
    internal static class PrintExportBounds
    {
        internal const double TransferToleranceMm = .02;
        internal static bool Finite(double value) => !Double.IsNaN(value) && !Double.IsInfinity(value);
    }
}
