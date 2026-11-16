using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class ContourChecks
{
    internal static void Run(string root)
    {
        var image=Noisy();var original=LetterTransferImages.Pixels(image);
        var sharp=Task.Run(()=>LetterTransferContour.Refine(image,1.5,4,CancellationToken.None)).GetAwaiter().GetResult();
        var pixels=LetterTransferImages.Pixels(sharp);
        Program.Check(sharp.IsFrozen && sharp.PixelHeight>=390,"Refined contour failed on a background thread or was not rendered at higher resolution.");
        Program.Check(pixels.Where((v,i)=>i%4==3).All(a=>a==0 || a==255),"Refined edge contains semitransparent fringe.");
        Program.Check(Enumerable.Range(0,pixels.Length/4).All(p=>pixels[p*4+3]==0?pixels[p*4]==0 && pixels[p*4+1]==0 && pixels[p*4+2]==0:pixels[p*4]==30 && pixels[p*4+1]==150 && pixels[p*4+2]==210),"Contour rendering blended hidden background RGB or changed the constant foreground color.");
        Program.Check(pixels[(200*sharp.PixelWidth+260)*4+3]==0,"Counter inside the letter was filled during tracing.");
        Program.Check(Components(pixels,sharp.PixelWidth,sharp.PixelHeight)==2,"Detached accent or main letter was lost during contour fitting.");
        double before=Jitter(original,100,20,85,1),after=Jitter(pixels,sharp.PixelWidth,80,340,4);
        Program.Check(after<before*.65,"Straight edge was not made measurably more even: "+before+" -> "+after);
        Program.Check(original.SequenceEqual(LetterTransferImages.Pixels(image)),"Contour fitting mutated the source bitmap.");
        var rectangle=LetterTransferContour.Refine(Program.Solid(40,40,Colors.Gold),1.5,4,CancellationToken.None);var rect=LetterTransferImages.Pixels(rectangle);
        Program.Check(rectangle.PixelWidth==160 && rectangle.PixelHeight==160 && new[]{0,159,159*160,160*160-1}.All(p=>rect[p*4+3]==255),"Stable square corners were rounded or shifted.");
        var thin=new byte[40*40*4];for(int y=5;y<35;y++){int p=(y*40+10)*4;thin[p]=210;thin[p+1]=100;thin[p+2]=50;thin[p+3]=255;}
        var stroke=BitmapSource.Create(40,40,96,96,PixelFormats.Bgra32,null,thin,160);stroke.Freeze();
        var line=LetterTransferContour.Refine(stroke,2,4,CancellationToken.None);
        Program.Check(line.PixelWidth==4 && line.PixelHeight==120,"A separate one-pixel stroke was erased by contour smoothing.");
        var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;try{LetterTransferContour.Refine(image,1.5,4,canceled.Token);}catch(OperationCanceledException){stopped=true;}
        Program.Check(stopped,"Contour fitting ignored cancellation.");
        Counters();Persistence(root);Preview(sharp);
    }
    private static void Counters()
    {
        int width=40,height=60;var data=LetterTransferImages.Pixels(Program.Solid(width,height,Colors.Gold));
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(x>=10 && x<25 && (y>=10 && y<20 || y>=35 && y<50))data[(y*width+x)*4+3]=0;
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);image.Freeze();
        var sharp=LetterTransferContour.Refine(image,1.5,4,CancellationToken.None);var pixels=LetterTransferImages.Pixels(sharp);
        Program.Check(pixels[(60*sharp.PixelWidth+60)*4+3]==0 && pixels[(168*sharp.PixelWidth+60)*4+3]==0 && pixels[(110*sharp.PixelWidth+60)*4+3]==255,"Two counters were filled, merged or cut through their crossbar.");
        var large=Program.Solid(500,500,Colors.Gold);var set=new LetterTransferSet{SharpContour=true,EdgeTrimPx=0,SmoothEdges=false};
        var same=LetterTransferEdges.Finish(large,set,"А",CancellationToken.None);
        Program.Check(same.PixelWidth==500 && same.PixelHeight==500 && LetterTransferImages.Pixels(large).SequenceEqual(LetterTransferImages.Pixels(same)),"Already sufficient pixel resolution was downsampled or its interior changed.");
    }
    private static BitmapSource Noisy()
    {
        const int size=100;var data=new byte[size*size*4];for(int p=0;p<data.Length;p+=4){data[p]=255;data[p+2]=255;}
        void Paint(int x,int y){int p=(y*size+x)*4;data[p]=30;data[p+1]=150;data[p+2]=210;data[p+3]=255;}
        for(int y=10;y<size;y++)for(int x=(int)Math.Round(14+(y-10)*.35+Math.Sin(y*1.7)*1.4);x<size;x++)if(!(x>=55 && x<75 && y>=40 && y<65))Paint(x,y);
        for(int y=0;y<6;y++)for(int x=0;x<8;x++)Paint(x,y);
        var image=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,data,size*4);image.Freeze();return image;
    }
    private static double Jitter(byte[] pixels,int width,int first,int last,double scale)
    {
        var x=new List<double>();for(int y=first;y<last;y++){int at=0;while(at<width && pixels[(y*width+at)*4+3]==0)at++;x.Add(at/scale);}
        double middle=(x.Count-1)*.5,mean=x.Average(),sum=0,square=0;
        for(int i=0;i<x.Count;i++){sum+=(i-middle)*(x[i]-mean);square+=(i-middle)*(i-middle);}double slope=sum/square,error=0;
        for(int i=0;i<x.Count;i++){double d=x[i]-mean-slope*(i-middle);error+=d*d;}return Math.Sqrt(error/x.Count);
    }
    private static int Components(byte[] pixels,int width,int height)
    {
        var seen=new bool[width*height];int count=0;var pending=new Queue<int>();
        for(int start=0;start<seen.Length;start++)
        {
            if(seen[start] || pixels[start*4+3]==0)continue;seen[start]=true;pending.Enqueue(start);int area=0;
            while(pending.Count>0){int p=pending.Dequeue(),x=p%width,y=p/width;area++;for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++){int nx=x+dx,ny=y+dy;if(nx<0 || ny<0 || nx>=width || ny>=height)continue;int n=ny*width+nx;if(seen[n] || pixels[n*4+3]==0)continue;seen[n]=true;pending.Enqueue(n);}}
            if(area>=16)count++;
        }
        return count;
    }
    private static void Persistence(string root)
    {
        var set=new LetterTransferSet{SharpContour=true,ContourSmoothPx=1.5,EdgeTrimPx=0,SmoothEdges=false};string library=Path.Combine(root,"contour"),directory=LetterTransferStore.Create(library,set);
        var glyph=LetterTransferImages.Add(directory,set,"А",Program.Solid(40,40,Colors.Gold),"test");LetterTransferStore.Save(directory,set);
        var image=LetterTransferImages.Load(LetterTransferStore.Resolve(directory,glyph.File));
        Program.Check(image.PixelHeight>=470 && image.DpiY>=598 && Math.Abs(image.PixelHeight*25.4/image.DpiY-20)<.02,"High-resolution output lost the 20 mm physical height.");
        Program.Check(glyph.SharpContour && glyph.ContourSmoothPx==1.5 && LetterTransferStore.Load(directory).SharpContour,"Contour settings or applied glyph metadata were not saved.");
        string source=LetterTransferStore.Resolve(directory,glyph.OriginalFile);byte[] original=File.ReadAllBytes(source);string fingerprint=LetterTransferImages.Fingerprint(image),id=glyph.Id;
        LetterTransferEdges.Reclean(directory,set,CancellationToken.None);Program.Check(set.Glyphs[0].Id==id && LetterTransferImages.Fingerprint(LetterTransferImages.Load(LetterTransferStore.Resolve(directory,set.Glyphs[0].File)))==fingerprint,"Repeated contour processing accumulated changes or lost glyph identity.");
        var window=new LetterTransferWindow(()=>"",()=>null,(_,__)=>{},library);var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        try
        {
            var cache=(Dictionary<string,BitmapSource>)window.GetType().GetField("_cache",flags).GetValue(window);
            Program.Check(cache.Values.Any(i=>i!=null && i.PixelHeight>250),"Print preview still reduces the high-resolution letter to 250 px.");
        }
        finally{window.Close();}
        set.SharpContour=false;LetterTransferEdges.Reclean(directory,set,CancellationToken.None);
        Program.Check(set.Glyphs[0].Height==40 && !set.Glyphs[0].SharpContour && original.SequenceEqual(File.ReadAllBytes(source)),"Disabling contour refinement did not restore the original or overwrote source PNG.");
        Program.Check(LetterTransferImages.ActualSize(source)=="40 × 40 px","Actual source size was read from requested resolution or a thumbnail.");
    }
    private static void Preview(BitmapSource image)
    {
        var window=LetterTransferEdgePreview.Create(null,"А",image,image);
        try{var root=(FrameworkElement)window.Content;root.Measure(new Size(1000,700));root.Arrange(new Rect(0,0,1000,700));root.UpdateLayout();var controls=Descendants(root).ToArray();Program.Check(controls.OfType<Image>().Count()==2 && controls.OfType<ComboBox>().Any(c=>c.Items.Count==4),"Edge preview omitted before/after images or background/zoom choices.");}
        finally{window.Close();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root){yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
}
