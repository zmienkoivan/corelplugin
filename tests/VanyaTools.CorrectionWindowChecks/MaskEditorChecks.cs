using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Threading;
using VanyaTools.Native;

internal static partial class Program
{
    private static void MaskEditorChecks(string root)
    {
        var host = new DockPanel(); var tools = new StackPanel(); DockPanel.SetDock(tools, Dock.Top); host.Children.Add(tools);
        var source = BitmapSource.Create(1000,800,254,254,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)180,1000*800*4).ToArray(),4000); source.Freeze();
        var image = new Image { Source=source, Width=1000, Height=800, Stretch=Stretch.Fill, HorizontalAlignment=HorizontalAlignment.Left, VerticalAlignment=VerticalAlignment.Top };
        var content = new Grid(); content.Children.Add(image);
        var scroll = new ScrollViewer { Content=content, HorizontalScrollBarVisibility=ScrollBarVisibility.Auto, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        host.Children.Add(scroll); int changes=0; bool enabled=true;
        var editor = new RasterMaskEditor(tools,image,scroll,()=>new Rect(0,0,100,80),()=>enabled,()=>changes++,"Просмотр","Обводка","Другая маска");
        using(var surface = new HwndSource(new HwndSourceParameters("Vanya mask input checks") {Width=420,Height=380,WindowStyle=0}))
        {
            surface.RootVisual=host; host.Measure(new Size(420,380)); host.Arrange(new Rect(0,0,420,380)); host.UpdateLayout(); Pump();
            var tabs=Field<TabControl>(editor,"_tabs"); tabs.SelectedIndex=1; host.UpdateLayout(); Pump();
            var tool=Field<ComboBox>(editor,"_tool"); var size=Field<Slider>(editor,"_size"); size.Value=20;
            var layers=Field<List<RasterMaskShape>[]>(editor,"_layers");
            Check(tool.Items.Cast<string>().SequenceEqual(new[]{"Кисть","Ластик","Прямоугольник","Овал"}),"Mask toolbar did not expose an eraser beside the brush");
            Check((bool)Call(editor,"BeginStroke",image,new Point(40,40)),"Brush could not start on the visible image");
            Call(editor,"MoveStroke",new Point(140,40)); Call(editor,"EndStroke",true);
            tool.SelectedIndex=1; Check(size.IsEnabled,"Eraser diameter is disabled");
            Check((bool)Call(editor,"BeginStroke",image,new Point(90,40)),"Eraser could not start");
            Check(!Field<System.Windows.Shapes.Path>(editor,"_overlay").Data.FillContains(new Point(90,40)) && editor.Snapshot(0).Rasterize(1000,800,254,254,0,0,CancellationToken.None)[40*1000+90],"Live eraser preview did not subtract the mask or mutated its committed snapshot");
            Call(editor,"EndStroke",true);
            var mask=editor.Snapshot(0).Rasterize(1000,800,254,254,0,0,CancellationToken.None);
            var overlay=Field<System.Windows.Shapes.Path>(editor,"_overlay").Data;
            Check(mask[40*1000+50] && !mask[40*1000+90] && overlay.FillContains(new Point(50,40)) && !overlay.FillContains(new Point(90,40)),"Eraser preview and raster mask disagree");
            Check(ReferenceEquals(image.Source,source) && editor.Snapshot(1)==null,"Eraser changed the image or another mask layer");
            Field<Button>(editor,"_undo").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(editor.Snapshot(0).Rasterize(1000,800,254,254,0,0,CancellationToken.None)[40*1000+90] && Field<System.Windows.Shapes.Path>(editor,"_overlay").Data.FillContains(new Point(90,40)),"Undo failed to restore the erased mask");
            // Paint again after erasing: operation order must be preserved.
            Call(editor,"BeginStroke",image,new Point(90,40)); Call(editor,"EndStroke",true); tool.SelectedIndex=0; size.Value=6;
            Call(editor,"BeginStroke",image,new Point(90,40)); Call(editor,"EndStroke",true);
            Check(editor.Snapshot(0).Rasterize(1000,800,254,254,0,0,CancellationToken.None)[40*1000+90] && Field<System.Windows.Shapes.Path>(editor,"_overlay").Data.FillContains(new Point(90,40)),"Brush could not restore an erased region");
            Field<Button>(editor,"_clear").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var bar=(ScrollBar)scroll.Template.FindName("PART_VerticalScrollBar",scroll); bar.ApplyTemplate();
            Check(bar.Visibility==Visibility.Visible && !(bool)Call(editor,"BeginStroke",bar,new Point(40,40)),"Scrollbar was accepted as the image even when its projected point is inside image bounds");
            var down=new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Left) {RoutedEvent=Mouse.PreviewMouseDownEvent}; bar.RaiseEvent(down);
            Check(!down.Handled && Field<int>(editor,"_drawingLayer")==-1 && editor.Snapshot(0)==null,"Scrollbar press was captured or painted a mask");
            Check(!(bool)Call(editor,"BeginStroke",image,new Point(900,40)),"A point outside the viewport started a stroke on the oversized image");
            Check((bool)Call(editor,"BeginStroke",image,new Point(40,40)),"Visible stroke could not start before wheel navigation"); Call(editor,"MoveStroke",new Point(70,50));
            scroll.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,0,120) {RoutedEvent=Mouse.PreviewMouseWheelEvent});
            Check(Field<int>(editor,"_drawingLayer")==-1 && layers[0].Count==1 && !(bool)Call(editor,"MoveStroke",new Point(100,120)),"Wheel scrolling kept the brush active or resumed it without a new click");
            Check(layers[0][0].Points.Length==2 && Math.Abs(layers[0][0].Points.Last().X-7)<1e-9,"Navigation appended an image-edge point to the stroke");
            Call(editor,"BeginStroke",image,new Point(40,40)); scroll.ScrollToVerticalOffset(50); host.UpdateLayout(); Pump();
            Check(Field<int>(editor,"_drawingLayer")==-1 && layers[0].Count==2,"Scrollbar scrolling did not end the in-progress stroke");
            scroll.ScrollToVerticalOffset(0); host.UpdateLayout(); Pump();
            Call(editor,"BeginStroke",image,new Point(40,40));
            Check(!(bool)Call(editor,"MoveStroke",new Point(1100,40)) && !(bool)Call(editor,"MoveStroke",new Point(410,40)),"Leaving the image/viewport was clamped into an edge stripe"); Call(editor,"EndStroke",true);
            Check(layers[0].Last().Points.Length==1,"Out-of-image drag added an edge-clamped point");
            image.Clip=new RectangleGeometry(new Rect(0,0,20,20)); host.UpdateLayout();
            var detailTarget=scroll.InputHitTest(image.TranslatePoint(new Point(80,80),scroll));
            Check(ReferenceEquals(detailTarget,content),"Clipped exact-detail area has no safe image-surface hit target");
            Check((bool)Call(editor,"BeginStroke",detailTarget,new Point(80,80)),"Non-interactive exact-detail tiles blocked painting on their content grid"); Call(editor,"EndStroke",false); image.Clip=null;
            Call(editor,"BeginStroke",image,new Point(40,40));
            scroll.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,surface,0,Key.Space) {RoutedEvent=Keyboard.PreviewKeyDownEvent});
            Check(Field<int>(editor,"_drawingLayer")==-1,"Space navigation kept the brush active");
            Call(editor,"BeginStroke",image,new Point(40,40));
            scroll.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,0,MouseButton.Middle) {RoutedEvent=Mouse.PreviewMouseDownEvent});
            Check(Field<int>(editor,"_drawingLayer")==-1,"Middle-button navigation kept the brush active");
            int beforeSwitch=layers[0].Count; Call(editor,"BeginStroke",image,new Point(40,40)); tool.SelectedIndex=1;
            Check(Field<int>(editor,"_drawingLayer")==-1 && layers[0].Count==beforeSwitch,"Changing tools committed an unfinished stroke");
            enabled=false; Check(!(bool)Call(editor,"BeginStroke",image,new Point(40,40)),"Disabled editor accepted input"); enabled=true;
            tool.SelectedIndex=1; Call(editor,"BeginStroke",image,new Point(50,40)); Call(editor,"EndStroke",true);
            var previous=editor.Snapshot(0); editor.MoveOrigin(2,3);
            Check(layers[0].Last().Tool==RasterMaskTool.Eraser && layers[0].Last().Points[0].X==3 && layers[0].Last().Points[0].Y==1,"Moving the image origin lost the eraser or its physical position");
            Check(previous.Rasterize(1000,800,254,254,0,0,CancellationToken.None).Cast<bool>().SequenceEqual(editor.Snapshot(0).Rasterize(1000,800,254,254,-2,-3,CancellationToken.None).Cast<bool>()),"Mask including erasures moved after crop origin adjustment");
            tabs.SelectedIndex=2; Check(editor.Snapshot(1)==null,"Switching layers copied the previous mask");
            Check(changes>0,"Committed brush/eraser operations did not invalidate the effect preview");
        }
    }
}
