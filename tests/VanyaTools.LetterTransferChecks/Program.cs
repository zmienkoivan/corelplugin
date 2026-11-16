using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;
internal static class Program
{
    private static int _checks;
    private static string _root;
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    [STAThread]private static int Main()
    {
        _root=Path.Combine(Path.GetTempPath(),"Vanya-Letter-Checks-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(_root);
        try
        {
            Frequencies();Layouts();Images();PersistenceAndAi();InsertionChecks.Run(_root);Window();ConsentWindow();
            Console.WriteLine("Letter transfer checks passed: "+_checks+". Synthetic assets and fake Replicate/Corel only.");return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally
        {
            string parent=Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(Path.GetFullPath(_root).StartsWith(parent,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(_root).StartsWith("Vanya-Letter-Checks-",StringComparison.Ordinal))Directory.Delete(_root,true);
        }
    }
    internal static void Check(bool ok,string text){_checks++;if(!ok)throw new Exception(text);}
    private static void Reject(Action action,string text){bool failed=false;try{action();}catch(Exception){failed=true;}Check(failed,text);}
    private static void Frequencies()
    {
        var set=new LetterTransferSet();Check(set.Letters.Count==33 && set.Letters.Any(r=>r.Letter=="Ё"),"Alphabet lost Ё.");
        Check(!LetterTransferRules.ValidLetter("A") && !LetterTransferRules.ValidLetter("а"),"Accepted Latin or lowercase.");
        foreach(int n in new[]{33,34,95,100,999,5000})
        {
            LetterTransferRules.Allocate(set.Letters,n);Check(set.Letters.Sum(r=>r.Copies)==n && set.Letters.All(r=>r.Copies>=1),"Frequency allocation total/minimum wrong.");
            Check(n==33 || set.Letters.First(r=>r.Letter=="О").Copies>=set.Letters.First(r=>r.Letter=="Ъ").Copies,"Frequent letters not weighted.");
        }
        Reject(()=>LetterTransferRules.Allocate(set.Letters,32),"Accepted insufficient alphabet total.");set.Letters[0].Weight=Double.NaN;Reject(()=>LetterTransferRules.Validate(set),"Accepted NaN.");
        set.Letters[0].Weight=0;foreach(var row in set.Letters)row.Weight=0;Reject(()=>LetterTransferRules.Allocate(set.Letters,100),"Accepted zero weights.");
    }
    internal static LetterTransferSet Printable()
    {
        var set=new LetterTransferSet{SheetWidthMm=64,SheetHeightMm=55,HeightMm=20,GapMm=3,MarginMm=5};foreach(var row in set.Letters)row.Copies=0;
        set.Letters[0].Copies=11;set.Letters[1].Copies=7;
        set.Glyphs.Add(new LetterTransferGlyph{Letter="А",File="glyphs/a.png",Width=20,Height=20});set.Glyphs.Add(new LetterTransferGlyph{Letter="А",File="glyphs/a2.png",Width=16,Height=20});set.Glyphs.Add(new LetterTransferGlyph{Letter="Б",File="glyphs/b.png",Width=18,Height=20});return set;
    }
    private static void Layouts()
    {
        var set=Printable();var plan=LetterTransferLayout.Build(set,false);Check(plan.Count==18 && plan.Max(p=>p.Page)>0,"Copies or page breaks missing.");
        Check(plan.Where(p=>p.Glyph.Letter=="А").Select(p=>p.Glyph.Id).Distinct().Count()==2,"Visual variants not distributed.");
        foreach(var p in plan)Check(p.X>=set.MarginMm && p.Y>=set.MarginMm && p.X+p.Width<=set.SheetWidthMm-set.MarginMm+.00001 && p.Y+p.Height<=set.SheetHeightMm-set.MarginMm+.00001,"Clipped printed letter.");
        foreach(var group in plan.GroupBy(p=>p.Page))
        {
            var list=group.ToList();for(int i=0;i<list.Count;i++)for(int j=i+1;j<list.Count;j++)
            {var a=list[i];var b=list[j];Check(a.X+a.Width+set.GapMm<=b.X+.00001 || b.X+b.Width+set.GapMm<=a.X+.00001 || a.Y+a.Height+set.GapMm<=b.Y+.00001 || b.Y+b.Height+set.GapMm<=a.Y+.00001,"Overlap or gap lost.");}
        }
        set.Letters[2].Copies=2;Reject(()=>LetterTransferLayout.Build(set,false),"Incomplete print silently accepted.");Check(LetterTransferLayout.Build(set,true).Count==18,"Ready-only count wrong.");
        set.Glyphs[0].Width=1000;Reject(()=>LetterTransferLayout.Build(set,true),"Oversize letter cropped.");
        set=Printable();set.GapMm=Double.PositiveInfinity;Reject(()=>LetterTransferLayout.Build(set,true),"Infinite gap accepted.");
        set=Printable();set.SheetWidthMm=30;set.SheetHeightMm=30;set.Letters[0].Copies=5000;set.Letters[1].Copies=0;Reject(()=>LetterTransferLayout.Build(set,true),"Unbounded page generation.");
    }
    internal static BitmapSource Solid(int width,int height,Color color)
    {
        var data=new byte[width*height*4];for(int p=0;p<data.Length;p+=4){data[p]=color.B;data[p+1]=color.G;data[p+2]=color.R;data[p+3]=color.A;}
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);image.Freeze();return image;
    }
    private static BitmapSource Sheet(string key,bool clipped=false)
    {
        int width=160,height=160;byte[] data=LetterTransferImages.Pixels(Solid(width,height,(Color)ColorConverter.ConvertFromString(key)));
        for(int cell=0;cell<4;cell++)
        {
            int ox=(cell%2)*80,oy=(cell/2)*80;
            Box(ox+20,oy+28,38,40);Box(ox+20,oy+12,12,8);Box(ox+46,oy+12,12,8); // detached dots of Ё
        }
        if(clipped)Box(0,25,30,30);
        return BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);
        void Box(int x0,int y0,int w,int h){for(int y=y0;y<y0+h;y++)for(int x=x0;x<x0+w;x++){int p=(y*width+x)*4;data[p]=data[p+1]=data[p+2]=255;data[p+3]=255;}}
    }
    private static void Images()
    {
        foreach(string key in new[]{"#FF00FF","#00FF00","#00FFFF","#FFFF00","#0000FF","#FF0000"})
        {
            var cells=LetterTransferImages.Extract(Sheet(key),4,key,CancellationToken.None);Check(cells.All(i=>i!=null),"Failed technical key "+key);
            var pixels=LetterTransferImages.Pixels(cells[0]);Check(cells[0].PixelHeight>=56 && pixels.Where((b,i)=>i%4==3 && b==255).Count()>1600,"Detached dots or white ink removed.");
            Check(pixels.Where((b,i)=>i%4==3 && b==0).Any(),"Background remains opaque.");
        }
        Check(LetterTransferImages.Extract(Sheet("#FF00FF",true),4,"#FF00FF",CancellationToken.None)[0]==null,"Silently accepted clipped letter.");
        var cancel=new CancellationTokenSource();cancel.Cancel();Reject(()=>LetterTransferImages.Extract(Sheet("#FF00FF"),4,"#FF00FF",cancel.Token),"Ignored extraction cancellation.");
        var key2=LetterTransferImages.Key(Solid(30,30,Colors.Magenta));Check(key2!="#FF00FF","Chose key present in artwork.");
    }
    private static void PersistenceAndAi()
    {
        var set=Printable();string directory=LetterTransferStore.Create(_root,set);foreach(var glyph in set.Glyphs)LetterTransferImages.Save(Solid(glyph.Width,glyph.Height,Colors.White),LetterTransferStore.Resolve(directory,glyph.File));
        set.Reference="references/original.png";LetterTransferImages.Save(Sheet("#FF00FF"),LetterTransferStore.Resolve(directory,set.Reference));set.Notes="Грубая ткань, светлый контур";set.Letters[0].Copies=19;LetterTransferStore.Save(directory,set);
        var loaded=LetterTransferStore.Load(directory);Check(loaded.Letters[0].Copies==19 && loaded.Glyphs.Count==3 && loaded.Notes==set.Notes,"Save/load lost settings or variants.");
        Check(File.Exists(Path.Combine(directory,"set.json.bak")),"No recoverable previous save.");
        Reject(()=>LetterTransferStore.Resolve(directory,"../escape.png"),"Path escaped set.");Reject(()=>LetterTransferStore.Resolve(directory,Path.Combine(_root,"outside.png")),"Accepted rooted path.");
        string broken=Path.Combine(_root,"broken");Directory.CreateDirectory(broken);File.WriteAllText(Path.Combine(broken,"set.json"),"{}");Reject(()=>LetterTransferStore.Load(broken),"Empty JSON became a valid set.");
        Check(LetterTransferStore.List(_root).Count==1,"Broken set poisoned library.");
        var job=LetterTransferAi.Create(directory,set,new[]{"Ё","Й","Ъ","Я"},"google/nano-banana-2","4K");string prompt=job.Prompt;
        Check(prompt.Contains("U+0401") && prompt.Contains("U+0419") && prompt.Contains("EXACTLY 4") && prompt.Contains("NO rulers") && prompt.Contains("Грубая ткань"),"Exact Cyrillic/style requirements lost.");
        byte[] original=File.ReadAllBytes(LetterTransferStore.Resolve(directory,set.Reference));string snapshot=LetterTransferStore.Resolve(directory,job.Reference);
        LetterTransferAi.Generate(directory,job,"synthetic-key",null,CancellationToken.None);
        Check(ReplicateWorkerClient.Calls==1 && File.Exists(snapshot) && original.SequenceEqual(File.ReadAllBytes(LetterTransferStore.Resolve(directory,set.Reference))) && !File.Exists(ReplicateWorkerClient.LastUpload),"Worker destroyed original/snapshot or leaked upload.");
        LetterTransferAi.Generate(directory,job,"synthetic-key",null,CancellationToken.None);Check(ReplicateWorkerClient.Calls==1,"Paid duplicate for cached output.");
        Check(((string)ReplicateWorkerClient.Input["prompt"])==prompt && (string)ReplicateWorkerClient.Input["resolution"]=="4K","Worker parameters drifted from consent.");
        job=LetterTransferAi.Create(directory,set,new[]{"А"},"google/nano-banana-pro","2K");ReplicateWorkerClient.Fail=true;Reject(()=>LetterTransferAi.Generate(directory,job,"synthetic-key",null,CancellationToken.None),"Provider failure lost.");ReplicateWorkerClient.Fail=false;
        Check(!File.Exists(ReplicateWorkerClient.LastUpload) && File.Exists(LetterTransferStore.Resolve(directory,job.Reference)),"Failed request deleted saved reference or leaked upload.");
        Check(LetterTransferStore.Load(directory).Jobs.Count==2,"Request not durable before worker call.");
        Reject(()=>LetterTransferAi.Prompt(set,new[]{"A"},"#FF00FF"),"Prompt accepted Latin target.");Reject(()=>LetterTransferAi.Prompt(set,new[]{"А","А"},"#FF00FF"),"Prompt accepted duplicate target.");
    }
    private static void ConsentWindow()
    {
        var window=LetterTransferReviewWindow.Confirmation(null,Sheet("#FF00FF"),new[]{"Ё","Й","Я","Ъ"},"google/nano-banana-2","4K","Synthetic prompt",true);
        try
        {
            var root=(FrameworkElement)window.Content;var size=new Size(900,650);root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();
            var controls=Descendants(root).ToArray();Check(controls.OfType<TextBlock>().Any(t=>t.Text.Contains("Ё, Й, Я, Ъ") && t.Text.Contains("платный запрос")),"Consent omitted targets or paid notice.");
            Check(controls.OfType<Image>().Single().Source!=null && controls.OfType<TextBox>().Single().Text=="Synthetic prompt","Consent did not show exact reference and prompt.");
            Check(controls.OfType<Button>().Any(b=>b.IsCancel) && !controls.OfType<Button>().Any(b=>b.IsDefault),"Paid consent uses unsafe default or lacks escape.");
        }
        finally{window.Close();}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,Flags).GetValue(value);
    private static void Call(object value,string name,params object[] args)=>value.GetType().GetMethod(name,Flags).Invoke(value,args);
    private static void Window()
    {
        var set=Printable();string uiRoot=Path.Combine(_root,"ui");string directory=LetterTransferStore.Create(uiRoot,set);foreach(var glyph in set.Glyphs)LetterTransferImages.Save(Solid(glyph.Width,glyph.Height,Colors.DarkBlue),LetterTransferStore.Resolve(directory,glyph.File));
        int confirms=0,calls=ReplicateWorkerClient.Calls;set.Reference="references/style.png";LetterTransferImages.Save(Sheet("#FF00FF"),LetterTransferStore.Resolve(directory,set.Reference));LetterTransferStore.Save(directory,set);
        var window=new LetterTransferWindow(()=>"synthetic-key",()=>throw new Exception("Capture not requested"),(_,__)=>{},uiRoot,(reference,letters,model,resolution,prompt,retry)=>{confirms++;return false;});
        try
        {
            Call(window,"Generate",true);Check(confirms==1 && ReplicateWorkerClient.Calls==calls && Field<LetterTransferSet>(window,"_set").Jobs.Count==0,"Declined preview still sent or created a job.");
            Check(Field<DataGrid>(window,"_table").Items.Count==33,"UI alphabet missing.");Check(Field<StackPanel>(window,"_pages").Children.Count>0,"No print preview.");
            Field<TextBox>(window,"_height").Text="19,5";Call(window,"Save");Check(LetterTransferStore.Load(directory).HeightMm==19.5,"Comma decimal not saved.");
            var selected=Field<LetterTransferSet>(window,"_set");Check(selected.Letters[0].Copies==11,"Manual quantity overwritten during auto-save.");
            Call(window,"Zoom",2.0);Check(((ScaleTransform)Field<StackPanel>(window,"_pages").LayoutTransform).ScaleX==2,"Preview zoom missing.");
            foreach(var size in new[]{new Size(1320,790),new Size(1000,640)})
            {
                var root=(FrameworkElement)window.Content;root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();
                Check(Field<Grid>(window,"_body").ActualWidth>=960,"Content overflowed desktop width.");
                string render=Environment.GetEnvironmentVariable("VANYA_LETTER_RENDER");
                if(!String.IsNullOrEmpty(render))
                {var image=new RenderTargetBitmap((int)size.Width,(int)size.Height,96,96,PixelFormats.Pbgra32);var visual=new DrawingVisual();using(var context=visual.RenderOpen()){context.DrawRectangle(Brushes.White,null,new Rect(size));}image.Render(visual);image.Render(root);LetterTransferImages.Save(image,Path.Combine(render,"letters-"+(int)size.Width+".png"));}
            }
        }
        finally{window.Close();}
    }
}
