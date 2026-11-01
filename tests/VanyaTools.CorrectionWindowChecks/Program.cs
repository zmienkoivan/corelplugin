using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
            Console.WriteLine("Correction window checks passed: "+_checks+". Owned unshown WPF window, synthetic PNG, fake processor; no Corel."); return 0;
        }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
        finally { if(window!=null && !Field<bool>(window,"_closed")) window.Close(); }
    }
}
