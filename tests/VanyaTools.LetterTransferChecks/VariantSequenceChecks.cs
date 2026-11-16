using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using VanyaTools.Native;

internal static class VariantSequenceChecks
{
    private const BindingFlags Flags=BindingFlags.NonPublic|BindingFlags.Instance;
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,Flags).GetValue(value);
    internal static void Run(string root)
    {
        Sequence(root,false,0,false);Sequence(root,false,3,false);Sequence(root,false,0,true);Sequence(root,true,0,false);Fringe();
    }
    private static void Sequence(string root,bool selected,int decline,bool cancelReview)
    {
        string library=Path.Combine(root,"sequence-"+Guid.NewGuid().ToString("N"));
        var set=new LetterTransferSet{VariantsPerLetter=4,Reference="reference.png"};string directory=LetterTransferStore.Create(library,set);
        LetterTransferImages.Save(Sheet(1),LetterTransferStore.Resolve(directory,set.Reference));LetterTransferStore.Save(directory,set);
        int confirmations=0,reviews=0,calls=ReplicateWorkerClient.Calls;string[] targets=null;string status=null;
        var window=new LetterTransferWindow(()=>"synthetic-key",()=>null,(text,error)=>status=text,library,
            confirm:(reference,letters,model,resolution,prompt,retry)=>{confirmations++;targets=letters;return confirmations!=decline;},
            confirmRecognition:(preview,count,prompt,retry)=>false,
            review:(reference,image,sheet)=>{
                reviews++;if(cancelReview && reviews==2)return new List<LetterTransferApproved>();
                return sheet.Cells.Select((cell,index)=>new LetterTransferApproved{Letter=!selected && reviews==2 && targets[index]=="Ш"?"О":targets[index],Image=cell.Image,Original=cell.Original}).ToList();});
        ReplicateWorkerClient.GenerationOutput=output=>LetterTransferImages.Save(Sheet(targets.Length),output);
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        void Generate()
        {
            window.GetType().GetMethod("Generate",Flags).Invoke(window,new object[]{selected});
            var deadline=DateTime.UtcNow.AddSeconds(15);
            while(Field<bool>(window,"_sequenceBusy") && DateTime.UtcNow<deadline){var frame=new DispatcherFrame();window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(5);}
            Program.Check(!Field<bool>(window,"_sequenceBusy"),"Variant generation sequence did not finish.");
        }
        try
        {
            Generate();var saved=LetterTransferStore.Load(directory);
            if(decline>0){Program.Check(confirmations==3 && reviews==2 && ReplicateWorkerClient.Calls==calls+2 && saved.Jobs.Count==2,"Declining the next variant created an unconfirmed request.");return;}
            if(cancelReview){Program.Check(confirmations==2 && reviews==2 && ReplicateWorkerClient.Calls==calls+2,"Closing review without accepting letters still charged for another variant.");return;}
            if(selected){Program.Check(confirmations==4 && reviews==4 && saved.Glyphs.Count==4 && saved.Glyphs.All(g=>g.Letter=="А"),"Selected-letter generation ignored the target of four variants.");return;}
            Program.Check(confirmations==4 && reviews==4 && ReplicateWorkerClient.Calls==calls+4,"A single missing Ш stopped the remaining variant sheets.");
            Program.Check(saved.Glyphs.Count(g=>g.Letter=="О")==4 && saved.Glyphs.Count(g=>g.Letter=="Ш")==3 && saved.Glyphs.Count==131,"Sequence discarded a duplicate, over-generated a finished letter or fabricated the missing variant.");
            Program.Check(status.Contains("не хватает") && status.Contains("1"),"Partial sequence incorrectly reported all four variants as complete.");
            Program.Check(Field<Button>(window,"_complete").IsEnabled && Field<Button>(window,"_complete").Content.ToString().Contains("4"),"Completion button ignored missing variants after all 33 letter names were present.");
            Program.Check(saved.Letters.Single(r=>r.Letter=="А").Ready.Contains("4"),"Table omitted ready/target variant counts.");
            Generate();saved=LetterTransferStore.Load(directory);
            Program.Check(confirmations==5 && targets.SequenceEqual(new[]{"Ш"}) && saved.Glyphs.Count==132 && saved.Glyphs.GroupBy(g=>g.Letter).All(g=>g.Count()==4),"Resume did not generate only the missing fourth Ш.");
        }
        finally{ReplicateWorkerClient.GenerationOutput=null;window.Close();SynchronizationContext.SetSynchronizationContext(previous);}
    }
    private static BitmapSource Sheet(int count)
    {
        int columns=LetterTransferRules.Columns(count),rows=(count+columns-1)/columns,width=columns*100,height=rows*100;
        var data=LetterTransferImages.Pixels(Program.Solid(width,height,Colors.Magenta));
        for(int n=0;n<count;n++)for(int y=20;y<80;y++)for(int x=20;x<80;x++){int p=((n/columns*100+y)*width+n%columns*100+x)*4;data[p]=data[p+1]=data[p+2]=220;}
        var image=BitmapSource.Create(width,height,96,96,PixelFormats.Bgra32,null,data,width*4);image.Freeze();return image;
    }
    private static void Fringe()
    {
        const int size=80;var data=new byte[size*size*4];
        void Paint(int x,int y){int p=(y*size+x)*4;data[p]=180;data[p+1]=210;data[p+2]=235;data[p+3]=255;}
        for(int y=10;y<60;y++)for(int x=10;x<60;x++)Paint(x,y);
        for(int y=14;y<56;y++)Paint(62,y);for(int x=60;x<63;x++)Paint(x,35);
        var image=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,data,size*4);image.Freeze();
        var clean=LetterTransferEdges.Trim(image,2,130,CancellationToken.None);
        Program.Check(clean.PixelWidth==46 && clean.PixelHeight==46,"Thin background ridge attached to the body survived a two-pixel inset.");
    }
}
