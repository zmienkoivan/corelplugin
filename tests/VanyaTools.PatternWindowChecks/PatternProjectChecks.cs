using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class PatternProjectChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Flags).Invoke(target,args);
    private static PatternAsset Asset(int id,bool filler=false)
    {
        var image=BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat(new byte[]{40,100,220,255},4).SelectMany(b=>b).ToArray(),8);image.Freeze();
        var geometry=new RectangleGeometry(new Rect(-6,-4,12,8));geometry.Freeze();
        return new PatternAsset{Id=id,Label="Source "+id,IsFiller=filler,Size=new PatternPoint(12,8),Footprint=new PatternFootprint(new PatternPoint(12,8)),Silhouette=geometry,Preview=image};
    }
    private static PatternPlacement Pose(int motif,double x,double y)=>new PatternPlacement{Motif=motif,Center=new PatternPoint(x,y),Size=10,Cell=new[]{new PatternPoint(x-12,y-12),new PatternPoint(x+12,y-12),new PatternPoint(x+12,y+12),new PatternPoint(x-12,y+12)}};
    private static void Arrange(FrameworkElement element){element.Measure(new Size(900,700));element.Arrange(new Rect(0,0,900,700));element.UpdateLayout();}
    private static bool Same(PatternPlacement a,PatternPlacement b)=>a.Motif==b.Motif && a.Size==b.Size && a.Angle==b.Angle && a.Center.X==b.Center.X && a.Center.Y==b.Center.Y && a.Cell.Zip(b.Cell,(p,q)=>p.X==q.X && p.Y==q.Y).All(n=>n);
    internal static void Run(Action<bool,string> check)
    {
        var settings=new PatternEditorSettings{BottleTop=20,BottleBottom=20,BottleHeight=64,HeroSize=35,HeroGap=3,HeroAngle=90,HeroX=42,
            Layout=new PatternFillOptions{Mode=PatternFillMode.Pebbles,SizeMm=17,GapMm=2,Seed=123,AngleVariation=10}};
        var frame=new PatternFrame{Width=160,Height=100,Geometry=new RectangleGeometry(new Rect(0,0,160,100))};
        var assets=new[]{Asset(71),Asset(72),Asset(73,true)};
        var session=new PatternEditorSession{Seam=frame,Motifs=assets,MirroredIds=new[]{72},Hero=Asset(74)};
        var variants=new[]{assets[0],assets[1],assets[1].MirrorHorizontally(),assets[2]};
        PatternDraft New()=>new PatternDraft{Frame=frame,Options=settings.Layout,Motifs=variants,Items=new[]{Pose(0,30,35),Pose(1,75,35),Pose(3,120,75)}.ToList(),MirroredIds=new HashSet<int>{72}};
        var draft=New();var preview=new PatternPreviewSurface(session,frame);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya project round trip"){Width=900,Height=700,WindowStyle=0}))
        {
            var host=new Border{Child=preview};source.RootVisual=host;preview.Update(frame,draft,0);Arrange(host);
            Point Screen(PatternPoint p)=>(Point)Call(preview,"ToScreen",p);
            Call(preview,"SelectMotif",Screen(draft.Items[0].Center));preview.SetSelectedAnchor(true);
            preview.SetMotifTool(0);Call(preview,"TryMotifClick",Screen(draft.Items[0].Center));
            preview.SetMotifTool(3);Call(preview,"PaintMotif",Screen(draft.Items[1].Center));
            check(draft.Items.Count==2,"Project test did not erase an automatic cell");
            Call(preview,"SelectMotif",Screen(draft.Items[0].Center));
            string key=(string)preview.GetType().GetField("_selectedMotif",Flags).GetValue(preview);
            check((bool)Call(preview,"ApplyMotifTurn",key,15d),"Project fixture could not turn the replacement");
            ((Dictionary<string,double>)preview.GetType().GetField("_motifTurns",Flags).GetValue(preview))[key]=15;
            var expected=draft.Items.Select(PatternMotifRotation.Copy).ToArray();
            var edits=preview.SaveProjectEdits();
            draft.CellGuides=new RectangleGeometry(new Rect(1,2,3,4)){Transform=new TranslateTransform(7,11)};
            var data=PatternProjectArchive.Capture(session,settings,draft,edits,out var sources);
            string directory=Path.Combine(Path.GetTempPath(),"Vanya-project-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            try
            {
                string native=Path.Combine(directory,"source.cdr"),path=Path.Combine(directory,"roundtrip.vpattern");File.WriteAllBytes(native,new byte[]{1,2,3,4,5});
                System.Threading.Tasks.Task.Run(()=>PatternProjectArchive.Save(path,data,sources,native)).GetAwaiter().GetResult();
                System.Threading.Tasks.Task.Run(()=>PatternProjectArchive.Save(path,data,sources,native)).GetAwaiter().GetResult(); // Atomic replacement of an existing project.
                var package=System.Threading.Tasks.Task.Run(()=>PatternProjectArchive.Read(path,Path.Combine(directory,"read"))).GetAwaiter().GetResult();var read=package.Data;
                check(File.ReadAllBytes(package.NativePath).SequenceEqual(File.ReadAllBytes(native)),"Project changed its embedded native artwork");
                check(read.Sources.Length==4 && read.Sources[2].Filler && !read.Sources[0].Filler && package.Previews.All(b=>b!=null && b.IsFrozen),"Source roles or independent previews were lost");
                check(read.DraftSources.SequenceEqual(new[]{0,1,1,2}) && read.DraftMirrors.SequenceEqual(new[]{false,false,true,false}) && read.Mirrors.SequenceEqual(new[]{72}),"Mirrored variant indices changed during archive round trip");
                check(read.CellGuides.Read().Bounds==draft.CellGuides.Bounds,"Project transformed geometry twice or lost its transform");
                var restoredSettings=new PatternEditorSettings();PatternProjectData.ReadValues(restoredSettings,read.Settings);PatternProjectData.ReadValues(restoredSettings.Layout,read.Layout);
                check(restoredSettings.HeroX==42 && restoredSettings.HeroAngle==90 && restoredSettings.Layout.Seed==123 && restoredSettings.Layout.Mode==PatternFillMode.Pebbles,"Settings were not restored from JSON");
                var restoredAssets=variants.Select(a=>{var result=Asset(a.Id+100,a.IsFiller);return a.Mirrored?result.MirrorHorizontally():result;}).ToArray();
                var restoredSession=new PatternEditorSession{Seam=frame,Motifs=restoredAssets};
                var restoredDraft=new PatternDraft{Frame=frame,Options=restoredSettings.Layout,Motifs=restoredAssets,Items=read.Items.Select(p=>p.Read(4)).ToList()};
                var opened=new PatternProjectOpened{Session=restoredSession,Draft=restoredDraft,Settings=restoredSettings,Edits=read.Edits,Ids=new Dictionary<int,int>{{71,171},{72,172},{73,173},{74,174}},FilePath=path};
                var window=new PatternFillWindow(restoredSession,restoredSettings,opened);
                try
                {
                    var actual=(PatternPreviewSurface)window.GetType().GetField("_preview",Flags).GetValue(window);
                    check(window.Draft.Items.Count==expected.Length && window.Draft.Items.Zip(expected,Same).All(v=>v),"Opening regenerated the saved hand-edited layout");
                    var state=actual.SaveProjectEdits();check(state.Anchors.Length==1 && state.Removed.All(k=>!k.StartsWith("71/") && !k.StartsWith("72/")),"Anchors or old-ID remapping were lost");
                    // Re-rendering the same seed must retain deletion, replacement and rotation.
                    var next=New();next.Motifs=restoredAssets;actual.Update(frame,next,0);
                    check(next.Items.Count==expected.Length && next.Items.Any(p=>p.Motif==expected[0].Motif && Math.Abs(p.Angle-expected[0].Angle)<1e-8),"A preview refresh discarded restored manual edits");
                    actual.RestoreAutomaticMotifs();check(next.Items.Count==3,"The saved automatic layout could not be restored");
                }
                finally{window.Close();}
                read.Sources[0]=null;bool invalid=false;try{PatternProjectArchive.Validate(read);}catch(InvalidDataException){invalid=true;}check(invalid,"Invalid source records were accepted");
            }
            finally{Directory.Delete(directory,true);}
        }
        var sourceItem=new PatternSource(new object(),7,"Mini");int notifications=0;sourceItem.PropertyChanged+=(_,e)=>{if(e.PropertyName=="IsFiller")notifications++;};sourceItem.IsFiller=true;sourceItem.IsFiller=true;sourceItem.IsFiller=false;
        check(notifications==2,"Mini role does not notify WPF on an actual role change");
        var template=(DataTemplate)typeof(PatternFillTab).GetMethod("MotifTemplate",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,null);
        template.Seal();var row=(DockPanel)template.LoadContent();row.DataContext=sourceItem;
        using(var bindingHost=new HwndSource(new HwndSourceParameters("Vanya mini choice binding"){Width=600,Height=100,WindowStyle=0}))
        {
            bindingHost.RootVisual=row;Arrange(row);var checkbox=(CheckBox)row.Children[0];
            checkbox.IsChecked=true;check(sourceItem.IsFiller,"The actual Mini checkbox did not update the chosen source role");
            sourceItem.IsFiller=false;check(checkbox.IsChecked==false,"Changing the mini source role did not update its visible checkbox");
        }
    }
}
