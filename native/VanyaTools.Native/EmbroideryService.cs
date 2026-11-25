using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace VanyaTools.Native
{
    internal delegate void EmbroideryWorker(string model,string token,Dictionary<string,object> input,string output,Action<string> progress,CancellationToken cancellation,bool resumeOnly);
    internal static class EmbroideryService
    {
        internal static EmbroideryResult Run(EmbroideryJob job,string token,bool resumeOnly,Action<string> progress,CancellationToken cancel,EmbroideryWorker worker=null)
        {
            job.Request.Validate(job.HasReference);cancel.ThrowIfCancellationRequested();
            if(!File.Exists(job.RawPath) || new FileInfo(job.RawPath).Length==0)
            {
                if(String.IsNullOrWhiteSpace(token))throw new InvalidOperationException("Укажите ключ Replicate в настройках ИИ.");
                if(!resumeOnly && File.Exists(job.StartedPath))throw new InvalidOperationException("Этот запрос уже запускался. Используйте «Продолжить запрос».");
                if(resumeOnly && !File.Exists(job.StartedPath))throw new InvalidOperationException("Сохранённый запрос ещё не запускался. Новая генерация не отправлена.");
                // The shared worker deletes successfully uploaded local files.
                // Keep the history reference and send only a disposable copy.
                string upload=job.HasReference?Path.Combine(job.DirectoryPath,"upload-"+Guid.NewGuid().ToString("N").Substring(0,8)+".png"):null;
                try
                {
                    if(upload!=null)File.Copy(job.ReferencePath,upload);
                    var input=job.Request.Input(upload!=null?"replicate-file:"+upload:null);
                    File.WriteAllText(job.StartedPath,DateTime.UtcNow.ToString("O"));
                    (worker??ReplicateWorkerClient.Run)(job.Request.Model,token,input,job.RawPath,progress,cancel,resumeOnly);
                }
                finally { if(upload!=null && File.Exists(upload))File.Delete(upload); }
            }
            // Never delete a completed paid sheet, even if cancellation or local
            // image processing fails. Resume opens it without another prediction.
            cancel.ThrowIfCancellationRequested();progress?.Invoke("Подготовка четырёх кадров и проверка эскизов…");
            return EmbroideryImages.Prepare(job,cancel);
        }
    }
}
