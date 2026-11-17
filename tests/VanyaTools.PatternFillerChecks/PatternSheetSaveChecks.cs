using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class PatternSheetSaveChecks
{
    private static T Worker<T>(Func<T> action)
    {
        T result=default(T); Exception error=null;
        var thread=new Thread(()=>{try{result=action();}catch(Exception failure){error=failure;}});
        thread.Start();thread.Join();if(error!=null)throw error;return result;
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var bitmap=new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0);var bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return bytes;
    }
    internal static void Run(Action<bool,string> check)
    {
        string root=Path.Combine(Path.GetTempPath(),"Vanya-PatternSave-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string input=Path.Combine(root,"source.png"),output=Path.Combine(root,"saved.png");
            var image=AiPrintTab.Bitmap(input); var metadata=new BitmapMetadata("png"){Title="Исходный лист ИИ"};
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image,null,metadata,null));using(var file=File.Create(input))encoder.Save(file);
            byte[] original=File.ReadAllBytes(input);var loaded=Worker(()=>PatternFillerExtraction.LoadSheet(input,null,CancellationToken.None));File.Delete(input);
            check(loaded.Sheet.IsFrozen && loaded.Count==4 && !File.Exists(input),"Worker sheet retained a file lock or lost preview/sprites");
            loaded.SaveSheet(output);
            check(original.SequenceEqual(File.ReadAllBytes(output)),"Saving a background-thread PNG re-encoded or lost the original sheet");
            loaded.SaveSheet(output); check(original.SequenceEqual(File.ReadAllBytes(output)),"Repeated sheet save failed or changed PNG bytes");
            check(Pixels(loaded.Sheet).SequenceEqual(Pixels(image)),"Cross-thread preview was changed by matte removal");
            // A raw decoded frame also needs a decoder-free fallback export.
            var fallback=Worker(()=>{using(var stream=new MemoryStream(original)){var frame=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);frame.Freeze();return PatternFillerExtraction.Extract(frame,null,CancellationToken.None);}});
            fallback.SaveSheet(output);
            using(var stream=File.OpenRead(output)){var saved=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);check(Pixels(saved).SequenceEqual(Pixels(image)) && saved.PixelWidth==image.PixelWidth && Math.Abs(saved.DpiX-image.DpiX)<.02,"Fallback cross-thread save lost original pixels, dimensions or DPI");}
            // Preserve high-depth PNG bytes even though the preview is 8-bit.
            var gray=BitmapSource.Create(64,64,300,450,PixelFormats.Gray16,null,new byte[64*64*2],64*2);encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(gray));using(var file=File.Create(input))encoder.Save(file);
            original=File.ReadAllBytes(input);var high=Worker(()=>PatternFillerExtraction.LoadSheet(input,null,CancellationToken.None));File.Delete(input);high.SaveSheet(output);
            check(original.SequenceEqual(File.ReadAllBytes(output)),"Saving the paid sheet reduced bit depth or altered physical resolution");
            using(var held=new FileStream(output,FileMode.Open,FileAccess.Read,FileShare.None))
            {
                bool busy=false;try{high.SaveSheet(output);}catch(IOException){busy=true;}check(busy,"Saving over a genuinely locked destination falsely succeeded");
            }
            check(original.SequenceEqual(File.ReadAllBytes(output)),"Failed save damaged the previously saved PNG");
            high.SaveSheet(output);check(original.SequenceEqual(File.ReadAllBytes(output)),"Save could not retry after releasing an external file lock");
            using(var cancel=new CancellationTokenSource()){cancel.Cancel();bool stopped=false;try{PatternFillerExtraction.LoadSheet(input,null,cancel.Token);}catch(OperationCanceledException){stopped=true;}check(stopped,"Canceled sheet loading still accessed the source");}
            int calls=ReplicateWorkerClient.Calls;var generated=Worker(()=>PatternFillerAi.Generate(image,"synthetic",PatternFillerBrief.Preset(0),CancellationToken.None));var paidBytes=ReplicateWorkerClient.LastImage;
            check(!File.Exists(ReplicateWorkerClient.Output) && !File.Exists(AiPrintTab.Upload),"Finished generation retained temporary source files");
            generated.SaveSheet(output);generated.SaveSheet(output);
            check(paidBytes.SequenceEqual(File.ReadAllBytes(output)) && ReplicateWorkerClient.Calls==calls+1,"Saving after generation cleanup lost the paid original or submitted another request");
        }
        finally{Directory.Delete(root,true);}
    }
}
