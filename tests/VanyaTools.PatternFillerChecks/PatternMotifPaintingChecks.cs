using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class PatternMotifPaintingChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object Call(object value,string name,params object[] args)=>value.GetType().GetMethod(name,Flags).Invoke(value,args);
    private static PatternPoint[] Box(double x,double y,double w,double h)=>new[]{new PatternPoint(x,y),new PatternPoint(x+w,y),new PatternPoint(x+w,y+h),new PatternPoint(x,y+h)};
    private static PatternAsset Asset(int id,bool filler,double w,double h)
    {var geometry=new RectangleGeometry(new Rect(-w/2,-h/2,w,h));geometry.Freeze();return new PatternAsset{Id=id,IsFiller=filler,Label=filler?"Заполнитель":"Основной принт",Size=new PatternPoint(w,h),Footprint=new PatternFootprint(new PatternPoint(w,h)),Silhouette=geometry};}
    private static RenderTargetBitmap Render(FrameworkElement root)
    {root.Measure(new Size(1000,740));root.Arrange(new Rect(0,0,1000,740));root.UpdateLayout();var dispatch=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>dispatch.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(dispatch);var image=new RenderTargetBitmap(1000,740,96,96,PixelFormats.Pbgra32);image.Render(root);return image;}
    internal static void Run(Action<bool,string> check)
    {
        var plan=PatternFillerBrief.Preset(0);plan.SmallFillers=true;
        var prompt=PatternFillerBrief.GenerationPrompt(plan);var analysis=PatternFillerBrief.AnalysisTask(4,true);
        check(prompt.Contains("ONE QUARTER") && prompt.Contains("LOWER DETAIL") && !prompt.Contains("Do not simplify detailed reference"),"Filler generation kept the contradictory full-detail instruction or omitted the quarter-size target");
        check(analysis.Contains("LOWER DETAIL") && analysis.Contains("ONE QUARTER") && analysis.Contains("ONLY subordinate MINIATURE GAP FILLERS") && prompt.Contains("Never turn a filler into a new main subject"),"Filler analysis still proposes over-detailed full-size prints");
        foreach(var mode in new[]{PatternFillMode.Pebbles,PatternFillMode.Crocodile})Editor(check,mode);
        RefreshSafety(check);RepeatClick(check,0);RepeatClick(check,31);AutoFill(check);
    }
    private static void Editor(Action<bool,string> check,PatternFillMode mode)
    {
        var main=Asset(1,false,20,10);var small=Asset(2,true,12,8);var assets=new[]{main,small};
        var geometry=new RectangleGeometry(new Rect(0,0,200,140));geometry.Freeze();var frame=new PatternFrame{Width=200,Height=140,Geometry=geometry};
        var original=new PatternPlacement{Motif=0,Angle=0,Size=20,Center=new PatternPoint(150,110),Cell=Box(130,90,40,40)};
        var baseline=new List<PatternPlacement>{original};var options=new PatternFillOptions{Mode=mode,SizeMm=30,GapMm=2,Seed=8123};
        var draft=new PatternDraft{Frame=frame,Options=options,Motifs=assets,Items=baseline};var preview=new PatternPreviewSurface(new PatternEditorSession{Seam=frame},frame);int calls=ReplicateWorkerClient.Calls;
        using(var source=new HwndSource(new HwndSourceParameters("Vanya pebble painting checks"){Width=1000,Height=740,WindowStyle=0}))
        {
            var root=new Border{Child=preview,Background=Brushes.White};source.RootVisual=root;preview.Update(frame,draft,0);Render(root);
            bool Click(double x,double y)=>(bool)Call(preview,"PaintMotif",(Point)Call(preview,"ToScreen",new PatternPoint(x,y)));
            preview.SetMotifTool(1);check(Click(50,70) && draft.Items.Count==2 && baseline.Count==1,"Click failed to add a main pebble or mutated the automatic layout");
            var large=draft.Items.Last();check(!assets[large.Motif].IsFiller && PatternHeroFill.Longest(assets[large.Motif],large)>=9,"Primary click selected a filler or created an unreadably tiny print");
            preview.SetMotifTool(2);check(Click(73,70) && draft.Items.Count==3,"Click failed to add a smaller dependent filler");
            var tiny=draft.Items.Last();check(assets[tiny.Motif].IsFiller && PatternHeroFill.Longest(small,tiny)<=PatternHeroFill.Longest(main,large)*.25+1e-7,"Click filler exceeded one quarter of its neighbouring main print");
            int count=draft.Items.Count;check(!Click(tiny.Center.X,tiny.Center.Y) && draft.Items.Count==count,"Click inside an existing print created an overlapping extra pebble");
            for(int i=0;i<draft.Items.Count;i++)for(int j=i+1;j<draft.Items.Count;j++)check(PatternMotifRotation.Separated(assets[draft.Items[i].Motif].Footprint.At(draft.Items[i]),assets[draft.Items[j].Motif].Footprint.At(draft.Items[j]),options.GapMm),"Click placement violated the neighbouring gap");
            var render=Render(root);var pixels=new byte[1000*740*4];render.CopyPixels(pixels,4000,0);

            string path=Environment.GetEnvironmentVariable("VANYA_PATTERN_RENDER");if(!String.IsNullOrEmpty(path)){Directory.CreateDirectory(path);var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(render));using(var file=File.Create(Path.Combine(path,"pebble-click-"+mode+".png")))e.Save(file);}
            check(Enumerable.Range(0,pixels.Length/4).Any(i=>pixels[i*4+1]>pixels[i*4]*1.25 && pixels[i*4+1]>pixels[i*4+2]*1.25),"Filler green contour was not rendered");
            var refreshed=new PatternDraft{Frame=frame,Options=options,Motifs=assets,Items=baseline};preview.Update(frame,refreshed,0);Render(root);
            check(refreshed.Items.Count==3,"Refreshing the same layout lost click-created pebbles");draft=refreshed;
            preview.SetMotifTool(3);tiny=draft.Items.Last();check(Click(tiny.Center.X,tiny.Center.Y) && draft.Items.Count==2 && draft.Items.All(p=>!assets[p.Motif].IsFiller),"Click eraser failed to remove only its target filler");
            check(Click(original.Center.X,original.Center.Y) && draft.Items.Count==1,"Click eraser failed to remove the selected automatic print");
            var again=new PatternDraft{Frame=frame,Options=options,Motifs=assets,Items=baseline};preview.Update(frame,again,0);Render(root);check(again.Items.Count==1,"A deleted automatic pebble reappeared after refresh");
            preview.ClearMotifs();check(again.Items.Count==0,"Clear did not produce an empty click canvas");
            preview.RestoreAutomaticMotifs();check(again.Items.Count==1 && again.Items[0].Center.X==original.Center.X && original.Size==20,"Restoring automatic layout failed or changed the original cache");
            check(ReplicateWorkerClient.Calls==calls,"A local click/erase/restore sent a paid AI request");
        }
    }
    private static void RefreshSafety(Action<bool,string> check)
    {
        var assets=new[]{Asset(1,false,20,10),Asset(2,true,12,8)};var geometry=new RectangleGeometry(new Rect(0,0,200,140));geometry.Freeze();
        var frame=new PatternFrame{Width=200,Height=140,Geometry=geometry};var options=new PatternFillOptions{Mode=PatternFillMode.Pebbles,SizeMm=25,GapMm=2};
        PatternDraft Next(Geometry reserve,PatternAsset[] source)=>new PatternDraft{Frame=frame,Options=options,Motifs=source,Items=new List<PatternPlacement>(),Hero=new PatternPlacement{Size=30},Reserve=reserve};
        var draft=Next(null,assets);var preview=new PatternPreviewSurface(new PatternEditorSession{Seam=frame,Hero=Asset(3,false,30,30)},frame);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya manual update safety"){Width=1000,Height=740,WindowStyle=0}))
        {
            var root=new Border{Child=preview};source.RootVisual=root;preview.Update(frame,draft,0);Render(root);preview.SetMotifTool(1);
            check((bool)Call(preview,"PaintMotif",(Point)Call(preview,"ToScreen",new PatternPoint(60,60))),"Manual safety fixture did not place its main print");
            var reserve=new RectangleGeometry(new Rect(35,35,50,50));reserve.Freeze();var occupied=Next(reserve,assets);preview.Update(frame,occupied,0);
            check(occupied.Items.Count==0,"Moving the hero over a manual print caused an overlap");
            var freed=Next(null,assets);preview.Update(frame,freed,0);check(freed.Items.Count==1 && freed.Items[0].Motif==0,"A hidden manual print did not return when its space became free");
            var reordered=Next(null,assets.Reverse().ToArray());preview.Update(frame,reordered,0);check(reordered.Items.Count==0,"Source index changes silently replaced a manual print with another asset");
        }
    }
    private static void RepeatClick(Action<bool,string> check,double angle)
    {
        var assets=new[]{Asset(1,false,20,10),Asset(2,true,12,8)};var geometry=new RectangleGeometry(new Rect(0,0,200,140));geometry.Freeze();
        var frame=new PatternFrame{Width=200,Height=140,Geometry=geometry};var options=new PatternFillOptions{Mode=PatternFillMode.Pebbles,SizeMm=25,GapMm=2,Angle=angle,RepeatWidthMm=100,RepeatHeightMm=70};
        var draft=new PatternDraft{Frame=frame,Options=options,Motifs=assets,Items=new List<PatternPlacement>()};var preview=new PatternPreviewSurface(new PatternEditorSession{Seam=frame},frame);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya manual rapport checks"){Width=1000,Height=740,WindowStyle=0}))
        {
            var root=new Border{Child=preview};source.RootVisual=root;preview.Update(frame,draft,0);Render(root);
            bool Click(double x,double y)=>(bool)Call(preview,"PaintMotif",(Point)Call(preview,"ToScreen",new PatternPoint(x,y)));
            preview.SetMotifTool(1);check(Click(35,35) && draft.Items.Count>1,"A local repeat click did not add its corresponding rapport copies");
            if(angle==0){preview.SetMotifTool(2);check(Click(55,35) && draft.Items.Any(p=>assets[p.Motif].IsFiller),"A local repeat click did not place dependent fillers");}
            for(int i=0;i<draft.Items.Count;i++)for(int j=i+1;j<draft.Items.Count;j++)check(PatternMotifRotation.Separated(assets[draft.Items[i].Motif].Footprint.At(draft.Items[i]),assets[draft.Items[j].Motif].Footprint.At(draft.Items[j]),options.GapMm),"Manual rapport copies overlap");
            preview.SetMotifTool(3);check(Click(35,35) && draft.Items.All(p=>assets[p.Motif].IsFiller),"The eraser left duplicate rapport copies of the selected main print");
            preview.ClearMotifs();options.RepeatWidthMm=.001;options.RepeatHeightMm=.001;preview.SetMotifTool(1);
            check(!Click(35,35) && draft.Items.Count==0,"A tiny repeat period created a partial or unbounded manual layout");
        }
    }
    private static void AutoFill(Action<bool,string> check)
    {
        var main=Asset(1,false,20,20);var filler=Asset(2,true,10,6);var geometry=new RectangleGeometry(new Rect(0,0,100,100));geometry.Freeze();
        var frame=new PatternFrame{Width=100,Height=100,Geometry=geometry};var item=new PatternPlacement{Motif=0,Size=20,Center=new PatternPoint(50,50),Cell=Box(0,0,100,100)};
        var draft=new PatternDraft{Frame=frame,Options=new PatternFillOptions{Mode=PatternFillMode.Pebbles,SizeMm=30,GapMm=1},Motifs=new[]{main,filler},Items=new List<PatternPlacement>{item}};
        PatternHeroFill.FillVoids(draft,new[]{item},CancellationToken.None);
        var extras=draft.Items.Where(p=>draft.Motifs[p.Motif].IsFiller).ToArray();check(extras.Length>0 && extras.All(p=>PatternHeroFill.Longest(filler,p)<=5+1e-7),"Automatic void fillers were not added or exceeded one-quarter scale");
        check(item.Size==20 && item.Center.X==50 && item.Center.Y==50,"Filler generation changed a main print to make space");
        item.Size=8;PatternHeroFill.LimitFillerSizes(draft);check(draft.Items.Where(p=>draft.Motifs[p.Motif].IsFiller).All(p=>PatternHeroFill.Longest(filler,p)<=2+1e-7),"Fillers did not shrink when the neighbouring main print became smaller");
    }
}
