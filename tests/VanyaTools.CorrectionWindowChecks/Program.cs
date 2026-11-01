using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VanyaTools.Native;

namespace VanyaTools.Native
{
    internal static class Log { internal static void Info(string value) { } internal static void Error(string text, Exception error) { Console.Error.WriteLine(text + " " + error.Message); } }
    internal static class DockerTheme
    {
        internal const string WindowBackground="window", ErrorTextBrush="error", TextBrush="text", MutedTextBrush="muted", BorderBrush="border";
        internal static void Apply(Window window) { } internal static void UseTheme(DependencyObject target, DependencyProperty property, object key) { }
        internal static Button Primary(Button button) => button;
    }
    internal static class DockerHelp { internal static void Add(Panel panel, string text) { } }
    internal static class DockerLayout { internal static FrameworkElement FieldRow(string title, FrameworkElement field, double width) => field; }
    internal static class WindowsColorPicker { internal static Color? Pick(IntPtr owner, Color color) => null; }
    internal static class ScreenColorPicker { internal static Task<Color?> PickAsync() => Task.FromResult<Color?>(null); }
    internal static class ThinElementReference { internal static void Insert(bool uv, ThinAuditOptions options) => throw new Exception("Live Corel forbidden"); }
}
internal static class Program
{
    private static int _checks, _active, _maximum, _calls, _canceled;
    private static void Check(bool value, string message) { _checks++; if (!value) throw new Exception(message); }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(value);
    private static object Call(object value, string method, params object[] args) => value.GetType().GetMethod(method,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(value,args);
    private static void Pump()
    {
        var frame=new DispatcherFrame(); Dispatcher.CurrentDispatcher.BeginInvoke(new Action(()=>frame.Continue=false),DispatcherPriority.Background);
        Dispatcher.PushFrame(frame); Thread.Sleep(2);
    }
    private static void Until(Func<bool> ready, string message)
    { var clock=Stopwatch.StartNew(); while(!ready() && clock.ElapsedMilliseconds<15000) Pump(); Check(ready(),message); }
    private static byte[] Data(BitmapSource source)
    { var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0); var data=new byte[source.PixelWidth*source.PixelHeight*4]; converted.CopyPixels(data,source.PixelWidth*4,0); return data; }
    private static DtfCorrectionResult Process(string input,string output,DtfCorrectionOptions options,CancellationToken cancel)
    {
        Interlocked.Increment(ref _calls); int current=Interlocked.Increment(ref _active); _maximum=Math.Max(_maximum,current);
        try { for(int i=0;i<30;i++) { cancel.ThrowIfCancellationRequested(); Thread.Sleep(10); } File.Copy(input,output,true); return new DtfCorrectionResult { OutputPath=output }; }
        catch(OperationCanceledException) { Interlocked.Increment(ref _canceled); throw; }
        finally { Interlocked.Decrement(ref _active); }
    }
    private static void ViewportChecks(string root)
    {
        DetailPixelChecks(root);
        var scene=new Rect(0,0,6000,1000);
        var plan=CorrectionViewportPlan.Create(6000,1000,scene,new Rect(5500,0,500,700),1,1,40,40);
        Check(plan!=null && plan.Input.X>0 && plan.Input.X+plan.Input.Width==6000,"ROI source clipping incorrect");
        Check(plan.CutEdges==9,"ROI excludes real edge or retains synthetic edge");
        Check(CorrectionViewportPlan.WorldRect(plan.Target).Contains(new Rect(5500,0,500,700)),"ROI misses visible pixels");
        Check(CorrectionViewportPlan.Create(6000,1000,scene,scene,.1,1,8,8)==null,"Huge visible ROI bypasses budget");
        Check(CorrectionViewportPlan.Create(6000,1000,scene,new Rect(0,0,700,500),1,4,8,8)==null,"Supersampling omitted from budget");
        var outside=CorrectionViewportPlan.Create(6000,1000,new Rect(-20,-20,6040,1040),new Rect(-20,-20,100,100),1,1,30,30);
        Check(outside.Target.X==-20 && outside.Work.X<0 && outside.Input.X==0 && (outside.CutEdges&5)==0,"Outside expansion shifts ROI or ignores true artwork edge");
        const int w=6000,h=1000; var pixels=new byte[w*h*4];
        for(int y=0;y<h;y++)for(int x=0;x<w;x++) { int p=(y*w+x)*4; pixels[p]=(byte)(x%251); pixels[p+1]=(byte)(y%251); pixels[p+2]=(byte)((x+y)%251); pixels[p+3]=255; }
        string path=Path.Combine(root,"large.png");using(var stream=File.Create(path))FastRasterPng.Write(stream,pixels,w,h,400,400);
        var window=new DtfCorrectionWindow(path,new DtfCorrectionOptions(),new DtfOutlineSettings(),Process);
        using(var surface=new HwndSource(new HwndSourceParameters("Vanya automatic detail checks") {Width=1220,Height=820,WindowStyle=0}))
        {
            try
            {
                var content=(FrameworkElement)window.Content;window.Content=null;surface.RootVisual=content;
                var load=(Task)Call(window,"LoadAsync");Until(()=>load.IsCompleted,"Large load stalled");load.GetAwaiter().GetResult();
                var apply=Field<Button>(window,"_apply");Until(()=>apply.IsEnabled,"Large overview stalled");
                var scroll=Field<ScrollViewer>(window,"_beforeScroll"); var before=Field<Image>(window,"_before");
                Until(()=>scroll.ViewportWidth>100,"Owned viewport has no layout");
                Call(window,"ZoomTo",1d,scroll,null);
                Until(()=>Field<Image>(window,"_beforeDetail").Source!=null,"Zoom did not refine automatically without 1:1 mode");
                Check(Field<ComboBox>(window,"_view").SelectedIndex==0,"Detail changed viewing mode");
                Check(Math.Abs(before.Width*VisualTreeHelper.GetDpi(before).DpiScaleX-w)<.01,"100% still measures overview pixels");
                var bounds=Field<Int32Rect>(window,"_tileBounds"); var detail=(BitmapSource)Field<Image>(window,"_beforeDetail").Source;
                Check(detail.PixelWidth==bounds.Width&&detail.PixelHeight==bounds.Height,"ROI was resampled instead of source pixels");
                var expected=Field<DtfCorrectionPixels>(window,"_originalPixels").Crop(bounds,CancellationToken.None).Bitmap();
                Check(Data(detail).SequenceEqual(Data(expected)),"Detail pixels do not match original ROI");
                Check(before.Clip!=null,"Transparent detail can reveal blurred overview underneath");
                double width=before.Width; scroll.ScrollToHorizontalOffset(4200);
                Until(()=>Field<Int32Rect>(window,"_tileBounds").X>3900&&!Field<bool>(window,"_detailRendering"),"Pan did not refine new region");
                Check(before.Width==width&&Math.Abs(scroll.HorizontalOffset-Math.Min(4200,scroll.ScrollableWidth))<1&&apply.IsEnabled,"Refinement jumped canvas or disabled Apply");
                bounds=Field<Int32Rect>(window,"_tileBounds");
                Check(Data((BitmapSource)Field<Image>(window,"_beforeDetail").Source).SequenceEqual(Data(Field<DtfCorrectionPixels>(window,"_originalPixels").Crop(bounds,CancellationToken.None).Bitmap())),"Panned tile uses stale source coordinates");
                var image=Field<Image>(window,"_beforeDetail").Source;Call(window,"QueueDetail");
                var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<450)Pump();
                Check(ReferenceEquals(image,Field<Image>(window,"_beforeDetail").Source),"Cached visible area was recomputed");
                Field<CheckBox>(window,"_highlightWhite").IsChecked=true;
                Until(()=>apply.IsEnabled&&Field<Image>(window,"_afterDetail").Source!=null,"Settings did not refresh exact tile");
                Field<ComboBox>(window,"_view").SelectedIndex=1;
                Check(Field<Image>(window,"_beforeDetail").Source==null&&before.Clip==null,"Fast mode retained exact tile clipping");
                Field<ComboBox>(window,"_view").SelectedIndex=0;
                Until(()=>Field<Image>(window,"_beforeDetail").Source!=null,"Auto mode did not resume");
                var quality=Field<ComboBox>(window,"_previewQuality");quality.SelectedIndex=0;quality.SelectedIndex=2;quality.SelectedIndex=1;
                Until(()=>apply.IsEnabled&&Field<int>(window,"_qualityActive")==0&&Field<Image>(window,"_beforeDetail").Source!=null,"Rapid quality changes did not recover cached overview and exact area");
                Check(before.Width==width&&Field<double>(window,"_zoomScale")==1,"Overview quality changed source zoom");
                int canceled=_canceled;Field<CheckBox>(window,"_outline").IsChecked=true;
                Until(()=>apply.IsEnabled,"Outline overview stalled");
                Until(()=>Field<bool>(window,"_detailRendering")&&_active>0,"Outline detail did not start");
                scroll.ScrollToHorizontalOffset(2000);
                Until(()=>!Field<bool>(window,"_detailRendering")&&Field<Int32Rect>(window,"_tileBounds").X<2000*VisualTreeHelper.GetDpi(before).DpiScaleX+10,"Canceled tile did not recover after pan");
                Check(_maximum==1&&_canceled>canceled,"Detail and overview overlap or pan did not cancel stale detail");
                scroll.ScrollToHorizontalOffset(3000);Until(()=>Field<bool>(window,"_detailRendering"),"Close detail did not start");
                window.Close();Until(()=>!Field<bool>(window,"_detailRendering"),"Closing left detail worker active");
                Check(!Field<DispatcherTimer>(window,"_detailDelay").IsEnabled,"Closed detail timer restarted");
            }
            finally { if(!Field<bool>(window,"_closed"))window.Close();surface.RootVisual=null; }
        }
    }
    private static void DetailPixelChecks(string root)
    {
        const int w=32,h=24;var data=new byte[w*h*4];
        for(int p=0;p<data.Length;p+=4){data[p]=17;data[p+1]=33;data[p+2]=55;data[p+3]=255;}
        var source=DtfCorrectionPixels.Capture(BitmapSource.Create(w,h,400,400,PixelFormats.Bgra32,null,data,w*4));
        var plan=CorrectionViewportPlan.Create(w,h,new Rect(-3,-3,w+6,h+6),new Rect(-3,-3,w+6,h+6),1,2,8,8);
        var method=typeof(DtfCorrectionWindow).GetMethod("RenderDetail",BindingFlags.Static|BindingFlags.NonPublic);
        Func<string,string,DtfCorrectionOptions,CancellationToken,DtfCorrectionResult> expanded=(input,output,options,cancel)=>
        {
            int width=(w+6)*2,height=(h+6)*2;var pixels=new byte[width*height*4];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++) {int p=(y*width+x)*4;pixels[p+3]=255;
                if(x>=6&&x<6+w*2&&y>=6&&y<6+h*2){pixels[p]=17;pixels[p+1]=33;pixels[p+2]=55;}else pixels[p]=255;}
            using(var stream=File.Create(output))FastRasterPng.Write(stream,pixels,width,height,800,800);
            return new DtfCorrectionResult{OutputPath=output,OffsetMm=Tuple.Create(3*25.4/400,3*25.4/400)};
        };
        var frame=method.Invoke(null,new object[]{source,plan,new DtfCorrectionOptions{Outline=true,SmoothStairs=true,StairsScale=2},null,false,false,false,Colors.Magenta,root,expanded,CancellationToken.None});
        var before=Field<DtfCorrectionPixels>(frame,"Before");var after=Field<DtfCorrectionPixels>(frame,"After");var bp=Data(before.Bitmap());var ap=Data(after.Bitmap());
        Check(after.Width==76&&after.Height==60&&Math.Abs(after.DpiX-800)<.013,"Expanded supersampled tile changed physical size");
        Check(bp[3]==0&&bp[(6*76+6)*4+3]==255&&bp[(6*76+6)*4]==17,"Expanded canvas shifted original pixels");
        Check(ap[0]==255&&ap[3]==255&&ap[(6*76+6)*4]==17,"Outline offset did not align with source");
        var empty=DtfCorrectionPixels.Capture(BitmapSource.Create(w,h,400,400,PixelFormats.Bgra32,null,new byte[w*h*4],w*4));
        var plain=CorrectionViewportPlan.Create(w,h,new Rect(0,0,w,h),new Rect(0,0,w,h),1,1,8,8);
        Func<string,string,DtfCorrectionOptions,CancellationToken,DtfCorrectionResult> forbidden=(_,__,___,____)=>throw new Exception("Empty ROI called processor");
        var blank=method.Invoke(null,new object[]{empty,plain,new DtfCorrectionOptions{Outline=true},null,false,false,false,Colors.Magenta,root,forbidden,CancellationToken.None});
        Check(!Field<DtfCorrectionPixels>(blank,"After").HasVisiblePixels(CancellationToken.None),"Empty ROI failed or gained pixels");
        Check(!Directory.GetFiles(root,"detail-*.png").Any(),"Detail processor left temporary images");
    }
    [STAThread] private static int Main()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root=Path.Combine(Path.GetTempPath(),"Vanya-CorrectionChecks-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string path=Path.Combine(root,"source.png"); var pixels=new byte[320*160*4];
        for(int y=15;y<145;y++) for(int x=158;x<=161;x++) { int p=(y*320+x)*4; pixels[p]=140;pixels[p+1]=120;pixels[p+2]=100;pixels[p+3]=255; }
        using(var stream=File.Create(path)) FastRasterPng.Write(stream,pixels,320,160,400,400);
        byte[] originalFile=File.ReadAllBytes(path); DtfCorrectionWindow window=null;
        try
        {
            window=new DtfCorrectionWindow(path,new DtfCorrectionOptions(),new DtfOutlineSettings(),Process,true,(_,__,___)=>throw new Exception("COM callback forbidden"));
            Check(Field<CheckBox>(window,"_thinEnabled").IsChecked==true,"Quick entry did not enable audit in shared window");
            var load=(Task)Call(window,"LoadAsync"); Until(()=>load.IsCompleted,"Source load stalled"); load.GetAwaiter().GetResult();
            var apply=Field<Button>(window,"_apply"); var after=Field<Image>(window,"_after");
            Until(()=>apply.IsEnabled && after.Source!=null,"Initial automatic audit did not finish");
            Check(Field<TextBlock>(window,"_thinNote").Text.Contains("пиксел"),"Audit status not displayed");
            Check(!Data((BitmapSource)after.Source).SequenceEqual(pixels),"Thin diagnostics missing from preview");
            Check(Data(Field<DtfCorrectionPixels>(window,"_originalPixels").Bitmap()).SequenceEqual(pixels),"Diagnostics modified clean source");
            var previous=after.Source; var minimum=Field<Slider>(window,"_thinMinimum"); minimum.Value=.5; minimum.Value=.55; minimum.Value=.6;
            Check(!apply.IsEnabled && Field<DispatcherTimer>(window,"_delay").IsEnabled,"Slider did not schedule automatic recompute");
            Check(Field<DispatcherTimer>(window,"_delay").Interval.TotalMilliseconds==300,"Audit drag has no debounce");
            Until(()=>apply.IsEnabled && !ReferenceEquals(after.Source,previous),"Slider requires a manual check");
            Field<ComboBox>(window,"_thinPreset").SelectedIndex=1;
            Check(minimum.Value==.4 && Field<Slider>(window,"_thinSafe").Value==.6 && Field<Slider>(window,"_thinDot").Value==.8,"UV preset values wrong");
            minimum.Value=1.2;
            Check(Field<Slider>(window,"_thinSafe").Value>=1.2 && Field<Slider>(window,"_thinDot").Value>=1.2,"Dragging minimum left invalid dependent thresholds");
            Field<ComboBox>(window,"_thinPreset").SelectedIndex=0;
            Until(()=>apply.IsEnabled,"Latest preset did not render");
            var beforeScroll=Field<ScrollViewer>(window,"_beforeScroll");
            Call(window,"ZoomTo",2d,beforeScroll,(Point?)new Point(20,15));
            Check(Field<double>(window,"_zoomScale")==2 && Field<Image>(window,"_before").Width==after.Width,"Two previews do not share zoom");
            Check(beforeScroll.HorizontalScrollBarVisibility==ScrollBarVisibility.Auto && beforeScroll.VerticalScrollBarVisibility==ScrollBarVisibility.Auto,"Zoom did not enable both scrollbars");
            var low=DtfCorrectionPixels.Capture(BitmapSource.Create(320,160,50,50,PixelFormats.Bgra32,null,pixels,320*4));
            object[] lowArgs={low,new ThinAuditOptions(),false,CancellationToken.None,null};
            var audit=typeof(DtfCorrectionWindow).GetMethod("AnalyzeThinPreview",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,lowArgs);
            Check(audit==null && ((string)lowArgs[4]).Contains("1:1"),"Low-resolution overview silently measured tiny lines");
            Field<CheckBox>(window,"_outline").IsChecked=true;
            Until(()=>Volatile.Read(ref _active)>0,"Outline preview did not start");
            minimum.Value=.5; minimum.Value=.55; minimum.Value=.6;
            Until(()=>apply.IsEnabled && Volatile.Read(ref _active)==0,"Superseded render did not recover");
            Check(_maximum==1 && _canceled>0,"Stale renders overlap or are not canceled");
            Check(originalFile.SequenceEqual(File.ReadAllBytes(path)),"Automatic audit changed source file");
            Field<TextBox>(window,"_expansion").Text="0.2";
            Until(()=>Volatile.Read(ref _active)>0,"Close-cancellation preview did not start");
            window.Close(); Until(()=>!Field<bool>(window,"_rendering"),"Closed window kept rendering");
            Check(!Field<DispatcherTimer>(window,"_delay").IsEnabled && Field<bool>(window,"_closed"),"Close restarted preview timer");
            ViewportChecks(root);
            Console.WriteLine("Correction window checks passed: "+_checks+". Owned hidden WPF viewport, synthetic PNG, fake processor; no Corel."); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if(window!=null && !Field<bool>(window,"_closed")) window.Close(); }
    }
}
