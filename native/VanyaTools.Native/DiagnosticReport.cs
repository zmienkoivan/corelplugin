using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace VanyaTools.Native
{
    internal sealed class SavedDiagnosticReport
    {
        internal string Id, FilePath;
    }
    internal static class DiagnosticReport
    {
        internal const int MaxBytes=4*1024*1024;
        private static readonly string[] Names={"VanyaTools.Native.log","Install.log","Update.log"};
        internal static string Folder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VanyaTools","Diagnostics");
        internal static SavedDiagnosticReport Save(string directory,string employee,string note,string corelVersion,IEnumerable<string> secrets,IDictionary<string,string> sources=null)
        {
            var keys=(secrets??Enumerable.Empty<string>()).Where(value=>!String.IsNullOrEmpty(value)).ToArray();
            if(sources==null)sources=new Dictionary<string,string>{
                [Names[0]]=Path.Combine(Path.GetTempPath(),Names[0]),
                [Names[1]]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VanyaTools",Names[1]),
                [Names[2]]=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VanyaTools",Names[2])};
            var logs=new List<Dictionary<string,object>>();
            foreach(var name in Names)
            {
                var entry=new Dictionary<string,object>{{"name",name},{"available",false},{"truncated",false},{"text",""}};
                if(sources.TryGetValue(name,out string source) && File.Exists(source))
                {
                    try
                    {
                        int limit=name==Names[0]?1024*1024:256*1024;
                        using(var file=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
                        {
                            long length=file.Length;int count=(int)Math.Min(length,limit);file.Seek(-count,SeekOrigin.End);
                            var data=new byte[count];int read=0,next;while(read<count && (next=file.Read(data,read,count-read))>0)read+=next;
                            entry["text"]=Redact(Encoding.UTF8.GetString(data,0,read),keys);entry["available"]=true;entry["truncated"]=length>limit;
                        }
                    }
                    catch(IOException){entry["text"]="Журнал занят или недоступен.";}
                    catch(UnauthorizedAccessException){entry["text"]="Нет доступа к журналу.";}
                }
                logs.Add(entry);
            }
            string id=Guid.NewGuid().ToString("N");
            var report=new Dictionary<string,object>{{"schema",1},{"id",id},{"createdUtc",DateTime.UtcNow.ToString("o")},
                {"machine",Environment.MachineName},{"employee",Redact(Limit(employee,128),keys)},
                {"pluginVersion",typeof(DiagnosticReport).Assembly.GetName().Version.ToString()},
                {"corelVersion",Limit(corelVersion,128)},{"windows",Environment.OSVersion.ToString()+" · "+(Environment.Is64BitOperatingSystem?"x64":"x86")},
                {"runtime",Environment.Version.ToString()},{"processor",Limit(Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),256)},
                {"note",Redact(Limit(note,2000),keys)},{"logs",logs}};
            var serializer=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};byte[] bytes;
            while(true)
            {
                bytes=Encoding.UTF8.GetBytes(serializer.Serialize(report));if(bytes.Length<=MaxBytes)break;
                var largest=logs.OrderByDescending(log=>((string)log["text"]).Length).First();string text=(string)largest["text"];
                if(text.Length<1024)throw new InvalidDataException("Отчёт слишком велик.");
                largest["text"]=text.Substring(text.Length/2);largest["truncated"]=true;
            }
            Directory.CreateDirectory(directory);string path=Path.Combine(directory,"VanyaTools-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+id+".json");
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))output.Write(bytes,0,bytes.Length);
            return new SavedDiagnosticReport{Id=id,FilePath=path};
        }
        private static string Limit(string value,int length)=>(value??"").Length<=length?(value??""):value.Substring(0,length);
        internal static string Redact(string text,IEnumerable<string> secrets)
        {
            foreach(string secret in secrets.Where(value=>!String.IsNullOrEmpty(value)).OrderByDescending(value=>value.Length))text=text.Replace(secret,"[REDACTED]");
            text=Replace(text,@"\b(?:r8_[A-Za-z0-9_-]{12,}|sk-[A-Za-z0-9_-]{12,}|(?:bot)?\d{7,12}:[A-Za-z0-9_-]{25,})\b","[REDACTED]");
            text=Replace(text,@"(Bearer\s+)[A-Za-z0-9._~+/-]+","$1[REDACTED]");
            text=Replace(text,@"([?&](?:key|token|api_key|access_token|signature|sig|pin)=)[^&\s""<>]+","$1[REDACTED]");
            return Replace(text,@"((?:""?(?:key|token|password|secret|api_key|admin_key|publish_key)""?)\s*[:=]\s*""?)[^""\s,}]+","$1[REDACTED]");
        }
        private static string Replace(string text,string pattern,string replacement)=>Regex.Replace(text,pattern,replacement,RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromSeconds(2));
        internal static string CorelVersion()
        {
            try {using(var process=Process.GetCurrentProcess())return process.ProcessName.IndexOf("Corel",StringComparison.OrdinalIgnoreCase)>=0?process.MainModule.FileVersionInfo.ProductVersion:"Не запущено внутри CorelDRAW";}
            catch{return "Не удалось определить";}
        }
    }
}
