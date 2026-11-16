using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace VanyaTools.Updater
{
    internal sealed class ReplicateSubmissionStamp
    {
        public int Schema {get;set;}=1;
        public string Model {get;set;}
        public string SentUtc {get;set;}
        public string[] Keys {get;set;}
        public string[] Files {get;set;}
        public string ParametersHash {get;set;}
        public string ContentHash {get;set;}
        public string[] IgnoreIds {get;set;}=new string[0];
    }
    // Match the submitted request, not merely its prompt. Receipts contain hashes
    // and field paths only: no API token, prompt, base64 image or file contents.
    internal static class ReplicateSubmissionRecovery
    {
        private const string FileMarker="__replicate_input_file__";
        internal static ReplicateSubmissionStamp Create(string model,Dictionary<string,object> input,DateTime sentUtc)
        {
            var files=new List<string>();var content=Normalize(input,"",null,(text)=>{
                if(text.StartsWith("replicate-file:",StringComparison.OrdinalIgnoreCase))
                    using(var stream=File.OpenRead(text.Substring("replicate-file:".Length)))return Digest(stream);
                return DataHash(text);
            },files);
            var paths=new HashSet<string>(files,StringComparer.Ordinal);
            return new ReplicateSubmissionStamp{Model=model,SentUtc=sentUtc.ToUniversalTime().ToString("o"),Keys=input.Keys.OrderBy(k=>k,StringComparer.Ordinal).ToArray(),Files=files.ToArray(),
                ContentHash=Hash(content),ParametersHash=Hash(Normalize(input,"",paths,_=>FileMarker,null,true))};
        }
        internal static List<Dictionary<string,object>> Match(ReplicateSubmissionStamp stamp,IEnumerable<Dictionary<string,object>> candidates,Func<string,string> remoteHash)
        {
            Validate(stamp);var result=new List<Dictionary<string,object>>();var seen=new HashSet<string>();
            var paths=new HashSet<string>(stamp.Files,StringComparer.Ordinal);
            var sent=DateTime.Parse(stamp.SentUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind).ToUniversalTime();
            foreach(var candidate in candidates)
            {
                string id=Value(candidate,"id");if(String.IsNullOrEmpty(id) || (stamp.IgnoreIds??new string[0]).Contains(id) || !seen.Add(id) || !ModelMatches(stamp.Model,candidate))continue;
                if(!DateTime.TryParse(Value(candidate,"created_at"),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out var created) || Math.Abs((created.ToUniversalTime()-sent).TotalMinutes)>5)continue;
                var actual=candidate.ContainsKey("input")?candidate["input"] as Dictionary<string,object>:null;
                if(actual==null || stamp.Keys.Any(k=>!actual.ContainsKey(k)))continue;
                var projected=stamp.Keys.ToDictionary(k=>k,k=>actual[k]);
                if(Hash(Normalize(projected,"",paths,_=>FileMarker,null,true))!=stamp.ParametersHash)continue;
                object normalized;
                try{normalized=Normalize(projected,"",paths,text=>text.StartsWith("data:image/",StringComparison.OrdinalIgnoreCase)?DataHash(text):remoteHash(text),null);}
                catch(Exception error)when(error is IOException || error is InvalidDataException || error is InvalidOperationException || error is ArgumentException || error is System.Net.WebException){continue;}
                if(Hash(normalized)==stamp.ContentHash)result.Add(candidate);
            }
            return result;
        }
        private static object Normalize(object value,string path,HashSet<string> filePaths,Func<string,string> hashFile,List<string> discovered,bool placeholders=false)
        {
            if(value is Dictionary<string,object> dictionary)
            {
                var result=new Dictionary<string,object>();foreach(var key in dictionary.Keys.OrderBy(k=>k,StringComparer.Ordinal))result[key]=Normalize(dictionary[key],path+"/"+Escape(key),filePaths,hashFile,discovered,placeholders);return result;
            }
            if(value is IList list)
            {
                var result=new List<object>();for(int i=0;i<list.Count;i++)result.Add(Normalize(list[i],path+"/"+i,filePaths,hashFile,discovered,placeholders));return result;
            }
            bool file=filePaths!=null?filePaths.Contains(path):value is string text && (text.StartsWith("replicate-file:",StringComparison.OrdinalIgnoreCase) || text.StartsWith("data:image/",StringComparison.OrdinalIgnoreCase));
            if(!file)return value;
            if(!(value is string fileText))throw new InvalidDataException("Unexpected input file shape.");
            discovered?.Add(path);if(placeholders)return FileMarker;
            string hash=hashFile(fileText);if(hash==null || hash.Length!=64 || hash.Any(c=>!Uri.IsHexDigit(c)))throw new InvalidDataException("Input file fingerprint missing.");
            return "sha256:"+hash.ToLowerInvariant();
        }
        internal static bool ModelMatches(string expected,Dictionary<string,object> candidate)
        {
            string[] parts=expected.Split(':');if(parts[0].Contains("/"))
                return Value(candidate,"model")==parts[0] && (parts.Length==1 || Value(candidate,"version")==parts[1] || Value(candidate,"version")=="hidden");
            return Value(candidate,"version")==expected;
        }
        internal static string DataHash(string text)
        {
            int comma=text.IndexOf(',');if(!text.StartsWith("data:image/",StringComparison.OrdinalIgnoreCase) || comma<0 || text.Substring(0,comma).IndexOf(";base64",StringComparison.OrdinalIgnoreCase)<0)
                throw new InvalidDataException("Invalid inline input image.");
            using(var stream=new MemoryStream(Convert.FromBase64String(text.Substring(comma+1))))return Digest(stream);
        }
        internal static string Digest(Stream stream){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
        private static string Hash(object value){using(var stream=new MemoryStream(Encoding.UTF8.GetBytes(Canonical(value))))return Digest(stream);}
        private static string Canonical(object value)
        {
            var serializer=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};
            if(value is Dictionary<string,object> dictionary)return "{"+String.Join(",",dictionary.Keys.OrderBy(k=>k,StringComparer.Ordinal).Select(k=>serializer.Serialize(k)+":"+Canonical(dictionary[k])))+"}";
            if(value is IList list)return "["+String.Join(",",list.Cast<object>().Select(Canonical))+"]";
            return serializer.Serialize(value);
        }
        internal static void Validate(ReplicateSubmissionStamp stamp)
        {
            if(stamp==null || stamp.Schema!=1 || String.IsNullOrWhiteSpace(stamp.Model) || stamp.Model.Length>200 || stamp.Keys==null || stamp.Keys.Length>256 || stamp.Files==null || stamp.Files.Length>128 ||
                (stamp.IgnoreIds!=null && (stamp.IgnoreIds.Length>100 || stamp.IgnoreIds.Any(id=>id==null || id.Length>200))) || stamp.Keys.Any(k=>k==null || k.Length>200) || stamp.Files.Any(p=>p==null || p.Length>1000) || stamp.ParametersHash?.Length!=64 || stamp.ContentHash?.Length!=64 ||
                !DateTime.TryParse(stamp.SentUtc,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out _))throw new InvalidDataException("Invalid submission receipt.");
        }
        private static string Escape(string key)=>key.Replace("~","~0").Replace("/","~1");
        internal static string Value(Dictionary<string,object> value,string key)=>value!=null && value.ContainsKey(key)?Convert.ToString(value[key]):"";
    }
    internal static class ReplicateInputEncoding
    {
        internal const int InlineBudget=1024*1024;
        internal static bool TryInline(byte[] content,string mime,ref int remaining,out string value)
        {
            value=null;if(content.Length>InlineBudget || content.Length>remaining)return false;
            remaining-=content.Length;value="data:"+mime+";base64,"+Convert.ToBase64String(content);return true;
        }
    }
}
