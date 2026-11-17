using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;
internal static class ComponentGridChecks
{
    private static readonly string[] Alphabet=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();
    internal static void Run(string root)
    {
        foreach(var key in new[]{"#FF00FF","#00FF00"})Synthetic(key);
        var canceled=new CancellationTokenSource();canceled.Cancel();bool stopped=false;
        try{LetterTransferComponentGrid.Read(Program.Solid(40,40,Colors.White),Alphabet,canceled.Token);}catch(OperationCanceledException){stopped=true;}
        Program.Check(stopped,"Component extraction ignored cancellation.");
        Program.Check(LetterTransferComponentGrid.Read(Program.Solid(80,80,Colors.White),Alphabet,CancellationToken.None)==null,"An opaque unsplit sheet was accepted as a whole alphabet.");
        Observed(root);
    }
    private static void Synthetic(string key)
    {
        const int size=720;var data=LetterTransferImages.Pixels(Program.Solid(size,size,(Color)ColorConverter.ConvertFromString(key)));
        void Box(int x,int y,int w,int h,Color c){for(int yy=y;yy<y+h;yy++)for(int xx=x;xx<x+w;xx++){int at=(yy*size+xx)*4;data[at]=c.B;data[at+1]=c.G;data[at+2]=c.R;data[at+3]=255;}}
        for(int n=0;n<33;n++)
        {
            int x=n%6*120+24,y=n/6*112+20;var color=n==8?Color.FromRgb(60,145,50):Color.FromRgb((byte)(55+n),70,175);
            if(n==28){Box(x,y,52,78,color);Box(x+60,y,12,78,color);}else Box(x,y,72,78,color);
            if(n==6){Box(x+10,y-24,12,12,color);Box(x+42,y-24,12,12,color);}
            if(n==10)Box(x+20,y-24,32,12,color);
            if(n==26){Box(x+66,y+62,14,18,color);Box(x+76,y+72,16,62,color);}
        }
        // The descender overlaps the next row in Y, without touching its ink.
        // An extra, clipped drawing is visible but cannot be auto-accepted.
        Box(520,650,80,70,Colors.Pink);
        var source=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,data,size*4);source.Freeze();
        var result=LetterTransferSheet.Read(source,Alphabet,key,130,CancellationToken.None);
        Program.Check(result.ContentLayout && result.Cells.Length==34 && result.Cells.Count(c=>c.Safe)==33,"Overlapping rows were not recovered by silhouette: "+result.Cells.Length+", safe="+result.Cells.Count(c=>c.Safe));
        Program.Check(result.LayoutChanged && result.Cells.All(c=>c.Letter==null),"Recovery fabricated alphabet labels for an unverified layout.");
        Program.Check(result.Cells[6].Image.PixelHeight==102 && result.Cells[10].Image.PixelHeight==102,"Detached accents were lost or attached to the row above.");
        Program.Check(result.Cells[26].Image.PixelHeight==134 && result.Cells[32].Image.PixelHeight==78,"A descender or the letter below it was truncated.");
        Program.Check(result.Cells[28].Image.PixelWidth==72,"Disconnected Ы stems were split into two letters.");
        Program.Check(!result.Cells[33].Safe && result.Cells[33].Warning!=null,"A drawing cut by the sheet edge was auto-accepted.");
        for(int i=0;i<33;i++)
        {
            var pixels=LetterTransferImages.Pixels(result.Cells[i].Image);
            Program.Check(Enumerable.Range(0,pixels.Length/4).All(p=>pixels[p*4+3]==0 || pixels[p*4+2]==(i==8?60:55+i)),"Neighboring artwork leaked into an isolated letter "+i);
        }
        Program.Check(data.SequenceEqual(LetterTransferImages.Pixels(source)),"Silhouette recovery mutated the saved source.");
    }
    private static void Observed(string root)
    {
        string path=Environment.GetEnvironmentVariable("VANYA_LETTER_CELL_SHEET");if(String.IsNullOrEmpty(path))return;
        var source=LetterTransferImages.Load(path);var before=LetterTransferImages.Pixels(source);
        var result=LetterTransferSheet.Read(source,Alphabet,"#FF00FF",130,CancellationToken.None);
        Console.WriteLine("Observed cells="+result.Cells.Length+", safe="+result.Cells.Count(c=>c.Safe));
        Program.Check(result.Cells.Length==34 && result.Cells.Take(33).All(c=>c.Safe) && !result.Cells[33].Safe,"Saved full alphabet was not separated into 33 complete glyphs plus one clipped artifact.");
        Program.Check(result.Cells[6].Image.PixelHeight>650 && result.Cells[10].Image.PixelHeight>650,"Observed Ё/Й accents were lost.");
        Program.Check(result.Cells[28].Image.PixelWidth>580,"Observed Ы lost its second stem.");
        Program.Check(before.SequenceEqual(LetterTransferImages.Pixels(source)),"Observed source was modified.");
        var plan=LetterTransferRecognition.Prepare(Path.Combine(root,"observed-components"),new LetterTransferJob{Id="0123456789abcdef",Output="requests/sheet.png"},result);
        Program.Check(plan.Count==33 && plan.CellIndices.SequenceEqual(Enumerable.Range(0,33)),"The clipped extra fragment was sent to recognition or letters were lost.");
        string export=Environment.GetEnvironmentVariable("VANYA_LETTER_CELL_EXPORT");
        if(!String.IsNullOrEmpty(export))
        {
            LetterTransferImages.Save(plan.Preview,Path.Combine(export,"recovered-preview.png"));
            foreach(int i in new[]{0,6,10,26,27,28,32,33})LetterTransferImages.Save(result.Cells[i].Image,Path.Combine(export,"cell-"+i+".png"));
        }
        foreach(string name in new[]{"VANYA_LETTER_CELL_GREEN_1","VANYA_LETTER_CELL_GREEN_2"})
        {
            string green=Environment.GetEnvironmentVariable(name);if(String.IsNullOrEmpty(green))continue;
            var saved=LetterTransferImages.Load(green);
            var baseline=(LetterTransferSheet)typeof(LetterTransferSheet).GetMethod("ReadCore",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{saved,Alphabet,"#00FF00",130,CancellationToken.None,true,true});
            var previous=LetterTransferSheet.Read(saved,Alphabet,"#00FF00",130,CancellationToken.None);
            Console.WriteLine(name+": cells="+previous.Cells.Length+", safe="+previous.Cells.Count(c=>c.Safe));
            Program.Check(previous.Cells.Count(c=>c.Safe)>=baseline.Cells.Count(c=>c.Safe),"The previous green-matte alphabet regressed.");
            if(previous.Cells.Count(c=>c.Safe)==baseline.Cells.Count(c=>c.Safe))Program.Check(previous.Cells.Select(c=>LetterTransferImages.Fingerprint(c.Image??c.Preview)).SequenceEqual(baseline.Cells.Select(c=>LetterTransferImages.Fingerprint(c.Image??c.Preview))),"A working prior division was needlessly replaced.");
        }
    }
}
