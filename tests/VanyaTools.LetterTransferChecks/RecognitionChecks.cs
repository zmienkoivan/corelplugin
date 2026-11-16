using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Web.Script.Serialization;
using VanyaTools.Native;
internal static class RecognitionChecks
{
    private static readonly string[] Targets={"А","Б","В","Г"};
    private static string Reply()=>"{\"cells\":[{\"id\":4,\"letter\":\"В\",\"uncertain\":false},{\"id\":2,\"letter\":\"Б\",\"uncertain\":false},{\"id\":1,\"letter\":\"A\",\"uncertain\":false},{\"id\":3,\"letter\":\"Б\",\"uncertain\":false}]}";
    private static LetterTransferSheet Sheet()=>new LetterTransferSheet{Requested=Targets,LayoutChanged=false,Cells=Targets.Select(l=>new LetterTransferCell{Image=Program.Solid(80,100,Colors.DarkRed),Preview=Program.Solid(80,100,Colors.DarkRed),Letter=l,Safe=true}).ToArray()};
    internal static void Run(string root)
    {
        var labels=LetterTransferRecognition.Parse(Reply(),4);
        Program.Check(labels.Select(l=>l.Letter).SequenceEqual(new[]{"А","Б","Б","В"}),"OCR changed duplicate labels, identifiers or Latin lookalikes.");
        void Reject(string text){bool failed=false;try{LetterTransferRecognition.Parse(text,4);}catch(Exception){failed=true;}Program.Check(failed,"Invalid recognition response was accepted.");}
        Reject(Reply().Replace("\"id\":4","\"id\":2"));Reject(Reply().Replace("\"id\":4","\"id\":9"));Reject(Reply().Replace("false","\"false\""));Reject("{\"cells\":[]}");
        var uncertain=LetterTransferRecognition.Parse(Reply().Replace("\"letter\":\"A\"","\"letter\":\"?\""),4);Program.Check(uncertain[0].Letter==null && uncertain[0].Uncertain,"Ambiguous label was guessed.");
        var job=new LetterTransferJob{Output="requests/result.png"};string directory=Path.Combine(root,"recognition");var sheet=Sheet();var plan=LetterTransferRecognition.Prepare(directory,job,sheet);
        var before=File.ReadAllBytes(plan.PreviewFile);int calls=ReplicateWorkerClient.Calls;
        ReplicateWorkerClient.RecognitionResponse=Reply();var result=LetterTransferRecognition.Run(plan,"synthetic-key",null,CancellationToken.None);
        Program.Check(ReplicateWorkerClient.Calls==calls+1 && (int)ReplicateWorkerClient.Input["max_completion_tokens"]==5000,"Recognition did not use one text-analysis request.");
        Program.Check(File.Exists(plan.PreviewFile) && before.SequenceEqual(File.ReadAllBytes(plan.PreviewFile)) && !File.Exists(ReplicateWorkerClient.LastUpload),"Recognition deleted or changed approved preview.");
        var cached=LetterTransferRecognition.Prepare(directory,job,sheet);Program.Check(LetterTransferRecognition.TryCached(cached,out var restored) && restored.Count(l=>l.Letter=="Б")==2,"Saved recognition lost duplicates.");
        LetterTransferRecognition.Run(cached,"",null,CancellationToken.None);Program.Check(ReplicateWorkerClient.Calls==calls+1,"Cache made another charged request or required token.");
        sheet.Cells[0].Image=Program.Solid(80,100,Colors.DarkBlue);var changed=LetterTransferRecognition.Prepare(directory,job,sheet);Program.Check(changed.Output!=plan.Output && !LetterTransferRecognition.TryCached(changed,out _),"Recognition reused labels for altered artwork.");
        var cts=new CancellationTokenSource();cts.Cancel();bool canceled=false;try{LetterTransferRecognition.Run(changed,"synthetic-key",null,cts.Token);}catch(OperationCanceledException){canceled=true;}
        Program.Check(canceled && ReplicateWorkerClient.Calls==calls+1,"Canceled recognition started another request.");
        // A previously paid malformed reply stays on disk. Only the explicitly
        // requested next attempt gets a new persistent output path.
        ReplicateWorkerClient.RecognitionResponse="{}";bool malformed=false;try{LetterTransferRecognition.Run(changed,"synthetic-key",null,CancellationToken.None);}catch(Exception){malformed=true;}
        string malformedPath=changed.Output;ReplicateWorkerClient.RecognitionResponse=Reply();LetterTransferRecognition.Run(changed,"synthetic-key",null,CancellationToken.None);
        Program.Check(malformed && File.Exists(malformedPath) && changed.Output!=malformedPath && LetterTransferRecognition.Prepare(directory,job,sheet).Output==changed.Output,"Retry lost previous response or pending-attempt identity.");
        sheet=Sheet();labels[0].Uncertain=true;LetterTransferRecognition.Apply(sheet,labels);
        Program.Check(sheet.Cells.Count(c=>c.Letter=="Б")==2 && sheet.Notice.Contains("1"),"Duplicate variants or recognition summary lost.");
        var dialog=LetterTransferReviewWindow.ReviewDialog(null,plan.Preview,plan.Preview,sheet,new List<LetterTransferApproved>());
        try
        {
            var content=(FrameworkElement)dialog.Content;content.Measure(new Size(950,760));content.Arrange(new Rect(0,0,950,760));content.UpdateLayout();
            var checkboxes=Descendants(content).OfType<CheckBox>().Where(c=>(string)c.Tag=="accept").ToArray();
            Program.Check(checkboxes.Length==4 && checkboxes[0].IsChecked!=true && checkboxes.Skip(1).All(c=>c.IsChecked==true),"Uncertain recognition was silently preaccepted or confident variants lost.");
        }
        finally{dialog.Close();}
        var consent=LetterTransferReviewWindow.RecognitionConfirmation(null,plan.Preview,4,plan.Prompt);
        try
        {
            var content=(FrameworkElement)consent.Content;content.Measure(new Size(950,760));content.Arrange(new Rect(0,0,950,760));content.UpdateLayout();var all=Descendants(content).ToArray();
            Program.Check(all.OfType<TextBlock>().Any(t=>t.Text.Contains("платный") && t.Text.Contains("4") && t.Text.Contains("GPT-5 mini")) && all.OfType<Image>().Single().Source==plan.Preview,"Analysis consent omitted exact preview, model, count or billing.");
            Program.Check(all.OfType<Button>().Any(b=>b.IsCancel) && !all.OfType<Button>().Any(b=>b.IsDefault),"Analysis consent has unsafe default.");
        }
        finally{consent.Close();}
        Flow(root);ReplicateWorkerClient.RecognitionResponse=null;
    }
    private static void Flow(string root)
    {
        string library=Path.Combine(root,"recognition-flow");var set=new LetterTransferSet{Name="Recognition flow",Reference="reference.png"};string directory=LetterTransferStore.Create(library,set);
        LetterTransferImages.Save(Program.Solid(60,70,Colors.Green),LetterTransferStore.Resolve(directory,set.Reference));
        var job=LetterTransferAi.Create(directory,set,Targets,"google/nano-banana-2","2K");
        // Four separated white rectangles on magenta, no network generation.
        var image=Program.Solid(200,200,Colors.Magenta);var pixels=LetterTransferImages.Pixels(image);
        for(int n=0;n<4;n++)for(int y=20;y<80;y++)for(int x=20;x<80;x++){int p=((n/2*100+y)*200+n%2*100+x)*4;pixels[p]=pixels[p+1]=pixels[p+2]=255;}
        image=System.Windows.Media.Imaging.BitmapSource.Create(200,200,96,96,PixelFormats.Bgra32,null,pixels,800);image.Freeze();LetterTransferImages.Save(image,LetterTransferStore.Resolve(directory,job.Output));
        bool allow=false,accept=false;int confirmations=0,reviews=0,calls=ReplicateWorkerClient.Calls;ReplicateWorkerClient.RecognitionResponse=Reply();
        var window=new LetterTransferWindow(()=>"synthetic-key",()=>null,(_,__)=>{},library,confirmRecognition:(preview,count,prompt,retry)=>{confirmations++;return allow;},review:(reference,sheet,cells)=>{reviews++;if(!accept)return new List<LetterTransferApproved>();return cells.Cells.Select(c=>new LetterTransferApproved{Letter=c.Letter,Image=c.Image}).ToList();});
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        var activeSet=(LetterTransferSet)typeof(LetterTransferWindow).GetField("_set",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(window);job=activeSet.Jobs.Single();
        void Review(bool missingOnly=false)
        {
            var task=(Task)typeof(LetterTransferWindow).GetMethod("Review",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(window,new object[]{job,false,missingOnly});
            var deadline=DateTime.UtcNow.AddSeconds(5);
            while(!task.IsCompleted && DateTime.UtcNow<deadline){var frame=new DispatcherFrame();window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(5);}
            if(!task.IsCompleted)throw new Exception("Recognition UI did not finish.");task.GetAwaiter().GetResult();
        }
        try
        {
            Review();Program.Check(confirmations==1 && reviews==1 && ReplicateWorkerClient.Calls==calls,"Declining analysis sent preview or skipped manual review.");
            allow=true;Review();Program.Check(confirmations==2 && ReplicateWorkerClient.Calls==calls+1,"Confirmed analysis did not run exactly once.");
            accept=true;Review();var saved=LetterTransferStore.Load(directory);
            Program.Check(confirmations==2 && ReplicateWorkerClient.Calls==calls+1 && saved.Glyphs.Count==4 && saved.Glyphs.Count(g=>g.Letter=="Б")==2 && saved.Jobs.Single().Reviewed,"Cached review recharged, lost labels or discarded duplicate variants.");
            Review();Program.Check(LetterTransferStore.Load(directory).Glyphs.Count==4,"Repeated review imported already accepted source cells again.");
            Review(true);Program.Check(LetterTransferStore.Load(directory).Glyphs.Count==4 && confirmations==2,"Reopening a partially reviewed sheet duplicated existing letters or reran recognition.");
        }
        finally{window.Close();SynchronizationContext.SetSynchronizationContext(previous);}
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {yield return root;for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i)))yield return child;}
}
