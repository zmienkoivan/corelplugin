using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using VanyaTools.Updater;
internal static class PollingChecks
{
    internal static void Run()
    {
        DateTime started=new DateTime(2026,11,17,10,0,0,DateTimeKind.Utc),now=started;int reads=0,history=0,retries=0;var delays=new List<TimeSpan>();
        var ready=new Dictionary<string,object>{{"id","saved-id"},{"status","succeeded"}};
        var result=ReplicateStatusPolling.Wait(()=>{if(++reads<=10)throw new IOException("Synthetic 503");return ready;},()=>{history++;return null;},e=>e is IOException,
            ()=>ReplicatePredictionLifetime.Expired(started,now),d=>{delays.Add(d);now+=d;},(n,e)=>retries=n,e=>null,()=>now);
        Program.Check(ReferenceEquals(result,ready) && reads==11 && retries==10 && history==3,"Ten transient polling errors still require manual continuation or bypass the history route.");
        Program.Check(delays.Count==10 && delays.All(d=>d.TotalSeconds>=2 && d.TotalSeconds<=15),"Automatic polling does not use bounded backoff.");
        Program.Check(ReplicateStatusPolling.Delay(1,"45",started).TotalSeconds==45 && ReplicateStatusPolling.Delay(1,started.AddMinutes(1).ToString("R"),started).TotalSeconds==60,"Retry-After was not respected.");
        Program.Check(ReplicateStatusPolling.Delay(1,"NaN",started).TotalSeconds==2 && ReplicateStatusPolling.Delay(1,"-3",started).TotalSeconds==2,"Invalid Retry-After changed the retry delay.");
        bool stop=false;reads=0;
        result=ReplicateStatusPolling.Wait(()=>{reads++;throw new IOException();},null,e=>true,()=>stop,d=>stop=true,(n,e)=>{},e=>null,()=>started);
        Program.Check(result==null && reads==1,"Cancellation during backoff sends another request.");
        now=started;reads=0;
        result=ReplicateStatusPolling.Wait(()=>{reads++;throw new IOException();},null,e=>true,()=>ReplicatePredictionLifetime.Expired(started,now),d=>now+=d,(n,e)=>{},e=>null,()=>now);
        Program.Check(result==null && now>=started.AddMinutes(10) && reads<60,"Automatic polling reset or ignored the original deadline.");
        bool rejected=false;try{ReplicateStatusPolling.Wait(()=>throw new ArgumentException("permanent"),null,e=>e is IOException,()=>false,d=>{},(n,e)=>{},e=>null,()=>started);}catch(ArgumentException){rejected=true;}
        Program.Check(rejected,"A permanent failure was retried indefinitely.");
        var rows=new ArrayList {new Dictionary<string,object>{{"id","different"},{"status","succeeded"}},ready};
        Program.Check(ReferenceEquals(ReplicateStatusPolling.Find(rows,"saved-id"),ready) && ReplicateStatusPolling.Find(rows,"absent")==null,"History polling selected another request.");
        reads=0;history=0;
        result=ReplicateStatusPolling.Wait(()=>{reads++;throw new IOException();},()=>{history++;return ReplicateStatusPolling.Find(rows,"saved-id");},e=>true,()=>false,d=>throw new Exception("Unexpected wait"),(n,e)=>{},e=>null,()=>started);
        Program.Check(ReferenceEquals(result,ready) && reads==1 && history==1,"Available history result was not returned immediately.");
    }
}
