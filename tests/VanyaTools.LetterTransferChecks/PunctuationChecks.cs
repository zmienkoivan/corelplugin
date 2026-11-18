using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;
internal static class PunctuationChecks
{
    private static readonly string[] Characters=LetterTransferRules.Characters.Select(c=>c.ToString()).ToArray();
    internal static void Run(string root)
    {
        var set=new LetterTransferSet();
        Program.Check(set.Schema==2 && set.Letters.Count==37 && set.Letters.Select(r=>r.Letter).SequenceEqual(Characters),"New set lost punctuation or duplicated colon.");
        foreach(string mark in new[]{"!","?","-",":"})Program.Check(LetterTransferRules.ValidLetter(mark),"Rejected supported punctuation.");
        Program.Check(!LetterTransferRules.ValidLetter(";") && !LetterTransferRules.ValidLetter("::"),"Unsupported or combined marks were accepted.");
        foreach(int total in new[]{33,34,37,100,5000})
        {
            LetterTransferRules.Allocate(set.Letters,total);
            Program.Check(set.Letters.Sum(r=>r.Copies)==total && set.Letters.Where(r=>LetterTransferRules.Alphabet.Contains(r.Letter)).All(r=>r.Copies>=1),"Punctuation changed the print total or dropped a required letter.");
            if(total>=37)Program.Check(set.Letters.All(r=>r.Copies>=1),"Punctuation was missing from the allocated set.");
        }
        Program.Check(Hash(LetterTransferRecognition.Prompt(33))=="546128ca19cc0138791e3e4b4f00f56105a5c7b6b16e85ad1b1e76a63b4b320b","Punctuation invalidated the paid v1.2.72 letter-recognition cache.");
        Program.Check(Hash(LetterTransferAi.Prompt(new LetterTransferSet(),LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray(),"#FF00FF"))=="543b4eb9d83b280077dcf6e2761b9e9aa4a697156241c6d378610303adb9fddc","Existing letter-only generation jobs lost their exact prompt.");
        Program.Check(LetterTransferRecognition.Prompt(37,true).Contains("uppercase letter or punctuation mark"),"Recognition still restricted punctuation to uppercase letters.");
        var complete=new LetterTransferSet();complete.Glyphs=LetterTransferRules.Alphabet.Select(c=>new LetterTransferGlyph{Letter=c.ToString(),Width=50,Height=70,File="glyphs/existing.png"}).ToList();
        var marks=LetterTransferRules.MissingPasses(complete);
        Program.Check(marks.Count==1 && String.Concat(marks[0])=="!?-:","A complete letter bank regenerated letters instead of only the new marks.");
        int calls=ReplicateWorkerClient.Calls;
        var passes=LetterTransferRules.MissingPasses(set);Program.Check(passes.Count==1 && passes[0].SequenceEqual(Characters),"The full 37-character set was split or lost punctuation.");
        foreach(string model in new[]{LetterTransferModels.Nano2,LetterTransferModels.NanoPro,LetterTransferModels.Seedream})
        {
            string prompt=LetterTransferAi.Prompt(set,Characters,"#FF00FF",model);
            Program.Check(prompt.Contains("EXACTLY 37") && prompt.Contains("7 columns by 6 rows") && prompt.Contains("two vertically aligned dots") && prompt.Contains("single horizontal hyphen"),"Generation did not explicitly request the punctuation shapes.");
            if(model==LetterTransferModels.Seedream)Program.Check(prompt.Length<=4000,"Punctuation exceeded Seedream's prompt limit.");
        }
        var sheet=LetterTransferSheet.Read(Sheet(),Characters,"#FF00FF",130,CancellationToken.None);
        Program.Check(sheet.Cells.Length==37 && sheet.Cells.All(c=>c.Safe && c.Image!=null),"Punctuation sheet was not separated: cells="+sheet.Cells.Length+", safe="+sheet.Cells.Count(c=>c.Safe));
        Program.Check(sheet.Cells[33].Image.PixelHeight>60 && sheet.Cells[35].Image.PixelHeight==10 && sheet.Cells[36].Image.PixelHeight==50,"Exclamation dot, hyphen or colon dots were lost or merged with neighbors.");
        string directory=LetterTransferStore.Create(Path.Combine(root,"punctuation"),set);set.Reference="reference.png";LetterTransferImages.Save(Program.Solid(50,50,Colors.Blue),LetterTransferStore.Resolve(directory,set.Reference));
        var job=LetterTransferAi.Create(directory,set,new[]{"!","?","-",":"},LetterTransferModels.Nano2,"2K");
        var plan=LetterTransferRecognition.Prepare(directory,job,sheet);
        Program.Check(plan.Prompt.Contains("! ? - :") && LetterTransferRecognition.Prompt(33).Contains("Я. "),"Punctuation recognition was absent or changed the legacy letter-only prompt.");
        string json=new JavaScriptSerializer().Serialize(new{cells=new[]{"!","?","—",":"}.Select((letter,i)=>new{id=i+1,letter,uncertain=false}).ToArray()});
        Program.Check(LetterTransferRecognition.Parse(json,4,true).Select(l=>l.Letter).SequenceEqual(new[]{"!","?","-",":"}),"Recognized punctuation was rejected or normalized incorrectly.");
        foreach(var row in set.Letters)row.Copies=LetterTransferRules.Punctuation.Contains(row.Letter)?1:0;
        set.PrintHeightsMm=new System.Collections.Generic.List<double>{20,80};
        for(int i=33;i<37;i++)LetterTransferImages.Add(directory,set,Characters[i],sheet.Cells[i].Image,"offline test");
        LetterTransferStore.Save(directory,set);var loaded=LetterTransferStore.Load(directory);
        Program.Check(loaded.Glyphs.Count==4 && loaded.Glyphs.Select(g=>g.Letter).SequenceEqual(new[]{"!","?","-",":"}),"Punctuation could not be saved/reopened as separate PNG variants.");
        var layout=LetterTransferLayout.Build(loaded,false);
        foreach(double height in new[]{20d,80d})foreach(var placement in layout.Where(p=>p.PrintHeightMm==height))
        {
            double expected=height*LetterTransferRules.HeightFactor(placement.Glyph.Letter);
            Program.Check(Math.Abs(placement.Height-expected)<.0001 && placement.Y>=placement.CellY && placement.Y+placement.Height<=placement.CellY+placement.CellHeight+.0001,"Punctuation size or vertical position was wrong.");
            var image=LetterTransferImages.Load(LetterTransferStore.Resolve(directory,placement.Glyph.File));
            Program.Check(Math.Abs(image.PixelHeight/image.DpiY*25.4-80*LetterTransferRules.HeightFactor(placement.Glyph.Letter))<.05,"Punctuation PNG physical size disagreed with the print layout.");
        }
        Program.Check(layout.Count==8 && layout.GroupBy(p=>p.Page).Count()==2,"Separate size sheets lost punctuation.");
        Migration(root);
        Program.Check(ReplicateWorkerClient.Calls==calls,"Punctuation checks sent a paid request.");
    }
    private static string Hash(string value){using(var hash=SHA256.Create())return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").ToLowerInvariant();}
    private static BitmapSource Sheet()
    {
        const int width=840,height=720;var data=LetterTransferImages.Pixels(Program.Solid(width,height,Colors.Magenta));
        void Box(int x,int y,int w,int h){for(int yy=y;yy<y+h;yy++)for(int xx=x;xx<x+w;xx++){int at=(yy*width+xx)*4;data[at]=170;data[at+1]=70;data[at+2]=30;data[at+3]=255;}}
        for(int n=0;n<37;n++)
        {
            int x=n%7*120+30,y=n/7*120;
            if(n<33)Box(x,y+24,60,76);
            else if(n==33 || n==34){Box(x+24,y+22,12,52);Box(x+24,y+88,12,12);}
            else if(n==35)Box(x,y+55,60,10);
            else {Box(x+24,y+34,12,12);Box(x+24,y+72,12,12);}
        }
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);source.Freeze();return source;
    }
    private static void Migration(string root)
    {
        var legacy=new LetterTransferSet{Schema=1,TargetCopies=33};legacy.Letters=legacy.Letters.Where(r=>LetterTransferRules.Alphabet.Contains(r.Letter)).ToList();
        for(int i=0;i<legacy.Letters.Count;i++)legacy.Letters[i].Copies=i%3;
        string directory=Path.Combine(root,"punctuation-migration","legacy");Directory.CreateDirectory(directory);
        var glyph=LetterTransferImages.Add(directory,legacy,"А",Program.Solid(50,70,Colors.DarkBlue),"existing");
        string path=Path.Combine(directory,"set.json"),json=new JavaScriptSerializer().Serialize(legacy);File.WriteAllText(path,json,new UTF8Encoding(false));var original=File.ReadAllBytes(path);
        var loaded=LetterTransferStore.Load(directory);
        Program.Check(loaded.Schema==2 && loaded.Letters.Count==37 && loaded.TargetCopies==33 && loaded.Letters.Take(33).Select(r=>r.Copies).SequenceEqual(legacy.Letters.Select(r=>r.Copies)) && loaded.Letters.Skip(33).All(r=>r.Copies==0),"Opening a legacy set changed the existing print quantities.");
        Program.Check(original.SequenceEqual(File.ReadAllBytes(path)) && loaded.Glyphs.Single().Id==glyph.Id,"Opening a legacy set rewrote the file or lost its glyph.");
        LetterTransferStore.Save(directory,loaded);
        Program.Check(File.ReadAllBytes(path+".bak").SequenceEqual(original) && LetterTransferStore.Load(directory).Letters.Count==37,"Migration lost the original backup or was not persistent.");
    }
}
