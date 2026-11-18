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
        internal static string Prompt(LetterTransferSet set,string[] letters,string key,string model=null)
        {
            if(letters==null || letters.Length<1 || letters.Length>LetterTransferRules.Characters.Length || letters.Any(l=>!LetterTransferRules.ValidLetter(l)) || letters.Distinct().Count()!=letters.Length || !LetterTransferImages.ValidKey(key))
                throw new ArgumentException("Выберите русские заглавные буквы и знаки ! ? - : (до 37 разных символов).");
            int columns=LetterTransferRules.Columns(letters.Length),rows=(letters.Length+columns-1)/columns;
            if(model==LetterTransferModels.Seedream)
            {
                string compact="Create EXACTLY "+letters.Length+" separate RUSSIAN CYRILLIC UPPERCASE letter designs, following the supplied style reference. "+
                    "Preserve its letter shapes, texture, palette, line quality, outlines and intentional collage/paper backings. A uniform reference requires one consistent alphabet; a collage requires coherent eclectic designs. "+
                    "One complete character per invisible cell, "+columns+" columns by "+rows+" rows. Required row-major order: "+String.Join(", ",letters)+". Unused cells stay empty. "+
                    "Correct Cyrillic anatomy; no Latin substitutions. Keep Ё (U+0401) dots and Й (U+0419) breve. Their bodies match Е and И in cap height, with extra space above for accents. "+
                    "Center each glyph and leave at least 10% empty margin on every cell side. Never repeat, combine, crop or touch borders. "+
                    "NO grid lines, frames, labels, numbers, rulers, dimensions, captions, words, mockups, products or cast shadows. "+
                    "Use one flat technical background "+key+" in empty areas and letter holes; reserve this hue exclusively for background. Keep white ink, intentional letter backings and sharp edges without a colored fringe. "+
                    "Style: "+(set.Style??"")+". User direction: "+(set.Notes??"");
                compact=WithPunctuation(compact,letters);LetterTransferModels.ValidatePrompt(model,compact);return compact;
            }
            string prompt="Create a print-ready collection of EXACTLY "+letters.Length+" separate RUSSIAN CYRILLIC UPPERCASE LETTER designs from the supplied visual style reference. "+
                "This is not a font file and not a product size chart. Keep the reference's letter construction, texture, line quality, palette, outlines and intentional collage/paper backings. "+
                "If the reference is a uniform alphabet, keep a consistent alphabet family; if it is an eclectic collage, use its coherent eclectic language. Do not copy whole words or the reference sheet. "+
                "Required Cyrillic characters: "+String.Join(", ",letters.Select(l=>"«"+l+"» (U+"+((int)l[0]).ToString("X4")+")"))+". Correct letter anatomy is essential; retain Ё dots and Й breve. Ё must have the same body cap height as Е, and Й as И, with extra room above for their accents; never shrink their bodies to fit the accent. Do not substitute a Latin lookalike or invent letters. "+
                "Arrange exactly one complete requested character per invisible grid cell: "+columns+" columns by "+rows+" rows, row-major order. "+
                String.Join(" ",letters.Select((l,i)=>"Cell "+(i+1)+": only «"+l+"»."))+" Any unused cell must remain empty. Center each letter, similar cap height, keep at least 10% empty margin on every side of each cell. "+
                "Never tile, repeat, combine two letters, crop or touch a cell border. NO rulers, dimensions, arrows, captions, labels, numbers, grid lines, frames, cast shadows, mockups or products. "+
                "Use an absolutely flat solid technical background "+key+" in all empty space and letter holes. Reserve that exact color for background only; do not use it in the artwork. White ink is artwork and must remain white. Sharp print edges, no colored matte fringe. "+
                "Style choice: "+(set.Style??"По референсу")+". Additional user direction: "+(set.Notes??"");
            return WithPunctuation(prompt,letters);
        }
        private static string WithPunctuation(string prompt,string[] letters)
        {
            if(!letters.Any(l=>LetterTransferRules.Punctuation.Contains(l)))return prompt;
            return prompt.Replace("RUSSIAN CYRILLIC UPPERCASE LETTER designs","RUSSIAN CYRILLIC UPPERCASE LETTER AND PUNCTUATION designs")
                .Replace("RUSSIAN CYRILLIC UPPERCASE letter designs","RUSSIAN CYRILLIC UPPERCASE letter and punctuation designs")
                .Replace("Required Cyrillic characters:","Required characters:")+
                " Requested punctuation is artwork, not a label: ! is one exclamation mark, ? is one question mark, - is a single horizontal hyphen, : is exactly two vertically aligned dots. Render only the requested symbols. Keep both parts of !, ? and : together in their own cell. Match the reference texture and border. ! and ? match cap height; - and : have normal typographic proportions and sit around the middle of the cap height, never stretched to full letter height.";
        }
        internal static LetterTransferJob Create(string directory,LetterTransferSet set,string[] letters,string model,string resolution,BitmapSource approvedReference=null)
        {
            if(!LetterTransferModels.Supports(model) || (resolution!="2K" && resolution!="4K"))throw new ArgumentException("Проверьте модель и размер ответа.");
            if(letters.Length>4 && resolution!="4K")throw new ArgumentException("Для полного листа букв выберите 4K.");
            if(set.Jobs.Count>=200)throw new InvalidOperationException("История содержит 200 запросов. Создайте новый набор.");
            var reference=approvedReference??LetterTransferImages.RequestReference(LetterTransferImages.Load(LetterTransferStore.Resolve(directory,set.Reference)));
            string key=LetterTransferImages.Key(reference);
            var job=new LetterTransferJob{Letters=letters.ToArray(),Model=model,Resolution=resolution,Key=key};
            job.Reference="requests/"+job.Id+"-reference.png";job.Output="requests/"+job.Id+"-output.png";job.Prompt=Prompt(set,letters,key,model);
            LetterTransferImages.Save(reference,LetterTransferStore.Resolve(directory,job.Reference));set.Jobs.Add(job);
            try{LetterTransferStore.Save(directory,set);}catch{set.Jobs.Remove(job);throw;}return job;
        }
        internal static bool UncertainSubmission(string directory,LetterTransferJob job)
        {
            string output=LetterTransferStore.Resolve(directory,job.Output);
            return File.Exists(output+".submitted") && !File.Exists(output+".prediction.json") && !HasResult(directory,job);
        }
        internal static bool InFlight(string directory,LetterTransferJob job)
        {
            if(HasResult(directory,job))return false;string output=LetterTransferStore.Resolve(directory,job.Output),state=output+".prediction.json";
            if(!File.Exists(state))return File.Exists(output+".submitted");
            try
            {
                var saved=new System.Web.Script.Serialization.JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(state));
                string status=saved!=null && saved.TryGetValue("status",out var value)?Convert.ToString(value):"";return status!="failed" && status!="canceled";
            }
            catch(Exception error)when(error is IOException || error is ArgumentException || error is InvalidOperationException){return true;}
        }
        internal static bool HasResult(string directory,LetterTransferJob job)
        {
            try{LetterTransferImages.Load(LetterTransferStore.Resolve(directory,job.Output));return true;}
            catch(Exception error)when(error is IOException || error is InvalidDataException || error is NotSupportedException || error is System.IO.FileFormatException || error is ArgumentException){return false;}
        }
        internal static void Generate(string directory,LetterTransferJob job,string token,Action<string> progress,CancellationToken cancel,bool resumeOnly=false)
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
                ReplicateWorkerClient.Run(job.Model,token,LetterTransferModels.Input(job,"replicate-file:"+upload),output,progress,cancel,resumeOnly);
            }
            finally{try{File.Delete(upload);}catch(Exception error){Log.Error("Could not clean letter reference upload.",error);}}
        }
    }
}
