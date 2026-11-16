using System;
using System.Collections.Generic;

namespace VanyaTools.Native
{
    internal static class LetterTransferModels
    {
        internal const string Nano2="google/nano-banana-2",NanoPro="google/nano-banana-pro",Seedream="bytedance/seedream-4.5";
        internal static readonly string[] Ids={Nano2,NanoPro,Seedream};
        internal static readonly string[] Names={"Nano Banana 2","Nano Banana Pro","Seedream 4.5 · ByteDance"};
        internal static bool Supports(string model)=>Array.IndexOf(Ids,model)>=0;
        internal static string Name(string model){int index=Array.IndexOf(Ids,model);return index>=0?Names[index]:model;}
        internal static string At(int index)=>index>=0 && index<Ids.Length?Ids[index]:Nano2;
        internal static void ValidatePrompt(string model,string prompt)
        {
            if(!Supports(model))throw new ArgumentException("Выберите поддерживаемую модель генерации букв.");
            if(model==Seedream && prompt.Length>4000)throw new ArgumentException("Seedream 4.5 принимает до 4000 символов задания. Сократите уточнения для генерации; сейчас "+prompt.Length+" символов. Запрос не отправлен.");
        }
        internal static Dictionary<string,object> Input(LetterTransferJob job,string reference)
        {
            ValidatePrompt(job.Model,job.Prompt);
            var input=new Dictionary<string,object>{{"prompt",job.Prompt},{"image_input",new[]{reference}},{"aspect_ratio",LetterTransferRules.Aspect(job.Letters.Length)}};
            if(job.Model==Seedream){input["size"]=job.Resolution;input["sequential_image_generation"]="disabled";input["max_images"]=1;}
            else{input["resolution"]=job.Resolution;input["output_format"]="png";}
            return input;
        }
    }
}
