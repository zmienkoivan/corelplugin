using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using VanyaTools.Native;

internal static class MotifReplacementChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Flags).Invoke(target,args);
    private static PatternPoint[] Box(double x,double y,double w,double h)=>new[]{new PatternPoint(x,y),new PatternPoint(x+w,y),new PatternPoint(x+w,y+h),new PatternPoint(x,y+h)};
    private static PatternAsset Asset(int id,bool filler,double w,double h)
    {
        var shape=new RectangleGeometry(new Rect(-w/2,-h/2,w,h));shape.Freeze();
        return new PatternAsset{Id=id,Label="Print "+id,IsFiller=filler,Size=new PatternPoint(w,h),Footprint=new PatternFootprint(new PatternPoint(w,h)),Silhouette=shape};
    }
    private static void Render(FrameworkElement root)
    {root.Measure(new Size(1000,740));root.Arrange(new Rect(0,0,1000,740));root.UpdateLayout();new System.Windows.Media.Imaging.RenderTargetBitmap(1000,740,96,96,PixelFormats.Pbgra32).Render(root);}
    private static Point Screen(PatternPreviewSurface preview,PatternPoint point)=>(Point)Call(preview,"ToScreen",point);
    private static bool SameCell(PatternPlacement a,PatternPlacement b)=>a.Cell.Length==b.Cell.Length && a.Cell.Zip(b.Cell,(x,y)=>x.X==y.X && x.Y==y.Y).All(v=>v);
    private static bool SamePose(PatternPlacement a,PatternPlacement b)=>a.Motif==b.Motif && a.Size==b.Size && a.Angle==b.Angle && a.Center.X==b.Center.X && a.Center.Y==b.Center.Y && SameCell(a,b);

    internal static void Run(Action<bool,string> check)
    {
        foreach(var mode in new[]{PatternFillMode.Pebbles,PatternFillMode.Crocodile})
            foreach(bool repeat in new[]{false,true})Flat(check,mode,repeat);
        Bottle(check);
        SingleChoice(check);
    }
    private static void Flat(Action<bool,string> check,PatternFillMode mode,bool repeat)
    {
        var assets=new[]{Asset(1,false,20,10),Asset(1,false,20,10),Asset(2,false,10,20),Asset(3,false,15,15),Asset(10,true,6,4),Asset(11,true,4,6)};
        assets[1].Mirrored=true;
        var frame=new PatternFrame{Width=200,Height=140,Geometry=new RectangleGeometry(new Rect(0,0,200,140))};
        var options=new PatternFillOptions{Mode=mode,SizeMm=25,GapMm=2,RepeatWidthMm=repeat?80:0,RepeatHeightMm=repeat?140:0};
        var first=new PatternPlacement{Motif=0,Size=20,Angle=17,Center=new PatternPoint(50,55),Cell=Box(30,35,40,40)};
        var second=new PatternPlacement{Motif=0,Size=20,Angle=17,Center=new PatternPoint(130,55),Cell=Box(110,35,40,40)};
        var filler=new PatternPlacement{Motif=4,Size=4,Center=new PatternPoint(95,120),Cell=Box(89,114,12,12)};
        var baseline=new List<PatternPlacement>{first,second,filler};
        PatternDraft Draft()=>new PatternDraft{Frame=frame,Options=options,Motifs=assets,Items=baseline};
        var draft=Draft();var preview=new PatternPreviewSurface(new PatternEditorSession{Seam=frame,Motifs=assets},frame);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya motif replacement"){Width=1000,Height=740,WindowStyle=0}))
        {
            var root=new Border{Child=preview};source.RootVisual=root;preview.Update(frame,draft,0);Render(root);
            Call(preview,"SelectMotif",Screen(preview,first.Center));
            string key=(string)preview.GetType().GetField("_selectedMotif",Flags).GetValue(preview);
            check((bool)Call(preview,"ApplyMotifTurn",key,23d),"Replacement fixture could not rotate the original print");
            ((Dictionary<string,double>)preview.GetType().GetField("_motifTurns",Flags).GetValue(preview))[key]=23d;
            var before=draft.Items.Select(PatternMotifRotation.Copy).ToArray();
            preview.SetMotifTool(0);
            check((bool)Call(preview,"TryMotifClick",Screen(preview,draft.Items[0].Center)),"Click on an existing cell was not handled");
            check(draft.Items.Count==3 && assets[draft.Items[0].Motif].Id==2,"A click added a cell, changed role or only mirrored the same picture");
            check(Math.Abs(draft.Items[0].Angle-40)<1e-6 && SameCell(draft.Items[0],before[0]),"Replacement lost the angle or rebuilt the cell");
            check(repeat?assets[draft.Items[1].Motif].Id==2:SamePose(draft.Items[1],before[1]),"Replacement changed an unrelated neighbour or broke corresponding repeat copies");
            check(SamePose(draft.Items[2],before[2]),"Replacing a main print changed its filler neighbour");
            var refreshed=Draft();preview.Update(frame,refreshed,0);Render(root);draft=refreshed;
            check(draft.Items.Count==3 && draft.Items.Count(p=>assets[p.Motif].Id==2)==(repeat?2:1),"Preview refresh lost replacement pictures");
            var selected=draft.Items.First(p=>assets[p.Motif].Id==2);Call(preview,"SelectMotif",Screen(preview,selected.Center));preview.SetSelectedAnchor(true);
            check((bool)Call(preview,"TryMotifClick",Screen(preview,selected.Center)) && preview.SelectedMotifAnchored,"Replacing a pinned cell removed its red anchor");
            check(draft.Items.Any(p=>assets[p.Motif].Id==3 && SameCell(p,selected)),"Repeated clicks did not advance to another main picture");
            var small=draft.Items.Single(p=>assets[p.Motif].IsFiller);var smallCell=PatternMotifRotation.Copy(small);
            check((bool)Call(preview,"TryMotifClick",Screen(preview,small.Center)),"Click on a filler was not handled");
            small=draft.Items.Single(p=>assets[p.Motif].IsFiller);
            check(assets[small.Motif].Id==11 && SameCell(small,smallCell) && PatternHeroFill.Longest(assets[small.Motif],small)<=5+1e-6,"Filler replacement changed its role, cell or miniature size");
            preview.RestoreAutomaticMotifs();
            check(draft.Items.Count==3 && draft.Items[0].Motif==0 && SameCell(draft.Items[0],first),"Restore automatic layout retained stale replacements");
            check(first.Motif==0 && first.Angle==17 && baseline.Count==3,"Replacement modified the automatic source cache");
        }
    }
    private static void Bottle(Action<bool,string> check)
    {
        foreach(bool seam in new[]{false,true})
        {
        var bottle=new PatternBottle(20,20,64);var frame=PatternBottleLayout.Frame(bottle,new PatternPoint(100,100));
        var assets=new[]{Asset(1,false,17,12),Asset(2,false,12,17),Asset(10,true,6,4),Asset(11,true,4,6)};
        var options=new PatternFillOptions{Mode=PatternFillMode.Pebbles,SizeMm=17,GapMm=2};
        var draft=new PatternDraft{Frame=frame,Options=options,Motifs=assets,Items=new List<PatternPlacement>()};
        var preview=new PatternPreviewSurface(new PatternEditorSession{BottlePageIndex=1,Motifs=assets},frame);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya bottle replacement"){Width=1000,Height=740,WindowStyle=0}))
        {
            var root=new Border{Child=preview};source.RootVisual=root;preview.Update(frame,draft,0);Render(root);
            preview.SetMotifTool(1);var point=PatternBottleLayout.OnFrame(frame,seam?.5:bottle.Width/2,bottle.Slant/2);
            check((bool)Call(preview,"PaintMotif",Screen(preview,point)),"Bottle fixture did not place a main print");
            var original=PatternMotifRotation.Copy(draft.Items.Single());preview.SetMotifTool(0);
            Call(preview,"SelectMotif",Screen(preview,original.Center));preview.SetSelectedAnchor(true);
            check((bool)Call(preview,"TryMotifClick",Screen(preview,original.Center)),"Bottle cell click was ignored");
            var replaced=draft.Items.Single();
            check(replaced.Motif==1 && SameCell(replaced,original) && preview.SelectedMotifAnchored,"Bottle replacement rebuilt its grid or lost the pinned cell");
            check(PatternBottleLayout.FitsOnRing(frame,assets,replaced,new List<PatternPlacement>(),2,false),"Bottle replacement broke the ring gap");
            var silhouette=PatternSilhouette.At(assets[replaced.Motif],replaced).Clone();var matrix=silhouette.Transform.Value;matrix.Append(new Matrix(1,0,0,-1,0,frame.Height));silhouette.Transform=new MatrixTransform(matrix);
            double area=silhouette.GetArea(),inside=Geometry.Combine(draft.BottleCutView??frame.Geometry,silhouette,GeometryCombineMode.Intersect,null).GetArea();
            check(Math.Abs(area-inside)<=Math.Max(.001,area*.00001),"Bottle replacement cutting outline crops the new print");
            var fresh=new PatternDraft{Frame=frame,Options=options,Motifs=assets,Items=new List<PatternPlacement>()};preview.Update(frame,fresh,0);
            check(fresh.Items.Count==1 && fresh.Items[0].Motif==1 && SamePose(fresh.Items[0],replaced),"Refreshing the bottle lost or moved the replaced anchor");
        }
        }
    }
    private static void SingleChoice(Action<bool,string> check)
    {
        var frame=new PatternFrame{Width=100,Height=100,Geometry=new RectangleGeometry(new Rect(0,0,100,100))};
        var item=new PatternPlacement{Size=20,Center=new PatternPoint(50,50),Cell=Box(30,30,40,40)};
        var draft=new PatternDraft{Frame=frame,Options=new PatternFillOptions{Mode=PatternFillMode.Pebbles},Motifs=new[]{Asset(1,false,20,10),Asset(2,true,5,5)},Items=new List<PatternPlacement>{item}};
        var preview=new PatternPreviewSurface(new PatternEditorSession{Seam=frame},frame);string notice=null;preview.MotifNotice+=s=>notice=s;
        using(var source=new HwndSource(new HwndSourceParameters("Vanya single-picture replacement"){Width=1000,Height=740,WindowStyle=0}))
        {
            var root=new Border{Child=preview};source.RootVisual=root;preview.Update(frame,draft,0);Render(root);
            var before=PatternMotifRotation.Copy(draft.Items[0]);
            check((bool)Call(preview,"TryMotifClick",Screen(preview,item.Center)) && SamePose(draft.Items.Single(),before),"A single-choice click added a pebble or substituted a filler");
            check(notice!=null && notice.Contains("Нет другого основного"),"Single-picture replacement failed silently");
        }
    }
}
