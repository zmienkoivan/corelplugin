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
    private static int _checks, _active, _maximum, _calls, _canceled, _normalizations;
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
        try {
            for(int i=0;i<30;i++) { cancel.ThrowIfCancellationRequested(); Thread.Sleep(10); }
            if(options.Resample300) {
                Interlocked.Increment(ref _normalizations);BitmapSource source;
                using(var file=File.OpenRead(input))source=BitmapFrame.Create(file,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var pixels=DtfCorrectionPixels.Capture(source).Resample300(cancel);using(var file=File.Create(output))pixels.WritePng(file,cancel);
            } else File.Copy(input,output,true);
            return new DtfCorrectionResult { OutputPath=output };
        }
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
            Field<CheckBox>(window,"_thinEnabled").IsChecked=false;
            var beforeKnockout=after.Source;
            Field<CheckBox>(window,"_knockout").IsChecked=true;
            Field<TextBox>(window,"_knockoutHex").Text="#64788C"; Field<Slider>(window,"_knockoutTolerance").Value=0;
            Until(()=>!ReferenceEquals(after.Source,beforeKnockout) && after.Opacity==1,"Knockout preview did not update");
            Check(Data((BitmapSource)after.Source).Where((b,i)=>i%4==3).All(a=>a==0),"Knockout did not remove selected print colour");
            Check(Field<TextBox>(window,"_knockoutHex").IsEnabled,"Knockout controls unavailable");
            Field<ComboBox>(window,"_previewBackground").SelectedIndex=3;
            Check(((SolidColorBrush)Field<ScrollViewer>(window,"_beforeScroll").Background).Color==Color.FromRgb(100,120,140),"Fabric preview background did not use knockout colour");
            Field<TextBox>(window,"_knockoutHex").Text="#223344";
            Check(((SolidColorBrush)Field<ScrollViewer>(window,"_afterScroll").Background).Color==Color.FromRgb(34,51,68),"Fabric preview background did not follow colour selection");
            Field<ComboBox>(window,"_previewBackground").SelectedIndex=0;
            Check(Field<DtfCorrectionPixels>(window,"_originalPixels").Sample(159,20,out Color sample) && sample==Color.FromRgb(100,120,140),"Source pipette sampled a diagnostic colour");
            Field<CheckBox>(window,"_knockout").IsChecked=false;
            Field<ComboBox>(window,"_screenMode").SelectedIndex=1;
            Field<CheckBox>(window,"_halftone").IsChecked=true;
            Until(()=>apply.IsEnabled,"Halftone preview did not update");
            int screened=Data((BitmapSource)after.Source).Where((b,i)=>i%4==3).Count(a=>a!=0);
            Check(screened>0 && screened<520,"Full-image grid did not affect dense print");
            var oldPreview=after.Source;
            Field<Slider>(window,"_screenLpiSlider").Value=30;
            Field<Slider>(window,"_screenAngleSlider").Value=-30;
            Field<Slider>(window,"_screenDotSlider").Value=.5;
            Field<Slider>(window,"_screenBoostSlider").Value=50;
            Field<ComboBox>(window,"_screenShape").SelectedIndex=(int)DtfDotShape.Star;
            Check(Field<TextBox>(window,"_screenLpi").Text=="30" && Field<TextBox>(window,"_screenAngle").Text=="-30","Frequency/angle sliders do not update numeric fields");
            Check(!Field<bool>(window,"_automaticDot") && Field<TextBox>(window,"_screenBoost").Text=="50","Dot/boost slider state missing");
            Until(()=>apply.IsEnabled && !ReferenceEquals(after.Source,oldPreview),"Slider or shape change did not automatically refresh preview");
            Field<TextBox>(window,"_screenAngle").Text="-22,5";
            Check(Field<Slider>(window,"_screenAngleSlider").Value==-22.5,"Numeric angle does not update slider");
            Field<ComboBox>(window,"_screenMode").SelectedIndex=0;
            Check(!Field<Slider>(window,"_screenBoostSlider").IsEnabled,"Colour boost remains enabled for alpha-only mode");
            Field<ComboBox>(window,"_screenMode").SelectedIndex=1;
            Check(Field<Slider>(window,"_screenBoostSlider").IsEnabled,"Colour boost does not resume for full-image mode");
            Field<TextBox>(window,"_screenAngle").Text="-22,5"; Field<TextBox>(window,"_screenDot").Text="0,4";
            object[] prepArgs={null}; Check((bool)Call(window,"ReadOptions",prepArgs),"Valid negative angle/manual dot rejected");
            var prepOptions=(DtfCorrectionOptions)prepArgs[0];
            Check(prepOptions.HalftoneAngle==-22.5 && prepOptions.HalftoneDotMm==.4 && !prepOptions.HalftoneAutoDot,"Preparation settings lost");
            Check(prepOptions.KnockoutSoft && prepOptions.HalftoneShape==DtfDotShape.Star && prepOptions.HalftoneColorBoost==50,"Soft knockout, shape or boost was not sent to processing");
            Field<Button>(window,"_screenAuto").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(Field<bool>(window,"_automaticDot"),"Automatic dot size cannot be restored");
            Field<TextBox>(window,"_screenLpi").Text="0"; prepArgs=new object[]{null};
            Check(!(bool)Call(window,"ReadOptions",prepArgs),"Invalid LPI accepted");
            Field<TextBox>(window,"_screenLpi").Text="20";
            Field<CheckBox>(window,"_halftone").IsChecked=false; Field<CheckBox>(window,"_thinEnabled").IsChecked=true;
            Until(()=>apply.IsEnabled,"Original audit did not resume after preparation toggles");
            PreparationDetail(window,root);
            PresetUi(window);
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
            ResolutionChecks(root);
            Console.WriteLine("Correction window checks passed: "+_checks+". Owned hidden WPF viewport, synthetic PNG, fake processor; no Corel."); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if(window!=null && !Field<bool>(window,"_closed")) window.Close(); }
    }

    private static void ResolutionChecks(string root)
    {
        var level=typeof(DtfCorrectionWindow).GetMethod("ResolutionLevel",BindingFlags.Static|BindingFlags.NonPublic);
        Check((int)level.Invoke(null,new object[]{139.9})==0 && (int)level.Invoke(null,new object[]{140d})==1,"DPI red/yellow boundary incorrect");
        Check((int)level.Invoke(null,new object[]{199.9})==1 && (int)level.Invoke(null,new object[]{200d})==2,"DPI yellow/green boundary incorrect");
        var matches=typeof(DtfCorrectionWindow).GetMethod("ResolutionSizeMatches",BindingFlags.Static|BindingFlags.NonPublic);
        Check((bool)matches.Invoke(null,new object[]{286,142.9896,600,300d}),"PNG DPI rounding rejected valid native normalization");
        Check(!(bool)matches.Invoke(null,new object[]{286,143d,600,290d}) && !(bool)matches.Invoke(null,new object[]{286,143d,800,300d}),"Incorrect normalized DPI or physical size accepted");
        string path=Path.Combine(root,"low-dpi.png");var data=new byte[32*16*4];
        for(int p=0;p<data.Length;p+=4){data[p]=data[p+1]=data[p+2]=255;data[p+3]=p%20==0?(byte)128:(byte)255;}
        using(var file=File.Create(path))FastRasterPng.Write(file,data,32,16,100,100);var original=File.ReadAllBytes(path);
        var window=new DtfCorrectionWindow(path,new DtfCorrectionOptions(),new DtfOutlineSettings(),Process);
        try {
            var loaded=(Task)Call(window,"LoadAsync");Until(()=>loaded.IsCompleted,"Resolution test source did not load");loaded.GetAwaiter().GetResult();
            Until(()=>Field<Button>(window,"_apply").IsEnabled,"Resolution test initial preview stalled");
            Check(Field<Button>(window,"_normalizeResolution").IsEnabled && Field<TextBlock>(window,"_resolutionInfo").Text.Contains("100"),"Low DPI was not assessed or normalization unavailable");
            int calls=_normalizations;
            var prepared=(Task)Call(window,"NormalizeResolutionAsync");var duplicate=(Task)Call(window,"NormalizeResolutionAsync");
            Until(()=>prepared.IsCompleted && duplicate.IsCompleted,"Normalization did not complete");prepared.GetAwaiter().GetResult();
            Until(()=>Field<Button>(window,"_apply").IsEnabled,"Normalization did not restart automatic preview");
            var full=Field<DtfCorrectionPixels>(window,"_originalPixels");
            Check(full.Width==96 && full.Height==48 && full.DpiX>299.99,"Normalization did not produce 300-DPI working pixels");
            Check(Field<bool>(window,"_resolutionNormalized") && _normalizations==calls+1,"Normalization repeated on concurrent click");
            Check(Data(full.Bitmap()).Where((b,i)=>i%4!=3).All(v=>v==255),"Normalization changed source white");
            object[] options={null};Check((bool)Call(window,"ReadOptions",options) && !((DtfCorrectionOptions)options[0]).Resample300,"Every preview would repeat full-image normalization");
            var reset=(Task)Call(window,"NormalizeResolutionAsync");Until(()=>reset.IsCompleted,"Resolution reset stalled");reset.GetAwaiter().GetResult();
            Until(()=>Field<Button>(window,"_apply").IsEnabled,"Reset preview stalled");
            Check(Field<DtfCorrectionPixels>(window,"_originalPixels").Width==32 && Field<string>(window,"_workingSourcePath")==path,"Original source was not restored");
            var cached=(Task)Call(window,"NormalizeResolutionAsync");Until(()=>cached.IsCompleted,"Cached normalization stalled");cached.GetAwaiter().GetResult();
            Until(()=>Field<Button>(window,"_apply").IsEnabled,"Cached preview stalled");
            Check(_normalizations==calls+1 && Field<DtfCorrectionPixels>(window,"_originalPixels").Width==96,"Cached normalization was recalculated");
            Check(original.SequenceEqual(File.ReadAllBytes(path)),"DPI normalization changed original source file");
        } finally {window.Close();Until(()=>!Field<bool>(window,"_rendering") && !Field<bool>(window,"_normalizing"),"Resolution test close left work running");}
        var failed=new DtfCorrectionWindow(path,new DtfCorrectionOptions(),new DtfOutlineSettings(),(input,output,options,cancel)=> {
            if(options.Resample300)throw new IOException("Controlled resample failure");return Process(input,output,options,cancel);
        });
        try {
            var loaded=(Task)Call(failed,"LoadAsync");Until(()=>loaded.IsCompleted && Field<Button>(failed,"_apply").IsEnabled,"Failure-case source did not load");
            var task=(Task)Call(failed,"NormalizeResolutionAsync");Until(()=>task.IsCompleted,"Failed normalization did not recover");task.GetAwaiter().GetResult();
            var clock=Stopwatch.StartNew();while(clock.ElapsedMilliseconds<450)Pump();
            Check(!Field<bool>(failed,"_normalizing") && !Field<bool>(failed,"_resolutionNormalized") && Field<DtfCorrectionPixels>(failed,"_originalPixels").Width==32,"Failed normalization changed source or left UI busy");
            Check(Field<TextBlock>(failed,"_message").Text.Contains("Controlled resample failure") && Field<Button>(failed,"_normalizeResolution").IsEnabled,"Resample error disappeared or retry unavailable");
        } finally {failed.Close();}
        var canceled=new DtfCorrectionWindow(path,new DtfCorrectionOptions(),new DtfOutlineSettings(),Process);
        var ready=(Task)Call(canceled,"LoadAsync");Until(()=>ready.IsCompleted && Field<Button>(canceled,"_apply").IsEnabled,"Cancellation-case source did not load");
        var pending=(Task)Call(canceled,"NormalizeResolutionAsync");Until(()=>_active>0,"Cancellation-case normalization did not start");
        string workspace=Field<string>(canceled,"_workspace");canceled.Close();Until(()=>pending.IsCompleted && !Field<bool>(canceled,"_normalizing"),"Closing left normalization worker running");
        Check(!Directory.Exists(workspace) && original.SequenceEqual(File.ReadAllBytes(path)),"Canceled normalization left temporary data or changed original");

        int automaticCalls = _normalizations;
        var automatic = new DtfCorrectionWindow(path, new DtfCorrectionOptions(), new DtfOutlineSettings(),
            Process, normalizeOnLoad: true);
        try
        {
            var load = (Task)Call(automatic, "LoadAsync");
            Until(() => _active > 0, "Checked normalization did not start on load");
            Check(!Field<Button>(automatic, "_apply").IsEnabled, "Apply enabled before startup normalization");
            Until(() => load.IsCompleted && Field<Button>(automatic, "_apply").IsEnabled,
                "Startup normalization did not enable the corrected preview");
            load.GetAwaiter().GetResult();
            Check(Field<bool>(automatic, "_resolutionNormalized") && _normalizations == automaticCalls + 1 &&
                Field<DtfCorrectionPixels>(automatic, "_originalPixels").Width == 96,
                "Startup normalization did not select one 300-DPI working copy");
            Check(original.SequenceEqual(File.ReadAllBytes(path)), "Startup normalization changed the source file");
        }
        finally { automatic.Close(); }

        string printDpiPath = Path.Combine(root, "print-dpi.png");
        using (var file = File.Create(printDpiPath)) FastRasterPng.Write(file, data, 32, 16, 300, 400);
        var printDpi = new DtfCorrectionWindow(printDpiPath, new DtfCorrectionOptions(), new DtfOutlineSettings(),
            Process, normalizeOnLoad: true);
        try
        {
            var load = (Task)Call(printDpi, "LoadAsync");
            Until(() => load.IsCompleted && Field<Button>(printDpi, "_apply").IsEnabled, "Print-DPI preview stalled");
            load.GetAwaiter().GetResult();
            Check(!Field<bool>(printDpi, "_resolutionNormalized") && _normalizations == automaticCalls + 1 &&
                Field<string>(printDpi, "_workingSourcePath") == printDpiPath,
                "Startup option resampled an image already at print DPI");
        }
        finally { printDpi.Close(); }
    }

    private static void PreparationDetail(DtfCorrectionWindow window,string root)
    {
        var pixels=Field<DtfCorrectionPixels>(window,"_originalPixels");
        var plan=CorrectionViewportPlan.Create(pixels.Width,pixels.Height,new Rect(0,0,pixels.Width,pixels.Height),
            new Rect(100,10,150,120),1,1,8,8);
        var options=new DtfCorrectionOptions { Halftone=true,HalftoneAlphaOnly=false,HalftoneAngle=-31.5 };
        var method=typeof(DtfCorrectionWindow).GetMethod("RenderDetail",BindingFlags.Static|BindingFlags.NonPublic);
        var frame=method.Invoke(null,new object[]{pixels,plan,options,null,false,false,false,Colors.Magenta,root,
            (Func<string,string,DtfCorrectionOptions,CancellationToken,DtfCorrectionResult>)Process,CancellationToken.None});
        var exact=Field<DtfCorrectionPixels>(frame,"After");
        var expected=pixels.Correct(options,CancellationToken.None).Crop(plan.Target,CancellationToken.None);
        Check(Data(exact.Bitmap()).SequenceEqual(Data(expected.Bitmap())),"Exact detail grid phase differs from final full-image phase");
        Check(options.GridOriginXmm==0 && options.GridOriginYmm==0,"Detail rendering changed global settings");
    }
    private static void PresetUi(DtfCorrectionWindow window)
    {
        var original=(DtfCorrectionPreset)Call(window,"CapturePreset");
        var preset=DtfCorrectionPreset.FromOptions(original.ToOptions());
        preset.Settings.Knockout=true;preset.Settings.KnockoutColor="#00AAFF";preset.Settings.KnockoutTolerance=12;
        preset.Settings.Halftone=true;preset.Settings.HalftoneAlphaOnly=true;preset.Settings.HalftoneAutoDot=false;
        preset.Settings.HalftoneLpi=28;preset.Settings.HalftoneAngle=-30;preset.Settings.HalftoneDotMm=.75;
        preset.Settings.KnockoutSoft=false;preset.Settings.HalftoneShape=(int)DtfDotShape.Hexagon;preset.Settings.HalftoneColorBoost=65;
        Field<ComboBox>(window,"_units").SelectedIndex=1;
        Call(window,"ApplyPreset",preset);
        Check(Field<ComboBox>(window,"_units").SelectedIndex==0 && Field<int>(window,"_previousUnit")==0,"Preset retained source pixel units");
        Check(Field<CheckBox>(window,"_knockout").IsChecked==true && Field<TextBox>(window,"_knockoutHex").Text=="#00AAFF" && Field<Slider>(window,"_knockoutTolerance").Value==12,"Preset did not restore knockout controls");
        Check(Field<CheckBox>(window,"_halftone").IsChecked==true && Field<ComboBox>(window,"_screenMode").SelectedIndex==0 && !Field<bool>(window,"_automaticDot"),"Preset did not restore halftone mode");
        var captured=((DtfCorrectionPreset)Call(window,"CapturePreset")).ToOptions();
        Check(captured.HalftoneLpi==28 && captured.HalftoneAngle==-30 && captured.HalftoneDotMm==.75,"Preset loading overwrote manual point size");
        Check(!captured.KnockoutSoft && captured.HalftoneShape==DtfDotShape.Hexagon && captured.HalftoneColorBoost==65,"Preset did not restore new preparation settings");
        Check(Field<Slider>(window,"_screenLpiSlider").Value==28 && Field<Slider>(window,"_screenBoostSlider").Value==65,"Preset did not synchronize sliders");
        Check(captured.RadiusMm==original.Settings.RadiusMm && captured.ExpansionMm==original.Settings.ExpansionMm,"Preset physical outline dimensions changed");
        Until(()=>Field<Button>(window,"_apply").IsEnabled,"Preset did not automatically update preview");
        var bad=DtfCorrectionPreset.FromOptions(captured);bad.Settings.HalftoneLpi=0;string before=Field<TextBox>(window,"_screenLpi").Text;
        bool rejected=false;try{Call(window,"ApplyPreset",bad);}catch(TargetInvocationException error) when(error.InnerException is InvalidDataException){rejected=true;}
        Check(rejected && Field<TextBox>(window,"_screenLpi").Text==before,"Rejected preset partially changed controls");
        var tooLarge=DtfCorrectionPreset.FromOptions(captured);tooLarge.Settings.Outline=true;tooLarge.Settings.ExpansionMm=10;
        rejected=false;try{Call(window,"ApplyPreset",tooLarge);}catch(TargetInvocationException error) when(error.InnerException is InvalidOperationException){rejected=true;}
        Check(rejected && Field<TextBox>(window,"_screenLpi").Text==before && Field<CheckBox>(window,"_outline").IsChecked==false,"Preset exceeding source DPI limits partially changed controls");
        Check(Field<Button>(window,"_savePreset").IsEnabled && Field<Button>(window,"_loadPreset").IsEnabled,"Preset buttons unavailable");
        var cleanup=DtfCorrectionPreset.FromOptions(captured);cleanup.Settings.KnockoutSoft=true;cleanup.Settings.KnockoutTolerance=-90;
        cleanup.Settings.HalftoneRemoveSmall=true;cleanup.Settings.HalftoneRemoveMm=.25;cleanup.Settings.HalftoneGrowSmall=true;cleanup.Settings.HalftoneGrowMm=.55;
        Call(window,"ApplyPreset",cleanup);Until(()=>Field<Button>(window,"_apply").IsEnabled,"Cleanup preset did not update");
        var cleanOptions=((DtfCorrectionPreset)Call(window,"CapturePreset")).ToOptions();
        Check(cleanOptions.KnockoutTolerance==-90 && cleanOptions.HalftoneRemoveSmall && cleanOptions.HalftoneGrowSmall && cleanOptions.HalftoneGrowMm==.55,"New processing settings were not restored");
        Check(Field<Slider>(window,"_knockoutTolerance").Minimum==-255 && Field<Slider>(window,"_knockoutTolerance").Value==-90,"Negative knockout tolerance was clamped during preset load");
        Check(Field<Expander>(window,"_thinSection").IsExpanded && Field<TextBox>(window,"_hex").IsEnabled && !Field<TextBox>(window,"_radius").IsEnabled,"Unified cleanup section, grow colour or hard outline availability incorrect");
        Field<Slider>(window,"_removeSmallSlider").Value=.2;Field<Slider>(window,"_growSmallSlider").Value=.65;
        var changed=((DtfCorrectionPreset)Call(window,"CapturePreset")).ToOptions();Check(changed.HalftoneRemoveMm==.2 && changed.HalftoneGrowMm==.65,"Cleanup sliders do not update physical thresholds");
        Field<CheckBox>(window,"_knockoutSoft").IsChecked=false;
        Check(Field<Slider>(window,"_knockoutTolerance").Minimum==0 && Field<Slider>(window,"_knockoutTolerance").Value>=0,"Hard knockout retained negative tolerance");
        Call(window,"ApplyPreset",original);Until(()=>Field<Button>(window,"_apply").IsEnabled,"Original settings did not resume after preset test");
    }
}
