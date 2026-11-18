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
using System.Windows.Threading;
using VanyaTools.Native;

internal static class WorkflowChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object value,string name)=>(T)value.GetType().GetField(name,Flags).GetValue(value);
    private static void Call(object value,string method,params object[] args)=>value.GetType().GetMethod(method,Flags).Invoke(value,args);
    private static void Pump(Func<bool> finished)
    {
        var deadline=DateTime.UtcNow.AddSeconds(30);
        while(!finished() && DateTime.UtcNow<deadline){var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(5);}
        Program.Check(finished(),"Letter workflow did not return control.");
    }
    internal static void Run(string root)
    {Interface(root);Saved(root,"cached");Saved(root,"active");Saved(root,"unknown");Interrupted(root,false);Interrupted(root,true);LegacyAnalysis(root);}
    private static void Interface(string root)
    {
        var window=new LetterTransferWindow(()=>"synthetic-key",()=>null,(_,__)=>{},Path.Combine(root,"workflow-ui"),confirm:(a,b,c,d,e,f)=>false);
        try
        {
            var set=Field<LetterTransferSet>(window,"_set");
            Program.Check(LetterTransferWorkflow.EdgeMode(set)==0,"New set did not use the simple crisp edge preset.");
            Program.Check(Field<TabControl>(window,"_steps").Items.Count==3 && Field<TabControl>(window,"_steps").SelectedIndex==0 && Field<TabControl>(window,"_views").SelectedIndex==3,"New project did not begin with the reference step.");
            Program.Check(!Field<Expander>(window,"_edgeExpert").IsExpanded && Field<DataGrid>(window,"_table").Columns[1].Visibility==Visibility.Collapsed,"Advanced edge/weight controls leaked into the main flow.");
            string directory=Field<string>(window,"_directory");var glyph=LetterTransferImages.Add(directory,set,"А",Program.Solid(45,60,Colors.RoyalBlue),"manual");Call(window,"Save");Call(window,"RefreshAssets");Call(window,"Preview");
            string original=LetterTransferStore.Resolve(directory,glyph.OriginalFile);var raw=File.ReadAllBytes(original);var old=File.ReadAllBytes(LetterTransferStore.Resolve(directory,glyph.File));
            Field<ComboBox>(window,"_edgeQuality").SelectedIndex=1;Call(window,"Save");
            Program.Check(LetterTransferWorkflow.EdgeMode(set)==1 && !set.SharpContour && set.EdgeTrimPx==0,"Original silhouette preset did not remain reversible.");
            Field<ComboBox>(window,"_edgeQuality").SelectedIndex=0;Call(window,"Save");
            Program.Check(LetterTransferWorkflow.EdgeMode(LetterTransferStore.Load(directory))==0,"Simple edge preset did not persist through JSON.");
            Program.Check(LetterTransferSourceRecovery.Rebuild(directory,set,CancellationToken.None,null,true)==1 && set.Glyphs[0].Id==glyph.Id && raw.SequenceEqual(File.ReadAllBytes(original)),"Unified processing lost an imported variant or overwrote its source.");
            var processed=LetterTransferImages.Load(LetterTransferStore.Resolve(directory,set.Glyphs[0].File));
            Program.Check(LetterTransferImages.Pixels(processed).Where((b,i)=>i%4==3).All(a=>a==0 || a==255) && Math.Abs(processed.PixelHeight*25.4/processed.DpiY-set.HeightMm)<.1,"Simple edge processing introduced partial alpha or changed physical height.");
            string first=LetterTransferImages.Fingerprint(processed);LetterTransferSourceRecovery.Rebuild(directory,set,CancellationToken.None,null,true);
            Program.Check(first==LetterTransferImages.Fingerprint(LetterTransferImages.Load(LetterTransferStore.Resolve(directory,set.Glyphs[0].File))) && old.SequenceEqual(File.ReadAllBytes(LetterTransferStore.Resolve(directory,glyph.File))),"Repeated edge update accumulated damage or removed the previous PNG.");
            double previousHeight=set.HeightMm;Field<TextBox>(window,"_height").Text="0";bool invalid=false;try{Call(window,"Save");}catch(TargetInvocationException){invalid=true;}
            Program.Check(invalid && set.HeightMm==previousHeight && ReferenceEquals(Field<LetterTransferSet>(window,"_set"),set),"Intermediate invalid input corrupted the active set or detached bindings.");Field<TextBox>(window,"_height").Text=previousHeight.ToString(System.Globalization.CultureInfo.InvariantCulture);Call(window,"Save");
            Field<TextBox>(window,"_target").Text="150";Call(window,"Save");Program.Check(set.Letters.Sum(r=>r.Copies)==150,"Copy count still required a separate distribution button.");
            foreach(int step in new[]{0,1,2})
            {
                Call(window,"SelectStep",step);Program.Check(Field<TabControl>(window,"_views").SelectedIndex==(step==0?3:step==1?1:0),"Step did not select its matching workspace.");
                Program.Check(Field<FrameworkElement>(window,"_countsPanel").Visibility==(step==2?Visibility.Visible:Visibility.Collapsed),"Frequency table appeared outside the print step.");
                var visual=(FrameworkElement)window.Content;var size=new Size(1120,760);visual.Measure(size);visual.Arrange(new Rect(size));visual.UpdateLayout();
                string render=Environment.GetEnvironmentVariable("VANYA_LETTER_RENDER");if(!String.IsNullOrEmpty(render)){var image=new RenderTargetBitmap(1120,760,96,96,PixelFormats.Pbgra32);var background=new DrawingVisual();using(var dc=background.RenderOpen())dc.DrawRectangle(Brushes.White,null,new Rect(size));image.Render(background);image.Render(visual);LetterTransferImages.Save(image,Path.Combine(render,"workflow-step-"+(step+1)+".png"));}
            }
        }
        finally{window.Close();}
        var legacy=new LetterTransferSet{EdgeTrimPx=2,SharpContour=false,ContourSmoothPx=2.25};
        Program.Check(LetterTransferWorkflow.EdgeMode(legacy)==2 && legacy.EdgeTrimPx==2,"Reading an old manual preset changed saved edge settings.");
    }
    private static BitmapSource Sheet(int count,string key)
    {
        int columns=LetterTransferRules.Columns(count),rows=(count+columns-1)/columns,w=columns*100,h=rows*100;var data=LetterTransferImages.Pixels(Program.Solid(w,h,(Color)ColorConverter.ConvertFromString(key)));
        for(int n=0;n<count;n++)for(int y=20;y<80;y++)for(int x=25;x<75;x++){int p=((n/columns*100+y)*w+n%columns*100+x)*4;data[p]=190;data[p+1]=100;data[p+2]=20;data[p+3]=255;}
        var image=BitmapSource.Create(w,h,96,96,PixelFormats.Bgra32,null,data,w*4);image.Freeze();return image;
    }
    private static string Labels(string[] letters)=>new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new{cells=letters.Select((letter,i)=>new{id=i+1,letter,uncertain=false}).ToArray()});
    private static void Saved(string root,string kind)
    {
        string library=Path.Combine(root,"workflow-"+kind);var set=new LetterTransferSet{Reference="reference.png"};string directory=LetterTransferStore.Create(library,set);
        LetterTransferImages.Save(Program.Solid(50,50,Colors.Gold),LetterTransferStore.Resolve(directory,set.Reference));var job=LetterTransferAi.Create(directory,set,new[]{"А"},LetterTransferModels.Nano2,"4K");string output=LetterTransferStore.Resolve(directory,job.Output);
        if(kind=="cached")
        {
            LetterTransferImages.Save(Sheet(1,job.Key),output);var sheet=LetterTransferSheet.Read(LetterTransferImages.Load(output),job.Letters,job.Key,130,CancellationToken.None);var plan=LetterTransferRecognition.Prepare(directory,job,sheet);File.WriteAllText(plan.Output,Labels(job.Letters));
        }
        else if(kind=="active")File.WriteAllText(output+".prediction.json","{\"status\":\"processing\"}");else File.WriteAllText(output+".submitted","sent");
        int approvals=0,analyses=0,reviews=0,calls=ReplicateWorkerClient.Calls,resumes=ReplicateWorkerClient.ResumeCalls,recoveries=ReplicateWorkerClient.Recoveries;
        var window=new LetterTransferWindow(()=>"synthetic-key",()=>null,(_,__)=>{},library,confirm:(a,b,c,d,e,f)=>{approvals++;return false;},confirmRecognition:(a,b,c,d)=>{analyses++;return false;},review:(a,b,sheet)=>{reviews++;return sheet.Cells.Select(c=>new LetterTransferApproved{Letter=c.Letter,Image=c.Image,Original=c.Original,Bounds=c.Bounds,SourceJobId=c.SourceJobId}).ToList();});
        ReplicateWorkerClient.GenerationOutput=path=>LetterTransferImages.Save(Sheet(1,job.Key),path);
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            Call(window,"Generate",false);Pump(()=>!Field<bool>(window,"_sequenceBusy") && Field<CancellationTokenSource>(window,"_request")==null);
            var saved=LetterTransferStore.Load(directory);
            Program.Check(approvals==0 && ReplicateWorkerClient.Calls==calls && saved.Jobs.Count==1,"Saved/in-flight request created another generation or requested a new generation approval: "+kind);
            if(kind=="unknown")Program.Check(reviews==0 && saved.Glyphs.Count==0 && ReplicateWorkerClient.Recoveries==recoveries+1,"Unknown submission was not safely recovered/blocked.");
            else Program.Check(reviews==1 && saved.Glyphs.Count==1 && analyses==(kind=="cached"?0:1) && ReplicateWorkerClient.ResumeCalls==resumes+(kind=="active"?1:0),"Saved result did not continue through one review: "+kind);
        }
        finally{ReplicateWorkerClient.GenerationOutput=null;window.Close();SynchronizationContext.SetSynchronizationContext(context);}
    }
    private static void Interrupted(string root,bool cancel)
    {
        string library=Path.Combine(root,"workflow-interrupt-"+cancel);var set=new LetterTransferSet{Reference="reference.png",VariantsPerLetter=4};string directory=LetterTransferStore.Create(library,set);LetterTransferImages.Save(Program.Solid(50,50,Colors.Gold),LetterTransferStore.Resolve(directory,set.Reference));LetterTransferStore.Save(directory,set);
        int approvals=0,reviews=0,generations=0,calls=ReplicateWorkerClient.Calls;
        var window=new LetterTransferWindow(()=>"synthetic-key",()=>null,(_,__)=>{},library,confirm:(a,b,c,d,e,f)=>{approvals++;return true;},confirmRecognition:(a,b,c,d)=>throw new Exception("Unexpected extra recognition consent"),review:(a,b,sheet)=>{reviews++;return sheet.Cells.Select(c=>new LetterTransferApproved{Letter=c.Letter,Image=c.Image,Original=c.Original,Bounds=c.Bounds,SourceJobId=c.SourceJobId}).ToList();});
        ReplicateWorkerClient.GenerationOutput=output=>{
            generations++;if(!cancel && generations==2)throw new IOException("Controlled provider failure");var job=LetterTransferStore.Load(directory).Jobs.Last(j=>LetterTransferStore.Resolve(directory,j.Output)==output);
            ReplicateWorkerClient.RecognitionResponse=Labels(job.Letters);
            if(cancel){window.Dispatcher.BeginInvoke(new Action(()=>Field<Button>(window,"_cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));Thread.Sleep(80);}
            LetterTransferImages.Save(Sheet(job.Letters.Length,job.Key),output);
        };
        var context=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        try
        {
            Call(window,"Generate",false);Pump(()=>!Field<bool>(window,"_sequenceBusy") && Field<CancellationTokenSource>(window,"_request")==null);var saved=LetterTransferStore.Load(directory);
            Program.Check(approvals==1 && generations==(cancel?1:2) && ReplicateWorkerClient.Calls==calls+(cancel?1:3),"Stopping a series still submitted remaining jobs.");
            Program.Check(cancel?saved.Jobs.Count==1 && saved.Glyphs.Count==0 && reviews==0:saved.Jobs.Count==2 && saved.Glyphs.Count==37 && reviews==1,"Interrupting a series lost a completed sheet or imported an unreviewed one.");
        }
        finally{ReplicateWorkerClient.GenerationOutput=null;ReplicateWorkerClient.RecognitionResponse=null;window.Close();SynchronizationContext.SetSynchronizationContext(context);}
    }
    private static void LegacyAnalysis(string root)
    {
        var set=new LetterTransferSet{Reference="reference.png"};string directory=LetterTransferStore.Create(Path.Combine(root,"legacy-analysis"),set);LetterTransferImages.Save(Program.Solid(50,50,Colors.Gold),LetterTransferStore.Resolve(directory,set.Reference));var job=LetterTransferAi.Create(directory,set,new[]{"А"},LetterTransferModels.Nano2,"4K");var sheet=LetterTransferSheet.Read(Sheet(1,job.Key),job.Letters,job.Key,130,CancellationToken.None);var plan=LetterTransferRecognition.Prepare(directory,job,sheet);
        string legacy=LetterTransferStore.Resolve(directory,job.Output)+".labels-"+plan.Fingerprint.Substring(0,24);File.WriteAllText(legacy+".txt",Labels(job.Letters));var cached=LetterTransferRecognition.Prepare(directory,job,sheet);
        Program.Check(cached.Prefix==legacy && LetterTransferRecognition.TryCached(cached,out _),"Short analysis names lost a legacy paid cache.");File.Delete(legacy+".txt");File.WriteAllText(legacy+".txt.submitted","sent");var pending=LetterTransferRecognition.Prepare(directory,job,sheet);
        Program.Check(pending.Prefix==legacy,"Short analysis names abandoned an unknown legacy submission.");
    }
}
