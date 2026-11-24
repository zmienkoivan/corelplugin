using System;
using System.Diagnostics;

namespace VanyaTools.Native
{
    internal sealed class StickerPrintPlateMemory
    {
        private readonly string _job=Guid.NewGuid().ToString("N").Substring(0,8);
        private readonly string _ink;
        private long _baseline=-1;
        internal StickerPrintPlateMemory(StickerPrintPlateKind kind)
        { _ink=StickerPrintPlateModel.Ink(kind);Mark("start"); }

        internal void Mark(string stage)
        {
            try
            {
                using(var process=Process.GetCurrentProcess())
                {
                    long current=process.PrivateMemorySize64;
                    if(_baseline<0)_baseline=current;
                    Log.Info("Sticker plate memory: job="+_job+", ink="+_ink+", stage="+stage+
                        ", pid="+process.Id+", process="+process.ProcessName+
                        ", private="+current+", working set="+process.WorkingSet64+
                        ", managed="+GC.GetTotalMemory(false)+", private change="+(current-_baseline));
                }
            }
            catch(Exception error){Log.Error("Could not read printing-plate memory diagnostics.",error);}
        }
    }
}
