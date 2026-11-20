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
        try{Model();Geometry();Ui();Palette();Pdf();Service();Console.WriteLine("Sticker printing-plate checks passed: "+checks+". Synthetic alpha and fake Corel; no customer files.");return 0;}
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
        int paletteShows = 0;
        string folder=System.IO.Path.Combine(System.IO.Path.GetTempPath(),"Vanya-plate-settings-"+Guid.NewGuid().ToString("N"));
        string path=System.IO.Path.Combine(folder,"settings.json");
        try{
        var settings=new StickerPrintPlateSettings(path);
        var called=new List<Tuple<StickerPrintPlateKind,double,int>>();var ui=new StickerPrintPlateControls((k,t,d)=>called.Add(Tuple.Create(k,t,d)), () => paletteShows++,settings);
        ui.Measure(new Size(360,400));ui.Arrange(new Rect(0,0,360,400));ui.UpdateLayout();var all=Descendants(ui).ToArray();
        var combos=all.OfType<ComboBox>().ToArray();var buttons=all.OfType<Button>().ToArray();
        Check(combos.Length==2 && combos.All(c=>c.Items.Count==21),"Trapping controls do not provide all 0.05 mm steps");
        ui.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
        Check(paletteShows == 1 && called.Count == 0, "Opening the sticker tab did not show the palette independently of creating artwork");
        buttons.Single(b=>(string)b.Content=="Палитра WHITE/CLEAR").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(paletteShows == 2 && called.Count == 0, "Palette button changed artwork instead of reopening swatches");
        buttons.Single(b=>(string)b.Content=="Создать подложку").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        buttons.Single(b=>(string)b.Content=="Создать лак").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(called[0].Item1==StickerPrintPlateKind.White && called[1].Item1==StickerPrintPlateKind.Clear,"Buttons created the wrong ink");Equal(called[0].Item2,-.1,"Default white choke");Equal(called[1].Item2,.1,"Default clear spread");
        Check(called.All(c=>c.Item3==100),"New fill density did not default to 100%");
        var densities=all.OfType<TextBox>().ToArray();
        Check(densities.Length==2,"Independent WHITE/CLEAR fill density controls missing");
        densities[0].Text="10";densities[1].Text="37";
        var saved=new StickerPrintPlateSettings(path);
        Check(saved.WhiteTint==10 && saved.ClearTint==37,"Fill densities were not independently saved before creation");
        buttons.Single(b=>(string)b.Content=="Создать лак").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(called.Last().Item3==37,"Entered spot density was not passed to creation");
        foreach(string invalid in new[]{"","9","101","35.5","abc"}){
            densities[0].Text=invalid;
            Check(!buttons.Single(b=>(string)b.Content=="Создать подложку").IsEnabled && new StickerPrintPlateSettings(path).WhiteTint==10,"Invalid density changed a saved default or enabled creation");
        }
        densities[0].Text="100";
        Check(buttons.Single(b=>(string)b.Content=="Создать подложку").IsEnabled && new StickerPrintPlateSettings(path).WhiteTint==100,"Maximum density did not restore creation/persist");
        var reopened=new StickerPrintPlateControls((k,t,d)=>{},()=>{},new StickerPrintPlateSettings(path));
        reopened.Measure(new Size(360,500));reopened.Arrange(new Rect(0,0,360,500));reopened.UpdateLayout();
        Check(Descendants(reopened).OfType<TextBox>().Select(t=>t.Text).SequenceEqual(new[]{"100","37"}),"Reopened controls forgot per-ink density defaults");
        }finally{if(System.IO.Directory.Exists(folder))System.IO.Directory.Delete(folder,true);}
    }
    private static void Palette()
    {
        var fresh = new PlateApp();
        fresh.Palettes.Registered = false;
        var selection = fresh.Selected.ToArray();
        StickerPrintSpotColor.Show(fresh);
        Check(fresh.Palettes.FileOpens == 1 && System.IO.Path.GetFileName(fresh.Palettes.LastPath) == "VanyaTools.PrintPalette.xml", "First use did not load the installed named-spot palette");
        Check(fresh.Palettes.Palette.IsOpen && fresh.RequestedColors.Distinct().SequenceEqual(new[] { "WHITE", "CLEAR" }), "First use did not show and validate both spot inks");
        Check(!fresh.Document.Dirty && fresh.Outputs.Count == 0 && fresh.Selected.SequenceEqual(selection), "Showing swatches changed the document or selection");

        var cached = new PlateApp();
        StickerPrintSpotColor.Show(cached);
        Check(cached.Palettes.FileOpens == 0 && cached.Palettes.Palette.OpenCalls == 1 && cached.Palettes.Palette.IsOpen, "An already registered but hidden palette stayed invisible");
        StickerPrintSpotColor.Show(cached);
        Check(cached.Palettes.FileOpens == 0 && cached.Palettes.Palette.OpenCalls == 1, "Repeated use duplicated or reopened an already visible palette");
        cached.Palettes.Palette.IsOpen = false;
        StickerPrintSpotColor.Create(cached, StickerPrintPlateKind.Clear);
        Check(cached.Palettes.Palette.OpenCalls == 2 && cached.Palettes.Palette.IsOpen, "Creating a plate did not restore swatches closed by the user");

        var ignored = new PlateApp(); ignored.Palettes.Palette.IgnoreOpen = true;
        Reject(() => StickerPrintSpotColor.Show(ignored), "A palette that Corel refused to show was reported as open");
        Check(ignored.Outputs.Count == 0 && !ignored.Document.Dirty, "Failed palette opening continued with artwork changes");
        var wrong = new PlateApp(); wrong.Palettes.Registered = false; wrong.Palettes.Palette.Identifier = "foreign-palette";
        Reject(() => StickerPrintSpotColor.Show(wrong), "An unrelated palette was accepted as WHITE/CLEAR");
        var process = new PlateApp { Failure = "spot" };
        Reject(() => StickerPrintSpotColor.Show(process), "Ordinary process colors were accepted as named WHITE/CLEAR spots");
        var legacy = new PlateApp();legacy.Palettes.LegacyWhite=true;legacy.Palettes.Palette.IsOpen=true;
        var refreshed=(PlateColor)StickerPrintSpotColor.Create(legacy,StickerPrintPlateKind.White);
        Check(legacy.Palettes.FileOpens==1 && legacy.Palettes.Palette.CloseCalls==1 && refreshed.IsSpot && refreshed.SpotColorName=="WHITE" && refreshed.RGBRed==0 && refreshed.RGBGreen==255 && refreshed.RGBBlue==0,"Old white preview was not refreshed to green while retaining the named spot ink");
        var stale=new PlateApp();stale.Palettes.LegacyWhite=true;stale.Palettes.IgnoreRefresh=true;
        Reject(()=>StickerPrintSpotColor.Show(stale),"A stale paper-white preview was reported as updated");
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
            Check(kind==StickerPrintPlateKind.White?app.Document.ActivePage.Layers.First()==plate.Layer:app.Document.ActivePage.Layers.Last()==plate.Layer,"White underbase or clear top layer was placed on the wrong side of artwork");
            Check(originals.All(s=>app.Selected.Contains(s)) && app.Selected.Contains(plate),"Printer selection lost artwork or the new plate");
            Check(app.Document.Unit==1 && app.Document.ReferencePoint==9 && !app.Optimization && app.Document.Groups==0,"Corel state was not restored");
            Check(app.Temporary.All(s=>s.Deleted) && originals.All(s=>!s.Deleted),"Temporary capture leaked or source was deleted");
            Equal(plate.LeftX,15-trap,"Source placement / signed trap");
        }
        var pair=new PlateApp();StickerPrintPlateService.Create(pair,StickerPrintPlateKind.White,-.1);StickerPrintPlateService.Create(pair,StickerPrintPlateKind.Clear,.1);
        Check(pair.Outputs.Count==2 && pair.Selected.Count==3 && pair.InputCounts.All(n=>n==1),"Second button used the old plate as artwork or lost it from print selection");
        Check(pair.Document.ActivePage.Layers.First()==pair.Outputs[0].Layer && pair.Document.ActivePage.Layers.Last()==pair.Outputs[1].Layer,"Combined order is not WHITE / artwork / CLEAR");
        foreach(var kind in new[]{StickerPrintPlateKind.White,StickerPrintPlateKind.Clear})foreach(int tint in new[]{10,37,100}){
            var tinted=new PlateApp();StickerPrintPlateService.Create(tinted,kind,0,tint);
            var plate=tinted.Outputs.Single();
            Check(plate.Fill.UniformColor.IsSpot && plate.Fill.UniformColor.SpotColorName==StickerPrintPlateModel.Ink(kind) && plate.Fill.UniformColor.Tint==tint && plate.OverprintFill,"Density changed ink identity or lost the requested spot tint/overprint");
        }
        foreach(int tint in new[]{0,9,101}){
            var invalid=new PlateApp();Reject(()=>StickerPrintPlateService.Create(invalid,StickerPrintPlateKind.White,0,tint),"Invalid density accepted");
            Check(invalid.Outputs.Count==0 && invalid.Temporary.Count==0 && !invalid.Document.Dirty,"Invalid density modified the document");
        }
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
        foreach(string failure in new[]{"spot","alpha","fill","overprint","duplicate","layer-order"})
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
