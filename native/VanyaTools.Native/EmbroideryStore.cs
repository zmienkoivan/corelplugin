using System;
using System.IO;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal sealed class EmbroideryJob
    {
        internal readonly string DirectoryPath;
        internal readonly EmbroideryRequest Request;
        internal string ReferencePath=>Path.Combine(DirectoryPath,"reference.png");
        internal string RawPath=>Path.Combine(DirectoryPath,"sheet.png");
        internal string StartedPath=>Path.Combine(DirectoryPath,"started.txt");
        internal bool HasReference=>File.Exists(ReferencePath);
        internal EmbroideryJob(string path,EmbroideryRequest request){DirectoryPath=path;Request=request;}
    }
    internal sealed class EmbroideryStore
    {
        internal readonly string Root;
        internal EmbroideryStore(string root=null)
        {Root=Path.GetFullPath(root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VanyaTools","embroidery"));}
        internal EmbroideryJob Create(EmbroideryRequest request,BitmapSource reference)
        {
            request.Validate(reference!=null);
            var json=new JavaScriptSerializer();
            var snapshot=json.Deserialize<EmbroideryRequest>(json.Serialize(request));
            string path=Path.Combine(Root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);
            var job=new EmbroideryJob(path,snapshot);
            if(reference!=null)
            {
                double scale=Math.Min(1,2048d/Math.Max(reference.PixelWidth,reference.PixelHeight));
                BitmapSource image=scale<1?new TransformedBitmap(reference,new ScaleTransform(scale,scale)):reference;
                EmbroideryImages.Save(image,job.ReferencePath);
            }
            File.WriteAllText(Path.Combine(path,"job.json"),json.Serialize(snapshot));
            Atomic(Path.Combine(Root,"last.txt"),Path.GetFileName(path));return job;
        }
        internal EmbroideryJob Last()
        {
            string pointer=Path.Combine(Root,"last.txt");if(!File.Exists(pointer))return null;
            string id=File.ReadAllText(pointer).Trim();
            if(id.Length!=32 || !Guid.TryParseExact(id,"N",out _))throw new InvalidDataException("Повреждена ссылка на сохранённую вышивку.");
            string path=Path.Combine(Root,id),file=Path.Combine(path,"job.json");
            if(!File.Exists(file) || new FileInfo(file).Length>32000)throw new InvalidDataException("Не найдено сохранённое задание вышивки.");
            var request=new JavaScriptSerializer().Deserialize<EmbroideryRequest>(File.ReadAllText(file));
            if(request==null)throw new InvalidDataException("Повреждено задание вышивки.");
            var job=new EmbroideryJob(path,request);request.Validate(job.HasReference);return job;
        }
        private static void Atomic(string path,string text)
        {
            string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try{File.WriteAllText(temporary,text);if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);}
            finally{if(File.Exists(temporary))File.Delete(temporary);}
        }
    }
}
