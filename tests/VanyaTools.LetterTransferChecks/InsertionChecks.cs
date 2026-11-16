using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Media;
using VanyaTools.Native;
internal static class InsertionChecks
{
    internal static void Run(string root)
    {
        var set=Program.Printable();string directory=LetterTransferStore.Create(root,set);foreach(var glyph in set.Glyphs)LetterTransferImages.Save(Program.Solid(glyph.Width,glyph.Height,Colors.Black),LetterTransferStore.Resolve(directory,glyph.File));
        var app=new FakeLetterApp();var plan=LetterTransferLayout.Build(set,false);int result=LetterTransferService.Insert(app,directory,set,false);
        Program.Check(result==18 && app.ActiveDocument.Pages.Count==plan.Max(p=>p.Page)+2,"Page insertion lost copies.");
        var shapes=app.ActiveDocument.Pages.Skip(1).SelectMany(p=>p.Layer.Shapes).ToArray();Program.Check(shapes.Length==18 && shapes.All(s=>s.Height==20),"Imported copies duplicated or wrong size.");
        int offset=0;foreach(var page in app.ActiveDocument.Pages.Skip(1))foreach(var shape in page.Layer.Shapes)
        {var p=plan[offset++];Program.Check(Math.Abs(shape.LeftX-page.LeftX-p.X)<.00001 && Math.Abs(page.TopY-shape.TopY-p.Y)<.00001,"Placement does not match preview.");}
        Program.Check(app.ActiveDocument.Imports<18 && app.ActiveDocument.Unit==7 && !app.Optimization && app.ActiveDocument.Dirty && app.ActiveDocument.Ends==1,"Import reuse or state restoration failed.");
        var fail=new FakeLetterApp();fail.ActiveDocument.FailImport=2;bool threw=false;try{LetterTransferService.Insert(fail,directory,set,false);}catch{threw=true;}
        Program.Check(threw && fail.ActiveDocument.Pages.Count==1 && fail.ActiveDocument.Unit==7 && !fail.Optimization && !fail.ActiveDocument.Dirty && fail.ActiveDocument.Ends==1,"Partial insertion not rolled back.");
        File.Delete(LetterTransferStore.Resolve(directory,set.Glyphs[0].File));fail=new FakeLetterApp();threw=false;try{LetterTransferService.Insert(fail,directory,set,false);}catch{threw=true;}
        Program.Check(threw && fail.ActiveDocument.Pages.Count==1 && fail.ActiveDocument.Ends==0,"Missing file mutated Corel.");
    }
}
public sealed class FakeLetterApp
{
    public FakeLetterDoc ActiveDocument{get;}=new FakeLetterDoc();public bool Optimization{get;set;}public FakeLetterWindow ActiveWindow{get;}=new FakeLetterWindow();
    public object CreateStructImportOptions()=>new object();public FakeLetterRange CreateShapeRange()=>new FakeLetterRange();
}
public sealed class FakeLetterWindow{public void Refresh(){} }
public sealed class FakeLetterRange{public void Add(FakeLetterShape shape){}public void CreateSelection(){} }
public sealed class FakeLetterDoc
{
    public int Unit{get;set;}=7;public bool Dirty{get;set;}public List<FakeLetterPage> Pages{get;}=new List<FakeLetterPage>();public FakeLetterPage ActivePage{get;set;}public int Ends,Imports,FailImport;
    public FakeLetterDoc(){ActivePage=new FakeLetterPage(this);Pages.Add(ActivePage);}public void BeginCommandGroup(string text){}public void EndCommandGroup(){Ends++;}
    public FakeLetterPage AddPages(int count){var page=new FakeLetterPage(this);Pages.Add(page);Dirty=true;return page;}
}
public sealed class FakeLetterPage
{
    public FakeLetterDoc Doc;public string Name{get;set;}public double LeftX=>40;public double TopY=>500;public FakeLetterLayer Layer;
    public FakeLetterPage(FakeLetterDoc doc){Doc=doc;}public void Activate(){Doc.ActivePage=this;}public void SetSize(double w,double h){}public void Delete(){Doc.Pages.Remove(this);}
    public FakeLetterLayer CreateLayer(string text){Layer=new FakeLetterLayer(this);return Layer;}
}
public sealed class FakeLetterLayer
{
    public FakeLetterPage Page;public List<FakeLetterShape> Shapes{get;}=new List<FakeLetterShape>();public FakeLetterLayer(FakeLetterPage page){Page=page;}
    public FakeLetterFilter ImportEx(object path,int type,object options)
    {
        Page.Doc.Imports++;if(Page.Doc.Imports==Page.Doc.FailImport)throw new InvalidOperationException("Synthetic failed import");
        string file=Convert.ToString((path as BStrWrapper)?.WrappedObject??path);if(!File.Exists(file))throw new FileNotFoundException();var s=new FakeLetterShape(this);Shapes.Add(s);return new FakeLetterFilter();
    }
}
public sealed class FakeLetterFilter{public void Finish(){} }
public sealed class FakeLetterShape
{
    private static int _ids;private FakeLetterLayer _layer;public int StaticID{get;}=++_ids;public int Type=>CorelConstants.CdrBitmapShape;public string Name{get;set;}public double LeftX{get;set;}public double TopY{get;set;}public double Width,Height;
    public FakeLetterShape(FakeLetterLayer layer){_layer=layer;}public void SetSize(double w,double h){Width=w;Height=h;}public void Move(double x,double y){LeftX+=x;TopY+=y;}
    public FakeLetterShape Duplicate(double x,double y){var copy=new FakeLetterShape(_layer);_layer.Shapes.Add(copy);return copy;}
}
