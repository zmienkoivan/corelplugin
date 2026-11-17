using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VanyaTools.Native;
internal static class PatternRequestChecks
{
    private const BindingFlags Flags=BindingFlags.Instance|BindingFlags.NonPublic;
    private static T Field<T>(object target,string name)=>(T)target.GetType().GetField(name,Flags).GetValue(target);
    private static void Wait(Task task)
    {
        DateTime deadline=DateTime.UtcNow.AddSeconds(10);
        while(!task.IsCompleted && DateTime.UtcNow<deadline)
        {var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);Thread.Sleep(5);}
        if(!task.IsCompleted)throw new Exception("Saved request UI did not finish");task.GetAwaiter().GetResult();
    }
    internal static void Run(Action<bool,string> check)
    {
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        string root=Path.Combine(Path.GetTempPath(),"Vanya-PatternRequest-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            var reference=AiPrintTab.Bitmap("synthetic");var plan=PatternFillerBrief.Preset(0);plan.SmallFillers=true;
            var job=PatternFillerRequests.Create(reference,plan,PatternFillerBrief.GptMiniModel,true,root);string output=job.OutputPath;
            int submissions=0;ReplicateWorkerClient.BeforeRun=(path,resume)=>{if(!resume)submissions++;File.WriteAllText(path+".submitted","sent");File.WriteAllText(path+".prediction.json","{\"id\":\"saved-synthetic\",\"status\":\"processing\"}");};
            ReplicateWorkerClient.Failure=new InvalidOperationException("Связь оборвалась при проверке запроса. Нажмите «Повторить запрос».");
            bool failed=false;try{job.Run("synthetic",false,CancellationToken.None);}catch(InvalidOperationException){failed=true;}
            var reopened=PatternFillerRequests.Find(reference,plan.Source,root);
            check(failed && reopened!=null && reopened.OutputPath==output && reopened.SmallFillers && File.Exists(Path.Combine(job.DirectoryPath,"reference.png")),"A failed pattern request lost its reference, plan or stable output path");
            bool blocked=false;try{PatternFillerRequests.Create(reference,plan,PatternFillerBrief.GptMiniModel,true,root);}catch(InvalidOperationException){blocked=true;}
            check(blocked && submissions==1,"A second new request was allowed while the first was unresolved");
            var window=new PatternFillerWindow(reference,"synthetic",new PatternFillerPlan(),requestRoot:root);
            try
            {
                check(Field<Button>(window,"_resume").Visibility==Visibility.Visible && Field<Button>(window,"_resume").IsEnabled && !Field<Button>(window,"_generate").IsEnabled && !Field<Button>(window,"_analyze").IsEnabled,"The failed request offered no continuation button or allowed a paid duplicate");
                Task Resume()=>(Task)typeof(PatternFillerWindow).GetMethod("Execute",Flags).Invoke(window,new object[]{true,true});
                Wait(Resume());Wait(Resume());
                check(submissions==1 && ReplicateWorkerClient.ResumeOnly && ReplicateWorkerClient.Output==output && Field<Button>(window,"_resume").IsEnabled,"Two continuation failures resubmitted the model, changed ID/path or hid the button");
                check(Field<TextBlock>(window,"_message").Text.Contains("Продолжить запрос") && !Field<TextBlock>(window,"_message").Text.Contains("Повторить запрос"),"Pattern error still points at a nonexistent button");
                ReplicateWorkerClient.Failure=null;Wait(Resume());
                check(submissions==1 && Field<Button>(window,"_resume").Visibility==Visibility.Collapsed && Field<Button>(window,"_generate").IsEnabled && PatternFillerRequests.Find(reference,plan.Source,root)==null,"Successful continuation did not release the UI or submitted another model");
            }
            finally{window.Close();}
            ReplicateWorkerClient.BeforeRun=null;ReplicateWorkerClient.Failure=null;
            var generation=PatternFillerRequests.Create(reference,plan,SublimationModels.Nano2,false,root);generation.Run("synthetic",false,CancellationToken.None);
            var result=generation.ReadGeneration(CancellationToken.None);int calls=ReplicateWorkerClient.Calls;
            generation.Run("synthetic",true,CancellationToken.None);
            check(result.Count==4 && ReplicateWorkerClient.Calls==calls && File.Exists(generation.OutputPath),"Reading an already downloaded sheet sent another request or discarded the paid original");
            var ready=PatternFillerRequests.Find(reference,plan.Source,root);check(ready!=null && ready.OutputPath==generation.OutputPath,"A downloaded result was lost when reopening the unfinished request");
            generation.Finish();
            var absent=PatternFillerRequests.Create(reference,plan,SublimationModels.Nano2,false,root);blocked=false;try{absent.Run("synthetic",true,CancellationToken.None);}catch(InvalidOperationException){blocked=true;}
            check(blocked && ReplicateWorkerClient.Calls==calls,"Resume without a receipt started a new model");
            File.WriteAllText(absent.OutputPath+".prediction.json","{\"id\":\"terminal\",\"status\":\"failed\"}");
            check(!absent.CanResume && PatternFillerRequests.Find(reference,plan.Source,root)==null,"A terminal failed request permanently blocked a separately confirmed new attempt");
            var malformed=PatternFillerRequests.Create(reference,plan,PatternFillerBrief.GptMiniModel,true,root);File.WriteAllText(malformed.OutputPath+".prediction.json","{\"id\":\"bad-text\",\"status\":\"processing\"}");
            ReplicateWorkerClient.TextOutput="invalid response";var repairable=new PatternFillerWindow(reference,"synthetic",plan,requestRoot:root);
            try
            {
                Wait((Task)typeof(PatternFillerWindow).GetMethod("Execute",Flags).Invoke(repairable,new object[]{true,true}));
                check(Field<Button>(repairable,"_analyze").IsEnabled && Field<Button>(repairable,"_resume").Visibility==Visibility.Collapsed && File.ReadAllText(malformed.OutputPath)=="invalid response","An invalid downloaded answer lost its source or permanently blocked a new confirmed analysis");
            }
            finally{repairable.Close();ReplicateWorkerClient.TextOutput=null;}
            check(!File.ReadAllText(Path.Combine(job.DirectoryPath,"request.json")).Contains("synthetic"),"Request metadata persisted a token");
        }
        finally{ReplicateWorkerClient.BeforeRun=null;ReplicateWorkerClient.Failure=null;ReplicateWorkerClient.TextOutput=null;Directory.Delete(root,true);SynchronizationContext.SetSynchronizationContext(previous);}
    }
}
