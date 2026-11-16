using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class GridAndLabelChecks
{
    internal static void Run(string root)
    {
        var set=Program.Printable();var plan=LetterTransferLayout.Build(set,false);
        foreach(var page in plan.GroupBy(p=>p.Page))
        {
            foreach(var column in page.GroupBy(p=>p.Column))Program.Check(column.Select(p=>p.CellX).Distinct().Count()==1,"Vertical cuts drift between rows.");
            foreach(var row in page.GroupBy(p=>p.Row))Program.Check(row.Select(p=>p.CellY).Distinct().Count()==1 && row.Select(p=>p.Y+p.Height).Distinct().Count()==1,"Row cuts or common baseline drift.");
            foreach(var item in page)Program.Check(item.X>=item.CellX && item.X+item.Width<=item.CellX+item.CellWidth+.00001 && item.Y>=item.CellY && item.Y+item.Height<=item.CellY+item.CellHeight+.00001,"Glyph escapes its cutting cell.");
        }
        // Detached accents must enlarge the cell, not reduce the main cap body.
        int w=100,h=120;var bytes=new byte[w*h*4];
        void Box(int x0,int y0,int width,int height){for(int y=y0;y<y0+height;y++)for(int x=x0;x<x0+width;x++){int p=(y*w+x)*4;bytes[p]=200;bytes[p+1]=140;bytes[p+2]=60;bytes[p+3]=255;}}
        Box(20,0,15,10);Box(65,0,15,10);Box(0,20,100,100);
        var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,bytes,w*4);image.Freeze();
        Program.Check(LetterTransferImages.BodyHeight("Ё",image)==100 && LetterTransferImages.BodyHeight("Й",image)==100,"Detached accent body was measured incorrectly.");
        set=new LetterTransferSet{HeightMm=20,SheetWidthMm=130};foreach(var row in set.Letters)row.Copies=0;
        string directory=LetterTransferStore.Create(Path.Combine(root,"accent-grid"),set);
        foreach(string letter in new[]{"Е","Ё","И","Й"})
        {
            var glyph=LetterTransferImages.Add(directory,set,letter,letter=="Ё" || letter=="Й"?image:Program.Solid(100,100,Colors.Gold),"test");set.Letters.Single(r=>r.Letter==letter).Copies=3;
            double expected=letter=="Ё" || letter=="Й"?24:20;var png=LetterTransferImages.Load(LetterTransferStore.Resolve(directory,glyph.File));
            Program.Check(Math.Abs(png.PixelHeight*25.4/png.DpiY-expected)<.01 && Math.Abs(LetterTransferLayout.GlyphHeight(set,glyph)-expected)<.001,"PNG or placement shrank the accent body.");
        }
        LetterTransferStore.Save(directory,set);plan=LetterTransferLayout.Build(set,false);
        Program.Check(plan.All(p=>p.CellHeight==24) && plan.Where(p=>p.Glyph.Letter=="Ё" || p.Glyph.Letter=="Й").All(p=>Math.Abs(p.Height*p.Glyph.BodyHeight/p.Glyph.Height-20)<.001),"Accent cells do not preserve nominal cap body.");
        foreach(var row in plan.GroupBy(p=>p.Row))Program.Check(row.Select(p=>p.Y+p.Height).Distinct().Count()==1,"Accents changed the common baseline.");
        var accented=set.Glyphs.Where(g=>g.Letter=="Ё" || g.Letter=="Й").ToArray();foreach(var glyph in accented)glyph.BodyHeight=0;LetterTransferStore.Save(directory,set);
        var loaded=LetterTransferStore.Load(directory);Program.Check(loaded.Glyphs.Where(g=>g.Letter=="Ё" || g.Letter=="Й").All(g=>g.BodyHeight==100),"Existing set did not acquire cap measurement without regenerating images.");
        // A wide variant sets every column width, even if first used late.
        set=Program.Printable();set.Glyphs[1].Width=35;set.SheetWidthMm=180;plan=LetterTransferLayout.Build(set,false);
        Program.Check(plan.All(p=>p.CellWidth==35) && plan.Select(p=>p.Glyph.Id).Distinct().Count()==3,"Variant widths changed cutting columns or variants were lost.");
        Order();Draft(root,image);QuickAssignment(image);CompletePartial(root);
    }
    private static void CompletePartial(string root)
    {
        string library=Path.Combine(root,"complete-partial");var set=new LetterTransferSet{Reference="reference.png"};string directory=LetterTransferStore.Create(library,set);
        LetterTransferImages.Save(Program.Solid(50,50,Colors.Gold),LetterTransferStore.Resolve(directory,set.Reference));
        var job=LetterTransferAi.Create(directory,set,LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray(),"google/nano-banana-2","4K");
        job.Key="#FF00FF";int size=600;var pixels=LetterTransferImages.Pixels(Program.Solid(size,size,Colors.Magenta));
        for(int n=0;n<33;n++)for(int y=20;y<80;y++)for(int x=20;x<80;x++){int p=((n/6*100+y)*size+n%6*100+x)*4;pixels[p]=pixels[p+1]=pixels[p+2]=(byte)(200+n);}
        var image=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,pixels,size*4);image.Freeze();LetterTransferImages.Save(image,LetterTransferStore.Resolve(directory,job.Output));
        var sheet=LetterTransferSheet.Read(image,job.Letters,job.Key,130,System.Threading.CancellationToken.None);
        for(int n=0;n<8;n++)LetterTransferImages.Add(directory,set,job.Letters[n],sheet.Cells[n].Image,"ИИ · "+job.Id.Substring(0,8));job.Reviewed=true;LetterTransferStore.Save(directory,set);
        int calls=ReplicateWorkerClient.Calls,reviews=0;
        var window=new LetterTransferWindow(()=>"synthetic-key",()=>null,(_,__)=>{},library,confirmRecognition:(preview,count,prompt,retry)=>false,review:(reference,source,cells)=>{reviews++;return cells.Cells.Select(c=>new LetterTransferApproved{Letter=c.Letter,Image=c.Image}).ToList();});
        try
        {
            typeof(LetterTransferWindow).GetMethod("CompleteAlphabet",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).Invoke(window,null);
            var saved=LetterTransferStore.Load(directory);
            Program.Check(reviews==1 && ReplicateWorkerClient.Calls==calls && saved.Glyphs.Count==33 && saved.Glyphs.Select(g=>g.Letter).Distinct().Count()==33,"Getting all letters failed to reopen a partial reviewed sheet, duplicated first eight, or sent another model.");
        }
        finally{window.Close();}
    }
    private static void Order()
    {
        string[] target=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();
        var plain=LetterTransferOrder.Assign(target,new string[33],out _);Program.Check(plain.SequenceEqual(target),"Unique alphabet order failed.");
        string sequence="АБВГДЕЁЖЗИИЙКЛМНОППРСТУФХЦЧШЩЪЫЬЭЮЯ";var labels=new string[35];
        foreach(int n in new[]{9,10,17,18})labels[n]=sequence[n].ToString();
        var result=LetterTransferOrder.Assign(target,labels,out _);Program.Check(String.Concat(result)==sequence,"Order with two duplicate anchors shifted letters.");
        result=LetterTransferOrder.Assign(target,sequence.Select(c=>c.ToString()).ToArray(),out _);Program.Check(String.Concat(result)==sequence,"Fully labeled duplicate variants were rejected.");
        result=LetterTransferOrder.Assign(target,new string[35],out string notice);Program.Check(result[0]=="А" && result[34]=="Я" && result.Skip(1).Take(33).Any(l=>l==null) && notice.Contains("повторяющиеся"),"Ambiguous duplicate position was guessed.");
        labels=new[]{"В",null,"А"};result=LetterTransferOrder.Assign(new[]{"А","Б","В"},labels,out notice);Program.Check(result.SequenceEqual(labels) && notice.Contains("сохранены"),"Conflicting labels were overwritten.");
        result=LetterTransferOrder.Assign(target,new string[5],out notice);Program.Check(result.Length==5 && notice.Contains("меньше"),"Partial sheet was force-labeled as a full alphabet.");
    }
    private static void Draft(string root,BitmapSource image)
    {
        string output=Path.Combine(root,"draft.png");LetterTransferImages.Save(image,output);
        var sheet=new LetterTransferSheet{Cells=new[]{new LetterTransferCell{Letter="Й",Manual=true},new LetterTransferCell{Letter="Ё",Manual=true,Excluded=true}}};
        LetterTransferReviewDraft.Save(output,sheet);sheet.Cells[0].Letter=null;sheet.Cells[1].Excluded=false;
        LetterTransferReviewDraft.Restore(output,sheet);Program.Check(sheet.Cells[0].Letter=="Й" && sheet.Cells[1].Excluded,"Manual labels/exclusions were lost on reopening.");
        LetterTransferImages.Save(Program.Solid(30,30,Colors.Red),output);sheet.Cells[0].Letter="А";LetterTransferReviewDraft.Restore(output,sheet);
        Program.Check(sheet.Cells[0].Letter=="А","Draft was attached to changed artwork.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
    private static void QuickAssignment(BitmapSource image)
    {
        var sheet=new LetterTransferSheet{Requested=new[]{"А","Б"},LayoutChanged=true,Cells=Enumerable.Range(0,3).Select(i=>new LetterTransferCell{Image=image,Preview=image,Safe=true}).ToArray()};
        var window=LetterTransferReviewWindow.ReviewDialog(null,image,image,sheet,new List<LetterTransferApproved>());
        try
        {
            var content=(FrameworkElement)window.Content;content.Measure(new Size(950,760));content.Arrange(new Rect(0,0,950,760));content.UpdateLayout();var controls=Descendants(content).ToArray();
            var keys=controls.OfType<Button>().Where(b=>(b.Tag as string)?.StartsWith("assign-")==true).ToArray();Program.Check(keys.Length==33,"Fast alphabet palette is incomplete.");
            foreach(string key in new[]{"А","А","Б"})keys.Single(b=>(string)b.Tag=="assign-"+key).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Program.Check(sheet.Cells.Select(c=>c.Letter).SequenceEqual(new[]{"А","А","Б"}) && controls.OfType<CheckBox>().Where(c=>(string)c.Tag=="accept").All(c=>c.IsChecked==true),"Fast entry did not advance or lost duplicate variants.");
        }
        finally{window.Close();}
    }
}
