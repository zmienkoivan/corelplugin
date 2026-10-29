using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); _checks++; }
    private static void Throws<T>(Action action, string name) where T : Exception
    { try { action(); } catch (T) { _checks++; return; } throw new Exception(name); }
    private static byte[] Pixels(int w, int h, byte gray)
    { var p = new byte[w*h*4]; for (int i=0; i<p.Length; i+=4) { p[i]=p[i+1]=p[i+2]=gray; p[i+3]=255; } return p; }
    private static void Gray(byte[] p, int w, int x, int y, byte value)
    { int i=(y*w+x)*4; p[i]=p[i+1]=p[i+2]=value; }
    private static byte[] Mask(byte[] p, int w, int h, bool border, int tol, params BackgroundRemovalSeed[] seeds)
        => BackgroundRegionMask.Create(p,w,h,new[]{0xffffff},tol,border,seeds,CancellationToken.None);

    [STAThread]
    private static int Main()
    {
        string dir=Path.Combine(Path.GetTempPath(),"Vanya-WandChecks-"+Guid.NewGuid().ToString("N"));
        try
        {
            // Two enclosed regions inside a one-pixel black wall. Only the outer
            // white frame touches the border. Downsampling this wall is unsafe.
            var image=Pixels(11,9,0);
            for(int x=0;x<11;x++) { Gray(image,11,x,0,255); Gray(image,11,x,8,255); }
            for(int y=0;y<9;y++) { Gray(image,11,0,y,255); Gray(image,11,10,y,255); }
            for(int y=2;y<=4;y++) for(int x=2;x<=3;x++) Gray(image,11,x,y,255);
            for(int y=3;y<=5;y++) for(int x=7;x<=8;x++) Gray(image,11,x,y,200);
            var original=(byte[])image.Clone();
            var border=Mask(image,11,9,true,0);
            Check(border.Count(p=>p!=0)==36,"Border only");
            Check(border[24]==0&&border[40]==0,"Enclosed regions preserved");
            var first=Mask(image,11,9,false,0,new BackgroundRemovalSeed(2,2));
            Check(first.Count(p=>p!=0)==6,"One clicked region");
            Check(first[0]==0&&first[40]==0,"Disconnected colors unaffected");
            var both=Mask(image,11,9,false,0,new BackgroundRemovalSeed(2,2),new BackgroundRemovalSeed(7,3));
            Check(both.Count(p=>p!=0)==12,"Different-color regions selected together");
            Check(Mask(image,11,9,true,0,new BackgroundRemovalSeed(2,2),new BackgroundRemovalSeed(7,3)).Count(p=>p!=0)==48,"Border plus picks");
            Check(image.SequenceEqual(original),"Source pixels untouched");
            Check(Mask(image,11,9,false,0,new BackgroundRemovalSeed(2,2),new BackgroundRemovalSeed(2,2)).SequenceEqual(first),"Duplicate click idempotent");
            var ramp=Pixels(5,1,0); for(int x=0;x<5;x++) Gray(ramp,5,x,0,(byte)(100+x*5));
            Check(Mask(ramp,5,1,false,10,new BackgroundRemovalSeed(0,0),new BackgroundRemovalSeed(2,0)).All(p=>p==1),"Overlapping selections can extend through previous mask");
            Check(Mask(ramp,5,1,false,10,new BackgroundRemovalSeed(0,0)).SequenceEqual(new byte[]{1,1,1,0,0}),"Inclusive tolerance fixed to seed");
            var diagonal=Pixels(3,3,0); Gray(diagonal,3,0,0,255); Gray(diagonal,3,1,1,255);
            Check(Mask(diagonal,3,3,false,0,new BackgroundRemovalSeed(0,0)).Count(p=>p!=0)==1,"4-connectivity does not leak through corner");
            var islands=Pixels(3,1,255); islands[7]=0;
            Check(Mask(islands,3,1,false,0,new BackgroundRemovalSeed(0,0)).SequenceEqual(new byte[]{1,0,0}),"Transparent gap does not join visible islands");
            Check(Mask(islands,3,1,false,0,new BackgroundRemovalSeed(1,0)).All(p=>p==0),"Transparent seed ignored");
            Check(Mask(image,11,9,false,0).All(p=>p==0),"No picks means no deletion");
            Throws<ArgumentOutOfRangeException>(()=>Mask(image,11,9,false,0,new BackgroundRemovalSeed(-1,0)),"Seed bounds checked");
            Throws<ArgumentException>(()=>Mask(image,11,9,false,0,new BackgroundRemovalSeed[65]),"Seed count bounded");
            var cancel=new CancellationTokenSource(); cancel.Cancel();
            Throws<OperationCanceledException>(()=>BackgroundRegionMask.Create(image,11,9,new[]{0xffffff},0,true,null,cancel.Token),"Cancellation honored");
            Check(BackgroundRegionMask.TryMapPoint(25,35,110,90,1100,900,out var seed)&&seed.X==250&&seed.Y==350,"Overview maps to full source");
            Check(BackgroundRegionMask.TryMapPoint(2500,3500,11000,9000,1100,900,out seed)&&seed.X==250&&seed.Y==350,"Zoom maps to same source");
            Check(BackgroundRegionMask.TryMapPoint(109.99,89.99,110,90,1100,900,out seed)&&seed.X==1099&&seed.Y==899,"Last pixel maps");
            Check(!BackgroundRegionMask.TryMapPoint(110,90,110,90,1100,900,out seed),"Outside click rejected");
            Check(!BackgroundRegionMask.TryMapPoint(Double.NaN,0,110,90,1100,900,out seed),"NaN rejected");
            Check(!BackgroundRegionMask.TryMapPoint(0,0,0,90,1100,900,out seed),"Unarranged image rejected");

            Directory.CreateDirectory(dir); string src=Path.Combine(dir,"source.png"),dst=Path.Combine(dir,"result.png");
            var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(11,9,300,300,PixelFormats.Bgra32,null,image,44)));
            using(var stream=File.Create(src)) encoder.Save(stream);
            BackgroundRemovalProcessor.RemoveColor(src,dst,new[]{Colors.White},0,0,false,new[]{new BackgroundRemovalSeed(2,2),new BackgroundRemovalSeed(7,3)},CancellationToken.None);
            var result=Read(dst); Check(result.Length==image.Length,"PNG canvas unchanged");
            Check(result[24*4+3]==0&&result[40*4+3]==0,"PNG internal holes transparent");
            Check(result[3]==255,"Unselected border remains opaque");
            Check(result[12*4+3]==255,"Thin enclosing wall remains opaque");
            Check(Read(src).SequenceEqual(image),"Input file unchanged");
            BackgroundRemovalProcessor.RemoveColor(src,dst,new[]{Colors.White},0,0,true,new[]{new BackgroundRemovalSeed(2,2)},CancellationToken.None);
            result=Read(dst); Check(result[3]==0&&result[24*4+3]==0&&result[40*4+3]==255,"Combined PNG removes only border and selected hole");
            Throws<InvalidOperationException>(()=>BackgroundRemovalProcessor.RemoveColor(src,dst,new[]{Colors.White},0,0,false,null,CancellationToken.None),"Empty wand cannot report success");
            Console.WriteLine("Background region checks passed: "+_checks); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if(Directory.Exists(dir)) Directory.Delete(dir,true); }
    }
    private static byte[] Read(string path)
    { var b=new FormatConvertedBitmap(AiPrintTab.Bitmap(path),PixelFormats.Bgra32,null,0); var p=new byte[b.PixelWidth*b.PixelHeight*4]; b.CopyPixels(p,b.PixelWidth*4,0); return p; }
}
namespace VanyaTools.Native
{
    internal static class AiPrintTab
    { internal static BitmapSource Bitmap(string path) { using(var s=File.OpenRead(path)) { var b=BitmapFrame.Create(s,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad); b.Freeze(); return b; } } }
    internal static class Log { internal static void Info(string text) {} }
}
