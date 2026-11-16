using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class ContentGridChecks
{
    private static readonly string[] Alphabet=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();
    internal static void Run(string root)
    {
        var sheet=Sheet(30);var before=LetterTransferImages.Pixels(sheet);var result=LetterTransferSheet.Read(sheet,Alphabet,"#00FF00",130,CancellationToken.None);
        Program.Check(result.Cells.Length==30 && result.Cells.All(c=>c.Image!=null && c.Safe) && result.LayoutChanged,"Five actual rows were force-sliced into six expected rows.");
        Program.Check(result.Cells.All(c=>c.Letter==null),"Missing source letters were assigned shifted alphabet labels.");
        Program.Check(result.Cells[6].Image.PixelHeight>80,"Detached Ё-like accents were separated from their main cell.");
        var green=LetterTransferImages.Pixels(result.Cells[8].Image);
        Program.Check(Enumerable.Range(0,green.Length/4).Any(p=>green[p*4+3]==255 && green[p*4+1]==145 && green[p*4+2]==60),"Actual green fabric was removed with the green backdrop.");
        Program.Check(before.SequenceEqual(LetterTransferImages.Pixels(sheet)),"Content separation mutated the source.");
        var full=LetterTransferSheet.Read(Sheet(33,true),Alphabet,"#00FF00",130,CancellationToken.None);
        Program.Check(full.Cells.Length==33 && full.Cells.All(c=>c.Image!=null && c.Safe),"Caption or unused slots corrupted whitespace separation: "+full.Cells.Length+", safe="+full.Cells.Count(c=>c.Safe));
        Guard(root);Draft(root);Observed(root);
    }
    private static BitmapSource Sheet(int count,bool header=false)
    {
        int width=660,height=((count+5)/6)*120;var data=LetterTransferImages.Pixels(Program.Solid(width,height,Colors.Lime));
        void Box(int x0,int y0,int w,int h,Color color){for(int y=y0;y<y0+h;y++)for(int x=x0;x<x0+w;x++){int p=(y*width+x)*4;data[p]=color.B;data[p+1]=color.G;data[p+2]=color.R;data[p+3]=255;}}
        for(int n=0;n<count;n++){int x=n%6*110+20,y=n/6*120+28;Box(x,y,65,70,n==8?Color.FromRgb(60,145,50):Colors.DarkBlue);if(n==6){Box(x+10,y-20,12,10,Colors.DarkBlue);Box(x+40,y-20,12,10,Colors.DarkBlue);}}
        if(header)Box(190,0,170,14,Colors.Black);
        var source=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);source.Freeze();return source;
    }
    private static void Guard(string root)
    {
        var image=Program.Solid(80,80,Colors.DarkBlue);var broken=new LetterTransferSheet{Requested=Alphabet,Cells=Enumerable.Range(0,33).Select(i=>new LetterTransferCell{Image=i==0?image:null,Preview=image,Safe=i==0}).ToArray()};
        int calls=ReplicateWorkerClient.Calls;bool rejected=false;
        try{LetterTransferRecognition.Prepare(Path.Combine(root,"broken-grid"),new LetterTransferJob{Output="requests/broken.png"},broken);}catch(InvalidDataException){rejected=true;}
        Program.Check(rejected && ReplicateWorkerClient.Calls==calls,"Mostly broken grid was still prepared for a paid recognition call.");
        var mixed=new LetterTransferSheet{Requested=new[]{"А","Б","В","Г"},Cells=Enumerable.Range(0,4).Select(i=>new LetterTransferCell{Image=i==1?null:image,Preview=image,Safe=i!=1}).ToArray()};
        var plan=LetterTransferRecognition.Prepare(Path.Combine(root,"safe-subset"),new LetterTransferJob{Output="requests/mixed.png"},mixed);
        Program.Check(plan.Count==3 && plan.CellIndices.SequenceEqual(new[]{0,2,3}) && plan.Prompt.Contains("exactly 3 tiles"),"Recognition did not exclude and map the unsafe fragment.");
        var labels=new[]{new LetterTransferRecognized{Id=1,Letter="А"},new LetterTransferRecognized{Id=2,Letter="В"},new LetterTransferRecognized{Id=3,Letter="Г"}};
        LetterTransferRecognition.Apply(mixed,labels,plan.CellIndices);
        Program.Check(mixed.Cells[1].Letter==null && !mixed.Cells[1].Recognized && mixed.Cells[2].Letter=="В" && mixed.Notice.Contains("Разных букв: 3") && mixed.Notice.Contains("Б"),"Invalid fragment was counted as a ready letter or subset labels shifted.");
        var fake=Alphabet.Select((letter,i)=>new LetterTransferRecognized{Id=i+1,Letter=letter}).ToArray();LetterTransferRecognition.Apply(broken,fake);
        Program.Check(broken.Cells.Count(c=>c.Recognized)==1 && broken.Notice.Contains("Разных букв: 1"),"A full model reply fabricated ready letters for missing images.");
        var job=new LetterTransferJob{Model=LetterTransferModels.NanoPro,Prompt="synthetic",Letters=new[]{"А"},Resolution="4K"};
        Program.Check((bool)LetterTransferModels.Input(job,"reference")["allow_fallback_model"]==false,"Nano Banana Pro silently allows a substitute model.");
    }
    private static void Draft(string root)
    {
        string source=Path.Combine(root,"layout-draft.png");var image=Program.Solid(40,40,Colors.Gold);LetterTransferImages.Save(image,source);
        var cells=new LetterTransferSheet{Cells=new[]{new LetterTransferCell{Image=image,Letter="А",Manual=true}},ContentLayout=true};
        LetterTransferReviewDraft.Save(source,cells);cells.Cells[0]=new LetterTransferCell{Image=Program.Solid(40,40,Colors.Blue)};
        LetterTransferReviewDraft.Restore(source,cells);Program.Check(!cells.Cells[0].Manual && cells.Cells[0].Letter==null,"Manual labels from a different division were applied to new cells.");
        cells.Cells[0].Image=image;LetterTransferReviewDraft.Restore(source,cells);Program.Check(cells.Cells[0].Manual && cells.Cells[0].Letter=="А","Matching manual layout was not restored.");
    }
    private static void Observed(string root)
    {
        string path=Environment.GetEnvironmentVariable("VANYA_LETTER_FIVE_ROW_SHEET");if(String.IsNullOrEmpty(path))return;
        var source=LetterTransferImages.Load(path);var result=LetterTransferSheet.Read(source,Alphabet,"#00FF00",130,CancellationToken.None);
        Program.Check(result.Cells.Length==30 && result.Cells.All(c=>c.Image!=null && c.Safe),"Observed 30-letter sheet was clipped or rejected.");
        string names="АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЮЯ";Program.Check(names.Length==30 && LetterTransferRules.Alphabet.Except(names).SequenceEqual("ЫЬЭ"),"Reviewed source inventory changed.");
        var plan=LetterTransferRecognition.Prepare(Path.Combine(root,"observed-safe-grid"),new LetterTransferJob{Output="requests/observed.png"},result);
        Program.Check(plan.Count==30 && plan.CellIndices.SequenceEqual(Enumerable.Range(0,30)),"Observed grid still sends 33 fragments to recognition.");
        string export=Environment.GetEnvironmentVariable("VANYA_LETTER_FIVE_ROW_EXPORT");if(String.IsNullOrEmpty(export))return;
        var set=new LetterTransferSet{Name="Буквы из сохранённого листа · 30 из 33",GenerationModel=LetterTransferModels.NanoPro,Reference="reference.png"};
        string directory=LetterTransferStore.Create(export,set);LetterTransferImages.Save(source,LetterTransferStore.Resolve(directory,set.Reference));
        for(int i=0;i<names.Length;i++)LetterTransferImages.Add(directory,set,names[i].ToString(),result.Cells[i].Image,"Восстановлено из сохранённого ответа Nano Banana Pro");
        LetterTransferStore.Save(directory,set);Program.Check(set.Glyphs.Select(g=>g.Letter).Distinct().Count()==30,"Recovery export lost separate glyphs.");
        Console.WriteLine("Recovered set: "+directory);
    }
}
