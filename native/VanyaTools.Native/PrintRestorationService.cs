using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace VanyaTools.Native
{
    internal static class PrintRestorationService
    {
        private static string LastPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VanyaTools","last-print-restoration.txt");
        internal static PrintRestorationHost CreateHost(Func<string> token)
        {
            return new PrintRestorationHost {
                Validate=()=>{if(String.IsNullOrWhiteSpace(token()))throw new InvalidOperationException("Укажите ключ Replicate в настройках ИИ.");},
                Confirm=PrintRestorationReviewWindow.Confirm,
                Review=PrintRestorationReviewWindow.Review,
                Generate=(request,progress,cancel)=> {
                    string key=token(); // Read settings on the UI thread, never from the worker.
                    return Task.Run(()=>Generate(request,key,progress,cancel),cancel);
                },
                RemoveBackground=(owner,source,color)=> {
                    var window=new BackgroundRemovalWindow(source,new BackgroundRemovalOptions{Color=color,Tolerance=10},applyCaption:"Принять фон") {Owner=owner};
                    return window.ShowDialog()==true?window.OutputPath:null;
                },
                Crop=(source,output,bounds,cancel)=>{if(!PrintRestorationNativeCrop.TryCrop(source,output,bounds,cancel))PrintRestorationImages.Crop(source,output,bounds,cancel);},
                LastResult=()=>File.Exists(LastPath)?File.ReadAllText(LastPath).Trim():null
            };
        }
        private static string Generate(PrintRestorationRequest request,string token,Action<string> progress,CancellationToken cancel)
        {
            var input=request.Input();string raw=Path.Combine(Path.GetDirectoryName(request.OutputPath),"model-output.png");
            try{ReplicateWorkerClient.Run(PrintRestorationRequest.Model,token,input,raw,progress,cancel);}
            catch
            {
                if(File.Exists(raw) && new FileInfo(raw).Length>0)try{Finish();}catch(Exception error){Log.Error("Could not retain interrupted restoration output.",error);}
                throw;
            }
            return Finish();
            string Finish()
            {
                // Keep a paid, completed image even when the owner closes at this point.
                PrintRestorationImages.SetPhysicalSize(raw,request.OutputPath,request.WidthMm,request.HeightMm,CancellationToken.None);
                try {Directory.CreateDirectory(Path.GetDirectoryName(LastPath));File.WriteAllText(LastPath,request.OutputPath);}catch(Exception error){Log.Error("Could not save restoration result pointer.",error);}
                return request.OutputPath;
            }
        }
    }
}
