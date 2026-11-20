using System;
using System.Collections.Generic;
using System.Linq;
using VanyaTools.Native;

internal static class SpotOrderChecks
{
    private static int checks;
    private static void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string message)
    { try { action(); } catch (InvalidOperationException) { checks++; return; } throw new Exception(message); }
    internal static void Run()
    {
        try
        {
            foreach (bool reverse in new[] { false, true }) foreach (int tint in new[] { 10, 80, 100 })
            {
                var app = new ExportApp { ReverseEnumeration = reverse };
                var page = new ExportPage(app); var baseLayer = page.CreateLayer("Art");
                var white = baseLayer.Add("WHITE", tint); var art = baseLayer.Add(null); var clear = baseLayer.Add("CLEAR", 37);
                // Reproduce the supplied button PDF: WHITE, opaque art, CLEAR.
                Check(page.PaintOrder().SequenceEqual(new[] { white, art, clear }), "Fixture does not reproduce knockout order");
                int count = PrintSpotPlateOrder.Prepare(app, page);
                Check(count == 2 && page.PaintOrder().SequenceEqual(new[] { art, white, clear }), "Technical inks remain under knockout artwork");
                Check(white.Fill.UniformColor.Tint == tint && clear.Fill.UniformColor.Tint == 37, "Spot density changed");
                Check(white.OverprintFill && clear.OverprintFill && !art.OverprintFill, "Plate preparation changed artwork overprint");
                Check(white.X == 10 && white.Y == 20 && white.Width == 30 && white.Height == 40, "Geometry moved");
                Check(white.Fill.UniformColor.IsSpot && white.Fill.UniformColor.SpotColorName == "WHITE", "WHITE turned into process colour");
            }
            foreach (string ink in new[] { "WHITE", "CLEAR" })
            {
                var app = new ExportApp(); var page = new ExportPage(app); var layer = page.CreateLayer("Art");
                var first = layer.Add(ink, 25); var art = layer.Add(null); var last = layer.Add(ink, 75);
                Check(PrintSpotPlateOrder.Prepare(app, page) == 2 && page.PaintOrder().Last() != art && layer.Shapes.SequenceEqual(new[] { art }), "Multiple plates or single-ink export lost objects");
                Check(first.Fill.UniformColor.Tint == 25 && last.Fill.UniformColor.Tint == 75, "Multiple densities merged");
            }
            var ordinary = new ExportPage(new ExportApp()); var ordinaryLayer = ordinary.CreateLayer("Art");
            ordinaryLayer.Add("CUT"); ordinaryLayer.Add("white"); ordinaryLayer.Add(null);
            var unchanged = ordinary.PaintOrder().ToArray();
            Check(PrintSpotPlateOrder.Prepare(ordinary.App, ordinary) == 0 && ordinary.Layers.Count == 1 && ordinary.PaintOrder().SequenceEqual(unchanged), "Ordinary artwork or unrelated spots changed");
            foreach (bool clipped in new[] { false, true })
            {
                var page = new ExportPage(new ExportApp()); var layer = page.CreateLayer("Art"); var child = layer.Add("WHITE");
                layer.Shapes.Remove(child); var root = layer.Add(null);
                if (clipped) root.PowerClip = new ExportClip { Shapes = new List<ExportShape> { child } };
                else { root.Type = 7; root.Shapes.Add(child); }
                Reject(() => PrintSpotPlateOrder.Prepare(page.App, page), "Nested plate lost group/PowerClip context");
                Check(page.Layers.Count == 1 && layer.Shapes.Single() == root, "Rejected nesting partially changed output");
            }
            foreach (string failure in new[] { "move", "geometry", "tint", "overprint", "printable" })
            {
                var page = new ExportPage(new ExportApp { Failure = failure }); var layer = page.CreateLayer("Art"); layer.Add("WHITE"); layer.Add(null);
                Reject(() => PrintSpotPlateOrder.Prepare(page.App, page), "Invalid plate move accepted: " + failure);
            }
            Console.WriteLine("Print spot-order checks passed: " + checks + ". Supplied PDF order reproduced with fake native objects; no source document modified.");
            return;
        }
        catch { throw; }
    }
}

public sealed class ExportApp
{
    public string Failure; public bool ReverseEnumeration;
    public ExportRange CreateShapeRange() => new ExportRange(this);
}
public sealed class ExportPage
{
    public readonly ExportApp App; public List<ExportLayer> Layers = new List<ExportLayer>();
    public ExportPage(ExportApp app) { App = app; }
    public ExportLayer CreateLayer(string name) { var layer = new ExportLayer(this, name); Layers.Add(layer); return layer; }
    public IEnumerable<ExportShape> PaintOrder() => Layers.SelectMany(l => l.Shapes);
}
public sealed class ExportLayer
{
    public readonly ExportPage Page; public string Name; public bool Visible = true, Editable = true;
    public bool Printable { get => Page.App.Failure != "printable" || Name == "Art"; set { } }
    public List<ExportShape> Shapes = new List<ExportShape>();
    public ExportLayer(ExportPage page, string name) { Page = page; Name = name; }
    public ExportShape Add(string ink, int tint = 100)
    {
        var shape = new ExportShape { Layer = this };
        if (ink != null) { shape.Fill.Type = 1; shape.Fill.UniformColor = new ExportColor { IsSpot = true, SpotColorName = ink, Tint = tint }; }
        Shapes.Add(shape); return shape;
    }
}
public sealed class ExportShape
{
    private static int next; public int StaticID = ++next, Type = 3;
    public ExportLayer Layer; public ExportFill Fill = new ExportFill(); public ExportOutline Outline = new ExportOutline();
    public List<ExportShape> Shapes = new List<ExportShape>(); public ExportClip PowerClip;
    public double X = 10, Y = 20, Width = 30, Height = 40;
    private bool overprint;
    public bool OverprintFill { get => overprint; set { if (Layer.Page.App.Failure != "overprint") overprint = value; } }
    public void GetBoundingBox(out double x, out double y, out double w, out double h, bool outline)
    { x = X; y = Y; w = Width; h = Height; }
}
public sealed class ExportRange
{
    private readonly ExportApp app; private readonly List<ExportShape> items = new List<ExportShape>();
    public ExportRange(ExportApp app) { this.app = app; }
    public void Add(object shape) => items.Add((ExportShape)shape);
    public void MoveToLayer(ExportLayer target)
    {
        if (app.Failure == "move") return;
        foreach (var shape in app.ReverseEnumeration ? items.AsEnumerable().Reverse() : items)
        {
            shape.Layer.Shapes.Remove(shape); shape.Layer = target; target.Shapes.Add(shape);
            if (app.Failure == "geometry") shape.X++;
            if (app.Failure == "tint") shape.Fill.UniformColor.Tint = 1;
        }
    }
}
public sealed class ExportFill { public int Type; public ExportColor UniformColor = new ExportColor(); }
public sealed class ExportColor { public bool IsSpot; public string SpotColorName; public double Tint; }
public sealed class ExportOutline { public int Type; }
public sealed class ExportClip { public List<ExportShape> Shapes; }
