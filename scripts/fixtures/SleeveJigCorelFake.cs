using System;
using System.Collections.Generic;

public sealed class JigTestApp
{
    public bool Optimization, FailCurve;
    public JigTestDoc LastDocument;
    public JigTestWindow ActiveWindow = new JigTestWindow();
    public JigTestDoc CreateDocument() { return LastDocument = new JigTestDoc { FailCurve = FailCurve }; }
}
public sealed class JigTestWindow { public int Refreshes; public void Refresh() { Refreshes++; } }
public sealed class JigTestDoc
{
    public int Unit;
    public bool Dirty = true, Closed, Ended, FailCurve;
    public JigTestPages Pages = new JigTestPages();
    public JigTestPage ActivePage { get { return Pages[Pages.Count]; } }
    public JigTestDoc() { Pages.Items.Add(new JigTestPage()); }
    public void BeginCommandGroup(string name) { }
    public void EndCommandGroup() { Ended = true; }
    public JigTestPage AddPages(int count) { var p = new JigTestPage(); Pages.Items.Add(p); return p; }
    public JigTestCurve CreateCurve() { if (FailCurve) throw new Exception("Injected curve failure"); return new JigTestCurve(); }
    public void Close() { if (Dirty) throw new Exception("Would prompt to save"); Closed = true; }
}
public sealed class JigTestPages
{
    public List<JigTestPage> Items = new List<JigTestPage>();
    public int Count { get { return Items.Count; } }
    public JigTestPage this[int index] { get { return Items[index - 1]; } }
}
public sealed class JigTestPage
{
    public string Name;
    public double Width, Height;
    public List<JigTestLayer> Layers = new List<JigTestLayer>();
    public void Activate() { }
    public void SetSize(double width, double height) { Width = width; Height = height; }
    public void GetBoundingBox(ref double x, ref double y, ref double width, ref double height)
    { x = 0; y = 0; width = Width; height = Height; }
    public JigTestLayer CreateLayer(string name) { var p = new JigTestLayer { Name = name }; Layers.Add(p); return p; }
}
public sealed class JigTestLayer
{
    public string Name;
    public bool Printable = true;
    public List<JigTestShape> Shapes = new List<JigTestShape>();
    public void Activate() { }
    public JigTestShape CreateCurve(JigTestCurve curve) { var s = new JigTestShape { Curve = curve }; Shapes.Add(s); return s; }
    public JigTestShape CreateArtisticTextWide(double x, double y, string text, int a, int b, string font, float size)
    { var s = new JigTestShape(); Shapes.Add(s); return s; }
}
public sealed class JigTestCurve
{
    public List<JigTestPath> Paths = new List<JigTestPath>();
    public JigTestPath CreateSubPath(double x, double y) { var p = new JigTestPath(); p.AppendLineSegment(x, y); Paths.Add(p); return p; }
}
public sealed class JigTestPath
{
    public bool Closed;
    public List<double[]> Points = new List<double[]>();
    public void AppendLineSegment(double x, double y) { Points.Add(new[] { x, y }); }
}
public sealed class JigTestShape
{
    public string Name;
    public double LeftX, TopY;
    public JigTestCurve Curve;
    public JigTestFill Fill = new JigTestFill();
    public JigTestOutline Outline = new JigTestOutline();
    public void Move(double x, double y) { LeftX += x; TopY += y; }
}
public sealed class JigTestFill
{
    public bool Empty;
    public JigTestColor UniformColor = new JigTestColor();
    public void ApplyNoFill() { Empty = true; }
}
public sealed class JigTestOutline
{
    public double Width;
    public JigTestColor Color = new JigTestColor();
    public void SetNoOutline() { }
}
public sealed class JigTestColor
{
    public int R, G, B;
    public void RGBAssign(int r, int g, int b) { R = r; G = g; B = b; }
}
