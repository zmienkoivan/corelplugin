using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    // Only local reference/brief/receipt data; credentials are never persisted.
    internal sealed class PatternFillerRequest
    {
        public int Schema { get; set; } = 1;
        public string CreatedUtc { get; set; }
        public string ReferenceKey { get; set; }
        public string Model { get; set; }
        public bool Analysis { get; set; }
        public bool Completed { get; set; }
        public string Theme { get; set; }
        public string Style { get; set; }
        public string[] Elements { get; set; }
        public bool SmallFillers { get; set; }
        public PatternReferenceKind Source { get; set; }
        internal string DirectoryPath;
        internal string OutputPath => Path.Combine(DirectoryPath, Analysis ? "result.txt" : "sheet.png");
        internal PatternFillerPlan Plan => new PatternFillerPlan { Theme=Theme,Style=Style,Elements=(string[])Elements.Clone(),SmallFillers=SmallFillers,Source=Source };
        internal string State(string field)
        {
            string file=OutputPath+".prediction.json";
            try
            {
                if(!File.Exists(file) || new FileInfo(file).Length>1000000)return "";
                var data=new JavaScriptSerializer {MaxJsonLength=1000000}.Deserialize<System.Collections.Generic.Dictionary<string,object>>(File.ReadAllText(file));
                return data!=null && data.TryGetValue(field,out var value)?Convert.ToString(value):"";
            }
            catch(Exception error) when(error is IOException || error is ArgumentException || error is InvalidOperationException){return "";}
        }
        internal bool CanResume => File.Exists(OutputPath) || State("status")!="failed" && State("status")!="canceled" &&
            (File.Exists(OutputPath+".prediction.json") || File.Exists(OutputPath+".submitted"));
        internal void Run(string token,bool resumeOnly,CancellationToken cancellation,Action<string> progress=null)
        {
            cancellation.ThrowIfCancellationRequested();
            if(File.Exists(OutputPath) && new FileInfo(OutputPath).Length>0)return;
            if(resumeOnly && !CanResume)throw new InvalidOperationException("Сохранённого активного задания нет. Новая генерация не отправлялась.");
            string input="";
            try
            {
                // A known prediction needs no reference upload or original file.
                if(!String.IsNullOrEmpty(State("id")))
                    input="data:image/png;base64,";
                else
                {
                    using(var stream=File.OpenRead(Path.Combine(DirectoryPath,"reference.png")))
                    {
                        var image=BitmapFrame.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);image.Freeze();
                        input=Analysis?AiPrintTab.DataUri(image,2048,2000000):AiPrintTab.DataUri(image);
                    }
                }
                var values=Analysis?PatternFillerBrief.AnalysisInput(Model,input,Elements.Length,SmallFillers,Source):
                    PatternFillerAi.GenerationInput(Plan,input);
                ReplicateWorkerClient.Run(Model,token,values,OutputPath,progress,cancellation,resumeOnly);
            }
            finally
            {
                const string prefix="replicate-file:";
                if(input.StartsWith(prefix,StringComparison.Ordinal))try{File.Delete(input.Substring(prefix.Length));}catch(IOException){}catch(UnauthorizedAccessException){}
            }
        }
        internal PatternFillerPlan ReadAnalysis()=>PatternFillerBrief.Parse(new FileInfo(OutputPath).Length<=48000?File.ReadAllText(OutputPath):"",Elements.Length,SmallFillers,Source);
        internal PatternFillerResult ReadGeneration(CancellationToken cancellation)=>PatternFillerExtraction.LoadSheet(OutputPath,Plan,cancellation);
        internal void Finish(){Completed=true;Save();}
        internal void Save()
        {
            string path=Path.Combine(DirectoryPath,"request.json"),temporary=path+".tmp";
            File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(this));
            if(File.Exists(path))File.Replace(temporary,path,null);else File.Move(temporary,path);
        }
    }

    internal static class PatternFillerRequests
    {
        private static readonly object Gate=new object();
        private static string Root(string root)=>root??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VanyaTools","PatternRequests");
        internal static PatternFillerRequest Find(BitmapSource reference,PatternReferenceKind source,string root=null)
        {
            lock(Gate)
            {
                string directory=Root(root);if(!Directory.Exists(directory))return null;
                string key=PatternFillerAi.ReferenceKey(reference);
                foreach(string folder in Directory.EnumerateDirectories(directory).OrderByDescending(Directory.GetCreationTimeUtc))
                {
                    if(!Guid.TryParseExact(Path.GetFileName(folder),"N",out _))continue;
                    string file=Path.Combine(folder,"request.json");
                    try
                    {
                        if(!File.Exists(file) || new FileInfo(file).Length>30000)continue;
                        var job=new JavaScriptSerializer().Deserialize<PatternFillerRequest>(File.ReadAllText(file));
                        if(job==null || job.Schema!=1 || job.Completed || job.ReferenceKey!=key || job.Source!=source || job.Elements==null || job.Elements.Length<4 || job.Elements.Length>10)continue;
                        if(job.Analysis)PatternFillerBrief.FindAnalysisModel(job.Model);else if(job.Model!=SublimationModels.Nano2)continue;
                        job.DirectoryPath=folder;
                        if(job.CanResume)return job;
                    }
                    catch(Exception error) when(error is IOException || error is ArgumentException || error is InvalidOperationException){Log.Error("Could not read saved pattern request.",error);}
                }
                return null;
            }
        }
        internal static PatternFillerRequest Create(BitmapSource reference,PatternFillerPlan plan,string model,bool analysis,string root=null)
        {
            lock(Gate)
            {
                if(Find(reference,plan.Source,root)!=null)throw new InvalidOperationException("Есть незавершённый запрос по этому референсу. Сначала нажмите «Продолжить запрос».");
                if(analysis)PatternFillerBrief.FindAnalysisModel(model);else plan.Validate();
                var job=new PatternFillerRequest {CreatedUtc=DateTime.UtcNow.ToString("o"),ReferenceKey=PatternFillerAi.ReferenceKey(reference),Analysis=analysis,
                    Model=analysis?model:SublimationModels.Nano2,Theme=plan.Theme,Style=plan.Style,Elements=(string[])plan.Elements.Clone(),SmallFillers=plan.SmallFillers,Source=plan.Source,
                    DirectoryPath=Path.Combine(Root(root),Guid.NewGuid().ToString("N"))};
                Directory.CreateDirectory(job.DirectoryPath);
                var bitmap=new FormatConvertedBitmap(reference,PixelFormats.Bgra32,null,0);var pixels=new byte[checked(bitmap.PixelWidth*bitmap.PixelHeight*4)];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);
                var snapshot=BitmapSource.Create(bitmap.PixelWidth,bitmap.PixelHeight,reference.DpiX,reference.DpiY,PixelFormats.Bgra32,null,pixels,bitmap.PixelWidth*4);snapshot.Freeze();
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(snapshot));using(var file=File.Create(Path.Combine(job.DirectoryPath,"reference.png")))encoder.Save(file);
                job.Save();return job;
            }
        }
    }
}
