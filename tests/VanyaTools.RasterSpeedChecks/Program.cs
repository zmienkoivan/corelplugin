using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class Program
{
    private static int checks;
    private static void Check(bool yes, string message) { if (!yes) throw new Exception(message); checks++; }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { checks++; return; } catch (OperationCanceledException) { checks++; return; } throw new Exception("Expected rejection"); }
    private static byte[] Pixels(int w, int h)
    {
        var bytes = new byte[w*h*4]; new Random(82713).NextBytes(bytes); byte[] alpha = {0,1,76,129,130,254,255};
        for (int i=0; i<bytes.Length; i+=4) { if (i%3==0) bytes[i]=bytes[i+1]=bytes[i+2]=(byte)(248+i%8); bytes[i+3]=alpha[(i/4)%7]; }
        return bytes;
    }
    private static byte[] Decode(Stream stream, int w, int h)
    {
        stream.Position=0; var frame=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        Check(frame.PixelWidth==w && frame.PixelHeight==h,"Exact PNG dimensions");
        Check(Math.Abs(frame.DpiX-400)<.013 && Math.Abs(frame.DpiY-300)<.013,"DPI within one pixel/meter");
        BitmapSource image=frame.Format==PixelFormats.Bgra32?frame:new FormatConvertedBitmap(frame,PixelFormats.Bgra32,null,0);
        var data=new byte[w*h*4]; image.CopyPixels(data,w*4,0); return data;
    }
    private static uint U(byte[] b,int i)=>((uint)b[i]<<24)|((uint)b[i+1]<<16)|((uint)b[i+2]<<8)|b[i+3];
    private static void PngChecks()
    {
        foreach(var size in new[]{Tuple.Create(1,1),Tuple.Create(1,9000),Tuple.Create(263,317)})
        {
            var pixels=Pixels(size.Item1,size.Item2); var original=(byte[])pixels.Clone();
            using(var stream=new MemoryStream())
            {
                FastRasterPng.Write(stream,pixels,size.Item1,size.Item2,400,300);
                Check(Decode(stream,size.Item1,size.Item2).SequenceEqual(pixels),"Every straight RGB/alpha byte survives Windows PNG decode");
                Check(pixels.SequenceEqual(original),"Encoder never changes source");
                var png=stream.ToArray(); int p=8,idats=0;
                while(p<png.Length)
                {
                    int length=checked((int)U(png,p)); uint crc=0xffffffff;
                    for(int i=p+4;i<p+8+length;i++) { crc^=png[i]; for(int bit=0;bit<8;bit++) crc=(crc&1)==0?crc>>1:(crc>>1)^0xedb88320; }
                    Check((crc^0xffffffff)==U(png,p+8+length),"Independent bitwise CRC verifies PNG chunk");
                    if(png[p+4]=='I' && png[p+5]=='D') idats++; p+=length+12;
                }
                Check(p==png.Length && idats>0,"Complete PNG chunks");
            }
        }
        using(var stream=new MemoryStream())
        {
            Reject(()=>FastRasterPng.Write(stream,new byte[4],2,1,400,300)); Reject(()=>FastRasterPng.Write(stream,new byte[4],1,1,0,300));
            using(var cancel=new CancellationTokenSource()) { cancel.Cancel(); Reject(()=>FastRasterPng.Write(stream,new byte[4],1,1,400,300,cancel.Token)); }
            Check(stream.Length==0,"Invalid/cancelled input emits nothing");
        }
    }
    private static void Operations()
    {
        const int w=137,h=97; var pixels=Pixels(w,h); var original=(byte[])pixels.Clone();
        foreach(int tolerance in new[]{0,1,6,64,255}) foreach(int threshold in new[]{0,1,130,255})
        {
            var expected=(byte[])pixels.Clone(); for(int i=3;i<expected.Length;i+=4) expected[i]=expected[i]>0 && expected[i]>=threshold?(byte)255:(byte)0;
            expected=DtfNearWhitePixels.Whiten(expected,DtfNearWhitePixels.CreateMask(expected,w,h,tolerance));
            Check(expected.SequenceEqual(DtfRasterOperations.Correct(pixels,true,threshold,true,tolerance,CancellationToken.None)),"Merged correction equals established threshold+mask result");
            foreach(bool red in new[]{false,true})
            {
                expected=DtfNearWhitePixels.Highlight(pixels,DtfNearWhitePixels.CreateMask(pixels,w,h,tolerance),237,19,168);
                for(int i=0;i<expected.Length;i+=4) if(expected[i+3]>0 && expected[i+3]<255) { expected[i]=30; expected[i+1]=red?(byte)35:(byte)245; expected[i+2]=red?(byte)255:(byte)20; expected[i+3]=255; }
                Check(expected.SequenceEqual(DtfRasterOperations.Audit(pixels,true,red,true,tolerance,237,19,168,CancellationToken.None)),"Combined audit matches previous overlays in their original order");
            }
        }
        Check(pixels.SequenceEqual(original),"Audit and correction preserve source");
        var snapshot=DtfCorrectionPixels.Capture(BitmapSource.Create(w,h,400,300,PixelFormats.Bgra32,null,pixels,w*4));
        var result=Task.Run(()=>snapshot.Correct(true,130,true,6,CancellationToken.None).Audit(true,false,true,6,255,0,168,CancellationToken.None)).GetAwaiter().GetResult();
        Check(result.Bitmap().PixelWidth==w,"Managed preview crosses worker/UI threads");
        Check(snapshot.Crop(new Int32Rect(5,6,11,12),CancellationToken.None).Bitmap().PixelHeight==12,"Exact fragment bounds");
        using(var cancel=new CancellationTokenSource()) { cancel.Cancel(); Reject(()=>DtfRasterOperations.Correct(pixels,true,130,true,6,cancel.Token)); }
    }
    private static void Benchmark()
    {
        string root=Path.Combine(Path.GetTempPath(),"Vanya-RasterSpeed-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            const int w=2200,h=1600; var pixels=Pixels(w,h); string input=Path.Combine(root,"input.png"),output=Path.Combine(root,"output.png");
            using(var stream=File.Create(input)) FastRasterPng.Write(stream,pixels,w,h,400,300);
            var watch=Stopwatch.StartNew(); DtfNearWhiteFilter.Process(input,output,6); long old=watch.ElapsedMilliseconds;
            watch.Restart(); var corrected=DtfRasterOperations.Correct(pixels,false,130,true,6,CancellationToken.None); long memory=watch.ElapsedMilliseconds;
            using(var stream=File.OpenRead(output)) Check(Decode(stream,w,h).SequenceEqual(corrected),"Memory/file whitening match");
            watch.Restart(); DtfRasterOperations.Audit(corrected,true,false,true,6,255,0,168,CancellationToken.None);
            Console.WriteLine("BENCH previewPixels="+w*h+"; oldFileWhitenMs="+old+"; memoryWhitenMs="+memory+"; combinedAuditMs="+watch.ElapsedMilliseconds);
        }
        finally
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if(Directory.Exists(root) && String.Equals(Path.GetDirectoryName(Path.GetFullPath(root)),parent,StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(root).StartsWith("Vanya-RasterSpeed-",StringComparison.Ordinal)) Directory.Delete(root,true);
        }
    }
    [STAThread] private static int Main(string[] args)
    { try { if(args.Length==1 && args[0]=="--benchmark") Benchmark(); else { PngChecks(); Operations(); Console.WriteLine("Raster speed checks passed: "+checks+". No Corel."); } return 0; } catch(Exception error) { Console.Error.WriteLine(error); return 1; } }
}
