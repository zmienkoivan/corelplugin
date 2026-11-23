using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

// Explicitly opt-in: creates and closes only its own Corel documents.
internal static class PatternProjectLiveChecks
{
    internal static void Run(Action<bool,string> check)
    {
        dynamic app=Activator.CreateInstance(Type.GetTypeFromProgID("CorelDRAW.Application.27",true));
        CorelApp.SetHostApplication((object)app);
        var originals=new List<Tuple<object,bool>>();foreach(dynamic document in app.Documents)originals.Add(Tuple.Create((object)document,(bool)document.Dirty));
        object original=app.ActiveDocument,oldPage=null,oldLayer=null;
        dynamic selection=app.CreateShapeRange();if(original!=null){oldPage=((dynamic)original).ActivePage;oldLayer=((dynamic)original).ActiveLayer;foreach(dynamic shape in app.ActiveSelectionRange)selection.Add(shape);}
        bool optimization=(bool)app.Optimization;var owned=new List<object>();
        string root=Path.Combine(Path.GetTempPath(),"Vanya-pattern-live-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        bool completed=false;
        Console.WriteLine("Live project QA: pre-existing documents "+originals.Count);
        try
        {
            foreach(bool bottle in new[]{true,false})
            {
                Console.WriteLine("Preparing "+(bottle?"BIC":"flat"));
                dynamic doc=app.CreateDocument();owned.Add((object)doc);doc.Unit=CorelConstants.CdrMillimeter;dynamic layer=doc.ActiveLayer;
                dynamic main=layer.CreateRectangle2(10d,10d,12d,8d);main.Fill.ApplyUniformFill(app.CreateRGBColor(235,60,30));main.Outline.SetNoOutline();main.Name="Native main source";
                dynamic mini=layer.CreateEllipse2(30d,30d,3d,2d);mini.Fill.ApplyUniformFill(app.CreateRGBColor(40,175,75));mini.Outline.SetNoOutline();mini.Name="Native mini source";
                var service=new PatternFillService();main.CreateSelection();service.AddMotifs(false);mini.CreateSelection();service.AddMotifs(true);
                check(service.Motifs.Count==2 && service.Motifs[1].IsFiller,"Live selection did not assign the mini role");
                dynamic target=null;if(!bottle){target=layer.CreateRectangle2(70d,80d,100d,64d);target.CreateSelection();}
                var session=bottle?service.OpenBottleEditor():service.OpenEditor();
                check(session.Motifs[1].IsFiller && !session.Motifs[0].IsFiller,"Native source capture dropped the manual mini role");
                foreach(var asset in session.Motifs)PatternFillService.PrepareSilhouette(asset);
                var settings=PatternBottlePresets.Bic();settings.UseHero=false;settings.Allowance=bottle?0:2;
                var frame=bottle?PatternBottleLayout.Frame(new PatternBottle(20,20,64),session.BottleCenter):PatternFillService.FrameFor(session,settings.Allowance);
                // Include a generated PNG as well as native Corel artwork in the portable archive.
                var pixels=new byte[16*16*4];for(int i=0;i<pixels.Length;i+=4){pixels[i]=220;pixels[i+3]=255;}
                var bitmap=BitmapSource.Create(16,16,96,96,PixelFormats.Bgra32,null,pixels,64);bitmap.Freeze();
                var memoryAsset=new PatternAsset{Id=-101,Label="Embedded generated source",IsFiller=true,Preview=bitmap,Size=new PatternPoint(4,4)};PatternFillService.PrepareSilhouette(memoryAsset);
                session.Motifs=session.Motifs.Concat(new[]{memoryAsset}).ToArray();session.MirroredIds=new[]{session.Motifs[0].Id};
                var variants=new[]{session.Motifs[0],session.Motifs[0].MirrorHorizontally(),session.Motifs[1],memoryAsset};
                var draft=new PatternDraft{Frame=frame,Options=settings.Layout,Motifs=variants,Items=new List<PatternPlacement>{new PatternPlacement{Motif=0,Center=new PatternPoint(20,20),Size=10},new PatternPlacement{Motif=2,Center=new PatternPoint(40,40),Size=3},new PatternPlacement{Motif=3,Center=new PatternPoint(45,50),Size=3}},Allowance=settings.Allowance,MirroredIds=new HashSet<int>(session.MirroredIds)};
                var data=PatternProjectArchive.Capture(session,settings,draft,null,out var sources);
                int shapeCount=Convert.ToInt32(doc.ActivePage.Shapes.Count),layerCount=Convert.ToInt32(doc.ActivePage.Layers.Count),documents=Convert.ToInt32(app.Documents.Count);
                bool dirty=(bool)doc.Dirty;int selected=Convert.ToInt32(app.ActiveSelectionRange.Count);
                string directory=Path.Combine(root,bottle?"bottle":"flat");Directory.CreateDirectory(directory);
                Console.WriteLine("Capturing native sources");
                string native=PatternProjectNative.Capture(session,sources,directory);
                check(File.Exists(native) && new FileInfo(native).Length>0,"Native project capture did not save a CDR");
                check(Object.Equals((object)app.ActiveDocument,(object)doc) && Convert.ToInt32(doc.ActivePage.Shapes.Count)==shapeCount && Convert.ToInt32(doc.ActivePage.Layers.Count)==layerCount && Convert.ToInt32(app.Documents.Count)==documents && (bool)doc.Dirty==dirty && Convert.ToInt32(app.ActiveSelectionRange.Count)==selected,"Saving changed the original document or left a temporary document/layer");
                check((string)main.Name=="Native main source" && (string)mini.Name=="Native mini source","Saving renamed original source objects");
                string path=Path.Combine(directory,"editable.vpattern");PatternProjectArchive.Save(path,data,sources,native);
                // Close the source to prove the archive has no dependency on it.
                doc.Dirty=false;doc.Close();owned.Remove((object)doc);
                var package=PatternProjectArchive.Read(path,Path.Combine(directory,"opened"));
                Console.WriteLine("Opening portable project");
                var project=PatternProjectNative.Open((object)app,package,path);owned.Add(project.Session.Document);
                check(project.Session.Motifs.Length==3 && project.Session.Motifs[1].IsFiller && project.Session.Motifs[2].Shape!=null,"Reopening lost mini roles or the embedded generated PNG");
                check(project.Draft.Motifs.Length==4 && project.Draft.Motifs[1].Mirrored && project.Session.MirroredIds.Contains(project.Session.Motifs[0].Id),"Reopening lost mirrored sources");
                check(Math.Abs(project.Draft.Frame.Width-frame.Width)<.02 && Math.Abs(project.Draft.Frame.Height-frame.Height)<.02,"Reopening changed template dimensions");
                var newService=new PatternFillService();newService.UseProjectSources(project.Session);
                check(newService.Apply(project.Session,project.Draft,true)>0 && newService.Apply(project.Session,project.Draft,true)>0,"Reopened project cannot insert successive variants");
                dynamic active=project.Session.Document;check(Convert.ToInt32(active.ActivePage.Shapes.Count)>=4,"Project source layer or inserted variants are missing");
                Console.WriteLine("Live Corel project passed: "+(bottle?"BIC":"flat contour with allowance")+"; native + generated sources, roles, mirrors and two insertions.");
            }
            completed=true;
        }
        catch(Exception e){Console.Error.WriteLine("Live project failure before cleanup: "+e);throw;}
        finally
        {
            foreach(dynamic doc in owned)try{doc.Dirty=false;doc.Close();}catch(Exception e){Console.Error.WriteLine("Owned project cleanup: "+e.Message);}
            if(original!=null){((dynamic)original).Activate();((dynamic)oldPage).Activate();((dynamic)oldLayer).Activate();if(Convert.ToInt32(selection.Count)>0)selection.CreateSelection();else ((dynamic)original).ClearSelection();}
            app.Optimization=optimization;
            bool untouched=Convert.ToInt32(app.Documents.Count)==originals.Count && originals.All(p=>Convert.ToBoolean(((dynamic)p.Item1).Dirty)==p.Item2);
            Console.WriteLine("Live document cleanup: before="+originals.Count+" after="+Convert.ToInt32(app.Documents.Count)+" unchanged="+untouched);
            if(completed)check(untouched,"Live QA changed a pre-existing document");
            Directory.Delete(root,true);
        }
    }
}
