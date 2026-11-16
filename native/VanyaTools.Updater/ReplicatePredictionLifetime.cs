using System;
using System.Collections.Generic;
using System.Globalization;

namespace VanyaTools.Updater
{
    internal static class ReplicatePredictionLifetime
    {
        internal const string CancelAfterHeader="10m";
        internal static readonly TimeSpan Maximum=TimeSpan.FromMinutes(10);
        internal static readonly TimeSpan ConfirmationGrace=TimeSpan.FromSeconds(30);
        internal static DateTime Started(Dictionary<string,object> prediction,string submittedUtc,DateTime fallbackUtc)
        {
            if(prediction!=null && prediction.TryGetValue("created_at",out var created) && Parse(Convert.ToString(created),out var timestamp))return timestamp;
            return Parse(submittedUtc,out var submitted)?submitted:fallbackUtc;
        }
        private static bool Parse(string value,out DateTime result)
        {
            if(!DateTime.TryParse(value,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out result))return false;
            result=result.ToUniversalTime();return result!=DateTime.MinValue;
        }
        internal static bool Expired(DateTime startedUtc,DateTime nowUtc)=>nowUtc-startedUtc>=Maximum;
        internal static bool ConfirmationExpired(TimeSpan elapsed)=>elapsed>=ConfirmationGrace;
    }
}
