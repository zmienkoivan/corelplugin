using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class LetterTransferAi
    {
        internal static string Prompt(LetterTransferSet set,string[] letters,string key)
        {
            if(letters==null || letters.Length<1 || letters.Length>4 || letters.Any(l=>!LetterTransferRules.ValidLetter(l)) || letters.Distinct().Count()!=letters.Length || !LetterTransferImages.ValidKey(key))
                throw new ArgumentException("Выберите от 1 до 4 разных русских заглавных букв.");
            int columns=letters.Length==1?1:2,rows=(letters.Length+columns-1)/columns;
            return "Create a print-ready collection of EXACTLY "+letters.Length+" separate RUSSIAN CYRILLIC UPPERCASE LETTER designs from the supplied visual style reference. "+
                "This is not a font file and not a product size chart. Keep the reference's letter construction, texture, line quality, palette, outlines and intentional collage/paper backings. "+
                "If the reference is a uniform alphabet, keep a consistent alphabet family; if it is an eclectic collage, use its coherent eclectic language. Do not copy whole words or the reference sheet. "+
                "Required Cyrillic characters: "+String.Join(", ",letters.Select(l=>"«"+l+"» (U+"+((int)l[0]).ToString("X4")+")"))+". Correct letter anatomy is essential; retain Ё dots and Й breve. Do not substitute a Latin lookalike or invent letters. "+
                "Arrange exactly one complete requested character per invisible grid cell: "+columns+" columns by "+rows+" rows, row-major order. "+
                String.Join(" ",letters.Select((l,i)=>"Cell "+(i+1)+": only «"+l+"»."))+" Any unused cell must remain empty. Center each letter, similar cap height, keep at least 10% empty margin on every side of each cell. "+
                "Never tile, repeat, combine two letters, crop or touch a cell border. NO rulers, dimensions, arrows, captions, labels, numbers, grid lines, frames, cast shadows, mockups or products. "+
                "Use an absolutely flat solid technical background "+key+" in all empty space and letter holes. Reserve that exact color for background only; do not use it in the artwork. White ink is artwork and must remain white. Sharp print edges, no colored matte fringe. "+
                "Style choice: "+(set.Style??"По референсу")+". Additional user direction: "+(set.Notes??"");
        }
        internal static LetterTransferJob Create(string directory,LetterTransferSet set,string[] letters,string model,string resolution,BitmapSource approvedReference=null)
        {
            if(set.Jobs.Count>=200)throw new InvalidOperationException("История содержит 200 запросов. Создайте новый набор.");
            var reference=approvedReference??LetterTransferImages.Load(LetterTransferStore.Resolve(directory,set.Reference));
            string key=LetterTransferImages.Key(reference);
            var job=new LetterTransferJob{Letters=letters.ToArray(),Model=model,Resolution=resolution,Key=key};
            job.Reference="requests/"+job.Id+"-reference.png";job.Output="requests/"+job.Id+"-output.png";job.Prompt=Prompt(set,letters,key);
            LetterTransferImages.Save(reference,LetterTransferStore.Resolve(directory,job.Reference));set.Jobs.Add(job);
            try{LetterTransferStore.Save(directory,set);}catch{set.Jobs.Remove(job);throw;}return job;
        }
        internal static bool HasResult(string directory,LetterTransferJob job)
        {
            try{LetterTransferImages.Load(LetterTransferStore.Resolve(directory,job.Output));return true;}
            catch(Exception error)when(error is IOException || error is InvalidDataException || error is NotSupportedException || error is System.IO.FileFormatException || error is ArgumentException){return false;}
        }
        internal static void Generate(string directory,LetterTransferJob job,string token,Action<string> progress,CancellationToken cancel)
        {
            if(String.IsNullOrWhiteSpace(token))throw new InvalidOperationException("Укажите ключ Replicate в настройках ИИ.");
            string output=LetterTransferStore.Resolve(directory,job.Output);
            if(HasResult(directory,job))return;
            if(File.Exists(output))File.Move(output,output+".invalid-"+Guid.NewGuid().ToString("N"));
            // The worker cleans replicate-file uploads. A saved reference must
            // never be the worker's disposable upload file.
            string upload=Path.Combine(Path.GetDirectoryName(output),job.Id+"-upload-"+Guid.NewGuid().ToString("N")+".png");
            try
            {
                File.Copy(LetterTransferStore.Resolve(directory,job.Reference),upload);
                ReplicateWorkerClient.Run(job.Model,token,new Dictionary<string,object>{
                    ["prompt"]=job.Prompt,["image_input"]=new[]{"replicate-file:"+upload},["output_format"]="png",
                    ["aspect_ratio"]=job.Letters.Length==2?"3:2":"1:1",["resolution"]=job.Resolution
                },output,progress,cancel);
            }
            finally{try{File.Delete(upload);}catch(Exception error){Log.Error("Could not clean letter reference upload.",error);}}
        }
    }
}
