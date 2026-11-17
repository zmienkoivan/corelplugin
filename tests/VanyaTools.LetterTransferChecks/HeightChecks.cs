using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class HeightChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Flags).GetValue(target);
    private static void Call(object target,string name,params object[] args)=>target.GetType().GetMethod(name,Flags).Invoke(target,args);
    private static void Reject(Action action,string message){bool rejected=false;try{action();}catch(ArgumentException){rejected=true;}Program.Check(rejected,message);}
    internal static void Run(string root)
    {
        Parsing();Layout();Persistence(root);Insertion(root);Window(root);
    }
    private static void Parsing()
    {
        Program.Check(LetterTransferPrintSizes.Parse("20; 80; 35,5; 20.0").SequenceEqual(new[]{20d,80d,35.5}),"Height list lost arbitrary values, comma decimals, order or deduplication");
        foreach(string bad in new[]{"",";","NaN","Infinity","0","501","20; text",String.Join(";",Enumerable.Range(1,17))})Reject(()=>LetterTransferPrintSizes.Parse(bad),"Invalid height list accepted: "+bad);
        var culture=Thread.CurrentThread.CurrentCulture;
        try
        {
            var set=new LetterTransferSet{PrintHeightsMm=new List<double>{20,80,35.1255}};
            foreach(string name in new[]{"ru-RU","en-US"})
            {
                Thread.CurrentThread.CurrentCulture=CultureInfo.GetCultureInfo(name);
                Program.Check(LetterTransferPrintSizes.Parse(LetterTransferPrintSizes.Format(set)).SequenceEqual(set.PrintHeightsMm),"Saved height display loses arbitrary precision or locale");
            }
        }
        finally{Thread.CurrentThread.CurrentCulture=culture;}
    }
    private static void Layout()
    {
        var set=Program.Printable();set.AutoLength=true;set.SheetWidthMm=300;set.PrintHeightsMm=new List<double>{20,80,35.5};
        var plan=LetterTransferLayout.Build(set,false);
        Program.Check(plan.Count==54 && plan.GroupBy(p=>p.Page).All(page=>page.Select(p=>p.PrintHeightMm).Distinct().Count()==1),"Print heights share pages or copies are missing");
        foreach(double height in set.PrintHeightsMm)
        {
            var size=plan.Where(p=>p.PrintHeightMm==height).ToList();
            Program.Check(size.Count==18 && size.All(p=>Math.Abs(p.Height-height)<.000001),"Physical print height is wrong");
            Program.Check(size.Where(p=>p.Glyph.Letter=="А").Select(p=>p.Glyph.Id).Distinct().Count()==2,"A height lost visual variants");
        }
        foreach(var page in plan.GroupBy(p=>p.Page))
        {
            Program.Check(page.GroupBy(p=>p.Column).All(col=>col.Select(p=>p.CellX).Distinct().Count()==1) && page.GroupBy(p=>p.Row).All(row=>row.Select(p=>p.CellY).Distinct().Count()==1),"Multiple heights broke straight cutting lines");
            Program.Check(page.All(p=>p.X>=set.MarginMm && p.Y>=set.MarginMm && p.X+p.Width<=set.SheetWidthMm-set.MarginMm+.00001 && p.Y+p.Height<=LetterTransferLayout.PageHeight(set,page)-set.MarginMm+.00001),"Height layout extends beyond the sheet");
        }
        set.Letters.ForEach(row=>row.Copies=0);
        foreach(string letter in new[]{"А","Ё","Й"})set.Letters.Single(row=>row.Letter==letter).Copies=1;
        foreach(string letter in new[]{"Ё","Й"})set.Glyphs.Add(new LetterTransferGlyph{Letter=letter,File="glyphs/"+Guid.NewGuid().ToString("N")+".png",Width=20,Height=25,BodyHeight=20});
        plan=LetterTransferLayout.Build(set,false);
        Program.Check(plan.Where(p=>p.Glyph.Letter=="Ё" || p.Glyph.Letter=="Й").All(p=>Math.Abs(p.Height-p.PrintHeightMm*1.25)<.000001 && p.CellHeight>=p.Height),"Accents lost their larger cell at an arbitrary height");
        set=Program.Printable();set.Letters[0].Copies=500;set.Letters[1].Copies=0;
        Program.Check(LetterTransferLayout.Build(set,false).Max(p=>p.Page)+1==125,"Unexpected baseline for combined page limit");
        set.PrintHeightsMm=new List<double>{20,21};bool failed=false;try{LetterTransferLayout.Build(set,false);}catch(InvalidOperationException){failed=true;}
        Program.Check(failed,"Combined heights bypassed the 200-page limit");
    }
    private static void Persistence(string root)
    {
        var set=Program.Printable();set.PrintHeightsMm=new List<double>{20,80,35.5};string directory=LetterTransferStore.Create(Path.Combine(root,"height-store"),set);
        var loaded=LetterTransferStore.Load(directory);Program.Check(loaded.PrintHeightsMm.SequenceEqual(set.PrintHeightsMm) && loaded.Glyphs.Select(g=>g.Id).SequenceEqual(set.Glyphs.Select(g=>g.Id)),"Saving heights changed the source bank");
        var serializer=new JavaScriptSerializer();string path=Path.Combine(directory,"set.json");var legacy=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(path));legacy.Remove("PrintHeightsMm");legacy["HeightMm"]=27.25;File.WriteAllText(path,serializer.Serialize(legacy));
        loaded=LetterTransferStore.Load(directory);Program.Check(LetterTransferPrintSizes.For(loaded).SequenceEqual(new[]{27.25}),"An older set lost its existing height");
        set.PrintHeightsMm=new List<double>{20,Double.NaN};Reject(()=>LetterTransferStore.Save(directory,set),"Corrupt persisted height list accepted");
        Program.Check(LetterTransferStore.Load(directory).HeightMm==27.25,"Invalid input replaced the saved set");
    }
    private static void Insertion(string root)
    {
        var set=Program.Printable();set.AutoLength=true;set.SheetWidthMm=300;set.PrintHeightsMm=new List<double>{20,80};string directory=LetterTransferStore.Create(Path.Combine(root,"height-insert"),set);
        foreach(var glyph in set.Glyphs)LetterTransferImages.Save(Program.Solid(glyph.Width,glyph.Height,Colors.Navy),LetterTransferStore.Resolve(directory,glyph.File));
        var physical=LetterTransferImages.Physical(set,"А",Program.Solid(100,120,Colors.Navy));
        Program.Check(Math.Abs(physical.PixelHeight*25.4/physical.DpiY-80)<.001,"New PNG metadata ignores the largest print height: "+physical.DpiY.ToString("R",CultureInfo.InvariantCulture)+" DPI; "+(physical.PixelHeight*25.4/physical.DpiY).ToString("R",CultureInfo.InvariantCulture)+" mm");
        var before=set.Glyphs.ToDictionary(g=>g.Id,g=>File.ReadAllBytes(LetterTransferStore.Resolve(directory,g.File)));
        var app=new FakeLetterApp();int count=LetterTransferService.Insert(app,directory,set,false);var pages=app.ActiveDocument.Pages.Skip(1).ToArray();
        Program.Check(count==36 && pages.Length==2 && pages[0].Layer.Shapes.All(s=>s.Height==20) && pages[1].Layer.Shapes.All(s=>s.Height==80),"Corel inserted incorrect physical heights");
        Program.Check(pages[0].Name.Contains("20 мм") && pages[1].Name.Contains("80 мм"),"Corel pages do not identify their print height");
        Program.Check(set.Glyphs.All(g=>before[g.Id].SequenceEqual(File.ReadAllBytes(LetterTransferStore.Resolve(directory,g.File)))),"Resizing print output altered source PNGs");
        app=new FakeLetterApp();app.ActiveDocument.FailImport=4;bool failed=false;try{LetterTransferService.Insert(app,directory,set,false);}catch(Exception){failed=true;}
        Program.Check(failed && app.ActiveDocument.Pages.Count==1 && !app.Optimization && app.ActiveDocument.Unit==7 && !app.ActiveDocument.Dirty,"A later height import did not roll back all created pages");
    }
    private static void Window(string root)
    {
        var set=Program.Printable();set.AutoLength=true;set.SheetWidthMm=300;string uiRoot=Path.Combine(root,"height-ui"),directory=LetterTransferStore.Create(uiRoot,set);
        foreach(var glyph in set.Glyphs)LetterTransferImages.Save(Program.Solid(glyph.Width,glyph.Height,Colors.Teal),LetterTransferStore.Resolve(directory,glyph.File));
        int calls=ReplicateWorkerClient.Calls;var window=new LetterTransferWindow(()=>"",()=>null,(_,__)=>{},uiRoot);
        try
        {
            Call(window,"SelectStep",2);Field<TextBox>(window,"_height").Text="20; 80; 35,5";Call(window,"Save");Call(window,"Preview");
            var saved=LetterTransferStore.Load(directory);
            Program.Check(saved.PrintHeightsMm.SequenceEqual(new[]{20d,80d,35.5}) && saved.HeightMm==20,"Height field did not save all sizes");
            Program.Check(Field<StackPanel>(window,"_pages").Children.Count==3 && Field<TextBlock>(window,"_summary").Text.Contains("54 из 54"),"Preview count omits height copies");
            string render=Environment.GetEnvironmentVariable("VANYA_LETTER_RENDER");
            foreach(var size in new[]{new Size(1320,790),new Size(1000,640)})
            {
                var content=(FrameworkElement)window.Content;content.Measure(size);content.Arrange(new Rect(size));content.UpdateLayout();
                Program.Check(Field<TextBox>(window,"_height").ActualWidth>=150 && Field<WrapPanel>(window,"_sizesPanel").Visibility==Visibility.Visible,"Arbitrary height field is hidden or too narrow");
                if(!String.IsNullOrEmpty(render)){var image=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);var back=new DrawingVisual();using(var dc=back.RenderOpen())dc.DrawRectangle(Brushes.White,null,new Rect(size));image.Render(back);image.Render(content);LetterTransferImages.Save(image,Path.Combine(render,"letter-heights-"+(int)size.Width+".png"));}
            }
            Field<TextBox>(window,"_height").Text="20; bad";bool rejected=false;try{Call(window,"Save");}catch(TargetInvocationException){rejected=true;}
            Program.Check(rejected && Field<LetterTransferSet>(window,"_set").PrintHeightsMm.SequenceEqual(new[]{20d,80d,35.5}) && LetterTransferStore.Load(directory).PrintHeightsMm.SequenceEqual(saved.PrintHeightsMm),"Invalid partial input damaged saved or active heights");
            Field<TextBox>(window,"_height").Text="20; 80; 35,5";
            Program.Check(ReplicateWorkerClient.Calls==calls,"Changing height generated new paid images");
        }
        finally{window.Close();}
    }
}
