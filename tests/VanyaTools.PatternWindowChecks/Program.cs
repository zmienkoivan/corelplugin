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
internal static class Program
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static int _checks;
    private static void Check(bool value,string error){_checks++;if(!value)throw new Exception(error);}
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,Flags).GetValue(value);
    private static object Call(object value,string name,params object[] args)=>value.GetType().GetMethod(name,Flags).Invoke(value,args);
    private static PatternAsset Asset(bool filler=false){var g=new RectangleGeometry(new Rect(-10,-5,20,10));g.Freeze();return new PatternAsset{Id=filler?2:1,Label="Synthetic",IsFiller=filler,Size=new PatternPoint(20,10),Footprint=new PatternFootprint(new PatternPoint(20,10)),Silhouette=g};}
    private static PatternFrame Frame(){var g=new RectangleGeometry(new Rect(0,0,200,140));g.Freeze();return new PatternFrame{Width=200,Height=140,Geometry=g,Curve=new object()};}
    private static PatternPoint[] Box(double x,double y,double w,double h)=>new[]{new PatternPoint(x,y),new PatternPoint(x+w,y),new PatternPoint(x+w,y+h),new PatternPoint(x,y+h)};
    private static RenderTargetBitmap Render(FrameworkElement root)
    {root.Measure(new Size(1100,800));root.Arrange(new Rect(0,0,1100,800));root.UpdateLayout();var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);var image=new RenderTargetBitmap(1100,800,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var dc=background.RenderOpen())dc.DrawRectangle(Brushes.White,null,new Rect(0,0,1100,800));image.Render(background);image.Render(root);return image;}
    [STAThread] private static int Main()
    {
        string previous=Environment.GetEnvironmentVariable("TEMP"),temporary=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Vanya-pattern-ui-"+Guid.NewGuid().ToString("N"));System.IO.Directory.CreateDirectory(temporary);Environment.SetEnvironmentVariable("TEMP",temporary);
        try{WindowActions();RetryButton();ClicksAndCellColors();MultipleInsertion();BottleEditorChecks.Run(Check);MotifReplacementChecks.Run(Check);Console.WriteLine("Pattern window checks passed: "+_checks+". Synthetic WPF and managed Corel stand-in only; no network or live document.");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{Environment.SetEnvironmentVariable("TEMP",previous);System.IO.Directory.Delete(temporary,true);}
    }
    private static void WindowActions()
    {
        var frame=Frame();var session=new PatternEditorSession{Seam=frame,Motifs=new[]{Asset(),Asset(true)}};
        var window=new PatternFillWindow(session,new PatternEditorSettings{Layout=new PatternFillOptions{Mode=PatternFillMode.Pebbles,SizeMm=35,GapMm=2,Seed=8123}});
        using(var source=new HwndSource(new HwndSourceParameters("Vanya editor action checks"){Width=1100,Height=800,WindowStyle=0}))
        try
        {
            var root=(FrameworkElement)window.Content;source.RootVisual=root;DateTime deadline=DateTime.UtcNow.AddSeconds(15);
            do{Render(root);Thread.Sleep(10);}while(window.Draft==null && DateTime.UtcNow<deadline);
            Check(window.Draft!=null,"Actual editor failed to produce a draft");var preview=Field<PatternPreviewSurface>(window,"_preview");
            Check(Field<int>(preview,"_motifTool")==0,"The editor did not default to click replacement / rotation");
            int applied=0;window.ApplyRequested+=draft=>{Check(ReferenceEquals(draft,window.Draft),"Apply exported a stale variant");applied++;};
            var button=Field<Button>(window,"_apply");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(applied==2 && window.DialogResult==null && button.IsEnabled && ReferenceEquals(window.Content,root),"Apply closed the editor or prevented a second variant");
            window.ApplyRequested+=_=>throw new InvalidOperationException("synthetic insertion failure");button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(window.DialogResult==null && button.IsEnabled && Field<TextBlock>(window,"_message").Text.Contains("synthetic insertion failure"),"An insertion failure closed or disabled the editor");
            string directory=Environment.GetEnvironmentVariable("VANYA_PATTERN_RENDER");if(!String.IsNullOrEmpty(directory)){var render=Render(root);var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(render));using(var file=System.IO.File.Create(System.IO.Path.Combine(directory,"editor-actions.png")))e.Save(file);}
        }
        finally{window.Close();}
    }
    private static void RetryButton()
    {
        string directory=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Vanya-pattern-window-"+Guid.NewGuid().ToString("N"));
        var reference=BitmapSource.Create(128,128,96,96,PixelFormats.Bgra32,null,new byte[128*128*4],512);reference.Freeze();
        var plan=PatternFillerBrief.Preset(0);plan.SmallFillers=true;var job=PatternFillerRequests.Create(reference,plan,SublimationModels.Nano2,false,directory);
        System.IO.File.WriteAllText(job.OutputPath+".prediction.json","{\"id\":\"synthetic-saved-id\",\"status\":\"processing\"}");
        var window=new PatternFillerWindow(reference,"synthetic-not-used",plan,requestRoot:directory);
        using(var source=new HwndSource(new HwndSourceParameters("Vanya saved request button"){Width=1100,Height=800,WindowStyle=0}))
        try
        {
            var root=(FrameworkElement)window.Content;source.RootVisual=root;var image=Render(root);
            var button=Field<Button>(window,"_resume");Check(button.Visibility==Visibility.Visible && button.IsEnabled && (string)button.Content=="Продолжить запрос" && !Field<Button>(window,"_generate").IsEnabled,"Actual window lacks the resume button or allows a duplicate generation");
            string output=Environment.GetEnvironmentVariable("VANYA_PATTERN_RENDER");if(!String.IsNullOrEmpty(output)){var e=new PngBitmapEncoder();e.Frames.Add(BitmapFrame.Create(image));using(var file=System.IO.File.Create(System.IO.Path.Combine(output,"resume-button.png")))e.Save(file);}
        }
        finally{window.Close();System.IO.Directory.Delete(directory,true);}
    }
    private static void ClicksAndCellColors()
    {
        var frame=Frame();var hero=Asset();var filler=Asset(true);var session=new PatternEditorSession{Seam=frame,Hero=hero};
        var preview=new PatternPreviewSurface(session,frame);var pose=new PatternPlacement{Motif=0,Center=new PatternPoint(65,65),Size=20,Cell=Box(40,40,50,50)};
        using(var source=new HwndSource(new HwndSourceParameters("Vanya click hit tests"){Width=1100,Height=800,WindowStyle=0}))
        {
            var root=new Border{Child=preview};source.RootVisual=root;
            foreach(var mode in new[]{PatternFillMode.Rows,PatternFillMode.Pebbles,PatternFillMode.Crocodile})
            {
                var draft=new PatternDraft{Frame=frame,Options=new PatternFillOptions{Mode=mode},Motifs=new[]{filler},Items=new List<PatternPlacement>{pose},Hero=new PatternPlacement{Center=new PatternPoint(120,100),Size=30}};
                preview.Update(frame,draft,0);var render=Render(root);
                Point Screen(double x,double y)=>(Point)Call(preview,"ToScreen",new PatternPoint(x,y));
                Check(!(bool)Call(preview,"HitHero",Screen(20,20)),"An empty click can still move the main logo in "+mode);
                Check((bool)Call(preview,"HitHero",Screen(120,100)),"The main logo no longer supports dragging by its actual drawing");
                if(mode==PatternFillMode.Rows)continue;
                var pixels=new byte[1100*800*4];render.CopyPixels(pixels,4400,0);var a=Screen(40,40);var b=Screen(90,90);int greens=0;
                for(int y=0;y<800;y++)for(int x=0;x<1100;x++)
                {
                    int i=(y*1100+x)*4;if(pixels[i+1]<=pixels[i]*1.25 || pixels[i+1]<=pixels[i+2]*1.25)continue;greens++;
                    Check(Math.Min(Math.Min(Math.Abs(x-a.X),Math.Abs(x-b.X)),Math.Min(Math.Abs(y-a.Y),Math.Abs(y-b.Y)))<3,"Green marking traced the artwork instead of the pebble cell");
                }
                Check(greens>0,"The filler cell contour was not marked green");
            }
        }
    }
    private static void MultipleInsertion()
    {
        var app=new FakeApp();var target=new FakeShape(app,7);app.Selected=target;var frame=Frame();frame.Width=100;
        var session=new PatternEditorSession{Application=app,Document=app.ActiveDocument,Target=target,Seam=frame,Replace=true};
        var draft=new PatternDraft{Frame=frame,Options=new PatternFillOptions(),Motifs=Array.Empty<PatternAsset>(),Items=new List<PatternPlacement>(),MirroredIds=new HashSet<int>()};
        var service=new PatternFillService();service.Apply(session,draft,true);frame.Width=120;service.Apply(session,draft,true);
        var shapes=app.ActiveDocument.ActiveLayer.Created;
        Check(shapes.Count==2 && !target.Deleted && shapes.All(s=>!s.Deleted),"Inserting another variant deleted the source or previous result");
        Check(shapes[0].X==0 && shapes[1].X==110 && session.VariantOffset==240 && app.Selected==target,"Variants overlap or selection was not preserved for the next apply");
        app.ActiveDocument.ActiveLayer.FailMove=true;bool failed=false;try{service.Apply(session,draft,true);}catch(InvalidOperationException){failed=true;}
        Check(failed && session.VariantOffset==240 && shapes.Last().Deleted && shapes.Take(2).All(s=>!s.Deleted) && !target.Deleted,"Failed insertion damaged prior variants or advanced their position");
        Check(app.ActiveDocument.Begun==app.ActiveDocument.Ended && !app.Optimization && app.ActiveDocument.Unit==0,"Insertion did not restore the document settings/undo group");
    }
}
public sealed class FakeApp
{
    public FakeDoc ActiveDocument{get;} public bool Optimization{get;set;} public FakeShape Selected;public FakeWindow ActiveWindow{get;}=new FakeWindow();
    public FakeApp(){ActiveDocument=new FakeDoc(this);}public FakeSelection ActiveSelectionRange=>new FakeSelection(Selected);public FakeRange CreateShapeRange()=>new FakeRange();
}
public sealed class FakeDoc {public FakeLayer ActiveLayer{get;}public int Unit{get;set;}public int Begun,Ended;public FakeDoc(FakeApp app){ActiveLayer=new FakeLayer(app);}public void BeginCommandGroup(string name){Begun++;}public void EndCommandGroup(){Ended++;}}
public sealed class FakeSelection {public int Count=>1;public FakeShapes Shapes{get;}public FakeSelection(FakeShape shape){Shapes=new FakeShapes(shape);}}
public sealed class FakeShapes {public FakeIndex Item{get;}public FakeShapes(FakeShape shape){Item=new FakeIndex(shape);}}
public sealed class FakeIndex {private readonly FakeShape _shape;public FakeIndex(FakeShape shape){_shape=shape;}public FakeShape this[int index]=>_shape;}
public sealed class FakeRange {}
public sealed class FakeWindow {public void Refresh(){}}
public sealed class FakeLayer {private readonly FakeApp _app;public List<FakeShape> Created=new List<FakeShape>();public bool FailMove;public FakeLayer(FakeApp app){_app=app;}public FakeShape CreateCurve(object curve){var shape=new FakeShape(_app,Created.Count+100){FailMove=FailMove};Created.Add(shape);return shape;}}
public sealed class FakeShape {private readonly FakeApp _app;public int StaticID{get;}public string Name{get;set;}public FakeFill Fill{get;}=new FakeFill();public FakeOutline Outline{get;}=new FakeOutline();public bool Deleted,FailMove;public double X;public FakeShape(FakeApp app,int id){_app=app;StaticID=id;}public void CreateSelection(){_app.Selected=this;}public void Delete(){Deleted=true;}public void Move(double x,double y){if(FailMove)throw new InvalidOperationException("synthetic move failure");X+=x;}}
public sealed class FakeFill {public void ApplyNoFill(){}}
public sealed class FakeOutline {public void SetNoOutline(){}}
