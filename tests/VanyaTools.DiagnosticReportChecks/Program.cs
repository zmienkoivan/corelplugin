using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using VanyaTools.Native;
internal static class Program
{
    private static int checks;
    private static void Check(bool ok,string message){checks++;if(!ok)throw new Exception(message);}
    private static string Hash(string path){using(var hash=SHA256.Create())return Convert.ToBase64String(hash.ComputeHash(File.ReadAllBytes(path)));}
    private static void Main()
    {
        string root=Path.Combine(Path.GetTempPath(),"Vanya-Diagnostics-Test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string input=Path.Combine(root,"live.log"),folder=Path.Combine(root,"reports"),secret="secret-worker-key-123";
            File.WriteAllText(input,"Начало\n"+new string('x',1100000)+"\nHTTP 500\nAuthorization: Bearer hidden-auth\nr8_1234567890abcdef\nhttps://api.test?token=private&ok=1\n{\"key\":\"private-json\"}\n"+secret);
            string original=Hash(input);
            var sources=new Dictionary<string,string>{{"VanyaTools.Native.log",input}};
            SavedDiagnosticReport saved;
            using(var writer=new FileStream(input,FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite))saved=DiagnosticReport.Save(folder,"МИЛЯ","Не работает ИИ · "+secret,"Corel 28",new[]{secret},sources);
            string body=File.ReadAllText(saved.FilePath,Encoding.UTF8);
            Check(Hash(input)==original,"Collecting diagnostics modified source log");
            foreach(string value in new[]{secret,"hidden-auth","r8_1234567890abcdef","token=private","private-json"})Check(!body.Contains(value),"Secret leaked: "+value);
            Check(body.Contains("HTTP 500") && body.Contains("МИЛЯ"),"Useful error context or worker name lost");
            Check(new FileInfo(saved.FilePath).Length<=DiagnosticReport.MaxBytes,"Oversize report");
            var report=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue}.Deserialize<Dictionary<string,object>>(body);
            Check((string)report["id"]==saved.Id && Guid.TryParseExact(saved.Id,"N",out _),"Report has no stable retry ID");
            var logs=(System.Collections.ArrayList)report["logs"];
            var native=(Dictionary<string,object>)logs[0];Check((bool)native["available"] && (bool)native["truncated"],"Large live log was not bounded and marked");
            Check(!(bool)((Dictionary<string,object>)logs[1])["available"],"Absent optional log was not recorded");
            var second=DiagnosticReport.Save(folder,"СОНЯ","",null,Array.Empty<string>(),new Dictionary<string,string>());
            Check(File.Exists(second.FilePath) && saved.Id!=second.Id,"A new report overwrote an earlier diagnostic");
            Check(!DiagnosticReport.Redact("https://api.telegram.org/file/bot123456789:ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567/file",Array.Empty<string>()).Contains("ABCDEFGHIJKLMNOPQRSTUVWXYZ"),"Telegram token leaked");
            Console.WriteLine("Diagnostic report checks passed: "+checks+". Synthetic logs only; no network or Corel.");
        }
        finally
        {
            string resolved=Path.GetFullPath(root),prefix=Path.Combine(Path.GetTempPath(),"Vanya-Diagnostics-Test-");
            if(!resolved.StartsWith(prefix,StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe fixture cleanup target");
            Directory.Delete(resolved,true);
        }
    }
}
