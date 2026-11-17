using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class GreenMaterialChecks
{
    internal static void Run(string root)
    {
        Check(Colors.Lime,"#00FF00",new[]{Color.FromRgb(8,155,9),Color.FromRgb(20,228,25),Color.FromRgb(60,205,20)});
        Check(Colors.Magenta,"#FF00FF",new[]{Color.FromRgb(155,8,154),Color.FromRgb(228,20,230),Color.FromRgb(205,20,195)});
        TintedCounter();ReflectedCounter();Recovery(root);Observed();
    }
    private static void TintedCounter()
    {
        const int w=160,h=200;var pixels=LetterTransferImages.Pixels(Program.Solid(w,h,Colors.Lime));
        void Paint(int x,int y,Color color){int p=(y*w+x)*4;pixels[p]=color.B;pixels[p+1]=color.G;pixels[p+2]=color.R;pixels[p+3]=255;}
        for(int y=20;y<180;y++)for(int x=20;x<140;x++)Paint(x,y,Colors.Navy);
        // Tinted technical matte around a real flat counter must not become
        // protected artwork merely because it differs by 20–35 RGB levels.
        for(int y=60;y<130;y++)for(int x=60;x<110;x++)Paint(x,y,Color.FromRgb(23,251,10));
        for(int y=75;y<115;y++)for(int x=70;x<100;x++)Paint(x,y,Colors.Lime);
        for(int y=30;y<80;y++)for(int x=140;x<146;x++)Paint(x,y,Color.FromRgb(95,255,45));
        var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,pixels,w*4);image.Freeze();
        var old=LetterTransferSheet.ReadBeforeSpill(image,new[]{"А"},"#00FF00",130,CancellationToken.None).Cells.Single().Image;
        var clean=LetterTransferSheet.Read(image,new[]{"А"},"#00FF00",130,CancellationToken.None).Cells.Single().Image;
        Program.Check(old.PixelWidth>120 && LetterTransferImages.Pixels(old).Where((v,i)=>i%4==3 && v>0).Count()>120*160-50*70,"Tinted counter fixture did not reproduce protected technical spill.");
        Program.Check(clean.PixelWidth==120 && clean.PixelHeight==160 && LetterTransferImages.Pixels(clean).Where((v,i)=>i%4==3 && v>0).Count()==120*160-50*70,"Tinted technical counter or thick bright fringe survived cleanup.");
    }
    private static void ReflectedCounter()
    {
        const int w=160,h=200;var pixels=LetterTransferImages.Pixels(Program.Solid(w,h,Colors.Lime));
        void Paint(int x,int y,byte r,byte g,byte b){int p=(y*w+x)*4;pixels[p]=b;pixels[p+1]=g;pixels[p+2]=r;pixels[p+3]=255;}
        for(int y=20;y<180;y++)for(int x=20;x<140;x++)Paint(x,y,100,100,100);
        for(int y=60;y<130;y++)for(int x=60;x<110;x++)Paint(x,y,30,208,30);
        for(int y=62;y<128;y++)for(int x=62;x<108;x++)Paint(x,y,0,255,0);
        var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,pixels,w*4);image.Freeze();
        var old=LetterTransferSheet.ReadBeforeSpill(image,new[]{"А"},"#00FF00",130,CancellationToken.None).Cells.Single().Image;
        var clean=LetterTransferSheet.Read(image,new[]{"А"},"#00FF00",130,CancellationToken.None).Cells.Single().Image;
        int visible=120*160-50*70;
        Program.Check(LetterTransferImages.Pixels(old).Where((v,i)=>i%4==3 && v>0).Count()>visible,"Reflected counter did not reproduce the protected mixed-color edge.");
        Program.Check(LetterTransferImages.Pixels(clean).Where((v,i)=>i%4==3 && v>0).Count()==visible,"Reflected technical hue survived against a neutral foreground core.");
    }
    private static void Recovery(string root)
    {
        var set=new LetterTransferSet{Reference="reference.png",VariantsPerLetter=4};set.Letters.Single(r=>r.Letter=="Б").Copies=9;
        string directory=LetterTransferStore.Create(Path.Combine(root,"green-recovery"),set);var image=Sample();LetterTransferImages.Save(image,LetterTransferStore.Resolve(directory,set.Reference));
        var job=LetterTransferAi.Create(directory,set,new[]{"Б"},LetterTransferModels.Nano2,"2K");job.Key="#00FF00";job.Reviewed=true;LetterTransferImages.Save(image,LetterTransferStore.Resolve(directory,job.Output));
        var legacy=LetterTransferSheet.ReadLegacy(image,job.Letters,job.Key,130,CancellationToken.None);var current=LetterTransferSheet.Read(image,job.Letters,job.Key,130,CancellationToken.None);
        var damaged=LetterTransferImages.Add(directory,set,"Б",legacy.Cells.Single().Image,"ИИ · "+job.Id.Substring(0,8));var manual=LetterTransferImages.Add(directory,set,"А",Program.Solid(40,40,Colors.Gold),"Импорт вручную");LetterTransferStore.Save(directory,set);
        string previousSource=LetterTransferStore.Resolve(directory,damaged.OriginalFile);var previousBytes=File.ReadAllBytes(previousSource);var ids=set.Glyphs.Select(g=>g.Id).ToArray();int calls=ReplicateWorkerClient.Calls;
        string expected=LetterTransferImages.Fingerprint(current.Cells.Single().Image);
        Program.Check(LetterTransferImages.Fingerprint(LetterTransferImages.Load(previousSource))!=expected,"Recovery fixture did not reproduce the damaged legacy source.");
        Program.Check(LetterTransferSourceRecovery.Rebuild(directory,set,CancellationToken.None)==1,"Recovery did not match the legacy source to exactly one saved AI cell.");
        var fixedGlyph=set.Glyphs.Single(g=>g.Id==damaged.Id);
        Program.Check(LetterTransferImages.Fingerprint(LetterTransferImages.Load(LetterTransferStore.Resolve(directory,fixedGlyph.OriginalFile)))==expected,"Recovery reused the already damaged PNG instead of the saved AI sheet.");
        Program.Check(set.Glyphs.Select(g=>g.Id).SequenceEqual(ids) && set.Glyphs.Single(g=>g.Id==manual.Id).File==manual.File && set.VariantsPerLetter==4 && set.Letters.Single(r=>r.Letter=="Б").Copies==9 && job.Reviewed,"Recovery changed variants, labels, quantities or a manually imported glyph.");
        Program.Check(fixedGlyph.SourceJobId==job.Id && fixedGlyph.SourceCell.SequenceEqual(new[]{0,0,140,180}) && previousBytes.SequenceEqual(File.ReadAllBytes(previousSource)) && ReplicateWorkerClient.Calls==calls,"Recovery lost source coordinates, overwrote an old PNG or called the provider.");
        string once=LetterTransferImages.Fingerprint(LetterTransferImages.Load(LetterTransferStore.Resolve(directory,fixedGlyph.File)));
        Program.Check(LetterTransferSourceRecovery.Rebuild(directory,set,CancellationToken.None)==1 && once==LetterTransferImages.Fingerprint(LetterTransferImages.Load(LetterTransferStore.Resolve(directory,set.Glyphs.Single(g=>g.Id==damaged.Id).File))),"Repeated source recovery accumulated changes.");
        var saved=File.ReadAllBytes(Path.Combine(directory,"set.json"));var list=set.Glyphs;int count=Directory.GetFiles(Path.Combine(directory,"glyphs")).Length;bool failed=false;
        try{LetterTransferSourceRecovery.Rebuild(directory,set,CancellationToken.None,(_,__)=>{throw new IOException("Synthetic recovery save failure");});}catch(IOException){failed=true;}
        Program.Check(failed && ReferenceEquals(list,set.Glyphs) && saved.SequenceEqual(File.ReadAllBytes(Path.Combine(directory,"set.json"))) && count==Directory.GetFiles(Path.Combine(directory,"glyphs")).Length,"Failed source recovery partially replaced the set or left output files.");
    }
    private static BitmapSource Sample()
    {
        const int width=140,height=180;var data=LetterTransferImages.Pixels(Program.Solid(width,height,Colors.Lime));
        for(int y=20;y<160;y++)for(int x=20;x<120;x++)
        {
            Color color=x<28 || x>=112 || y<28 || y>=152?Colors.Navy:(x/3+y/3)%2==0?Color.FromRgb(8,155,9):Color.FromRgb(20,228,25);
            if(x>=60 && x<100 && y>=60 && y<105)color=Colors.Lime;
            int p=(y*width+x)*4;data[p]=color.B;data[p+1]=color.G;data[p+2]=color.R;data[p+3]=255;
        }
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);image.Freeze();return image;
    }
    private static void Observed()
    {
        string path=Environment.GetEnvironmentVariable("VANYA_LETTER_GREEN_SHEET");if(String.IsNullOrEmpty(path))return;
        var image=LetterTransferImages.Load(path);var letters=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();
        var old=LetterTransferSheet.ReadLegacy(image,letters,"#00FF00",130,CancellationToken.None);var current=LetterTransferSheet.Read(image,letters,"#00FF00",130,CancellationToken.None);
        Program.Check(current.Cells.Length==33 && current.Cells.All(c=>c.Safe),"Observed green alphabet lost cells or retained the outside matte.");
        var before=LetterTransferImages.Pixels(old.Cells[11].Image);var after=LetterTransferImages.Pixels(current.Cells[11].Image);
        Program.Check(Enumerable.Range(0,after.Length/4).Count(p=>after[p*4+3]>0)>Enumerable.Range(0,before.Length/4).Count(p=>before[p*4+3]>0)+1000,"Observed green К did not recover the erased fabric gaps.");
    }
    private static void Check(Color backdrop,string key,Color[] material)
    {
        int width=160,height=200;var data=LetterTransferImages.Pixels(Program.Solid(width,height,backdrop));int[] counts=new int[material.Length];
        void Paint(int x,int y,Color color){int p=(y*width+x)*4;data[p]=color.B;data[p+1]=color.G;data[p+2]=color.R;data[p+3]=255;}
        for(int y=20;y<180;y++)for(int x=20;x<140;x++)Paint(x,y,Colors.Navy);
        for(int y=28;y<172;y++)for(int x=28;x<132;x++)
        {
            if(x>=65 && x<110 && (y>=50 && y<80 || y>=115 && y<150)){Paint(x,y,backdrop);continue;}
            int n=(x/3+y/3)%material.Length;Paint(x,y,material[n]);counts[n]++;
        }
        Color spill=key=="#00FF00"?Color.FromRgb(95,255,45):Color.FromRgb(255,70,210);
        for(int y=40;y<160;y++)Paint(140,y,spill);
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);image.Freeze();
        var legacy=LetterTransferSheet.ReadLegacy(image,new[]{"Б"},key,130,CancellationToken.None);
        Program.Check(legacy.Cells.Single().Image.PixelWidth>120,"Spill fixture did not reproduce the surviving bright technical edge.");
        var result=LetterTransferSheet.Read(image,new[]{"Б"},key,130,CancellationToken.None);var cell=result.Cells.Single();var clean=LetterTransferImages.Pixels(cell.Image);
        Program.Check(cell.Safe && cell.Image.PixelWidth==120 && cell.Image.PixelHeight==160,"Material protection retained the external matte or clipped the letter.");
        for(int n=0;n<material.Length;n++)
        {
            var color=material[n];int actual=Enumerable.Range(0,clean.Length/4).Count(p=>clean[p*4+3]==255 && clean[p*4]==color.B && clean[p*4+1]==color.G && clean[p*4+2]==color.R);
            Program.Check(actual==counts[n],"Reserved-hue fabric was erased as a shadow: "+key+", material "+n+", kept "+actual+" of "+counts[n]);
        }
        var fallback=LetterTransferImages.Extract(image,1,key,CancellationToken.None,130,true).Single();
        Program.Check(fallback!=null,"Protected fixed-grid extraction rejected an intact material glyph.");
        var fixedPixels=LetterTransferImages.Pixels(fallback);
        for(int n=0;n<material.Length;n++)
        {
            var color=material[n];int actual=Enumerable.Range(0,fixedPixels.Length/4).Count(p=>fixedPixels[p*4+3]==255 && fixedPixels[p*4]==color.B && fixedPixels[p*4+1]==color.G && fixedPixels[p*4+2]==color.R);
            Program.Check(actual==counts[n],"Fixed-grid extraction erased reserved-hue material: "+key+", material "+n+", kept "+actual+" of "+counts[n]);
        }
        int expected=120*160-45*(30+35);
        Program.Check(Enumerable.Range(0,clean.Length/4).Count(p=>clean[p*4+3]>0)==expected,"Real letter counters were filled or fabric acquired transparent holes.");
        Program.Check(clean.Where((v,i)=>i%4==3).All(a=>a==0 || a==255),"Material protection introduced partial alpha.");
        Program.Check(data.SequenceEqual(LetterTransferImages.Pixels(image)),"Material protection mutated the saved AI sheet.");
    }
}
