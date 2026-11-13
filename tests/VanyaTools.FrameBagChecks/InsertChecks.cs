using System;
using System.Collections.Generic;
using System.Linq;
using VanyaTools.Native;

internal static class InsertChecks
{
    private static bool _announced;
    internal static void Run(FrameBagPlan plan)
    {
        var app = new FakeBagApp();
        int result = FrameBagService.Insert(app,plan);
        Require(result==plan.Parts.Length && app.ActiveDocument.Pages.Count==2,"Kit not placed on its own page.");
        var page=app.ActiveDocument.ActivePage;
        var cut=page.Layers.First(layer=>layer.Printable);
        var guides=page.Layers.First(layer=>!layer.Printable);
        Require(cut.Shapes.Count==plan.Parts.Length && app.Range.Selected && app.Range.Shapes.All(cut.Shapes.Contains),"Selection contains guides or is incomplete.");
        for(int n=0;n<plan.Parts.Length;n++)
        {
            var path=cut.Shapes[n].Curve.Path;
            var part=plan.Parts[n];
            Require(path.Closed && path.Points.Count==part.Cut.Length && cut.Shapes[n].Curve.Paths.Count==1+part.Cutouts.Length &&
                cut.Shapes[n].Curve.Paths.All(p=>p.Closed),"A cutting piece split, opened or lost a slot.");
            double min=path.Points.Min(p=>p.X),max=path.Points.Max(p=>p.X);
            Require(Math.Abs((max-min)-part.Width)<.00001,"Native placement changed millimetres.");
            Require(cut.Shapes[n].Outline.Color.Red==255 && cut.Shapes[n].Outline.Color.Green==0,"Cutting color lost.");
        }
        Require(guides.Shapes.Count>plan.Parts.Length && app.ActiveDocument.Unit==7 && app.ActiveDocument.Ends==1 &&
            !app.Optimization && app.ActiveDocument.Dirty,"Guide separation or document state restoration failed.");
        var failed=new FakeBagApp();failed.ActiveDocument.FailAtCurve=3;
        bool threw=false;
        try{FrameBagService.Insert(failed,plan);}catch(InvalidOperationException){threw=true;}
        Require(threw && failed.ActiveDocument.Pages.Count==1 && failed.ActiveDocument.ActivePage==failed.ActiveDocument.Pages[0] &&
            failed.ActiveDocument.Unit==7 && !failed.Optimization && !failed.ActiveDocument.Dirty &&
            failed.ActiveDocument.Ends==1,"Failed insertion left a partial page or altered source state.");
        if (!_announced) Console.WriteLine("Native kit adapter: closed pieces, separate guides, new page, units and rollback passed.");
        _announced = true;
    }
    private static void Require(bool ok,string text){if(!ok)throw new Exception(text);}
}
public sealed class FakeBagApp
{
    public FakeBagDoc ActiveDocument {get;}=new FakeBagDoc();
    public bool Optimization {get;set;}
    public FakeBagWindow ActiveWindow {get;}=new FakeBagWindow();
    public FakeBagRange Range {get;private set;}
    public FakeBagRange CreateShapeRange(){Range=new FakeBagRange();return Range;}
}
public sealed class FakeBagWindow{public void Refresh(){}}
public sealed class FakeBagDoc
{
    public int Unit {get;set;}=7;
    public bool Dirty {get;set;}
    public int Ends,FailAtCurve,Curves;
    public List<FakeBagPage> Pages {get;}=new List<FakeBagPage>();
    public FakeBagPage ActivePage {get;set;}
    public FakeBagDoc(){ActivePage=new FakeBagPage(this);Pages.Add(ActivePage);}
    public FakeBagPage AddPages(int count){var p=new FakeBagPage(this);Pages.Add(p);Dirty=true;return p;}
    public void BeginCommandGroup(string title){}
    public void EndCommandGroup(){Ends++;}
    public FakeBagCurve CreateCurve(){if(++Curves==FailAtCurve)throw new InvalidOperationException("Simulated contour failure.");return new FakeBagCurve();}
}
public sealed class FakeBagPage
{
    private readonly FakeBagDoc _doc;
    public string Name {get;set;}
    public double Width,Height;
    public List<FakeBagLayer> Layers {get;}=new List<FakeBagLayer>();
    public FakeBagPage(FakeBagDoc doc){_doc=doc;}
    public void Activate(){_doc.ActivePage=this;}
    public void Delete(){_doc.Pages.Remove(this);}
    public void SetSize(double w,double h){Width=w;Height=h;}
    public void GetBoundingBox(ref double x,ref double y,ref double w,ref double h){x=-Width/2;y=-Height/2;w=Width;h=Height;}
    public FakeBagLayer CreateLayer(string title){var layer=new FakeBagLayer();Layers.Add(layer);return layer;}
}
public sealed class FakeBagLayer
{
    public bool Printable {get;set;}=true;
    public List<FakeBagShape> Shapes {get;}=new List<FakeBagShape>();
    public void Activate(){}
    public FakeBagShape CreateCurve(FakeBagCurve curve){var shape=new FakeBagShape{Curve=curve};Shapes.Add(shape);return shape;}
    public FakeBagShape CreateArtisticTextWide(double x,double y,string text,int a,int b,string font,float size)
    {var shape=new FakeBagShape{SizeWidth=text.Length*.5};Shapes.Add(shape);return shape;}
}
public sealed class FakeBagShape
{
    public FakeBagCurve Curve;
    public FakeBagFill Fill {get;}=new FakeBagFill();
    public FakeBagOutline Outline {get;}=new FakeBagOutline();
    public string Name {get;set;}
    public double LeftX {get;set;}
    public double TopY {get;set;}
    public double SizeWidth {get;set;}
    public void Move(double x,double y){LeftX+=x;TopY+=y;}
}
public sealed class FakeBagFill
{
    public FakeBagColor UniformColor {get;}=new FakeBagColor();
    public void ApplyNoFill(){}
}
public sealed class FakeBagColor
{
    public int Red,Green,Blue;
    public void RGBAssign(int r,int g,int b){Red=r;Green=g;Blue=b;}
}
public sealed class FakeBagOutline
{
    public double Width {get;set;}
    public FakeBagColor Color {get;}=new FakeBagColor();
    public void SetNoOutline(){}
}
public sealed class FakeBagCurve
{
    public FakeBagPath Path;
    public List<FakeBagPath> Paths {get;}=new List<FakeBagPath>();
    public FakeBagPath CreateSubPath(double x,double y){var next=new FakeBagPath();Paths.Add(next);if(Path==null)Path=next;next.AppendLineSegment(x,y);return next;}
}
public sealed class FakeBagPath
{
    public bool Closed {get;set;}
    public List<BagTestPoint> Points {get;}=new List<BagTestPoint>();
    public void AppendLineSegment(double x,double y){Points.Add(new BagTestPoint{X=x,Y=y});}
}
public sealed class BagTestPoint {public double X,Y;}
public sealed class FakeBagRange
{
    public bool Selected;
    public List<FakeBagShape> Shapes {get;}=new List<FakeBagShape>();
    public void Add(FakeBagShape shape){Shapes.Add(shape);}
    public void CreateSelection(){Selected=true;}
}
namespace VanyaTools.Native
{
    internal static class CorelConstants {internal const int CdrMillimeter=3;}
    internal static class CorelApp {internal static object Get(){throw new Exception("Live Corel must not be used in checks.");}}
    internal static class Log {internal static void Error(string title,Exception error){}}
}
