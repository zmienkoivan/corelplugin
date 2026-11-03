using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value,string message) { if(!value) throw new Exception(message);_checks++; }
    private static void Equal(double actual,double expected,string message)=>Check(Math.Abs(actual-expected)<.000001,message);
    private static void Reject(Action work,string message)
    { try {work();} catch(InvalidOperationException) {_checks++;return;} throw new Exception(message); }
    private static int Main()
    {
        try { Geometry();NativeAdapter();FontTransfer();Console.WriteLine("Print selection checks passed: "+_checks+". Fake transfer only; no Corel or customer files.");return 0; }
        catch(Exception error) {Console.Error.WriteLine(error);return 1;}
    }
    private static void Geometry()
    {
        var ink=new PrintExportBounds {Left=-73.2,Bottom=-19.7,Width=220.79,Height=244.412};
        var page=PrintExportBounds.PaddedPage(ink,2);
        Equal(page.Width,224.79,"2 mm each horizontal edge");Equal(page.Height,248.412,"2 mm each vertical edge");
        Equal(page.Left,-75.2,"Negative left supported");Equal(page.Bottom,-21.7,"Negative bottom supported");
        Equal(ink.Width,220.79,"Source width not scaled");Equal(ink.Height,244.412,"Source height not scaled");
        PrintExportBounds.RequireInsidePage(ink,page.Left,page.Bottom,page.Width,page.Height,2);_checks++;
        foreach(var shift in new[] {new[] {-.2,0.0},new[] {.2,0.0},new[] {0.0,-.2},new[] {0.0,.2}})
            Reject(()=>PrintExportBounds.RequireInsidePage(new PrintExportBounds {Left=ink.Left+shift[0],Bottom=ink.Bottom+shift[1],Width=ink.Width,Height=ink.Height},page.Left,page.Bottom,page.Width,page.Height,2),"Artwork invaded guard");
        foreach(double value in new[] {-1.0,Double.NaN,Double.PositiveInfinity}) Reject(()=>PrintExportBounds.PaddedPage(ink,value),"Invalid margin accepted");
        Reject(()=>PrintExportBounds.PaddedPage(new PrintExportBounds {Width=5078,Height=100},2),"Oversize page must not scale");
        var plain=PrintExportBounds.PaddedPage(ink,0);Equal(plain.Width,ink.Width,"Other callers keep original width");Equal(plain.Height,ink.Height,"Other callers keep original height");
    }
    private static void NativeAdapter()
    {
        foreach(double margin in new[] {0.0,2.0})
        {
            var app=new FakePrintApp();CorelClipboardTransfer.App=app;
            // Ink includes a 0.18 mm outline around the original 100 x 50 mm geometry.
            var ink=new FakePrintShape(-20.09,-30.09,100.18,50.18);var range=new FakePrintRange(app,new[] {ink});
            var before=new[] {ink.Left,ink.Bottom,ink.Width,ink.Height};
            var workspace=PrintSelectionWorkspace.Create(app,app.Source,app.Source.ActivePage,range,margin);
            var copy=app.Output.ActiveLayer.Range;
            Check(app.LastCreatedSnapshot.ReadsIncludeOutline&&copy.ReadsIncludeOutline,"Native bounds include outlines");
            Equal(copy.Width,100.18,"Ink width preserved 1:1");Equal(copy.Height,50.18,"Ink height preserved 1:1");
            Equal(workspace.PageWidthMm,100.18+margin*2,"PDF validation uses padded width");Equal(workspace.PageHeightMm,50.18+margin*2,"PDF validation uses padded height");
            Equal(copy.Left,app.Output.ActivePage.LeftX+margin,"Left guard after changed page origin");Equal(copy.Bottom,app.Output.ActivePage.BottomY+margin,"Bottom guard after changed page origin");
            Check(copy.CreateSelectionCalls==1,"Transferred objects selected");Check(copy.BoundingBoxReads>=3,"Bounds reread after resizing and moving");
            Check(new[] {ink.Left,ink.Bottom,ink.Width,ink.Height}.SequenceEqual(before),"Original never moved or scaled");
            Check(app.Output.ColorContext==app.Source.ColorContext,"Native color context copied");workspace.Dispose();
            Check(app.Output.Closed&&!app.Output.Dirty,"Owned workspace closed unsaved");
            Check(app.Source.Activations==1&&app.Source.ActivePage.Activations==1,"Source document and page restored");
            Check(app.LastCreatedSnapshot.CreateSelectionCalls==1,"Source selection snapshot restored");
        }
        foreach(string failure in new[] {"PasteScale","ResizeScale","MoveShift","PageSize"})
        {
            var app=new FakePrintApp {Failure=failure};CorelClipboardTransfer.App=app;
            var range=new FakePrintRange(app,new[] {new FakePrintShape(-5,-9,30,40)});
            Reject(()=>PrintSelectionWorkspace.Create(app,app.Source,app.Source.ActivePage,range,2),"Invalid transfer accepted: "+failure);
            Check(app.Output.Closed&&app.Source.Activations==1,"Failure restores original: "+failure);
        }
    }
    private static void FontTransfer()
    {
        foreach(bool dirty in new[] {false,true}) foreach(string placement in new[]{"root","group","powerclip","symbol"})
        {
            var app=new FakePrintApp();CorelClipboardTransfer.App=app;app.Source.Dirty=dirty;
            var text=new FakePrintShape(0,0,30,40) {Type=6,Glyphs="original cached font",Color="spot-orange"};
            FakePrintShape root=text;
            if(placement=="group") root=new FakePrintShape(0,0,100,50){Type=7,Shapes=new List<FakePrintShape>{text}};
            if(placement=="powerclip") root=new FakePrintShape(0,0,100,50){PowerClip=new FakePrintClip(new[]{new FakePrintShape(0,0,100,50){Type=7,Shapes=new List<FakePrintShape>{text}}})};
            if(placement=="symbol") root=new FakePrintShape(0,0,100,50){Type=23,Definition=new List<FakePrintShape>{text}};
            var range=new FakePrintRange(app,new[]{root});
            using(var workspace=PrintSelectionWorkspace.Create(app,app.Source,app.Source.ActivePage,range,2))
            {
                var drawn=app.Output.ActiveLayer.Range.Items.SelectMany(s=>s.All()).Where(s=>s.Glyphs!=null).ToArray();
                Check(drawn.Length==1&&drawn[0].Type==3&&drawn[0].Glyphs=="original cached font","Font changed before outlining: "+placement);
                Check(drawn[0].Color=="spot-orange","Text preparation changed spot colour");
                Check(text.Type==6&&!text.Deleted&&text.Glyphs=="original cached font","Original text was converted or deleted");
                Check(app.ConvertedInSource>0&&!app.ConvertedInOutput,"Text outlined after cross-document paste");
            }
            Check(app.Temporary.All(s=>s.Deleted)&&app.Source.Groups==0,"Temporary geometry or command group leaked");
            Check(app.Source.Dirty==dirty&&text.Type==6&&!text.Deleted,"Source dirty/text state was not restored");
            Check(app.SelectedRange.Items.Single()==root,"Original selection not restored after text cleanup");
        }
        foreach(string failure in new[]{"ConvertFail","ConvertIgnored","DuplicateShift","DuplicatePartial","PasteScale","CleanupFail"})
        {
            var app=new FakePrintApp {Failure=failure};CorelClipboardTransfer.App=app;app.Source.Dirty=false;
            var text=new FakePrintShape(0,0,30,40){Type=6,Glyphs="original cached font"};var range=new FakePrintRange(app,new[]{text});
            bool failed=false;
            try {using(var workspace=PrintSelectionWorkspace.Create(app,app.Source,app.Source.ActivePage,range,2)) {}}
            catch(InvalidOperationException) {failed=true;}catch(PrintTextCleanupException){failed=true;}
            Check(failed,"Invalid font preparation accepted: "+failure);
            Check(text.Type==6&&!text.Deleted&&app.Source.Groups==0,"Failure changed original or left command group open: "+failure);
            Check(app.Source.Dirty==(failure=="CleanupFail"||failure=="DuplicatePartial"),"Failure hid retained source copies or changed dirty state: "+failure);
            if(failure!="CleanupFail"&&failure!="DuplicatePartial") Check(app.Temporary.All(s=>s.Deleted),"Failure leaked temporary copies: "+failure);
            if(app.Output!=null) Check(app.Output.Closed,"Failure left output CDR open");
        }
        var legacy=new FakePrintApp();CorelClipboardTransfer.App=legacy;
        var original=new FakePrintShape(0,0,30,40){Type=6,Glyphs="original cached font"};
        CorelClipboardTransfer.Copy(new FakePrintRange(legacy,new[]{original}),"legacy");
        var pasted=legacy.CreateDocument().ActiveLayer.PasteEx(null).Items[0];pasted.ConvertToCurves();
        Check(pasted.Glyphs=="substituted font"&&pasted.Type==3,"Fixture does not reproduce late outlining of wrong glyphs");
    }
}
namespace VanyaTools.Native
{
    internal static class CorelConstants {internal const int CdrMillimeter=3,CdrGroupShape=7,CdrTextShape=6;}
    internal static class Log {internal static void Info(string text) {} internal static void Error(string text,Exception error) {}}
    internal static class CorelClipboardTransfer
    {
        internal static FakePrintApp App;
        internal static void Copy(object range,string label) {App.Clipboard=(FakePrintRange)range;}
        internal static void WaitAvailable(string label) {}
    }
}
public sealed class FakePrintShape
{
    private static int _next;public int StaticID {get;}=System.Threading.Interlocked.Increment(ref _next);
    public FakePrintApp App;public int Type=3;public bool Deleted;public string Glyphs,Color;
    public List<FakePrintShape> Shapes=new List<FakePrintShape>(),Definition=new List<FakePrintShape>();
    public FakePrintClip PowerClip;public FakePrintSymbol Symbol=>new FakePrintSymbol(this);
    public double Left,Bottom,Width,Height;
    public FakePrintShape(double left,double bottom,double width,double height) {Left=left;Bottom=bottom;Width=width;Height=height;}
    public IEnumerable<FakePrintShape> All()=>new[]{this}.Concat(Shapes.SelectMany(s=>s.All())).Concat(PowerClip?.Shapes.SelectMany(s=>s.All())??Enumerable.Empty<FakePrintShape>());
    public FakePrintShape Copy(double x=175,double y=-81,bool transfer=true)
    {
        var copy=new FakePrintShape(Left+x,Bottom+y,Width,Height){App=App,Type=Type,Glyphs=transfer&&Type==6?"substituted font":Glyphs,Color=Color,
            Shapes=Shapes.Select(s=>s.Copy(x,y,transfer)).ToList(),Definition=Definition.Select(s=>s.Copy(x,y,transfer)).ToList()};
        if(PowerClip!=null)copy.PowerClip=new FakePrintClip(PowerClip.Shapes.Select(s=>s.Copy(x,y,transfer)));
        return copy;
    }
    public void ConvertToCurves()
    {
        if(App.Failure=="ConvertFail")throw new InvalidOperationException("Synthetic conversion failure");
        if(App.Failure=="ConvertIgnored")return;
        if(App.Current==App.Source)App.ConvertedInSource++;else App.ConvertedInOutput=true;Type=3;
    }
    public void Delete(){if(App.Failure=="CleanupFail")throw new InvalidOperationException("Synthetic cleanup failure");foreach(var s in All())s.Deleted=true;}
}
public sealed class FakePrintClip {public List<FakePrintShape> Shapes;public FakePrintClip(IEnumerable<FakePrintShape> shapes){Shapes=shapes.ToList();}}
public sealed class FakePrintSymbol
{
    private readonly FakePrintShape _shape;public FakePrintSymbol(FakePrintShape shape){_shape=shape;}
    public FakePrintRange RevertToShapes(){_shape.Deleted=true;var shapes=_shape.Definition.Select(s=>s.Copy(0,0,false)).ToArray();_shape.App.Temporary.AddRange(shapes);return new FakePrintRange(_shape.App,shapes);}
}
public sealed class FakePrintRange:IEnumerable
{
    private readonly FakePrintApp _app;
    public readonly List<FakePrintShape> Items;
    public bool ReadsIncludeOutline=true;public int BoundingBoxReads,CreateSelectionCalls;
    public FakePrintRange(FakePrintApp app,IEnumerable<FakePrintShape> items=null) {_app=app;Items=new List<FakePrintShape>(items??Enumerable.Empty<FakePrintShape>());foreach(var s in Items)Attach(s,app);}
    private static void Attach(FakePrintShape shape,FakePrintApp app){foreach(var s in shape.All().Concat(shape.Definition.SelectMany(s=>s.All())))s.App=app;}
    public int Count=>Items.Count;
    public double Left=>Items.Min(s=>s.Left);public double Bottom=>Items.Min(s=>s.Bottom);
    public double Width=>Items.Max(s=>s.Left+s.Width)-Left;public double Height=>Items.Max(s=>s.Bottom+s.Height)-Bottom;
    public void Add(object shape)=>Items.Add((FakePrintShape)shape);
    public void GetBoundingBox(out double left,out double bottom,out double width,out double height,bool includeOutline)
    {ReadsIncludeOutline&=includeOutline;BoundingBoxReads++;left=Left;bottom=Bottom;width=Width;height=Height;}
    public void Move(double x,double y) {foreach(var s in Items) {s.Left+=x+(_app.Failure=="MoveShift"?.1:0);s.Bottom+=y;}}
    public void CreateSelection(){CreateSelectionCalls++;_app.SelectedRange=this;}public FakePrintRange All()=>this;
    public void Delete(){foreach(var s in Items)s.Delete();Items.Clear();}
    public FakePrintRange Duplicate(double x,double y)
    {var copies=Items.Select(s=>s.Copy(x+(_app.Failure=="DuplicateShift"?1:0),y,false)).ToArray();_app.Temporary.AddRange(copies);_app.Source.Dirty=true;
        if(_app.Failure=="DuplicatePartial")throw new InvalidOperationException("Synthetic partial duplication");return new FakePrintRange(_app,copies);}
    public IEnumerator GetEnumerator()=>Items.GetEnumerator();
}
public sealed class FakePrintLayer
{
    private readonly FakePrintApp _app;public FakePrintRange Range;
    public FakePrintLayer(FakePrintApp app) {_app=app;Range=new FakePrintRange(app);}
    public FakePrintRange Shapes=>Range;
    public FakePrintRange PasteEx(object options)
    {Range=new FakePrintRange(_app,_app.Clipboard.Items.Select(s=>s.Copy()));if(_app.Failure=="PasteScale") Range.Items[0].Width+=1;return Range;}
}
public sealed class FakePrintPage
{
    private readonly FakePrintDocument _doc;public FakePrintPage(FakePrintDocument document) {_doc=document;}
    public double LeftX {get;private set;}=-41;public double BottomY {get;private set;}=-77;
    public double SizeWidth {get;private set;}=210;public double SizeHeight {get;private set;}=297;public int Activations;
    public void SetSize(double width,double height)
    {
        SizeWidth=width+(_doc.App.Failure=="PageSize"?1:0);SizeHeight=height;LeftX=17-width/2;BottomY=-31-height*.2;
        foreach(var shape in _doc.ActiveLayer.Range.Items) {shape.Left+=112;shape.Bottom-=29;}
        if(_doc.App.Failure=="ResizeScale") _doc.ActiveLayer.Range.Items[0].Height+=1;
    }
    public void Activate()=>Activations++;
}
public sealed class FakePrintDocument
{
    public readonly FakePrintApp App;public int Unit,Activations,Groups;public string ColorContext="native-test-profile";
    public bool Dirty=true,Closed;public FakePrintPage ActivePage;public FakePrintLayer ActiveLayer;
    public FakePrintDocument(FakePrintApp app) {App=app;ActivePage=new FakePrintPage(this);ActiveLayer=new FakePrintLayer(app);}
    public void AssignColorContext(object value)=>ColorContext=(string)value;public void Activate(){Activations++;App.Current=this;}public void Close()=>Closed=true;
    public void BeginCommandGroup(string caption){Groups++;}public void EndCommandGroup(){Groups--;}
}
public sealed class FakePrintApp
{
    public string Failure;public readonly FakePrintDocument Source;public FakePrintDocument Output,Current;
    public int ConvertedInSource;public bool ConvertedInOutput;public FakePrintRange SelectedRange;
    public List<FakePrintShape> Temporary=new List<FakePrintShape>();
    public FakePrintRange Clipboard,LastCreatedSnapshot;public FakePrintApp() {Source=new FakePrintDocument(this);Current=Source;}
    public FakePrintRange CreateShapeRange() {LastCreatedSnapshot=new FakePrintRange(this);return LastCreatedSnapshot;}
    public FakePrintDocument CreateDocument() {Output=new FakePrintDocument(this);Current=Output;return Output;}public object CreateStructPasteOptions()=>new object();
}
namespace VanyaTools.Native
{
    internal static class WorkTimeRuntime { internal static void IgnoreTemporaryDocument(object document) { } }
}
