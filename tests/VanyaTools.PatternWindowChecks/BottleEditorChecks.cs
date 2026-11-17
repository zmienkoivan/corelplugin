using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VanyaTools.Native;
internal static class BottleEditorChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Flags).Invoke(target,args);
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Flags).GetValue(target);
    private static void Set(object target,string name,object value)=>target.GetType().GetField(name,Flags).SetValue(target,value);
    private static PatternAsset Asset(int id,bool filler,double w=20,double h=10)
    {var g=new RectangleGeometry(new Rect(-w/2,-h/2,w,h));g.Freeze();return new PatternAsset{Id=id,IsFiller=filler,Label=filler?"Миниатюра":"Принт",Size=new PatternPoint(w,h),Footprint=new PatternFootprint(new PatternPoint(w,h)),Silhouette=g};}
    private static void Render(FrameworkElement root)
    {root.Measure(new Size(1200,800));root.Arrange(new Rect(0,0,1200,800));root.UpdateLayout();var pump=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>pump.Continue=false));Dispatcher.PushFrame(pump);var image=new RenderTargetBitmap(1200,800,96,96,PixelFormats.Pbgra32);image.Render(root);}
    internal static void Run(Action<bool,string> check)
    {
        Preset(check);GridAndAnchors(check,new PatternBottle(20,20,64));GridAndAnchors(check,new PatternBottle(70,90,100));
        foreach(var mode in new[]{PatternFillMode.Pebbles,PatternFillMode.Crocodile})foreach(var bottle in new[]{new PatternBottle(20,20,64),new PatternBottle(70,90,100),new PatternBottle(90,70,100)})Editing(check,mode,bottle);
    }
    private static void Preset(Action<bool,string> check)
    {
        var session=new PatternEditorSession{BottlePageIndex=1,BottleCenter=new PatternPoint(100,100),Motifs=new[]{Asset(1,false),Asset(2,true,8,6)},Hero=Asset(3,false,40,10)};
        var initial=PatternBottlePresets.Bic();var window=new PatternFillWindow(session,initial);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya BIC preset checks"){Width=1200,Height=800,WindowStyle=0}))
        try
        {
            var root=(FrameworkElement)window.Content;source.RootVisual=root;DateTime deadline=DateTime.UtcNow.AddSeconds(20);
            do{Render(root);Thread.Sleep(5);}while(window.Draft==null && DateTime.UtcNow<deadline);
            check(window.Draft!=null,"The BIC preset failed to produce a draft: "+Field<TextBlock>(window,"_message").Text);
            var d=window.Draft;var preview=Field<PatternPreviewSurface>(window,"_preview");
            check(d.Frame.Bottle.TopDiameter==20 && d.Frame.Bottle.BottomDiameter==20 && d.Frame.Bottle.Height==64 && d.Options.SizeMm==17 && d.Options.GapMm==2,"BIC dimensions or print defaults are wrong");
            var size=PatternTransform.Fit(session.Hero.Size,d.Hero);
            check(Math.Abs(size.RotatedHeight-35)<.001 && size.RotatedWidth<size.RotatedHeight && window.Settings.HeroGap==3,"The BIC logo is not 35 mm high, vertical, with a 3 mm margin");
            check(preview.SupportsMotifEditing && Field<int>(preview,"_motifTool")==2,"Bottle pebbles still hide the click editing tools");
            Call(window,"ApplyBicPreset");deadline=DateTime.UtcNow.AddSeconds(20);do{Render(root);Thread.Sleep(5);}while((window.Draft==null || !Field<Button>(window,"_apply").IsEnabled) && DateTime.UtcNow<deadline);
            check(window.Draft!=null && window.Settings.BottleHeight==64 && window.Settings.Layout.SizeMm==17,"The BIC template button failed to restore its parameters");
            int applies=0;window.ApplyRequested+=_=>applies++;Field<Button>(window,"_apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Field<Button>(window,"_apply").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check(applies==2 && window.DialogResult==null,"Applying bottle variants closes the editor");
            string path=Environment.GetEnvironmentVariable("VANYA_PATTERN_RENDER");if(!String.IsNullOrEmpty(path)){var image=new RenderTargetBitmap(1200,800,96,96,PixelFormats.Pbgra32);var back=new DrawingVisual();using(var dc=back.RenderOpen())dc.DrawRectangle(Brushes.White,null,new Rect(0,0,1200,800));image.Render(back);image.Render(root);var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(image));using(var f=System.IO.File.Create(System.IO.Path.Combine(path,"bic-window.png")))e.Save(f);}
        }
        finally{window.Close();}
    }
    private static void GridAndAnchors(Action<bool,string> check,PatternBottle bottle)
    {
        var frame=PatternBottleLayout.Frame(bottle,new PatternPoint(100,100));var assets=new[]{Asset(1,false),Asset(2,true,8,6)};
        PatternDraft Draft(int seed)
        {
            var options=new PatternFillOptions{Mode=PatternFillMode.Pebbles,SizeMm=17,GapMm=2,Seed=seed};
            var d=new PatternDraft{Frame=frame,Options=options,Motifs=assets,Items=PatternBottleLayout.Build(frame,assets.Take(1).ToArray(),options,false,System.Threading.CancellationToken.None,out _)};
            PatternBottleLayout.CloseSeam(d,null,System.Threading.CancellationToken.None);return d;
        }
        var draft=Draft(8123);var preview=new PatternPreviewSurface(new PatternEditorSession{BottlePageIndex=1,Motifs=assets},frame);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya fixed bottle anchors"){Width=1200,Height=800,WindowStyle=0}))
        {
            var root=new Border{Child=preview};source.RootVisual=root;preview.Update(frame,draft,0);Render(root);
            Point Screen(PatternPoint p)=>(Point)Call(preview,"ToScreen",p);
            var first=draft.Items.OrderBy(p=>Math.Pow(p.Center.X-frame.Width*.5,2)+Math.Pow(p.Center.Y-frame.Height*.5,2)).First();
            var second=draft.Items.Where(p=>Math.Abs(p.Center.X-first.Center.X)+Math.Abs(p.Center.Y-first.Center.Y)>25).OrderBy(p=>Math.Pow(p.Center.X-frame.Width*.7,2)+Math.Pow(p.Center.Y-frame.Height*.3,2)).First();
            Call(preview,"SelectMotif",Screen(first.Center));preview.SetSelectedAnchor(true);
            string anchor=Field<string>(preview,"_selectedMotif");var frozen=PatternMotifRotation.Copy(draft.Items[Array.IndexOf(Field<string[]>(preview,"_motifKeys"),anchor)]);
            preview.SetMotifTool(2);Call(preview,"TryMotifClick",Screen(second.Center));Render(root);
            check(draft.Items.Any(p=>assets[p.Motif].IsFiller),"A miniature could not be inserted into a populated bottle grid");
            var keys=Field<string[]>(preview,"_motifKeys");var held=draft.Items[Array.IndexOf(keys,anchor)];
            bool Same(PatternPlacement a,PatternPlacement b)=>a.Center.X==b.Center.X && a.Center.Y==b.Center.Y && a.Size==b.Size && a.Angle==b.Angle && a.Cell.Select(p=>p.X+":"+p.Y).SequenceEqual(b.Cell.Select(p=>p.X+":"+p.Y));
            check(Same(held,frozen),"Adding a miniature moved a red anchor");
            string miniKey=Field<string>(preview,"_selectedMotif");check(miniKey!=anchor && preview.HasSelectedMotif,"A new mini cell was not selected");preview.SetSelectedAnchor(true);
            miniKey=Field<string>(preview,"_selectedMotif");var frozenMini=PatternMotifRotation.Copy(draft.Items[Array.IndexOf(Field<string[]>(preview,"_motifKeys"),miniKey)]);
            draft=Draft(7124);preview.Update(frame,draft,0);Render(root);keys=Field<string[]>(preview,"_motifKeys");
            check(Array.IndexOf(keys,anchor)>=0 && Array.IndexOf(keys,miniKey)>=0,"Regeneration lost a red main or miniature anchor");
            check(Same(draft.Items[Array.IndexOf(keys,anchor)],frozen) && Same(draft.Items[Array.IndexOf(keys,miniKey)],frozenMini),"Generator changed an anchor's position, size, angle or cell");
            var image=new RenderTargetBitmap(1200,800,96,96,PixelFormats.Pbgra32);image.Render(root);var bytes=new byte[1200*800*4];image.CopyPixels(bytes,4800,0);
            check(Enumerable.Range(0,bytes.Length/4).Any(i=>bytes[i*4+2]>bytes[i*4+1]*1.5 && bytes[i*4+2]>bytes[i*4]*1.5),"Anchor cells were not drawn red");
            foreach(var item in draft.Items)check(PatternBottleLayout.FitsOnRing(frame,assets,item,draft.Items.Where(p=>!ReferenceEquals(p,item)).ToList(),2,assets[item.Motif].IsFiller),"Reflow around anchors created overlap across the seam");
            // A neighbouring edit runs the filler-size limiter too. It must
            // preserve both the large and the small anchored cell exactly.
            var conflicting=Draft(9999);conflicting.Reserve=new RectangleGeometry(new Rect(-1000,-1000,3000,3000));
            bool refused=false;try{preview.Update(frame,conflicting,0);}catch(InvalidOperationException){refused=true;}
            check(refused && ReferenceEquals(Field<PatternDraft>(preview,"_draft"),draft),"A conflicting regeneration replaced the visible anchored layout");
            check(Same(draft.Items[Array.IndexOf(keys,anchor)],frozen) && Same(draft.Items[Array.IndexOf(keys,miniKey)],frozenMini),"A rejected update lost fixed cells");
            bool turned=false;
            for(int i=0;i<keys.Length;i++)if(keys[i]!=anchor && keys[i]!=miniKey)
            {
                Call(preview,"SelectMotif",Screen(draft.Items[i].Center));
                if((bool)Call(preview,"ApplyMotifTurn",keys[i],47d)){turned=true;break;}
            }
            check(turned,"No unpinned bottle print can turn beside anchors");
            check(Same(draft.Items[Array.IndexOf(keys,anchor)],frozen) && Same(draft.Items[Array.IndexOf(keys,miniKey)],frozenMini),"Editing a neighbour resized an anchor");
            Call(preview,"SelectMotif",Screen(frozenMini.Center));preview.ResetSelectedMotif();
            check(Same(draft.Items[Array.IndexOf(keys,miniKey)],frozenMini),"Reset changed a red miniature anchor");
            preview.SetSelectedAnchor(false);check(!preview.SelectedMotifAnchored,"The red miniature cannot be unpinned");
            Call(preview,"SelectMotif",Screen(frozen.Center));preview.SetMotifTool(3);Call(preview,"TryMotifClick",Screen(frozen.Center));
            check(!Field<string[]>(preview,"_motifKeys").Contains(anchor),"The eraser could not remove an explicit red anchor");
            preview.ClearMotifs();check(draft.Items.Count==0,"Clear did not remove pinned cells");
            draft=Draft(7124);preview.Update(frame,draft,0);check(draft.Items.Count==0,"Cleared hidden cells reappeared on refresh");
            preview.RestoreAutomaticMotifs();check(draft.Items.Count>0,"Automatic bottle grid could not be restored after clearing anchors");
        }
    }
    private static void Editing(Action<bool,string> check,PatternFillMode mode,PatternBottle bottle)
    {
        var frame=PatternBottleLayout.Frame(bottle,new PatternPoint(100,100));var assets=new[]{Asset(1,false),Asset(2,true,8,6)};
        var draft=new PatternDraft{Frame=frame,Options=new PatternFillOptions{Mode=mode,SizeMm=17,GapMm=2,Seed=8123},Motifs=assets,Items=new List<PatternPlacement>()};
        var preview=new PatternPreviewSurface(new PatternEditorSession{BottlePageIndex=1,BottleCenter=new PatternPoint(100,100),Motifs=assets},frame);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya bottle click checks"){Width=1200,Height=800,WindowStyle=0}))
        {
            var root=new Border{Child=preview};source.RootVisual=root;preview.Update(frame,draft,0);Render(root);
            Point Screen(PatternPoint point)=>(Point)Call(preview,"ToScreen",point);
            void Click(double u,double v){Call(preview,"TryMotifClick",Screen(PatternBottleLayout.OnFrame(frame,u,v)));Render(root);}
            preview.SetMotifTool(1);Click(bottle.Width/2,bottle.Slant/2);
            check(draft.Items.Count==1 && !assets[draft.Items[0].Motif].IsFiller,"A main click did not work on the bottle: "+mode);
            var initialCenter=draft.Items[0].Center;
            preview.SetMotifTool(2);Call(preview,"TryMotifClick",Screen(initialCenter));Render(root);
            check(draft.Items.Count==2 && draft.Items.Any(p=>assets[p.Motif].IsFiller),"A filler click did not work on the bottle: "+mode);
            check(PatternHeroFill.Longest(assets[1],draft.Items.First(p=>assets[p.Motif].IsFiller))<=PatternHeroFill.Longest(assets[0],draft.Items.First(p=>!assets[p.Motif].IsFiller))*.25+1e-6,"Bottle filler exceeded one-quarter size");
            var primary=draft.Items.First(p=>!assets[p.Motif].IsFiller);
            check(Math.Abs(primary.Center.X-initialCenter.X)+Math.Abs(primary.Center.Y-initialCenter.Y)>.1,"A mini cell did not displace the surrounding generation grid");
            var mini=draft.Items.First(p=>assets[p.Motif].IsFiller);
            check(mini.Cell.Length>=3 && mini.Cell.Max(p=>p.X)-mini.Cell.Min(p=>p.X)<17,"A filler still owns a full-size cell");
            preview.SetMotifTool(0);Call(preview,"SelectMotif",Screen(primary.Center));
            check(preview.HasSelectedMotif && draft.Items.Count==2,"A bottle print cannot be selected for rotation");
            object[] args={null};var handle=(Point)Call(preview,"MotifHandle",args);var center=(Point)args[0];var vector=handle-center;
            check(handle.X>=0 && handle.X<=preview.RenderSize.Width && handle.Y>=0 && handle.Y<=preview.RenderSize.Height,"Bottle rotation handle is outside the visible editor");
            Set(preview,"_motifDragCenter",center);Set(preview,"_motifDragVector",vector);Set(preview,"_motifStartTurn",0d);Set(preview,"_motifDragAngle",0d);
            Call(preview,"TurnMotifTo",center+new Vector(vector.X*Math.Cos(.45)-vector.Y*Math.Sin(.45),vector.X*Math.Sin(.45)+vector.Y*Math.Cos(.45)));
            check(Math.Abs(draft.Items.First(p=>!assets[p.Motif].IsFiller).Angle-primary.Angle)>.1,"Bottle print rotation was ignored");
            for(int i=0;i<draft.Items.Count;i++)check(PatternBottleLayout.FitsOnRing(frame,assets,draft.Items[i],draft.Items.Where((p,j)=>j!=i).ToList(),2,assets[draft.Items[i].Motif].IsFiller),"A manual bottle edit violated the gap across its seam");
            preview.SetMotifTool(3);Call(preview,"TryMotifClick",Screen(draft.Items.First(p=>assets[p.Motif].IsFiller).Center));check(draft.Items.Count==1 && !assets[draft.Items[0].Motif].IsFiller,"Bottle click eraser did not remove only its filler");
            preview.ClearMotifs();check(draft.Items.Count==0,"Bottle clear failed");preview.SetMotifTool(1);Click(.5,bottle.Slant/2);
            check(draft.Items.Count==1 && draft.BottleCutView!=null,"A bottle seam print was lost or did not rebuild the cutting outline");
            var before=draft.Items[0];Call(preview,"SelectMotif",Screen(before.Center));preview.SetSelectedAnchor(true);
            Click(bottle.Width-.5,bottle.Slant/2);check(draft.Items.Count==1,"A new cell displaced a red anchor across the seam");
            var geometry=PatternSilhouette.At(assets[0],draft.Items[0]);var matrix=geometry.Transform.Value;matrix.Append(new Matrix(1,0,0,-1,0,frame.Height));geometry=geometry.Clone();geometry.Transform=new MatrixTransform(matrix);
            double area=geometry.GetArea(),overlap=Geometry.Combine(draft.BottleCutView,geometry,GeometryCombineMode.Intersect,null).GetArea();
            check(Math.Abs(area-overlap)<=Math.Max(.001,area*.00001),"The seam cut removes visible area from a manually placed print");
            Call(preview,"SelectMotif",Screen(draft.Items[0].Center));check(preview.HasSelectedMotif,"A seam print cannot be selected");
            preview.ResetSelectedMotif();check(draft.Items.Count==1,"Resetting a bottle print angle lost its item");
        }
    }
}
