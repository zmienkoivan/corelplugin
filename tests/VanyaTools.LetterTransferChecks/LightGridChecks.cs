using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class LightGridChecks
{
    private static readonly string[] Alphabet=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();
    internal static void Run(string root)
    {
        var source=Sheet();var before=LetterTransferImages.Pixels(source);
        var sheet=LetterTransferSheet.Read(source,Alphabet,"#00FF00",130,CancellationToken.None);
        Program.Check(sheet.Cells.Length==33 && sheet.Cells.All(c=>c.Image!=null && c.Safe),"Pale grid without outer frame did not yield 33 complete glyphs: "+sheet.Cells.Length+", safe="+sheet.Cells.Count(c=>c.Safe));
        Program.Check(!sheet.ContentLayout && !sheet.LayoutChanged,"Drawn pale lines were ignored or changed the expected letter order.");
        Program.Check(sheet.Cells.Select(c=>c.Letter).SequenceEqual(Alphabet),"Pale grid dropped an outside row or column.");
        var white=LetterTransferImages.Pixels(sheet.Cells[0].Image);
        Program.Check(Enumerable.Range(0,white.Length/4).Any(p=>white[p*4+3]==255 && white[p*4]==255 && white[p*4+1]==255 && white[p*4+2]==255),"White artwork was removed as a separator.");
        var green=LetterTransferImages.Pixels(sheet.Cells[3].Image);
        Program.Check(Enumerable.Range(0,green.Length/4).Any(p=>green[p*4+3]==255 && green[p*4+1]==145 && green[p*4+2]==60),"Actual green fabric was removed while detecting pale dividers.");
        Program.Check(sheet.Cells.All(c=>c.Image.PixelWidth<100 && c.Image.PixelHeight<110),"Pale divider or neighboring artwork survived in a cell.");
        Program.Check(before.SequenceEqual(LetterTransferImages.Pixels(source)),"Pale-grid separation changed the saved source.");
        var clipped=LetterTransferSheet.Read(Sheet(true),Alphabet,"#00FF00",130,CancellationToken.None);
        Program.Check(clipped.Cells.Length==33 && !clipped.Cells[0].Safe && !clipped.Cells[1].Safe && clipped.Cells.Skip(2).All(c=>c.Safe),"Pale grid silently accepted a glyph crossing a divider.");
        Shadow();
        string path=Environment.GetEnvironmentVariable("VANYA_LETTER_PALE_GRID_SHEET");if(String.IsNullOrEmpty(path))return;
        var observed=LetterTransferImages.Load(path);before=LetterTransferImages.Pixels(observed);
        var result=LetterTransferSheet.Read(observed,Alphabet,"#00FF00",130,CancellationToken.None);
        Program.Check(result.Cells.Length==33 && result.Cells.All(c=>c.Image!=null && c.Safe),"Observed pale grid did not preserve the complete alphabet: "+result.Cells.Length+", safe="+result.Cells.Count(c=>c.Safe));
        Program.Check(!result.ContentLayout && !result.LayoutChanged && result.Cells.Select(c=>c.Letter).SequenceEqual(Alphabet),"Observed drawn grid was not used in row-major order.");
        Program.Check(result.Cells.All(c=>c.Image.PixelWidth<170 && c.Image.PixelHeight<170),"Observed cells contain a separator or two glyphs.");
        Program.Check(before.SequenceEqual(LetterTransferImages.Pixels(observed)),"Observed pale-grid source was modified.");
        var plan=LetterTransferRecognition.Prepare(Path.Combine(root,"pale-grid"),new LetterTransferJob{Output="requests/pale-grid.png"},result);
        Program.Check(plan.Count==33 && plan.CellIndices.SequenceEqual(Enumerable.Range(0,33)),"Observed pale-grid recognition still contains fragments or missing letters.");
    }
    private static void Shadow()
    {
        const int size=180;var data=LetterTransferImages.Pixels(Program.Solid(size,size,Color.FromRgb(7,221,6)));
        void Box(int x0,int y0,int width,int height,Color color){for(int y=y0;y<y0+height;y++)for(int x=x0;x<x0+width;x++){int p=(y*size+x)*4;data[p]=color.B;data[p+1]=color.G;data[p+2]=color.R;data[p+3]=255;}}
        Box(44,34,94,104,Color.FromRgb(3,110,3));Box(47,37,88,98,Color.FromRgb(6,170,5));
        Box(50,40,80,90,Colors.Pink);Box(54,44,72,82,Color.FromRgb(60,145,50));
        Box(80,60,5,20,Colors.Black);Box(90,80,10,10,Color.FromRgb(7,221,6));
        for(int n=0;n<10;n++){Box(90+n,80,1,1,Color.FromRgb(3,160,3));Box(90+n,89,1,1,Color.FromRgb(3,160,3));Box(90,80+n,1,1,Color.FromRgb(3,160,3));Box(99,80+n,1,1,Color.FromRgb(3,160,3));}
        // A pink antialias fringe mixed with a separately darkened green
        // backdrop is not just a darker copy of that backdrop.
        Box(49,60,1,8,Color.FromRgb(93,178,74));
        var source=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,data,size*4);source.Freeze();
        var result=LetterTransferSheet.Read(source,new[]{"А"},"#00FF00",130,CancellationToken.None);
        var image=result.Cells.Single().Image;var clean=LetterTransferImages.Pixels(image);
        Program.Check(result.Cells[0].Safe && image.PixelWidth==80 && image.PixelHeight==90,"Darkened technical backdrop remained as a drop shadow: "+image.PixelWidth+"x"+image.PixelHeight);
        Program.Check(Enumerable.Range(0,clean.Length/4).Count(p=>clean[p*4+3]>0)==80*90-100,"Shadow cleanup removed artwork or filled a letter hole: "+Enumerable.Range(0,clean.Length/4).Count(p=>clean[p*4+3]>0));
        Program.Check(Enumerable.Range(0,clean.Length/4).Count(p=>clean[p*4+3]==255 && clean[p*4+1]==145 && clean[p*4+2]==60)==72*82-200,"Shadow cleanup erased genuine green fabric.");
        Program.Check(Enumerable.Range(0,clean.Length/4).Count(p=>clean[p*4+3]==255 && clean[p*4]==0 && clean[p*4+1]==0 && clean[p*4+2]==0)==100,"Shadow cleanup erased black artwork.");
    }
    private static BitmapSource Sheet(bool crossing=false)
    {
        const int size=720,cell=120;var data=LetterTransferImages.Pixels(Program.Solid(size,size,Color.FromRgb(7,221,6)));
        void Paint(int x,int y,Color color){int p=(y*size+x)*4;data[p]=color.B;data[p+1]=color.G;data[p+2]=color.R;data[p+3]=255;}
        for(int n=0;n<33;n++)
        {
            Color ink=n==0?Colors.White:n==3?Color.FromRgb(60,145,50):Colors.DarkBlue;
            for(int y=20;y<100;y++)for(int x=20;x<100;x++)Paint(n%6*cell+x,n/6*cell+y,ink);
            if(n==6 || n==10)for(int y=8;y<15;y++)for(int x=40;x<70;x++)Paint(n%6*cell+x,n/6*cell+y,ink);
        }
        // Internal pale lines only: there is no enclosing outside rectangle.
        // Their color and pixel position vary, as on a generated response.
        for(int n=1;n<6;n++)for(int along=0;along<size;along++)
        {
            int cut=n*cell+along/31%3-1;var color=Color.FromRgb((byte)(170+along%30),255,(byte)(165+along%30));
            Paint(cut,along,color);Paint(along,cut,color);
        }
        if(crossing)for(int y=45;y<70;y++)for(int x=95;x<140;x++)Paint(x,y,Colors.DarkBlue);
        var source=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,data,size*4);source.Freeze();return source;
    }
}
