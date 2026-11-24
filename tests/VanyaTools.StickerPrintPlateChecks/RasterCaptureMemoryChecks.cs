using System;
using VanyaTools.Native;

internal static class RasterCaptureMemoryChecks
{
    private const ulong GiB=1024UL*1024*1024;
    internal static void Run(Action<bool,string> check)
    {
        // Exact failed WHITE/CLEAR operation from the diagnostic log.
        const long pixels=2627856;
        const ulong physical=1318449152,commit=3696345088;
        var small=RasterCaptureMemory.Assess(pixels,32*GiB,physical,commit);
        ulong oldRequired=checked((ulong)pixels*24+GiB/4+GiB);
        check(oldRequired==1405245824 && oldRequired>physical,"The reported memory failure was not reproduced");
        check(small.Enough && small.Required==663008000 && small.Required<physical,"A small printing plate still requires the fixed 1 GiB reserve");
        check(small.Working==(ulong)pixels*24+GiB/4,"The conservative pixel/working-buffer estimate was weakened");
        check(small.Reserve==small.Working && small.Available==physical,"Small capture headroom or available-memory selection is wrong");
        check(StickerPrintPlateModel.CaptureDpi(73.57,64)==600,"Fixing memory lowered the sticker plate resolution");
        check(RasterCaptureMemory.Assess(pixels,32*GiB,small.Required,small.Required).Enough,"An exactly sufficient budget was rejected");
        check(!RasterCaptureMemory.Assess(pixels,32*GiB,small.Required-1,commit).Enough,"Low physical memory no longer blocks a capture");
        check(!RasterCaptureMemory.Assess(pixels,32*GiB,physical,small.Required-1).Enough,"Low commit headroom no longer blocks a capture");
        check(!RasterCaptureMemory.Assess(pixels,32*GiB,0,commit).Enough && !RasterCaptureMemory.Assess(pixels,32*GiB,physical,0).Enough,"A zero available-memory value was ignored");
        check(RasterCaptureMemory.Assess(pixels,32*GiB,commit,physical).Available==physical,"The guard does not use the lower of RAM and commit headroom");

        foreach(ulong total in new[]{8*GiB,16*GiB,32*GiB,64*GiB,128*GiB})
        {
            ulong previous=0;
            foreach(long count in new[]{1L,8000,2627856,25000000,50000000,64000000,100000000,150000000,250000000})
            {
                var budget=RasterCaptureMemory.Assess(count,total,0,0);
                ulong legacyReserve=Math.Max(GiB,total/32),legacyRequired=(ulong)count*24+GiB/4+legacyReserve;
                check(budget.Required>=previous && budget.Required>=budget.Working,"Capture memory is not monotonic or lost working buffers");
                check(budget.Reserve>0 && budget.Reserve<=budget.Working && budget.Reserve<=legacyReserve && budget.Required<=legacyRequired,"Capture reserve is missing or out of proportion");
                check(!budget.Enough,"The capture proceeded without available memory");
                if(budget.Working>=legacyReserve)check(budget.Required==legacyRequired,"Large capture lost its original safety margin");
                previous=budget.Required;
            }
        }
        foreach(long count in new[]{100000000L,150000000L,250000000L})foreach(ulong total in new[]{16*GiB,32*GiB,64*GiB})
        {
            var budget=RasterCaptureMemory.Assess(count,total,GiB,GiB);
            check(budget.Required==(ulong)count*24+GiB/4+Math.Max(GiB,total/32) && !budget.Enough,"Large sport/AI capture protection changed");
        }
        foreach(long count in new[]{0L,-1,Int64.MaxValue})
        {
            bool rejected=false;try{RasterCaptureMemory.Assess(count,32*GiB,GiB,GiB);}catch(ArgumentOutOfRangeException){rejected=true;}catch(OverflowException){rejected=true;}
            check(rejected,"Invalid pixel count bypassed the memory estimate");
        }
    }
}
