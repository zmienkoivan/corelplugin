using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using VanyaTools.Updater;
internal static class WireChecks
{
    private const string Model="google/nano-banana-2";
    internal static void Run(string root)
    {
        using(var server=new FakeReplicateServer())
        {
            if(!WebRequest.RegisterPrefix("https://api.replicate.com/",new LoopbackFactory(server.Port)))throw new Exception("Could not isolate Replicate HTTP.");
            var assembly=Assembly.Load("VanyaTools.Updater");var method=assembly.GetType("VanyaTools.Updater.Program").GetMethod("CreatePrediction",BindingFlags.NonPublic|BindingFlags.Static);
            string file=Path.Combine(root,"wire-input.png");File.WriteAllBytes(file,new byte[]{10,20,30,40,50});
            string hash;using(var stream=File.OpenRead(file))hash=ReplicateSubmissionRecovery.Digest(stream);server.FileHash=hash;
            Dictionary<string,object> Input()=>new Dictionary<string,object>{{"prompt","Синтетическая буква А · золото"},{"image_input",new[]{"replicate-file:"+file}},{"resolution","2K"}};
            object Invoke(string output)
            {
                try{return method.Invoke(null,new object[]{"synthetic-not-a-real-token",Model,Input(),new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue},output,output+".cancel",false});}
                catch(TargetInvocationException e){throw e.InnerException;}
            }
            server.Reset("recover");string path=Path.Combine(root,"recover.png");
            string url=(string)Invoke(path);
            Program.Check(server.Posts==1 && server.Lists>=1 && url.EndsWith("/result.png"),"500 did not recover same request without another POST.");
            Program.Check(server.ValidJson && server.InlineImage && server.Uploads==0 && server.AutoCancelHeader=="10m","JSON framing or small-image inline route failed.");
            Program.Check(File.Exists(path+".submission.json") && File.Exists(path+".prediction.json"),"Recovery receipt or model ID missing.");
            string receipt=File.ReadAllText(path+".submission.json");Program.Check(!receipt.Contains("Синтетическая буква А · золото") && !receipt.Contains("synthetic-not-a-real-token") && !receipt.Contains("base64"),"Receipt leaked payload.");
            Invoke(path);Program.Check(server.Posts==1,"Ready recovered state caused a new POST.");
            server.Reset("missing");path=Path.Combine(root,"unknown.png");bool failed=false;try{Invoke(path);}catch(InvalidOperationException){failed=true;}
            Program.Check(failed && server.Posts==1 && File.Exists(path+".submitted"),"Unmatched 500 was silently retried or unguarded.");
            failed=false;try{Invoke(path);}catch(InvalidOperationException){failed=true;}
            Program.Check(failed && server.Posts==1,"Resume of unknown submission started a second model.");
            foreach(string mode in new[]{"ambiguous","truncated"})
            {
                server.Reset(mode);path=Path.Combine(root,mode+".png");failed=false;try{Invoke(path);}catch(InvalidOperationException){failed=true;}
                Program.Check(failed && server.Posts==1 && !File.Exists(path+".prediction.json"),"Ambiguous or truncated history attached a possibly wrong request.");
            }
            server.Reset("legacy");path=Path.Combine(root,"legacy.png");File.WriteAllText(path+".submitted","sent");
            server.SeedInput=new Dictionary<string,object>{{"prompt","Синтетическая буква А · золото"},{"image_input",new[]{"https://api.replicate.com/v1/files/original"}},{"resolution","2K"}};
            Invoke(path);Program.Check(server.Posts==0 && server.MetadataReads>0 && File.Exists(path+".prediction.json"),"Legacy marker did not recover using image checksum without POST/upload.");
            server.Reset("failed");path=Path.Combine(root,"terminal.png");failed=false;try{Invoke(path);}catch(InvalidOperationException){failed=true;}
            Program.Check(failed && server.Posts==1 && File.ReadAllText(path+".prediction.json").Contains("failed"),"Recovered terminal failure auto-started another paid model.");
            server.Mode="recover";server.IncludePrevious=true;Invoke(path);
            Program.Check(server.Posts==2 && File.ReadAllText(path+".prediction.json").Contains("recovered-2"),"Explicit retry attached a previous failed request instead of the new attempt.");
            server.IncludePrevious=false;
            server.Reset("reject");path=Path.Combine(root,"reject.png");failed=false;try{Invoke(path);}catch(InvalidOperationException){failed=true;}
            Program.Check(failed && server.Posts==1 && !File.Exists(path+".submitted") && server.Lists==0,"Definite rejection classification changed.");
            foreach(string mode in new[]{"expired","finished-before","cancel-race","manual","cancel-fails","cancel-500-done","cancel-poll-fails","server-expired","cancel-pending","no-created"})
            {
                server.Reset(mode);path=Path.Combine(root,mode+".png");
                var state=server.Live("processing");
                if(mode=="manual")state["created_at"]=DateTime.UtcNow.ToString("o");
                File.WriteAllText(path+".prediction.json",new JavaScriptSerializer().Serialize(state));
                File.WriteAllText(path+".submission.json",new JavaScriptSerializer().Serialize(new ReplicateSubmissionStamp{SentUtc=DateTime.UtcNow.AddMinutes(-11).ToString("o")}));
                if(mode=="manual")File.WriteAllText(path+".cancel","cancel");
                Exception error=null;object output=null;try{output=Invoke(path);}catch(Exception e){error=e;}
                Program.Check(server.Posts==0 && server.Uploads==0,"Cancellation submitted another paid model: "+mode);
                string saved=File.ReadAllText(path+".prediction.json");
                if(mode=="finished-before" || mode=="cancel-race")
                    Program.Check(error==null && Convert.ToString(output).EndsWith("result.png") && saved.Contains("succeeded") && server.Cancels==(mode=="finished-before"?0:1),"Finished result lost a race with cancellation: "+mode);
                else if(mode=="cancel-fails")
                    Program.Check(error is InvalidOperationException && error.Message.Contains("Не удалось подтвердить") && saved.Contains("live-job") && saved.Contains("processing") && server.Cancels==1,"Cancellation failure falsely marked job canceled or lost its ID.");
                else if(mode=="cancel-poll-fails")
                    Program.Check(error is InvalidOperationException && error.Message.Contains("не подтвердил остановку") && saved.Contains("processing") && saved.Contains("live-job") && server.Cancels==1,"Polling failure falsely confirmed cancellation or lost resume state.");
                else if(mode=="manual")
                    Program.Check(error is OperationCanceledException && saved.Contains("canceled") && server.Cancels==1,"Manual cancellation regressed.");
                else
                    Program.Check(error is TimeoutException && error.Message.Contains("подтвердил автоматическую отмену") && saved.Contains("canceled") && server.Cancels==(mode=="server-expired"?0:1),"Expired request was not canceled and confirmed: "+mode);
            }
            // The compact letter reference must take the same inline path as
            // small previews, without contacting file storage or altering bytes.
            string observed=Environment.GetEnvironmentVariable("VANYA_REPLICATE_COMPACT_REFERENCE");
            byte[] compact=String.IsNullOrEmpty(observed)?new byte[900*1024]:File.ReadAllBytes(observed);
            File.WriteAllBytes(file,compact);using(var stream=File.OpenRead(file))server.FileHash=ReplicateSubmissionRecovery.Digest(stream);
            server.Reset("recover");path=Path.Combine(root,"compact-reference.png");Invoke(path);
            string encoded=Convert.ToString(((System.Collections.IList)server.SeedInput["image_input"])[0]);
            Program.Check(server.Posts==1 && server.Uploads==0 && server.InlineImage && Convert.FromBase64String(encoded.Substring(encoded.IndexOf(',')+1)).SequenceEqual(compact),"Compact style preview did not stay byte-identical and inline.");
        }
    }
}
internal sealed class LoopbackFactory:IWebRequestCreate
{
    private readonly int _port;internal LoopbackFactory(int port){_port=port;}
    public WebRequest Create(Uri uri)=>WebRequest.Create("http://127.0.0.1:"+_port+uri.PathAndQuery);
}
internal sealed class FakeReplicateServer:IDisposable
{
    private readonly TcpListener _listener;private readonly Thread _thread;private volatile bool _stop;
    internal int Port,Posts,Lists,Uploads,MetadataReads,Cancels,PredictionReads;internal string AutoCancelHeader;internal bool ValidJson,InlineImage;internal string FileHash;internal Dictionary<string,object> SeedInput;private string _mode;internal bool IncludePrevious;internal string Mode{set{_mode=value;}}
    internal FakeReplicateServer(){_listener=new TcpListener(IPAddress.Loopback,0);_listener.Start();Port=((IPEndPoint)_listener.LocalEndpoint).Port;_thread=new Thread(Serve){IsBackground=true};_thread.Start();}
    internal void Reset(string mode){_mode=mode;Posts=Lists=Uploads=MetadataReads=Cancels=PredictionReads=0;AutoCancelHeader=null;SeedInput=null;ValidJson=InlineImage=false;}
    internal Dictionary<string,object> Live(string status)
    {
        var value=new Dictionary<string,object>{{"id","live-job"},{"status",status},{"urls",new Dictionary<string,object>{{"get","https://api.replicate.com/v1/predictions/live-job"},{"cancel","https://api.replicate.com/v1/predictions/live-job/cancel"}}},{"output",status=="succeeded"?"https://unit.replicate.delivery/result.png":null},{"error",null}};
        if(_mode!="no-created")value["created_at"]=DateTime.UtcNow.AddMinutes(-11).ToString("o");return value;
    }
    private void Serve()
    {
        while(!_stop)
        {
            try{using(var client=_listener.AcceptTcpClient()){client.ReceiveTimeout=10000;using(var stream=client.GetStream())Handle(stream);}}
            catch(SocketException){if(_stop)return;}
            catch(Exception e){if(!_stop)Console.Error.WriteLine("Synthetic HTTP server: "+e.Message);}
        }
    }
    private void Handle(NetworkStream stream)
    {
        var header=new List<byte>();int next;
        while((next=stream.ReadByte())>=0)
        {header.Add((byte)next);int n=header.Count;if(n>=4 && header[n-4]==13 && header[n-3]==10 && header[n-2]==13 && header[n-1]==10)break;if(n>65536)throw new IOException("Huge header");}
        string[] lines=Encoding.ASCII.GetString(header.ToArray()).Split(new[]{"\r\n"},StringSplitOptions.None);string[] request=lines[0].Split(' ');
        var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string line in lines){int colon=line.IndexOf(':');if(colon>0)headers[line.Substring(0,colon)]=line.Substring(colon+1).Trim();}
        int length=headers.ContainsKey("Content-Length")?Int32.Parse(headers["Content-Length"]):0;var bytes=new byte[length];int received=0;
        while(received<length){int n=stream.Read(bytes,received,length-received);if(n==0)throw new IOException("Truncated body");received+=n;}
        int code=200;object body;var json=new JavaScriptSerializer{MaxJsonLength=Int32.MaxValue};
        if(request[0]=="POST" && request[1]=="/v1/predictions")
        {
            AutoCancelHeader=headers.ContainsKey("Cancel-After")?headers["Cancel-After"]:null;
            Posts++;var payload=json.Deserialize<Dictionary<string,object>>(Encoding.UTF8.GetString(bytes));SeedInput=(Dictionary<string,object>)payload["input"];
            ValidJson=Convert.ToString(payload["version"])=="google/nano-banana-2" && Convert.ToString(SeedInput["prompt"])=="Синтетическая буква А · золото" && !headers.ContainsKey("Transfer-Encoding") && !headers.ContainsKey("Expect") && headers["Content-Type"].Contains("utf-8");
            InlineImage=Convert.ToString(((System.Collections.IList)SeedInput["image_input"])[0]).StartsWith("data:image/png;base64,");
            code=_mode=="reject"?422:500;body=new Dictionary<string,object>{{"detail","Synthetic API error"}};
        }
        else if(request[0]=="POST" && request[1]=="/v1/predictions/live-job/cancel")
        {
            Cancels++;
            if(_mode=="cancel-fails" || _mode=="cancel-500-done"){code=503;body=new Dictionary<string,object>{{"detail","Synthetic cancel unavailable"}};}
            else body=Live(_mode=="cancel-race"?"succeeded":(_mode=="cancel-pending" || _mode=="cancel-poll-fails")?"processing":"canceled");
        }
        else if(request[0]=="GET" && request[1]=="/v1/predictions/live-job")
        {PredictionReads++;if(_mode=="cancel-poll-fails" && Cancels>0)code=503;body=Live(_mode=="finished-before"?"succeeded":_mode=="server-expired" || (_mode=="cancel-pending" || _mode=="cancel-500-done") && Cancels>0?"canceled":"processing");}
        else if(request[0]=="GET" && request[1].StartsWith("/v1/predictions"))
        {
            Lists++;var items=new List<object>();if(_mode!="missing" && SeedInput!=null)items.Add(new Dictionary<string,object>{
                {"id","recovered-"+Posts},{"model","google/nano-banana-2"},{"version","hidden"},{"created_at",DateTime.UtcNow.ToString("o")},{"input",SeedInput},{"status",_mode=="failed"?"failed":"succeeded"},
                {"output","https://unit.replicate.delivery/result.png"},{"error",_mode=="failed"?"Synthetic terminal failure":null},{"urls",new Dictionary<string,object>{{"get","https://api.replicate.com/v1/predictions/recovered-test"}}}});
            if(IncludePrevious && Posts>1 && items.Count>0){var previous=new Dictionary<string,object>((Dictionary<string,object>)items[0]);previous["id"]="recovered-1";previous["status"]="failed";items.Add(previous);}
            if(_mode=="ambiguous" && items.Count>0){var extra=new Dictionary<string,object>((Dictionary<string,object>)items[0]);extra["id"]="another-match";items.Add(extra);}
            body=new Dictionary<string,object>{{"results",items},{"next",_mode=="truncated"?"https://api.replicate.com/v1/predictions?cursor=next":null}};
        }
        else if(request[0]=="GET" && request[1].StartsWith("/v1/files/"))
        {MetadataReads++;body=new Dictionary<string,object>{{"checksums",new Dictionary<string,object>{{"sha256",FileHash}}}};}
        else {Uploads++;code=400;body=new Dictionary<string,object>{{"detail","Unexpected synthetic request"}};}
        byte[] output=Encoding.UTF8.GetBytes(json.Serialize(body));string status=code==200?"OK":code==422?"Unprocessable Entity":"Internal Server Error";
        byte[] response=Encoding.ASCII.GetBytes("HTTP/1.1 "+code+" "+status+"\r\nContent-Type: application/json\r\nContent-Length: "+output.Length+"\r\nConnection: close\r\n\r\n");
        stream.Write(response,0,response.Length);stream.Write(output,0,output.Length);stream.Flush();
    }
    public void Dispose(){_stop=true;_listener.Stop();_thread.Join(1000);}
}
