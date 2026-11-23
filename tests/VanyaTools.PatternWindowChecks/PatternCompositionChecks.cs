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

internal static class PatternCompositionChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static object Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Flags).Invoke(target,args);
    private static PatternAsset Asset(int id,bool mini,double w,double h)
    {var shape=new EllipseGeometry(new Rect(-w/2,-h/2,w,h));shape.Freeze();return new PatternAsset{Id=id,Label="Art "+id,IsFiller=mini,Size=new PatternPoint(w,h),Silhouette=shape,Footprint=new PatternFootprint(new PatternPoint(w,h))};}
    private static PatternDraft Build(PatternEditorSession session,PatternEditorSettings settings)
    {
        var window=new PatternFillWindow(session,settings);
        try
        {
            var frame=session.BottleMode?PatternBottleLayout.Frame(new PatternBottle(settings.BottleTop,settings.BottleBottom,settings.BottleHeight),session.BottleCenter):session.Seam;
            return (PatternDraft)Call(window,"BuildDraft",frame,settings,session.Hero!=null,new HashSet<int>(),CancellationToken.None);
        }
        finally{window.Close();}
    }
    internal static void Run(Action<bool,string> check)
    {
        var assets=new[]{Asset(1,false,15,10),Asset(2,false,10,16),Asset(3,false,14,13),Asset(4,true,5,3),Asset(5,true,4,4)};
        foreach(int shape in new[]{0,1,2,3})foreach(int seed in new[]{0,21,381})
        {
            bool bottle=shape>0;
            var settings=PatternBottlePresets.Bic();settings.Layout.Seed=seed;
            settings.BottleTop=shape==2?25:20;settings.BottleBottom=shape==3?25:20;
            var session=new PatternEditorSession{BottlePageIndex=bottle?1:0,Seam=new PatternFrame{Width=63,Height=64,Geometry=new RectangleGeometry(new Rect(0,0,63,64))},Motifs=assets,Hero=Asset(7,false,12,35)};
            var loose=Build(session,settings);var again=Build(session,settings);
            settings.Layout.Loose=false;var dense=Build(session,settings);settings.Layout.Loose=true;
            var mains=loose.Items.Where(p=>!assets[p.Motif].IsFiller).ToArray();var small=loose.Items.Where(p=>assets[p.Motif].IsFiller).ToArray();
            check(mains.Length>=4 && small.Length>0,"Airy composition lost its main drawings or all its accents");
            check(loose.Items.Count==again.Items.Count && loose.Items.Zip(again.Items,(a,b)=>a.Motif==b.Motif && a.Center.X==b.Center.X && a.Center.Y==b.Center.Y && a.Size==b.Size && a.Angle==b.Angle).All(n=>n),"Composition is not deterministic for a saved seed");
            double Median(IEnumerable<double> values)=>values.OrderBy(n=>n).ElementAt(values.Count()/2);
            check(Median(mains.Select(p=>PatternHeroFill.Longest(assets[p.Motif],p)))<Median(dense.Items.Where(p=>!assets[p.Motif].IsFiller).Select(p=>PatternHeroFill.Longest(assets[p.Motif],p)))*.95,"Loose mode still enlarges art to fill its cells");
            check(mains.Select(p=>Math.Round(p.Angle/20)).Distinct().Count()>=3,"Main artwork has no varied orientations");
            foreach(var pose in loose.Items)
            {
                var asset=assets[pose.Motif];
                check(loose.Reserve.FillContainsWithDetail(PatternSilhouette.At(asset,pose),.05,ToleranceType.Absolute)==IntersectionDetail.Empty,"Loose composition overlaps its hero reserve");
                var others=loose.Items.Where(p=>!ReferenceEquals(p,pose)).ToList();
                if(bottle)check(PatternBottleLayout.FitsOnRing(loose.Frame,assets,pose,others,settings.Layout.GapMm,true),"Loose artwork overlaps its rim, seam neighbour or another drawing");
                else check(others.All(p=>PatternMotifRotation.Separated(asset.Footprint.At(pose),assets[p.Motif].Footprint.At(p),settings.Layout.GapMm)),"Loose drawings overlap");
                if(!asset.IsFiller)continue;
                double cap=PatternHeroFill.FillerSizeAt(loose,pose.Center),size=PatternHeroFill.Longest(asset,pose);
                check(size<=cap+1e-6 && size>=cap*.65-1e-6,"An automatic accent was squeezed into an unreadable fragment");
                double w=pose.Cell.Max(p=>p.X)-pose.Cell.Min(p=>p.X),h=pose.Cell.Max(p=>p.Y)-pose.Cell.Min(p=>p.Y);
                check(w/h<1.5 && w/h>.65 && Math.Max(w,h)<settings.Layout.SizeMm,"An accent owns a narrow sliver or a main-size cell");
            }
            if(shape==1 && seed==21)Editing(check,session,loose,settings);
        }
        var old=new PatternFillOptions();PatternProjectData.ReadValues(old,new Dictionary<string,object>{{"Mode",4}});check(!old.Loose,"An old saved project silently switched composition");
        var saved=new PatternFillOptions();PatternProjectData.ReadValues(saved,PatternProjectData.Values(new PatternFillOptions{Mode=PatternFillMode.Pebbles,Loose=true}));check(saved.Airy,"Saved project lost its loose composition choice");
        RenderReference();
    }
    private static void Editing(Action<bool,string> check,PatternEditorSession session,PatternDraft draft,PatternEditorSettings settings)
    {
        var preview=new PatternPreviewSurface(session,draft.Frame);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya airy motif editing"){Width=900,Height=800,WindowStyle=0}))
        {
            source.RootVisual=preview;preview.Update(draft.Frame,draft,0);preview.Measure(new Size(900,800));preview.Arrange(new Rect(0,0,900,800));new RenderTargetBitmap(900,800,96,96,PixelFormats.Pbgra32).Render(preview);
            Point Screen(PatternPoint p)=>(Point)Call(preview,"ToScreen",p);
            var main=draft.Items.First(p=>!draft.Motifs[p.Motif].IsFiller);double size=PatternHeroFill.Longest(draft.Motifs[main.Motif],main);
            Call(preview,"SelectMotif",Screen(main.Center));preview.SetSelectedAnchor(true);preview.SetMotifTool(0);Call(preview,"TryMotifClick",Screen(main.Center));
            var replaced=draft.Items.First(p=>Math.Abs(p.Center.X-main.Center.X)<1 && Math.Abs(p.Center.Y-main.Center.Y)<1);
            check(preview.SelectedMotifAnchored && PatternHeroFill.Longest(draft.Motifs[replaced.Motif],replaced)<=size+.001,"Click replacement enlarged the loose artwork or lost its anchor");
            double maximum=draft.Items.Where(p=>!draft.Motifs[p.Motif].IsFiller).Max(p=>PatternHeroFill.Longest(draft.Motifs[p.Motif],p));
            preview.SetMotifTool(2);bool added=false;
            foreach(var mini in draft.Items.Where(p=>draft.Motifs[p.Motif].IsFiller).ToArray())if((bool)Call(preview,"PaintMotif",Screen(mini.Center))){added=true;break;}
            check(added && draft.Items.Where(p=>!draft.Motifs[p.Motif].IsFiller).All(p=>PatternHeroFill.Longest(draft.Motifs[p.Motif],p)<=maximum+.001),"Adding a mini accent inflated the main artwork");
        }
    }
    private static void RenderReference()
    {
        string path=Environment.GetEnvironmentVariable("VANYA_COMPOSITION_REFERENCE"),directory=Environment.GetEnvironmentVariable("VANYA_PATTERN_RENDER");
        if(String.IsNullOrEmpty(path) || String.IsNullOrEmpty(directory))return;
        var reference=BitmapFrame.Create(new Uri(path),BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        var bg=new FormatConvertedBitmap(reference,PixelFormats.Bgra32,null,0);var background=new byte[4];bg.CopyPixels(new Int32Rect(0,0,1,1),background,4,0);
        PatternAsset Crop(int id,bool mini,int x,int y,int w,int h)
        {
            var bytes=new byte[w*h*4];bg.CopyPixels(new Int32Rect(x,y,w,h),bytes,w*4,0);
            for(int i=0;i<bytes.Length;i+=4)if(Math.Abs(bytes[i]-background[0])+Math.Abs(bytes[i+1]-background[1])+Math.Abs(bytes[i+2]-background[2])<25)bytes[i+3]=0;
            if(id==10)
            {
                // The reference is a composed sheet: exclude neighbouring
                // accents cut by the hero crop, keeping its connected artwork.
                var seen=new bool[w*h];var largest=new List<int>();
                for(int p=0;p<seen.Length;p++)
                {
                    if(seen[p] || bytes[p*4+3]==0)continue;var group=new List<int>();var queue=new Queue<int>();queue.Enqueue(p);seen[p]=true;
                    while(queue.Count>0)
                    {
                        int q=queue.Dequeue();group.Add(q);int xx=q%w,yy=q/w;
                        for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)
                        {int px=xx+dx,py=yy+dy;if(px<0 || px>=w || py<0 || py>=h)continue;int v=py*w+px;if(seen[v] || bytes[v*4+3]==0)continue;seen[v]=true;queue.Enqueue(v);}
                    }
                    if(group.Count>largest.Count)largest=group;
                }
                var kept=new HashSet<int>(largest);for(int p=0;p<seen.Length;p++)if(!kept.Contains(p))bytes[p*4+3]=0;
            }
            var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,bytes,w*4);image.Freeze();var a=new PatternAsset{Id=id,Label="Reference crop "+id,IsFiller=mini,Preview=image,Size=new PatternPoint(w*.1,h*.1)};PatternFillService.PrepareSilhouette(a);return a;
        }
        var assets=new[]{Crop(1,false,68,64,157,132),Crop(2,false,260,65,126,126),Crop(3,false,571,64,151,132),Crop(4,false,173,215,170,119),Crop(5,false,674,211,155,119),Crop(6,true,483,177,31,36),Crop(7,true,361,198,59,39),Crop(8,true,306,316,50,52),Crop(9,true,504,572,35,35)};
        var session=new PatternEditorSession{BottlePageIndex=1,Motifs=assets,Hero=Crop(10,false,331,222,217,373)};
        Directory.CreateDirectory(directory);
        foreach(bool loose in new[]{false,true})
        {
            var settings=PatternBottlePresets.Bic();settings.Layout.Loose=loose;settings.Layout.Seed=21;var draft=Build(session,settings);
            var bitmap=PatternBic3DTexture.Create(draft,session.Hero,maximumSide:1000);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using(var file=File.Create(Path.Combine(directory,loose?"composition-loose-final.png":"composition-dense-final.png")))encoder.Save(file);
            if(loose)PrintPreview(draft,session.Hero,Path.Combine(directory,"composition-preview.png"));
            Console.WriteLine((loose?"Loose":"Dense")+" reference composition: "+draft.Items.Count+" drawings; hero "+draft.Hero?.Center.X+","+draft.Hero?.Center.Y+" size="+draft.Hero?.Size);
        }
    }
    private static void PrintPreview(PatternDraft draft,PatternAsset hero,string path)
    {
        var clip=draft.BottleCutView??draft.Frame.Geometry;var bounds=clip.Bounds;double scale=840/Math.Max(bounds.Width,bounds.Height);
        var visual=new DrawingVisual();using(var dc=visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(63,63,63)),null,new Rect(0,0,900,900));
            dc.PushTransform(new ScaleTransform(scale,scale));dc.PushTransform(new TranslateTransform(30/scale-bounds.X,30/scale-bounds.Y));dc.PushClip(clip);
            foreach(var pose in draft.Items)Draw(draft.Motifs[pose.Motif],pose);Draw(hero,draft.Hero);dc.Pop();dc.Pop();dc.Pop();
            void Draw(PatternAsset asset,PatternPlacement pose)
            {
                var fit=PatternTransform.Fit(asset.Size,pose);double x=pose.Center.X,y=draft.Frame.Height-pose.Center.Y;
                dc.PushTransform(new RotateTransform(-pose.Angle,x,y));dc.DrawImage(asset.Preview,new Rect(x-fit.Width/2,y-fit.Height/2,fit.Width,fit.Height));dc.Pop();
            }
        }
        var image=new RenderTargetBitmap(900,900,96,96,PixelFormats.Pbgra32);image.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using(var file=File.Create(path))encoder.Save(file);
    }
}
