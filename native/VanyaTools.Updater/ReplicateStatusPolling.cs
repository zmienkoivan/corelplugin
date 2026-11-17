using System;
using System.Collections.Generic;
using System.Globalization;
namespace VanyaTools.Updater
{
    internal static class ReplicateStatusPolling
    {
        internal static TimeSpan Delay(int failures,string retryAfter,DateTime nowUtc)
        {
            double seconds=Math.Min(15,Math.Pow(2,Math.Min(4,Math.Max(1,failures))));
            if(Double.TryParse(retryAfter,NumberStyles.Float,CultureInfo.InvariantCulture,out double value) && !Double.IsNaN(value) && !Double.IsInfinity(value) && value>=0)
                seconds=Math.Max(seconds,Math.Min(600,value));
            else if(DateTime.TryParse(retryAfter,CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal|DateTimeStyles.AssumeUniversal,out var until))
                seconds=Math.Max(seconds,Math.Min(600,(until.ToUniversalTime()-nowUtc).TotalSeconds));
            return TimeSpan.FromSeconds(seconds);
        }
        // Only GET callbacks are supplied here. Cancellation/deadline returns
        // control to the existing confirmed server-cancellation path.
        internal static Dictionary<string,object> Wait(Func<Dictionary<string,object>> read,Func<Dictionary<string,object>> history,
            Func<Exception,bool> transient,Func<bool> stop,Action<TimeSpan> pause,Action<int,Exception> report,
            Func<Exception,string> retryAfter,Func<DateTime> now)
        {
            int failures=0;
            while(!stop())
            {
                try{return read();}
                catch(Exception error) when(transient(error))
                {
                    if(stop())return null;
                    report(++failures,error);
                    if(history!=null && (failures==1 || failures%4==0))
                    {
                        try{var saved=history();if(saved!=null)return saved;}
                        catch(Exception lookup) when(transient(lookup)){}
                    }
                    if(stop())return null;
                    pause(Delay(failures,retryAfter(error),now()));
                }
            }
            return null;
        }
        internal static Dictionary<string,object> Find(System.Collections.IList rows,string id)
        {
            if(String.IsNullOrEmpty(id) || rows==null)return null;
            foreach(object value in rows)if(value is Dictionary<string,object> row && ReplicateSubmissionRecovery.Value(row,"id")==id)
            {
                string status=ReplicateSubmissionRecovery.Value(row,"status");
                if(status=="starting" || status=="processing" || status=="succeeded" || status=="failed" || status=="canceled")return row;
            }
            return null;
        }
    }
}
