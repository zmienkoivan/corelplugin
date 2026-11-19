using System;
using System.Linq;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using VanyaTools.Native;
internal static class Program
{
    private static int checks;
    internal static void Check(bool condition,string text){checks++;if(!condition)throw new Exception(text);}
    private static void Equal(double value,double expected,string text)=>Check(Math.Abs(value-expected)<.012,text+": "+value+" vs "+expected);
    private static void Reject(Action work,string text){bool failed=false;try{work();}catch(Exception){failed=true;}Check(failed,text);}
    [STAThread]private static int Main()
    {
        try{Model();Geometry();Ui();Pdf();Service();Console.WriteLine("Sticker printing-plate checks passed: "+checks+". Synthetic alpha and fake Corel; no customer files.");return 0;}
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    private static void Model()
    {
        for(int i=-10;i<=10;i++)Equal(StickerPrintPlateModel.Validate(StickerPrintPlateKind.White,i*.05),i*.05,"Trapping step");
        foreach(double value in new[]{-.55,.55,.03,Double.NaN,Double.PositiveInfinity})Reject(()=>StickerPrintPlateModel.Validate(StickerPrintPlateKind.Clear,value),"Invalid trapping accepted");
        Equal(StickerPrintPlateModel.CaptureDpi(100,100),600,"Normal capture resolution");
        int dpi=StickerPrintPlateModel.CaptureDpi(500,500);Check(dpi>=72 && Math.Ceiling(500*dpi/25.4)*Math.Ceiling(500*dpi/25.4)<=StickerPrintPlateModel.MaxPixels,"Large capture exceeded memory limit");
        var xml=new XmlDocument();xml.Load(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"VanyaTools.PrintPalette.xml"));
        Check(xml.SelectNodes("/palette/colors/page/color").Cast<XmlElement>().Select(e=>e.GetAttribute("name")).SequenceEqual(new[]{"WHITE","CLEAR"}),"Installed spot names changed");
    }
    private static BitmapSource Mask()
    {
        int w=100,h=80;var data=new byte[w*h*4];
        for(int y=10;y<70;y++)for(int x=10;x<90;x++)if(!(x>=35 && x<65 && y>=25 && y<55))data[(y*w+x)*4+3]=255;
        // Separate dot in a transparent hole must survive as an island.
        for(int y=36;y<44;y++)for(int x=46;x<54;x++)data[(y*w+x)*4+3]=255;
        var image=BitmapSource.Create(w,h,254,254,PixelFormats.Bgra32,null,data,w*4);image.Freeze();return image;
    }
    private static bool Contains(Geometry geometry,Point point)=>geometry.FillContains(point,.001,ToleranceType.Absolute);
    private static void Geometry()
    {
        var source=Mask();var bytes=new byte[100*80*4];source.CopyPixels(bytes,400,0);
        foreach(double trap in new[]{-.5,-.1,0,.1,.5})
        {
            var g=StickerPrintPlateGeometry.Build(source,10,8,trap);
            Equal(g.Bounds.Left,1-trap,"Outer left offset");Equal(g.Bounds.Right,9+trap,"Outer right offset");
            Check(Contains(g,new Point(2,2)) && !Contains(g,new Point(4.05,4)),"Hole was filled or artwork lost");
            if(trap>=-.1)Check(Contains(g,new Point(5,4)),"Disconnected artwork island was removed");
            Check(trap<=0 || Contains(g,new Point(3.5+trap*.5,4)),"Spread did not shrink the transparent hole");
            Check(trap>=0 || !Contains(g,new Point(3.5+trap*.5,4)),"Choke did not expand the transparent hole");
        }
        var after=new byte[bytes.Length];source.CopyPixels(after,400,0);Check(bytes.SequenceEqual(after),"Geometry changed source alpha");
        var thin=BitmapSource.Create(1,10,96,96,PixelFormats.Bgra32,null,Enumerable.Repeat((byte)255,40).ToArray(),4);
        Reject(()=>StickerPrintPlateGeometry.Build(thin,.1,1,-.1),"Collapsed choke returned an empty plate");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var c in Descendants(VisualTreeHelper.GetChild(root,i)))yield return c;}
    private static void Ui()
    {
        var called=new List<Tuple<StickerPrintPlateKind,double>>();var ui=new StickerPrintPlateControls((k,t)=>called.Add(Tuple.Create(k,t)));
        ui.Measure(new Size(360,400));ui.Arrange(new Rect(0,0,360,400));ui.UpdateLayout();var all=Descendants(ui).ToArray();
        var combos=all.OfType<ComboBox>().ToArray();var buttons=all.OfType<Button>().ToArray();
        Check(combos.Length==2 && combos.All(c=>c.Items.Count==21),"Trapping controls do not provide all 0.05 mm steps");
        buttons.Single(b=>(string)b.Content=="Создать подложку").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        buttons.Single(b=>(string)b.Content=="Создать лак").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(called[0].Item1==StickerPrintPlateKind.White && called[1].Item1==StickerPrintPlateKind.Clear,"Buttons created the wrong ink");Equal(called[0].Item2,-.1,"Default white choke");Equal(called[1].Item2,.1,"Default clear spread");
    }
    private static void Pdf()
    {
        var settings=new FakePdfSettings();PrintPdfSpotSettings.Require(settings);Check(settings.OutputSpotColorsAs==0 && settings.Overprints,"Printer PDF lost spot/overprint settings");
        Reject(()=>PrintPdfSpotSettings.Require(new FakePdfSettings{Ignore=true}),"Ignored Corel PDF settings were accepted");
    }
    private static void Service()
    {
        foreach(var kind in new[]{StickerPrintPlateKind.White,StickerPrintPlateKind.Clear})foreach(double trap in new[]{-.5,0,.5})
        {
            var app=new PlateApp();var originals=app.Selected.ToArray();StickerPrintPlateService.Create(app,kind,trap);
            var plate=app.Outputs.Single();Check(plate.Fill.UniformColor.IsSpot && plate.Fill.UniformColor.SpotColorName==StickerPrintPlateModel.Ink(kind),"Wrong spot ink");
            Check(plate.OverprintFill && plate.Outline.Type==0 && plate.Layer.Printable,"Plate is not printable, overprinted fill-only artwork");
            Check(originals.All(s=>app.Selected.Contains(s)) && app.Selected.Contains(plate),"Printer selection lost artwork or the new plate");
            Check(app.Document.Unit==1 && app.Document.ReferencePoint==9 && !app.Optimization && app.Document.Groups==0,"Corel state was not restored");
            Check(app.Temporary.All(s=>s.Deleted) && originals.All(s=>!s.Deleted),"Temporary capture leaked or source was deleted");
            Equal(plate.LeftX,15-trap,"Source placement / signed trap");
        }
        var pair=new PlateApp();StickerPrintPlateService.Create(pair,StickerPrintPlateKind.White,-.1);StickerPrintPlateService.Create(pair,StickerPrintPlateKind.Clear,.1);
        Check(pair.Outputs.Count==2 && pair.Selected.Count==3 && pair.InputCounts.All(n=>n==1),"Second button used the old plate as artwork or lost it from print selection");
        var grouped=new PlateApp();var main=grouped.Selected.Single();var baseLayer=grouped.Document.BaseLayer;
        var cut=new PlateShape(grouped,baseLayer){Name="Cut",Type=3};cut.Outline.Type=1;cut.Outline.Color=new PlateColor{IsSpot=true,SpotColorName="CUT"};
        var white=new PlateShape(grouped,baseLayer){Name="Prior white",Type=3};white.Fill.Type=1;white.Fill.UniformColor=new PlateColor{IsSpot=true,SpotColorName="WHITE"};
        var technicalGroup=new PlateShape(grouped,baseLayer){Name="Technical only",Type=7,Children=new List<PlateShape>{cut,white}};
        var root=new PlateShape(grouped,baseLayer){Name="Sticker group",Type=7,Children=new List<PlateShape>{main,technicalGroup}};
        var ignoredLayer=new PlateLayer(grouped.Document){Printable=false};var ignored=new PlateShape(grouped,ignoredLayer){Name="Non-printing artwork"};
        grouped.Selected.Clear();grouped.Selected.Add(root);grouped.Selected.Add(ignored);
        StickerPrintPlateService.Create(grouped,StickerPrintPlateKind.White,-.1);
        Check(grouped.CapturedNames.SequenceEqual(new[]{"Artwork"}),"Nested CUT/WHITE or non-printing content entered the capture");
        Check(!main.Deleted && !cut.Deleted && !white.Deleted && !technicalGroup.Deleted && !root.Deleted,"Nested source pruning changed originals");
        Check(grouped.Selected.Contains(root) && grouped.Selected.Contains(ignored) && grouped.Selected.Count==3,"Group creation did not restore the whole original selection");
        Check(grouped.Temporary.All(s=>s.Deleted),"Nested capture cleanup leaked a duplicate");
        foreach(string failure in new[]{"spot","alpha","fill","overprint","duplicate"})
        {
            var app=new PlateApp{Failure=failure};var originals=app.Selected.ToArray();Reject(()=>StickerPrintPlateService.Create(app,StickerPrintPlateKind.White,-.1),"Controlled failure accepted: "+failure);
            Check(originals.All(s=>!s.Deleted) && app.Selected.SequenceEqual(originals) && app.Document.Groups==0 && app.Document.Unit==1 && !app.Optimization,"Failure damaged source or Corel state: "+failure);
            Check(app.Temporary.All(s=>s.Deleted) && app.Outputs.All(s=>s.Deleted),"Failure left partial output: "+failure);
        }
    }
}
namespace VanyaTools.Native
{
    internal static class CorelApp{internal static object Get()=>throw new Exception("Live Corel is forbidden in offline checks");}
    internal static class Log{internal static void Info(string s){}internal static void Error(string s,Exception e){}}
    internal static class DockerLayout
    {
        internal static StackPanel ToolGroup(string title,string help){var p=new StackPanel();p.Children.Add(new TextBlock{Text=title});p.Children.Add(new TextBlock{Text=help});return p;}
    }
}
public sealed class FakePdfSettings
{
    public bool Ignore;private int spot=1;private bool overprint;
    public int OutputSpotColorsAs{get=>spot;set{if(!Ignore)spot=value;}}
    public bool Overprints{get=>overprint;set{if(!Ignore)overprint=value;}}
}
