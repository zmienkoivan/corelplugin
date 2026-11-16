using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows.Threading;
namespace VanyaTools.Native
{
    internal static class DockerRenderingCompatibility{internal static void Attach(System.Windows.FrameworkElement element){} }
    internal static class Log{internal static void Error(string message,Exception error){} }
    internal static class OperationTiming{internal static string Display(string stage,TimeSpan elapsed,string key)=>stage;internal static void Record(string key,TimeSpan elapsed){} }
    internal static class DockerUiWatchdog{internal static IDisposable Trace(Dispatcher dispatcher,string text)=>new Scope();private sealed class Scope:IDisposable{public void Dispose(){} } }
    internal static class CorelApp{internal static object Get()=>throw new Exception("No real Corel allowed.");}
    internal static class ReplicateWorkerClient
    {
        internal static int Calls,Recoveries;internal static Dictionary<string,object> Input;internal static string LastUpload,LastModel;internal static bool Fail;internal static string RecognitionResponse;
        internal static void Run(string model,string token,Dictionary<string,object> input,string output,Action<string> progress,CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();if(File.Exists(output+".submitted") && !File.Exists(output+".prediction.json")){Recoveries++;throw new InvalidOperationException("Synthetic recovery: повтор заблокирован");}Calls++;LastModel=model;Input=input;LastUpload=((string[])input["image_input"])[0].Substring("replicate-file:".Length);
            if(!File.Exists(LastUpload))throw new Exception("Missing upload snapshot.");
            if(Fail)throw new IOException("Synthetic provider failure.");
            if(model==LetterTransferRecognition.Model)File.WriteAllText(output,RecognitionResponse??"{}");else File.Copy(LastUpload,output,true);File.Delete(LastUpload); // real worker cleans uploaded local files
        }
    }
}
