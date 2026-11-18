using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VanyaTools.Native;

internal static class AlternativeAndEdgeChecks
{
    private static void Reject(Action action,string message){bool failed=false;try{action();}catch(Exception){failed=true;}Program.Check(failed,message);}
    internal static void Run(string root){Model(root);Edges();Smoothing();Batch(root);Ui(root);}
    private static void Model(string root)
    {
        var set=new LetterTransferSet{Reference="reference.png",GenerationModel=LetterTransferModels.Seedream};string directory=LetterTransferStore.Create(Path.Combine(root,"seedream"),set);
        LetterTransferImages.Save(Program.Solid(80,80,Colors.Gold),LetterTransferStore.Resolve(directory,set.Reference));
        string[] letters=LetterTransferRules.Alphabet.Select(c=>c.ToString()).ToArray();
        var job=LetterTransferAi.Create(directory,set,letters,LetterTransferModels.Seedream,"4K");
        Program.Check(job.Prompt.Length<=4000 && job.Prompt.Contains("EXACTLY 33") && job.Prompt.Contains("6 columns by 6 rows") && job.Prompt.Contains(String.Join(", ",letters)) && job.Prompt.Contains("U+0401") && job.Prompt.Contains("U+0419"),"Compact Seedream prompt lost complete Cyrillic alphabet or accent instructions.");
        var input=LetterTransferModels.Input(job,"replicate-file:synthetic.png");
        Program.Check((string)input["size"]=="4K" && (string)input["sequential_image_generation"]=="disabled" && (int)input["max_images"]==1 && !input.ContainsKey("resolution") && !input.ContainsKey("output_format") && ((string[])input["image_input"]).Length==1,"Seedream payload does not match its actual schema.");
        int calls=ReplicateWorkerClient.Calls;LetterTransferAi.Generate(directory,job,"synthetic-key",null,CancellationToken.None);
        Program.Check(ReplicateWorkerClient.Calls==calls+1 && ReplicateWorkerClient.LastModel==LetterTransferModels.Seedream && (string)ReplicateWorkerClient.Input["size"]=="4K" && (string)ReplicateWorkerClient.Input["prompt"]==job.Prompt,"Generation did not use the selected alternative or exact confirmed prompt.");
        LetterTransferAi.Generate(directory,job,"synthetic-key",null,CancellationToken.None);Program.Check(ReplicateWorkerClient.Calls==calls+1,"Cached alternative response created a duplicate request.");
        Program.Check(LetterTransferStore.Load(directory).Jobs.Single().Model==LetterTransferModels.Seedream && LetterTransferStore.Load(directory).GenerationModel==LetterTransferModels.Seedream,"Saved set lost alternative selection/history.");
        job.Resolution="2K";input=LetterTransferModels.Input(job,"reference");Program.Check((string)input["size"]=="2K","Alternative 2K selection ignored.");job.Resolution="4K";
        set.Notes=new string('Ж',4000);int jobs=set.Jobs.Count;
        Reject(()=>LetterTransferAi.Create(directory,set,letters,LetterTransferModels.Seedream,"4K"),"Oversize Seedream prompt was submitted/truncated.");
        Program.Check(set.Jobs.Count==jobs && ReplicateWorkerClient.Calls==calls+1,"Prompt rejection still created a model/job.");set.Notes="";
        Reject(()=>LetterTransferAi.Create(directory,set,letters,"unknown/model","4K"),"Unsupported generation model was accepted.");
        // The provider can return JPEG even though the durable output path ends in
        // .png. Decoder selection must follow bytes, and separated glyphs are PNG.
        var jpeg=new JpegBitmapEncoder();jpeg.Frames.Add(BitmapFrame.Create(Program.Solid(80,80,Colors.Gold)));using(var stream=File.Create(LetterTransferStore.Resolve(directory,job.Output)))jpeg.Save(stream);
        Program.Check(LetterTransferAi.HasResult(directory,job),"JPEG alternative response was considered missing and would be regenerated.");
        LetterTransferAi.Generate(directory,job,"synthetic-key",null,CancellationToken.None);Program.Check(ReplicateWorkerClient.Calls==calls+1,"JPEG cache triggered a paid repeat.");
        var glyph=LetterTransferImages.Add(directory,set,"А",LetterTransferImages.Load(LetterTransferStore.Resolve(directory,job.Output)),"test");
        Program.Check(File.ReadAllBytes(LetterTransferStore.Resolve(directory,glyph.File)).Take(4).SequenceEqual(new byte[]{137,80,78,71}),"Alternative glyph was not saved as PNG.");
    }
    private static void Edges()
    {
        const int w=80,h=80;var bytes=new byte[w*h*4];
        void Paint(int x,int y,Color color){int p=(y*w+x)*4;bytes[p]=color.B;bytes[p+1]=color.G;bytes[p+2]=color.R;bytes[p+3]=255;}
        for(int y=10;y<60;y++)for(int x=10;x<60;x++)Paint(x,y,x==10 || y==10 || x==59 || y==59?Colors.Magenta:Colors.Gold);
        for(int y=28;y<40;y++)for(int x=28;x<40;x++){int p=(y*w+x)*4;bytes[p]=bytes[p+1]=bytes[p+2]=bytes[p+3]=0;}
        // One-pixel line and detached dots must survive as meaningful thin ink.
        for(int y=15;y<40;y++)Paint(70,y,Colors.Black);Paint(20,2,Colors.Gold);Paint(30,2,Colors.Gold);
        var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,bytes,w*4);image.Freeze();
        var original=LetterTransferImages.Pixels(image);var trimmed=LetterTransferEdges.Trim(image,1,130,CancellationToken.None);var result=LetterTransferImages.Pixels(trimmed);
        Program.Check(!Enumerable.Range(0,result.Length/4).Any(p=>result[p*4+3]!=0 && result[p*4]==255 && result[p*4+1]==0 && result[p*4+2]==255),"One-pixel background rim survived the edge inset.");
        Program.Check(Enumerable.Range(0,result.Length/4).Count(p=>result[p*4+3]!=0 && result[p*4]==0 && result[p*4+1]==0 && result[p*4+2]==0)==25,"Thin stroke was erased by inset.");
        Program.Check(Enumerable.Range(0,trimmed.PixelWidth).Count(x=>result[x*4+3]>0)==2,"Detached accent dots disappeared after edge cleanup.");
        Program.Check(trimmed.IsFrozen && original.SequenceEqual(LetterTransferImages.Pixels(image)),"Edge cleanup mutated the source bitmap.");
        Program.Check(result.Where((b,i)=>i%4==3).All(a=>a==0 || a==255) && Enumerable.Range(0,result.Length/4).All(p=>result[p*4+3]!=0 || (result[p*4]==0 && result[p*4+1]==0 && result[p*4+2]==0)),"Inset reintroduced partial alpha or hidden RGB.");
        var solid=Program.Solid(40,40,Colors.Gold);var inset=LetterTransferEdges.Trim(solid,1,130,CancellationToken.None);
        Program.Check(inset.PixelWidth==38 && inset.PixelHeight==38,"One-pixel inset did not remove each outer edge.");
        Program.Check(LetterTransferEdges.Trim(solid,0,130,CancellationToken.None).PixelWidth==40,"Zero inset changed the edge.");
        var canceled=new CancellationTokenSource();canceled.Cancel();Reject(()=>LetterTransferEdges.Trim(image,1,130,canceled.Token),"Canceled edge cleanup continued.");
        Reject(()=>LetterTransferEdges.Trim(image,7,130,CancellationToken.None),"Unbounded inset accepted.");
    }
    private static void Smoothing()
    {
        const int size=80;var data=new byte[size*size*4];
        void Ink(int x,int y){int p=(y*size+x)*4;data[p]=30;data[p+1]=120;data[p+2]=210;data[p+3]=255;}
        for(int y=20;y<60;y++)for(int x=20;x<60;x++)Ink(x,y);
        Ink(60,39); // isolated one-pixel spur attached to a straight border
        Ink(2,2);Ink(10,2);Ink(11,2);Ink(10,3);Ink(11,3); // isolated specks
        for(int n=0;n<16;n++)Ink(5+n,65+n/2); // a thin stepped stroke
        for(int y=3;y<11;y++)for(int x=40;x<48;x++)Ink(x,y); // meaningful detached accent
        var image=BitmapSource.Create(size,size,96,96,PixelFormats.Bgra32,null,data,size*4);image.Freeze();
        var plain=LetterTransferEdges.Process(image,0,130,false,CancellationToken.None);var clean=LetterTransferEdges.Process(image,0,130,true,CancellationToken.None);
        var pixels=LetterTransferImages.Pixels(clean);int w=clean.PixelWidth;
        // The surviving detached accent defines y=3 and the thin line x=6 after
        // endpoint smoothing. Compare components rather than relying on a crop.
        int tiny=Components(pixels,w,clean.PixelHeight).Count(n=>n<=4);
        Program.Check(tiny==0,"Smoothing retained isolated tiny debris.");
        Program.Check(clean.PixelWidth==54,"Smoothing retained a one-pixel spur on a straight border.");
        Program.Check(pixels.Where((b,i)=>i%4==3).All(a=>a==0 || a==255),"Smoothing introduced semitransparency.");
        Program.Check(Components(pixels,w,clean.PixelHeight).Any(n=>n>=40 && n<=70),"Smoothing erased a detached accent.");
        Program.Check(Components(pixels,w,clean.PixelHeight).Any(n=>n>=10 && n<=25),"Smoothing erased a thin stroke.");
        Program.Check(LetterTransferImages.Fingerprint(plain)!=LetterTransferImages.Fingerprint(clean) && data.SequenceEqual(LetterTransferImages.Pixels(image)),"Smoothing had no effect or mutated source ink.");
        Program.Check(Enumerable.Range(0,pixels.Length/4).Where(p=>pixels[p*4+3]!=0).All(p=>pixels[p*4]==30 && pixels[p*4+1]==120 && pixels[p*4+2]==210),"Mask smoothing blurred foreground colors or pulled in transparent RGB.");
    }
    private static int[] Components(byte[] data,int width,int height)
    {
        var result=new System.Collections.Generic.List<int>();var seen=new bool[width*height];
        for(int start=0;start<seen.Length;start++)
        {
            if(seen[start] || data[start*4+3]==0)continue;var todo=new System.Collections.Generic.Queue<int>();todo.Enqueue(start);seen[start]=true;int count=0;
            while(todo.Count>0){int p=todo.Dequeue(),x=p%width,y=p/width;count++;for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++){int nx=x+dx,ny=y+dy;if(nx<0 || ny<0 || nx>=width || ny>=height)continue;int next=ny*width+nx;if(seen[next] || data[next*4+3]==0)continue;seen[next]=true;todo.Enqueue(next);}}
            result.Add(count);
        }
        return result.ToArray();
    }
    private static void Batch(string root)
    {
        var set=new LetterTransferSet{EdgeTrimPx=0};string directory=LetterTransferStore.Create(Path.Combine(root,"edge-batch"),set);
        foreach(string letter in new[]{"А","Б"})LetterTransferImages.Add(directory,set,letter,Program.Solid(40,40,Colors.Gold),"original");
        LetterTransferStore.Save(directory,set);var ids=set.Glyphs.Select(g=>g.Id).ToArray();var originals=set.Glyphs.Select(g=>LetterTransferStore.Resolve(directory,g.OriginalFile)).ToArray();var sourceBytes=originals.Select(File.ReadAllBytes).ToArray();
        set.EdgeTrimPx=1;int calls=ReplicateWorkerClient.Calls;
        Program.Check(LetterTransferEdges.Reclean(directory,set,CancellationToken.None)==2 && set.Glyphs.All(g=>g.Width==38 && g.Height==38 && g.EdgeTrimPx==1) && set.Glyphs.Select(g=>g.Id).SequenceEqual(ids),"Batch cleanup lost glyph identity, variants or correct inset.");
        string once=LetterTransferImages.Fingerprint(LetterTransferImages.Load(LetterTransferStore.Resolve(directory,set.Glyphs[0].File)));
        LetterTransferEdges.Reclean(directory,set,CancellationToken.None);Program.Check(once==LetterTransferImages.Fingerprint(LetterTransferImages.Load(LetterTransferStore.Resolve(directory,set.Glyphs[0].File))),"Repeated cleanup accumulated erosion.");
        set.EdgeTrimPx=0;set.SmoothEdges=false;LetterTransferEdges.Reclean(directory,set,CancellationToken.None);
        Program.Check(set.Glyphs.All(g=>g.Width==40 && g.Height==40) && originals.Select((p,i)=>File.ReadAllBytes(p).SequenceEqual(sourceBytes[i])).All(b=>b),"Zero trim failed to restore the original or source files were overwritten.");
        Program.Check(ReplicateWorkerClient.Calls==calls && LetterTransferStore.Load(directory).Glyphs.Select(g=>g.Id).SequenceEqual(ids),"Local cleanup called AI or lost durable glyphs.");
        byte[] saved=File.ReadAllBytes(Path.Combine(directory,"set.json"));var list=set.Glyphs;int files=Directory.GetFiles(Path.Combine(directory,"glyphs")).Length;set.EdgeTrimPx=2;
        Reject(()=>LetterTransferEdges.Reclean(directory,set,CancellationToken.None,(_,__)=>{throw new IOException("Synthetic save failure");}),"Batch save failure ignored.");
        Program.Check(ReferenceEquals(list,set.Glyphs) && saved.SequenceEqual(File.ReadAllBytes(Path.Combine(directory,"set.json"))) && Directory.GetFiles(Path.Combine(directory,"glyphs")).Length==files,"Failed cleanup partially replaced metadata/files.");
    }
    private static void Ui(string root)
    {
        var set=new LetterTransferSet{Reference="reference.png"};string library=Path.Combine(root,"alternative-ui"),directory=LetterTransferStore.Create(library,set);
        LetterTransferImages.Save(Program.Solid(80,80,Colors.Gold),LetterTransferStore.Resolve(directory,set.Reference));LetterTransferStore.Save(directory,set);
        int confirmations=0,calls=ReplicateWorkerClient.Calls;string model=null,prompt=null,resolution=null;int count=0;
        var window=new LetterTransferWindow(()=>"synthetic-key",()=>null,(_,__)=>{},library,confirm:(reference,letters,m,r,p,retry)=>{confirmations++;model=m;prompt=p;resolution=r;count=letters.Length;return false;});
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        try
        {
            var choice=(ComboBox)window.GetType().GetField("_model",flags).GetValue(window);choice.SelectedIndex=2;
            window.GetType().GetMethod("Generate",flags).Invoke(window,new object[]{false});
            Program.Check(confirmations==1 && model==LetterTransferModels.Seedream && count==37 && resolution=="4K" && prompt.Length<=4000 && ReplicateWorkerClient.Calls==calls,"Alternative bypassed exact preview consent, split alphabet or sent declined request.");
            ((Slider)window.GetType().GetField("_edgeTrim",flags).GetValue(window)).Value=2;
            window.GetType().GetMethod("Save",flags).Invoke(window,null);
            var saved=LetterTransferStore.Load(directory);Program.Check(saved.GenerationModel==LetterTransferModels.Seedream && saved.EdgeTrimPx==2 && saved.SmoothEdges,"Selected model or edge inset was not saved.");
        }
        finally{window.Close();}
    }
}
