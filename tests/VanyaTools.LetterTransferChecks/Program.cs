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
            Frequencies();WholeAlphabet();FramedAlphabet();SavedAlphabet();AdaptiveGrid();IrregularAlphabet();Layouts();GridAndLabelChecks.Run(_root);Preparation();LargeReference();RequestReference();Images();PersistenceAndAi();InsertionChecks.Run(_root);Window();ConsentWindow();RecognitionChecks.Run(_root);AlternativeAndEdgeChecks.Run(_root);ContentGridChecks.Run(_root);
            Console.WriteLine("Letter transfer checks passed: "+_checks+". Offline checks; no Replicate/Corel requests.");return 0;
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
    private static void WholeAlphabet()
    {
        var set=new LetterTransferSet();foreach(var row in set.Letters)row.Copies=0;
        var passes=LetterTransferRules.MissingPasses(set);Check(passes.Count==1 && passes[0].Length==33 && passes[0].Contains("Ё"),"Whole alphabet was split or dependent on print quantities.");
        set.VariantsPerLetter=2;passes=LetterTransferRules.MissingPasses(set);Check(passes.Count==2 && passes.All(p=>p.Length==33),"Two styles should require two full sheets.");
        string prompt=LetterTransferAi.Prompt(set,passes[0],"#FF00FF");Check(prompt.Contains("EXACTLY 33") && prompt.Contains("6 columns by 6 rows") && prompt.Contains("Cell 33: only «Я»"),"Full alphabet prompt lost ordered Cyrillic/grid.");
        Check(LetterTransferRules.Columns(33)==6 && LetterTransferRules.Aspect(33)=="1:1","Full-sheet geometry is wrong.");
        var data=LetterTransferImages.Pixels(Solid(600,600,Colors.Magenta));
        for(int n=0;n<33;n++)for(int y=20;y<80;y++)for(int x=20;x<80;x++)
        {int p=(((n/6)*100+y)*600+(n%6)*100+x)*4;data[p]=data[p+1]=data[p+2]=(byte)(200+n);data[p+3]=255;}
        var sheet=BitmapSource.Create(600,600,96,96,PixelFormats.Bgra32,null,data,2400);
        var cells=LetterTransferImages.Extract(sheet,33,"#FF00FF",CancellationToken.None);Check(cells.Length==33 && cells.All(i=>i!=null),"Could not extract all 33 glyphs.");
        for(int n=0;n<33;n++){var pixels=LetterTransferImages.Pixels(cells[n]);int center=(cells[n].PixelHeight/2*cells[n].PixelWidth+cells[n].PixelWidth/2)*4;Check(pixels[center]==200+n,"Full-sheet cell assignment changed.");}
        set.Glyphs.Add(new LetterTransferGlyph{Letter="А",File="glyphs/a.png",Width=20,Height=20});
        passes=LetterTransferRules.MissingPasses(set);Check(passes.Count==2 && passes[0].Length==32 && !passes[0].Contains("А") && passes[1].Length==33,"Missing-only generation repeats completed variants or omits second style.");
    }
    private static void FramedAlphabet()
    {
        const int size=720,cell=120;var data=LetterTransferImages.Pixels(Solid(size,size,Colors.Lime));
        void Paint(int x,int y,byte ink){int p=(y*size+x)*4;data[p]=data[p+1]=data[p+2]=ink;data[p+3]=255;}
        void Divider(int x,int y){Paint(x,y,8);data[(y*size+x)*4+1]=28;}
        for(int n=0;n<33;n++)for(int y=20;y<100;y++)for(int x=20;x<100;x++)Paint(n%6*cell+x,n/6*cell+y,240);
        // A generated grid wanders between adjacent pixel rows and has an outer
        // frame. No individual scanline covers 98.5% of the sheet.
        for(int n=0;n<=6;n++)for(int along=0;along<size;along++)
        {
            int cut=n*cell+(along/31%3-1);
            for(int d=-1;d<=1;d++)
            {
                int at=Math.Max(0,Math.Min(size-1,cut+d));Divider(at,along);Divider(along,at);
            }
        }
        BitmapSource Sheet()=>BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,data,size*4);
        var cells=LetterTransferImages.Extract(Sheet(),33,"#00FF00",CancellationToken.None);
        Check(cells.All(i=>i!=null),"Framed, non-straight grid rejected intact alphabet.");
        foreach(var image in cells)
        {
            var bytes=LetterTransferImages.Pixels(image);
            Check(bytes.Where((b,i)=>i%4==3).All(a=>a==0 || a==255),"Framed alphabet retained partial alpha.");
            Check(Enumerable.Range(0,bytes.Length/4).All(p=>bytes[p*4+3]==0 || bytes[p*4]>200),"Grid fragments survived in a glyph.");
        }
        // Removing a divider must not hide a real glyph crossing it.
        for(int y=45;y<70;y++)for(int x=95;x<130;x++)Paint(x,y,240);
        cells=LetterTransferImages.Extract(Sheet(),33,"#00FF00",CancellationToken.None);
        Check(cells[0]==null && cells[1]==null && cells.Skip(2).All(i=>i!=null),"Separator cleanup hid a genuinely clipped glyph; accepted: "+String.Join(",",cells.Select((c,i)=>c==null?"-":i.ToString())));
        for(int y=45;y<70;y++)for(int x=95;x<130;x++)Paint(x,y,8);
        cells=LetterTransferImages.Extract(Sheet(),33,"#00FF00",CancellationToken.None);
        Check(cells[0]==null && cells[1]==null && cells.Skip(2).All(i=>i!=null),"Separator cleanup hid dark ink crossing the divider.");
    }
    private static void SavedAlphabet()
    {
        string path=Environment.GetEnvironmentVariable("VANYA_LETTER_ALPHABET_SHEET");if(String.IsNullOrEmpty(path))return;
        var sheet=LetterTransferImages.Load(path);var original=LetterTransferImages.Pixels(sheet);
        var cells=LetterTransferImages.Extract(sheet,33,"#00FF00",CancellationToken.None);
        Check(cells.All(i=>i!=null),"Saved 4K response rejected one or more of 33 intact letters.");
        var adaptive=LetterTransferSheet.Read(sheet,LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray(),"#00FF00",130,CancellationToken.None);
        Check(adaptive.Cells.Length==33 && adaptive.Cells.All(c=>c.Image!=null) && !adaptive.LayoutChanged,"Adaptive extraction regressed the previous golden alphabet: count="+adaptive.Cells.Length+", ready="+adaptive.Cells.Count(c=>c.Image!=null)+", changed="+adaptive.LayoutChanged);
        string export=Environment.GetEnvironmentVariable("VANYA_LETTER_ALPHABET_EXPORT");
        for(int n=0;n<cells.Length;n++)
        {
            var bytes=LetterTransferImages.Pixels(cells[n]);
            var inset=LetterTransferEdges.Process(cells[n],1,130,true,CancellationToken.None);
            if(n==6 || n==10)Check(LetterTransferImages.BodyHeight(n==6?"Ё":"Й",inset)>0,"Observed golden accent was lost during inset.");
            Check(bytes.Where((b,i)=>i%4==3).All(a=>a==0 || a==255),"Saved alphabet retained partial alpha.");
            Check(Enumerable.Range(0,bytes.Length/4).All(p=>bytes[p*4+3]!=0 || (bytes[p*4]==0 && bytes[p*4+1]==0 && bytes[p*4+2]==0)),"Saved alphabet has hidden matte RGB.");
            Check(Enumerable.Range(0,bytes.Length/4).All(p=>bytes[p*4+3]==0 || bytes[p*4+1]-Math.Max(bytes[p*4],bytes[p*4+2])<50),"Saved alphabet retained a green matte rim.");
            if(!String.IsNullOrEmpty(export))
            {
                double dpi=cells[n].PixelHeight*25.4/20;
                var physical=BitmapSource.Create(cells[n].PixelWidth,cells[n].PixelHeight,dpi,dpi,PixelFormats.Bgra32,null,bytes,cells[n].PixelWidth*4);
                LetterTransferImages.Save(physical,Path.Combine(export,LetterTransferRules.Alphabet[n]+".png"));
                Check(Math.Abs(LetterTransferImages.Load(Path.Combine(export,LetterTransferRules.Alphabet[n]+".png")).DpiY-dpi)<.1,"Export lost the 20 mm physical height.");
            }
        }
        Check(original.SequenceEqual(LetterTransferImages.Pixels(sheet)),"Saved source sheet was mutated.");
    }
    private static void AdaptiveGrid()
    {
        const int width=360,height=400;var data=LetterTransferImages.Pixels(Solid(width,height,Color.FromRgb(252,203,250)));
        void Pixel(int x,int y,Color color){int p=(y*width+x)*4;data[p]=color.B;data[p+1]=color.G;data[p+2]=color.R;data[p+3]=255;}
        int candidate=0;
        for(int row=0;row<4;row++)
        {
            int columns=row<2?3:2,cw=width/columns;
            for(int col=0;col<columns;col++,candidate++)
            {
                for(int y=row*100+20;y<row*100+80;y++)for(int x=col*cw+20;x<(col+1)*cw-20;x++)Pixel(x,y,Colors.White);
                // The requested key hue is legitimate ink when the model has
                // replaced the requested background with pale pink.
                if(candidate==3)for(int y=row*100+32;y<row*100+68;y++)for(int x=col*cw+32;x<(col+1)*cw-32;x++)Pixel(x,y,Colors.Magenta);
            }
            for(int x=0;x<width;x++)for(int d=0;d<2;d++){Pixel(x,row*100+d,Colors.Magenta);Pixel(x,(row+1)*100-1-d,Colors.Magenta);}
            for(int col=0;col<=columns;col++)for(int y=row*100;y<(row+1)*100;y++)for(int d=0;d<2;d++)Pixel(Math.Min(width-1,col*cw+d),y,Colors.Magenta);
        }
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);source.Freeze();
        var result=LetterTransferSheet.Read(source,new[]{"А","Б","В","Г","Д","Е","Ё","Ж"},"#FF00FF",130,CancellationToken.None);
        Check(result.Cells.Length==10 && result.Cells.All(c=>c.Image!=null && c.Safe),"Mixed-column grid discarded intact candidates.");
        Check(result.LayoutChanged && result.Cells.All(c=>c.Letter==null) && result.Notice.Contains("10"),"Mismatched grid silently mislabels candidates.");
        var magenta=LetterTransferImages.Pixels(result.Cells[3].Image);
        Check(Enumerable.Range(0,magenta.Length/4).Any(p=>magenta[p*4+3]>0 && magenta[p*4]>240 && magenta[p*4+2]>240 && magenta[p*4+1]<20),"Observed matte cleanup removed real magenta ink.");
        Check(data.SequenceEqual(LetterTransferImages.Pixels(source)),"Grid detection mutated original sheet.");
        var window=LetterTransferReviewWindow.ReviewDialog(null,source,source,result,new List<LetterTransferApproved>());
        try
        {
            var root=(FrameworkElement)window.Content;var size=new Size(950,760);root.Measure(size);root.Arrange(new Rect(size));root.UpdateLayout();
            var controls=Descendants(root).ToArray();var checks=controls.OfType<CheckBox>().Where(c=>(string)c.Tag=="accept").ToArray();var choices=controls.OfType<ComboBox>().ToArray();
            Check(controls.OfType<Image>().Count(i=>i.Source!=null)>=10,"Review hides rejected or unassigned previews.");
            Check(checks.All(c=>!c.IsEnabled) && choices.All(c=>(string)c.SelectedItem=="—"),"Unassigned candidates can be accepted under incorrect labels.");
            choices[0].SelectedItem="А";Check(checks[0].IsEnabled,"Manual assignment does not allow acceptance.");
            controls.OfType<Button>().Single(b=>(string)b.Content=="Отметить все целые").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(checks[0].IsChecked==true && checks.Skip(1).All(c=>c.IsChecked!=true),"Bulk acceptance selected unnamed candidates.");
            var skips=controls.OfType<CheckBox>().Where(c=>(string)c.Tag=="skip").ToArray();skips[1].IsChecked=true;skips[3].IsChecked=true;
            controls.OfType<Button>().Single(b=>(string)b.Content=="Назначить по порядку").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var active=choices.Where((c,i)=>i!=1 && i!=3).Select(c=>(string)c.SelectedItem).ToArray();
            Check(active.SequenceEqual(result.Requested) && !checks[1].IsEnabled && !checks[3].IsEnabled,"Order assignment ignored excluded duplicates.");
            Check(checks.Skip(1).All(c=>c.IsChecked!=true),"Order assignment accepted candidates without review.");
        }
        finally{window.Close();}
        var uniform=LetterTransferSheet.Read(Sheet("#00FF00"),new[]{"А","Б","В","Г"},"#00FF00",130,CancellationToken.None);
        Check(uniform.Cells.Length==4 && uniform.Cells.All(c=>c.Image!=null) && !uniform.LayoutChanged,"Regular sheet fallback changed existing extraction.");
    }
    private static void IrregularAlphabet()
    {
        string path=Environment.GetEnvironmentVariable("VANYA_LETTER_IRREGULAR_SHEET");if(String.IsNullOrEmpty(path))return;
        var sheet=LetterTransferImages.Load(path);var before=LetterTransferImages.Pixels(sheet);
        var result=LetterTransferSheet.Read(sheet,LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray(),"#FF00FF",130,CancellationToken.None);
        Check(result.Cells.Length==35 && result.Cells.All(c=>c.Image!=null),"Irregular source did not yield all 35 candidates: "+result.Cells.Length);
        Check(result.LayoutChanged && result.Cells.All(c=>c.Letter==null),"Wrong grid was silently assigned ordered labels.");
        string export=Environment.GetEnvironmentVariable("VANYA_LETTER_IRREGULAR_EXPORT");
        for(int n=0;n<result.Cells.Length;n++)
        {
            var cell=result.Cells[n];var bytes=LetterTransferImages.Pixels(cell.Image);
            var inset=LetterTransferEdges.Process(cell.Image,1,130,true,CancellationToken.None);var cleaned=LetterTransferImages.Pixels(inset);
            Check(cleaned.Where((b,i)=>i%4==3 && b>0).Count()>=bytes.Where((b,i)=>i%4==3 && b>0).Count()*.8,"Edge inset removed too much of an observed collage glyph.");
            Console.WriteLine("Candidate "+n+": "+cell.Image.PixelWidth+"x"+cell.Image.PixelHeight+", safe="+cell.Safe+", "+cell.Warning);
            Check(bytes.Where((b,i)=>i%4==3).All(a=>a==0 || a==255),"Irregular glyph retained partial alpha.");
            if(!String.IsNullOrEmpty(export))LetterTransferImages.Save(cell.Image,Path.Combine(export,n.ToString("00")+".png"));
        }
        Check(before.SequenceEqual(LetterTransferImages.Pixels(sheet)),"Irregular source was mutated.");
        string recognitionExport=Environment.GetEnvironmentVariable("VANYA_LETTER_RECOGNITION_PREVIEW");
        if(!String.IsNullOrEmpty(recognitionExport))
        {
            var plan=LetterTransferRecognition.Prepare(recognitionExport,new LetterTransferJob{Output="requests/observed-sheet.png"},result);
            var info=new System.Collections.Generic.Dictionary<string,object>{{"model",LetterTransferRecognition.Model},{"count",plan.Count},{"preview",plan.PreviewFile},{"output",plan.Output},{"prefix",plan.Prefix},{"fingerprint",plan.Fingerprint},{"prompt",plan.Prompt}};
            File.WriteAllText(Path.Combine(recognitionExport,"plan.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(info),new System.Text.UTF8Encoding(false));
            Console.WriteLine("Recognition preview: "+plan.PreviewFile+" · "+new FileInfo(plan.PreviewFile).Length+" bytes.");
        }
        string labels=Environment.GetEnvironmentVariable("VANYA_LETTER_IRREGULAR_LABELS");
        if(!String.IsNullOrEmpty(labels) && !String.IsNullOrEmpty(export))
        {
            Check(labels.Length==result.Cells.Length && labels.Distinct().Count()==33,"Reviewed recovery labels do not cover the alphabet.");
            var recovered=new LetterTransferSet{Name="Коллажные буквы · восстановленный набор",HeightMm=20,SheetWidthMm=300};
            string directory=Path.Combine(export,"recovered-set");
            for(int n=0;n<labels.Length;n++)LetterTransferImages.Add(directory,recovered,labels[n].ToString(),result.Cells[n].Image,"Разбор сохранённого листа · проверенный порядок");
            string reference=Environment.GetEnvironmentVariable("VANYA_LETTER_IRREGULAR_REFERENCE");
            if(!String.IsNullOrEmpty(reference)){recovered.Reference="reference.png";File.Copy(reference,LetterTransferStore.Resolve(directory,recovered.Reference),true);}
            LetterTransferStore.Save(directory,recovered);
            Check(LetterTransferStore.Load(directory).Glyphs.Count==35,"Recovery set lost extracted variants.");
        }
    }
    internal static LetterTransferSet Printable()
    {
        var set=new LetterTransferSet{AutoLength=false,SheetWidthMm=64,SheetHeightMm=55,HeightMm=20,GapMm=3,MarginMm=5};foreach(var row in set.Letters)row.Copies=0;
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
    private static void Preparation()
    {
        var set=Printable();set.AutoLength=true;var plan=LetterTransferLayout.Build(set,false);
        Check(plan.All(p=>p.Page==0),"Roll mode still breaks at fixed page height.");
        Check(LetterTransferLayout.PageHeight(set,plan)==plan.Max(p=>p.Y+p.Height)+set.MarginMm,"Roll length is not tight to contents.");
        var data=LetterTransferImages.Pixels(Solid(4,4,Colors.Red));data[3]=0;data[7]=129;data[11]=130;data[15]=254;
        var sample=BitmapSource.Create(4,4,96,96,PixelFormats.Bgra32,null,data,16);
        var batch=LetterTransferImages.PrepareBatch(new[]{sample,null,sample},130,CancellationToken.None);
        foreach(var image in batch.Where(i=>i!=null))
        {
            var pixels=LetterTransferImages.Pixels(image);Check(pixels.Where((b,i)=>i%4==3).All(a=>a==0 || a==255),"Prepared batch has semitransparent pixels.");
            Check(pixels[3]==0 && pixels[7]==0 && pixels[11]==255 && pixels[15]==255,"Threshold differs from correction tool.");
        }
        Check(batch[1]==null,"Missing AI cell became an asset.");
        Check(LetterTransferImages.Pixels(sample)[7]==129,"Batch alpha cleanup mutated the source image.");
        set.AlphaThreshold=0;Reject(()=>LetterTransferRules.Validate(set),"Invalid alpha threshold accepted.");set.AlphaThreshold=130;
        var reference=LetterTransferImages.Compose(new[]{Solid(100,180,Colors.Red),Solid(180,100,Colors.Blue),Solid(90,90,Colors.Green)});
        Check(reference.PixelWidth<=2048 && reference.PixelHeight<=2048,"Multi-reference collage exceeds upload size.");
        string directory=LetterTransferStore.Create(Path.Combine(_root,"preparation"),set);var glyph=LetterTransferImages.Add(directory,set,"Ё",sample,"Synthetic batch");
        var saved=LetterTransferImages.Load(LetterTransferStore.Resolve(directory,glyph.File));Check(Math.Abs(saved.PixelHeight*25.4/saved.DpiY-LetterTransferLayout.GlyphHeight(set,glyph))<.12,"Separate PNG physical height is wrong.");
        Check(LetterTransferImages.Pixels(saved).Where((b,i)=>i%4==3).All(a=>a==0 || a==255),"Saved PNG retained semitransparency.");
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
    private static void LargeReference()
    {
        string path=Path.Combine(_root,"large-reference.png");var original=Solid(6000,5000,Colors.DarkRed);LetterTransferImages.Save(original,path);
        var before=new FileInfo(path).Length;var preview=LetterTransferImages.LoadReference(path);
        Check(preview.PixelWidth==2048 && Math.Abs(preview.PixelWidth/(double)preview.PixelHeight-1.2)<.001,"Large reference was not decoded to a proportional preview.");
        Check(preview.IsFrozen && new FileInfo(path).Length==before,"Reference decoding changed source or retained a live decoder.");
        Check(LetterTransferImages.LoadReference(path).PixelHeight==preview.PixelHeight,"Preview kept source file locked.");
        string small=Path.Combine(_root,"small-reference.png");LetterTransferImages.Save(Solid(100,75,Colors.Blue),small);
        Check(LetterTransferImages.LoadReference(small).PixelWidth==100,"Small reference was enlarged.");
    }
    private static void RequestReference()
    {
        const int width=1085,height=1305;var data=new byte[width*height*4];var random=new Random(974);
        random.NextBytes(data);for(int p=3;p<data.Length;p+=4)data[p]=255;
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);source.Freeze();
        var preview=LetterTransferImages.RequestReference(source);
        string path=Path.Combine(_root,"request-reference.png");LetterTransferImages.Save(preview,path);
        Check(new FileInfo(path).Length<=LetterTransferImages.RequestReferenceByteLimit,"Large style reference still requires remote file-host resolution.");
        Check(preview.PixelWidth<width && preview.PixelHeight<height && Math.Abs(preview.PixelWidth/(double)preview.PixelHeight-width/(double)height)<.005,"Compact style preview changed aspect or was not reduced.");
        Check(data.SequenceEqual(LetterTransferImages.Pixels(source)),"Compact style preview modified original pixels.");
        var small=Solid(30,40,Colors.Transparent);Check(ReferenceEquals(small,LetterTransferImages.RequestReference(small)),"Small transparent preview was changed.");
        var set=Printable();set.Reference="reference.png";string directory=LetterTransferStore.Create(Path.Combine(_root,"compact-reference"),set);
        LetterTransferImages.Save(source,LetterTransferStore.Resolve(directory,set.Reference));
        byte[] before=File.ReadAllBytes(LetterTransferStore.Resolve(directory,set.Reference));
        var job=LetterTransferAi.Create(directory,set,new[]{"А","Ё"},"google/nano-banana-2","2K",preview);
        var saved=LetterTransferImages.Load(LetterTransferStore.Resolve(directory,job.Reference));
        Check(LetterTransferImages.Pixels(saved).SequenceEqual(LetterTransferImages.Pixels(preview)),"Saved request differs from approved compact preview.");
        Check(before.SequenceEqual(File.ReadAllBytes(LetterTransferStore.Resolve(directory,set.Reference))),"Saved full reference changed during compact request preparation.");
        string observed=Environment.GetEnvironmentVariable("VANYA_LETTER_LARGE_STYLE_REFERENCE");
        if(!String.IsNullOrEmpty(observed))
        {
            var actual=LetterTransferImages.Load(observed);var pixels=LetterTransferImages.Pixels(actual);var ready=LetterTransferImages.RequestReference(actual);
            string export=Environment.GetEnvironmentVariable("VANYA_LETTER_LARGE_STYLE_EXPORT");
            string target=String.IsNullOrEmpty(export)?Path.Combine(_root,"observed-compact-preview.png"):export;
            LetterTransferImages.Save(ready,target);
            Check(new FileInfo(target).Length<=LetterTransferImages.RequestReferenceByteLimit,"Observed reference still exceeds inline budget.");
            Check(pixels.SequenceEqual(LetterTransferImages.Pixels(actual)),"Observed reference was mutated.");
            Console.WriteLine("Prepared style preview: "+ready.PixelWidth+"x"+ready.PixelHeight+", "+new FileInfo(target).Length+" bytes.");
        }
    }
    private static void Images()
    {
        foreach(string key in new[]{"#FF00FF","#00FF00","#00FFFF","#FFFF00","#0000FF","#FF0000"})
        {
            var cells=LetterTransferImages.Extract(Sheet(key),4,key,CancellationToken.None);Check(cells.All(i=>i!=null),"Failed technical key "+key);
            var pixels=LetterTransferImages.Pixels(cells[0]);Check(cells[0].PixelHeight>=56 && pixels.Where((b,i)=>i%4==3 && b==255).Count()>1600,"Detached dots or white ink removed.");
            Check(pixels.Where((b,i)=>i%4==3 && b==0).Any(),"Background remains opaque.");
        }
        var shaded=LetterTransferImages.Pixels(Sheet("#00FF00"));
        for(int y=0;y<160;y++)for(int x=0;x<160;x++)
        {
            int p=(y*160+x)*4;
            if(shaded[p]==0 && shaded[p+1]==255 && shaded[p+2]==0){shaded[p+1]=(byte)(172+x/16);shaded[p]=20;}
            if(x==79 || x==80 || y==79 || y==80)shaded[p]=shaded[p+1]=shaded[p+2]=0;
        }
        var shadedSheet=BitmapSource.Create(160,160,96,96,PixelFormats.Bgra32,null,shaded,640);
        var shadedCells=LetterTransferImages.PrepareBatch(LetterTransferImages.Extract(shadedSheet,4,"#00FF00",CancellationToken.None),130,CancellationToken.None);
        Check(shadedCells.All(c=>c!=null),"Shaded key or separator lines discarded whole letters.");
        foreach(var cell in shadedCells)
        {var bytes=LetterTransferImages.Pixels(cell);Check(bytes.Where((b,i)=>i%4==3).All(a=>a==0 || a==255),"Prepared shaded-key glyph has partial alpha.");}
        string paid=Environment.GetEnvironmentVariable("VANYA_LETTER_PAID_SHEET");
        if(!String.IsNullOrEmpty(paid))
        {
            var cells=LetterTransferImages.PrepareBatch(LetterTransferImages.Extract(LetterTransferImages.Load(paid),4,"#00FF00",CancellationToken.None),130,CancellationToken.None);
            Check(cells.All(c=>c!=null),"Paid response did not yield four separate glyphs.");
            var preparedSet=new LetterTransferSet{Name="Проверочный набор · золотые А–Г",Notes="В золотом цвете, сохранить объёмную фактуру и форму букв референса",HeightMm=20};
            foreach(var row in preparedSet.Letters)row.Copies=0;
            string preparedRoot=Path.Combine(Path.GetDirectoryName(paid),"prepared-set");
            for(int i=0;i<cells.Length;i++)
            {
                byte[] data=LetterTransferImages.Pixels(cells[i]);Check(data.Where((b,p)=>p%4==3).All(a=>a==0 || a==255),"Real glyph retained partial alpha.");
                LetterTransferImages.Save(cells[i],Path.Combine(Path.GetDirectoryName(paid),"prepared-"+i+".png"));
                string letter=LetterTransferRules.Alphabet[i].ToString();preparedSet.Letters[i].Copies=1;
                var savedGlyph=LetterTransferImages.Add(preparedRoot,preparedSet,letter,cells[i],"Проверочный запрос Nano Banana 2");
                var savedImage=LetterTransferImages.Load(LetterTransferStore.Resolve(preparedRoot,savedGlyph.File));
                Check(Math.Abs(savedImage.PixelHeight*25.4/savedImage.DpiY-20)<.02,"Real PNG did not retain selected physical height.");
                Check(Enumerable.Range(0,data.Length/4).All(p=>data[p*4+3]!=0 || (data[p*4]==0 && data[p*4+1]==0 && data[p*4+2]==0)),"Hidden technical RGB remains in transparent pixels.");
            }
            string reference=Path.Combine(Path.GetDirectoryName(paid),"reference-upload.png");
            if(File.Exists(reference)){preparedSet.Reference="reference.png";File.Copy(reference,LetterTransferStore.Resolve(preparedRoot,preparedSet.Reference),true);}
            LetterTransferStore.Save(preparedRoot,preparedSet);
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
        int confirms=0,calls=ReplicateWorkerClient.Calls,referenceCaptures=0,artworkCaptures=0,lastCount=0;string lastResolution=null;set.Reference="references/style.png";LetterTransferImages.Save(Sheet("#FF00FF"),LetterTransferStore.Resolve(directory,set.Reference));LetterTransferStore.Save(directory,set);
        var window=new LetterTransferWindow(()=>"synthetic-key",()=>{artworkCaptures++;throw new Exception("Full artwork capture must not be used for a reference");},(_,__)=>{},uiRoot,(reference,letters,model,resolution,prompt,retry)=>{confirms++;lastCount=letters.Length;lastResolution=resolution;return false;},()=>{referenceCaptures++;return LetterTransferStore.Resolve(directory,set.Reference);});
        var previousContext=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            Call(window,"Reference",true);Check(referenceCaptures==1 && artworkCaptures==0,"Reference path used the full artwork capture.");
            Call(window,"Generate",false);Check(confirms==1 && lastCount==31 && lastResolution=="4K" && ReplicateWorkerClient.Calls==calls,"Whole-alphabet button did not prepare one 4K missing-letter sheet.");
            Call(window,"Generate",true);Check(confirms==2 && ReplicateWorkerClient.Calls==calls && Field<LetterTransferSet>(window,"_set").Jobs.Count==0,"Declined preview still sent or created a job.");
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
            var uncertain=LetterTransferAi.Create(directory,selected,new[]{"Ё"},"google/nano-banana-2","2K");
            File.WriteAllText(LetterTransferStore.Resolve(directory,uncertain.Output)+".submitted","sent");
            Check(LetterTransferAi.UncertainSubmission(directory,uncertain),"Missing prediction ID is not classified as uncertain.");
            int consentBefore=confirms,recoveriesBefore=ReplicateWorkerClient.Recoveries;Call(window,"Retry");
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(Field<CancellationTokenSource>(window,"_request")!=null && DateTime.UtcNow<deadline)
            {var frame=new System.Windows.Threading.DispatcherFrame();System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background,new Action(()=>frame.Continue=false));System.Windows.Threading.Dispatcher.PushFrame(frame);System.Threading.Thread.Sleep(10);}
            Check(confirms==consentBefore && ReplicateWorkerClient.Recoveries==recoveriesBefore+1 && ReplicateWorkerClient.Calls==calls && Field<TextBlock>(window,"_message").Text.Contains("повтор заблокирован"),"Unknown submission tried another upload or requested a misleading repeat consent.");
        }
        finally{window.Close();SynchronizationContext.SetSynchronizationContext(previousContext);}
    }
}
