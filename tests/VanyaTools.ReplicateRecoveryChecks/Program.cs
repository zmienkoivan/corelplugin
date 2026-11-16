using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Web.Script.Serialization;
using VanyaTools.Updater;
internal static class Program
{
    private static int _checks;
    [STAThread]private static int Main()
    {
        string originalTemp=Environment.GetEnvironmentVariable("TEMP");string root=Path.Combine(Path.GetTempPath(),"Vanya-Recovery-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("TEMP",root);
        try{Matching(root);WireChecks.Run(root);Console.WriteLine("Replicate recovery checks passed: "+_checks+". Loopback HTTP and synthetic credentials only.");return 0;}
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{Environment.SetEnvironmentVariable("TEMP",originalTemp);string parent=Path.GetFullPath(originalTemp).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(Path.GetFullPath(root).StartsWith(parent,StringComparison.OrdinalIgnoreCase) && Path.GetFileName(root).StartsWith("Vanya-Recovery-"))Directory.Delete(root,true);}
    }
    internal static void Check(bool ok,string message){_checks++;if(!ok)throw new Exception(message);}
    private static Dictionary<string,object> Copy(Dictionary<string,object> source)=>new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(new JavaScriptSerializer().Serialize(source));
    private static void Matching(string root)
    {
        string file=Path.Combine(root,"private.png");byte[] bytes={1,4,8,16,32,64};File.WriteAllBytes(file,bytes);
        var input=new Dictionary<string,object>{{"prompt","private reference direction"},{"image_input",new[]{"replicate-file:"+file}},{"resolution","4K"},{"aspect_ratio","1:1"},{"output_format","png"}};
        DateTime now=DateTime.UtcNow;var stamp=ReplicateSubmissionRecovery.Create("google/nano-banana-2",input,now);
        string imageHash;using(var stream=File.OpenRead(file))imageHash=ReplicateSubmissionRecovery.Digest(stream);
        var actual=Copy(input);actual["image_input"]=new[]{"https://api.replicate.com/v1/files/synthetic"};actual["server_default"]=false;
        var candidate=new Dictionary<string,object>{{"id","match-1"},{"model","google/nano-banana-2"},{"version","hidden"},{"input",actual},{"created_at",now.AddSeconds(2).ToString("o")},{"status","processing"}};
        Check(ReplicateSubmissionRecovery.Match(stamp,new[]{candidate},_=>imageHash).Count==1,"Exact request not recovered.");
        stamp.IgnoreIds=new[]{"match-1"};Check(ReplicateSubmissionRecovery.Match(stamp,new[]{candidate},_=>imageHash).Count==0,"Known terminal request was attached to a newly confirmed attempt.");stamp.IgnoreIds=new string[0];
        string receipt=new JavaScriptSerializer().Serialize(stamp);Check(!receipt.Contains("private reference direction") && !receipt.Contains(file) && !receipt.Contains("https://api.replicate"),"Receipt stored prompt or private image URL/path.");
        var other=Copy(candidate);other["model"]="google/nano-banana-pro";Check(ReplicateSubmissionRecovery.Match(stamp,new[]{other},_=>imageHash).Count==0,"Wrong model recovered.");
        other=Copy(candidate);((Dictionary<string,object>)other["input"])["resolution"]="2K";Check(ReplicateSubmissionRecovery.Match(stamp,new[]{other},_=>imageHash).Count==0,"Wrong resolution recovered.");
        Check(ReplicateSubmissionRecovery.Match(stamp,new[]{candidate},_=>new string('0',64)).Count==0,"Different image with same prompt recovered.");
        other=Copy(candidate);other["created_at"]=now.AddHours(-1).ToString("o");Check(ReplicateSubmissionRecovery.Match(stamp,new[]{other},_=>imageHash).Count==0,"Old matching request recovered.");
        other=Copy(candidate);other["id"]="match-2";Check(ReplicateSubmissionRecovery.Match(stamp,new[]{candidate,other},_=>imageHash).Count==2,"Duplicate ambiguity silently collapsed.");
        Check(ReplicateSubmissionRecovery.Match(stamp,new[]{candidate,candidate},_=>imageHash).Count==1,"Pagination duplicate fabricated ambiguity.");
        actual["image_input"]=new[]{"data:image/png;base64,"+Convert.ToBase64String(bytes)};Check(ReplicateSubmissionRecovery.Match(stamp,new[]{candidate},_=>throw new Exception("Should not fetch inline image")).Count==1,"File URL and inline representation differ.");
        var reOrdered=new Dictionary<string,object>();foreach(var item in input.Reverse())reOrdered[item.Key]=item.Value;
        Check(ReplicateSubmissionRecovery.Create(stamp.Model,reOrdered,now).ContentHash==stamp.ContentHash,"Dictionary ordering changed fingerprint.");
        int budget=ReplicateInputEncoding.InlineBudget;Check(ReplicateInputEncoding.TryInline(new byte[623669],"image/png",ref budget,out var inline) && inline.StartsWith("data:image/png;base64,"),"Observed preview still requires a file upload.");
        Check(!ReplicateInputEncoding.TryInline(new byte[623669],"image/png",ref budget,out _),"Multiple images exceeded total inline budget.");
        budget=ReplicateInputEncoding.InlineBudget;Check(!ReplicateInputEncoding.TryInline(new byte[budget+1],"image/png",ref budget,out _) && budget==ReplicateInputEncoding.InlineBudget,"Large image strategy changed data/budget.");
    }
}
