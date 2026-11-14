using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static partial class Program
{
    private static void RunRestorationStep(DtfCorrectionWindow window,string method,params object[] args)
    {
        var task=(Task)Call(window,method,args);Until(()=>task.IsCompleted,"Restoration step stalled: "+method);task.GetAwaiter().GetResult();
        Until(()=>!Field<bool>(window,"_restorationBusy"),"Restoration busy flag retained: "+method);
    }
    private static DtfCorrectionResult RestoreProcessor(string source,string output,DtfCorrectionOptions options,CancellationToken cancel)
    {
        var pixels=DtfCorrectionPixels.Capture(PrintRestorationImages.Load(source)).Correct(options,cancel);
        using(var file=File.Create(output))pixels.WritePng(file,cancel);return new DtfCorrectionResult{OutputPath=output};
    }
    private static void RestorationChecks(string folder)
    {
        const int width=100,height=60;var data=new byte[width*height*4];
        for(int p=0;p<data.Length;p+=4)data[p+3]=255;
        for(int y=10;y<50;y++)for(int x=20;x<80;x++){int p=(y*width+x)*4;data[p]=data[p+1]=data[p+2]=253;}
        data[(20*width+40)*4+3]=128;
        string path=Path.Combine(folder,"restoration-source.png");using(var file=File.Create(path))FastRasterPng.Write(file,data,width,height,100,100);
        var original=File.ReadAllBytes(path);var image=PrintRestorationImages.Load(path);
        var bounds=PrintRestorationImages.TrimBounds(image,Colors.Black,10,CancellationToken.None);
        Check(bounds==new Int32Rect(18,8,64,44),"Trimming dropped ink or padding");
        var counts=PrintRestorationImages.Audit(image,6,CancellationToken.None);
        Check(counts.SemiTransparent==1 && counts.NearWhite==2400,"Full pixel audit counts incorrect");
        var key=PrintRestorationImages.KeyColor(image);Check(key!=Colors.Black && key!=Colors.White,"Key color must be vivid and absent from print");
        var request=new PrintRestorationRequest{SourcePath=path,KeyColor=key,WidthMm=25.4,HeightMm=15.24};
        var input=request.Input();Check((string)input["resolution"]=="4K" && (string)input["output_format"]=="png","Restoration uses wrong output format or quality");
        Check(request.Prompt.Contains("distress") && request.Prompt.Contains("symbols replacing letters") && request.Prompt.Contains("Do not invent") && !request.Prompt.Contains("BREATHE"),"Restoration prompt must preserve intentional design without hardcoded example text");
        Check(!((bool)input["google_search"]) && !((bool)input["image_search"]),"Restoration should not source replacement art from the web");
        bool confirm=false,review=false,cancelRun=false;int sent=0,consents=0,reviews=0;PrintRestorationRequest last=null;
        var host=new PrintRestorationHost {
            Validate=()=>{},Confirm=(_,r)=>{consents++;last=r;return confirm;},Review=(_,source,output)=>{reviews++;return review;},
            Generate=async(r,progress,cancel)=> {
                sent++;last=r;
                await Task.Delay(cancelRun?5000:20,cancel);
                var pixels=Data(PrintRestorationImages.Load(r.SourcePath));var color=r.KeyColor??Colors.Black;
                for(int p=0;p<pixels.Length;p+=4)if(pixels[p]==0 && pixels[p+1]==0 && pixels[p+2]==0){pixels[p]=color.B;pixels[p+1]=color.G;pixels[p+2]=color.R;}
                using(var file=File.Create(r.OutputPath))FastRasterPng.Write(file,pixels,width,height,100,100);
                return r.OutputPath;
            },
            RemoveBackground=(_,source,color)=> {
                var bitmap=PrintRestorationImages.Load(source);var pixels=Data(bitmap);
                for(int p=0;p<pixels.Length;p+=4)if(pixels[p]==color.B && pixels[p+1]==color.G && pixels[p+2]==color.R)pixels[p+3]=0;
                string output=Path.Combine(folder,"wizard-no-background.png");using(var file=File.Create(output))FastRasterPng.Write(file,pixels,bitmap.PixelWidth,bitmap.PixelHeight,bitmap.DpiX,bitmap.DpiY);return output;
            },
            Crop=PrintRestorationImages.Crop,LastResult=()=>last?.OutputPath
        };
        var window=new DtfCorrectionWindow(path,new DtfCorrectionOptions(),new DtfOutlineSettings(),RestoreProcessor,restoration:host,startRestoration:true);
        try
        {
            var load=(Task)Call(window,"LoadAsync");Until(()=>load.IsCompleted,"Wizard load stalled");load.GetAwaiter().GetResult();
            Until(()=>Field<Button>(window,"_apply").IsEnabled,"Wizard preview stalled");
            Check(Field<Border>(window,"_restorationPane").Visibility==Visibility.Visible,"Wizard direct entry did not show right panel");
            var toggle=Field<System.Windows.Controls.Primitives.ToggleButton>(window,"_restoreToggle");toggle.IsChecked=false;
            Check(Field<Border>(window,"_restorationPane").Visibility==Visibility.Collapsed,"Master could not be hidden");toggle.IsChecked=true;
            Check(Field<Border>(window,"_restorationPane").Visibility==Visibility.Visible,"Hidden master could not be reopened");
            RunRestorationStep(window,"RestoreAiAsync");Check(sent==0 && consents==1,"Declined consent still submitted AI request");
            confirm=true;RunRestorationStep(window,"RestoreAiAsync");Check(sent==1 && consents==2 && reviews==1,"AI request must run once and require review");
            Check(!Field<bool>(window,"_restored") && Field<string>(window,"_sourcePath")==path,"Unaccepted AI must not replace working source");
            review=true;RunRestorationStep(window,"ReviewRestorationAsync");Check(sent==1 && Field<bool>(window,"_restored"),"Reviewing ready AI should be free and accept source");
            var editor=Field<RasterMaskEditor>(window,"_maskEditor");var layers=Field<System.Collections.Generic.List<RasterMaskShape>[]>(editor,"_layers");
            layers[0].Add(new RasterMaskShape(RasterMaskTool.Rectangle,new[]{new RasterMaskPoint(10,7),new RasterMaskPoint(11,8)},0));
            RunRestorationStep(window,"TrimRestorationAsync");
            var pixelsAfter=Field<DtfCorrectionPixels>(window,"_originalPixels");Check(pixelsAfter.Width==64 && pixelsAfter.Height==44 && Math.Abs(pixelsAfter.DpiX-100)<.01,"Crop changed scale or content bounds");
            Check(Math.Abs(Field<double>(window,"_restorationOffsetX")+18*25.4/image.DpiX)<1e-8,"Crop lost horizontal insertion offset");
            Check(Math.Abs(layers[0][0].Points[0].X-(10-18*25.4/image.DpiX))<1e-8,"Crop did not translate protection mask");
            RunRestorationStep(window,"ReviewRestorationAsync");
            Check(Field<DtfCorrectionPixels>(window,"_originalPixels").Width==100 && Math.Abs(Field<double>(window,"_restorationOffsetX"))<1e-8,"Reviewing the original AI result after crop kept a stale crop offset");
            Check(Math.Abs(layers[0][0].Points[0].X-10)<1e-8,"Reviewing the original AI result did not restore mask origin");
            RunRestorationStep(window,"TrimRestorationAsync");
            Call(window,"SetupRestorationKnockout");Check(Field<CheckBox>(window,"_knockout").IsChecked==true && Field<bool>(window,"_restoreKnockoutPending"),"Master did not expose live knockout preview");
            Field<CheckBox>(window,"_knockoutSoft").IsChecked=false;
            RunRestorationStep(window,"AcceptRestorationKnockoutAsync");
            Check(Field<bool>(window,"_restoreBackgroundDone") && Field<CheckBox>(window,"_knockout").IsChecked==false,"Accepted knockout was not baked once or remained enabled for a second pass");
            RunRestorationStep(window,"RemoveRestorationBackgroundAsync");Check(Field<bool>(window,"_restoreBackgroundDone"),"Background accepted without step completion");
            RunRestorationStep(window,"AuditRestorationAsync",false);var audit=Field<PrintRestorationAudit>(window,"_restoreAudit");Check(audit.SemiTransparent==1 && audit.NearWhite==2400,"Wizard audited preview instead of full result");
            Field<CheckBox>(window,"_remove").IsChecked=true;Field<CheckBox>(window,"_whiten").IsChecked=true;
            RunRestorationStep(window,"AuditRestorationAsync",true);audit=Field<PrintRestorationAudit>(window,"_restoreAudit");Check(audit.SemiTransparent==0 && audit.NearWhite==0,"Audit did not include chosen corrections");
            Check(Field<bool>(window,"_alphaChecked")&&Field<bool>(window,"_whiteChecked"),"Completed audit flags missing");
            Field<CheckBox>(window,"_whiten").IsChecked=false;
            Until(()=>!Field<bool>(window,"_whiteChecked"),"Changing correction retained stale audit completion");
            cancelRun=true;int previousReviews=reviews;var task=(Task)Call(window,"RestoreAiAsync");Until(()=>Field<bool>(window,"_restorationBusy") && sent==2,"AI request never reached the cancellable worker");
            Call(window,"CancelCorrectionOrClose");Until(()=>task.IsCompleted,"AI cancellation stalled");task.GetAwaiter().GetResult();
            Check(!Field<bool>(window,"_closed") && !Field<bool>(window,"_restorationBusy") && Field<Button>(window,"_cancel").IsEnabled,"Cancel closed window or left controls blocked");
            Check(reviews==previousReviews,"Canceled new AI request unexpectedly reviewed an older result");
            RunRestorationStep(window,"ResetRestorationAsync");Check(Math.Abs(Field<double>(window,"_restorationOffsetX"))<1e-8 && Field<string>(window,"_sourcePath")==path,"Reset did not restore original coordinates and source");
            Check(Math.Abs(layers[0][0].Points[0].X-10)<1e-8,"Reset did not restore mask coordinates");
            Check(File.ReadAllBytes(path).SequenceEqual(original),"Wizard modified captured source file");
        }
        finally {window.Close();Until(()=>!Field<bool>(window,"_restorationBusy"),"Closing retained wizard work");}
    }
}
