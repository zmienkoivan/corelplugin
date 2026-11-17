using System;
using System.Collections;
using System.Linq;
using System.Threading;
using VanyaTools.Native;

internal static class OutlineMaskChecks
{
    private static RasterMaskShape Box(double x,double y,double w,double h) => new RasterMaskShape(RasterMaskTool.Rectangle,
        new[]{new RasterMaskPoint(x,y),new RasterMaskPoint(x+w,y+h)},0);
    private static NativeRasterBuffer Source(int mode)
    {
        int n=100*60,channels=mode==5?4:mode==4?3:1;var alpha=new byte[n];var colors=new byte[n*channels];
        for(int p=0;p<n;p++)for(int c=0;c<channels;c++)colors[p*channels+c]=(byte)(20+c*35+p%11);
        for(int y=10;y<45;y++)for(int x=10;x<80;x++)if(x<30 || x>=60)alpha[y*100+x]=255;
        alpha[20*100+20]=117;
        return new NativeRasterBuffer(100,60,mode,254,508,colors,alpha,mode==5?new[]{2,0,3,1}:null);
    }
    private static NativeRasterFilterOptions Options(RasterProtectionMask protection=null) => new NativeRasterFilterOptions {
        Outline=true,RadiusMm=.1,ExpansionMm=.2,OutlineMask=protection };
    private static byte Alpha(NativeRasterFilterResult result,double x,double y,double originX=0,double originY=0)
    {
        int px=(int)Math.Floor((x-originX+result.OffsetMm.Item1)*result.Buffer.DpiX/25.4),
            py=(int)Math.Floor((y-originY+result.OffsetMm.Item2)*result.Buffer.DpiY/25.4);
        return px<0 || py<0 || px>=result.Buffer.Width || py>=result.Buffer.Height?(byte)0:result.Buffer.Alpha[py*result.Buffer.Width+px];
    }
    internal static void Run(Action<bool,string> check)
    {
        var emptyErase=new RasterProtectionMask(new[]{new RasterMaskShape(RasterMaskTool.Eraser,new[]{new RasterMaskPoint(1,1)},.5)});
        check(emptyErase.Rasterize(100,60,254,508,0,0,CancellationToken.None)==null,"Erasing an empty mask allocated or painted protection");
        foreach(int mode in new[]{4,5,2})
        {
            var source=Source(mode);var colors=(byte[])source.Colors.Clone();var alpha=(byte[])source.Alpha.Clone();
            var protection=new RasterProtectionMask(new[]{Box(-1,-1,5.5,5)});
            var baseline=NativeRasterFilters.Process(source,Options());var guarded=NativeRasterFilters.Process(source,Options(protection));
            check(Alpha(baseline,.95,1.025)>0 && Alpha(guarded,.95,1.025)==0,"Protected left edge still received an outline, model "+mode);
            check(Alpha(guarded,5.95,1.025)==Alpha(baseline,5.95,1.025) && Alpha(guarded,5.95,1.025)>0,"Independent right edge lost its outline, model "+mode);
            check(Alpha(guarded,2.05,1.025)==117 && guarded.Buffer.Mode==mode,"Outline protection changed source alpha/model");
            int px=(int)Math.Round(20+guarded.OffsetMm.Item1*guarded.Buffer.DpiX/25.4),py=(int)Math.Round(20+guarded.OffsetMm.Item2*guarded.Buffer.DpiY/25.4);
            check(guarded.Buffer.Colors.Skip((py*guarded.Buffer.Width+px)*source.Channels).Take(source.Channels).SequenceEqual(colors.Skip((20*source.Width+20)*source.Channels).Take(source.Channels)),"Outline mask recoloured protected native samples");
            var sourceOnly=NativeRasterFilters.Process(source,Options(new RasterProtectionMask(new[]{Box(1,.5,2,1.75)})));
            check(Alpha(sourceOnly,.95,1.025)==0,"Protected ink emitted an outline outside its mask");
            var destinationOnly=NativeRasterFilters.Process(source,Options(new RasterProtectionMask(new[]{Box(5.8,.7,.2,.8)})));
            check(Alpha(destinationOnly,5.95,1.025)==0 && Alpha(destinationOnly,5.75,1.025)>0,"Destination protection did not clip only the selected rim area");
            var erase=new RasterProtectionMask(new[]{Box(-1,-1,5.5,5),new RasterMaskShape(RasterMaskTool.Eraser,new[]{new RasterMaskPoint(1,1.2)},.45)});
            var erased=NativeRasterFilters.Process(source,Options(erase));
            check(Alpha(erased,.95,1.225)>0 && Alpha(erased,.95,2.025)==0,"Eraser did not restore outline only in the cleared area");
            var all=NativeRasterFilters.Process(source,Options(new RasterProtectionMask(new[]{Box(-2,-2,14,7)})));
            check(all.Buffer.Alpha.Count(a=>a!=0)==alpha.Count(a=>a!=0) && Alpha(all,2.05,1.025)==117,"Full outline protection added pixels or changed existing opacity");
            var smooth=Options(protection);smooth.SmoothStairs=true;smooth.StairsScale=2;
            var smoothed=NativeRasterFilters.Process(source,smooth);
            check(Alpha(smoothed,.85,1.025)==0 && Alpha(smoothed,5.95,1.025)>0,"Outline protection drifted after smoothing and crop offsets");
            var resample=Options(protection);resample.Resample300=true;var scaled=NativeRasterFilters.Process(source,resample);
            check(Alpha(scaled,.85,1.025)==0 && Alpha(scaled,5.95,1.025)>0 && scaled.Buffer.DpiX>=300 && scaled.Buffer.DpiY==source.DpiY,"Outline protection drifted after 300 DPI resampling: left="+Alpha(scaled,.85,1.025)+", right="+Alpha(scaled,5.95,1.025)+", dpi="+scaled.Buffer.DpiX+"/"+scaled.Buffer.DpiY);
            var crop=new NativeRasterBuffer(50,50,mode,source.DpiX,source.DpiY,new byte[50*50*source.Channels],new byte[50*50],source.CmykOrder);
            for(int y=0;y<50;y++) {Buffer.BlockCopy(source.Colors,((y+5)*100+50)*source.Channels,crop.Colors,y*50*source.Channels,50*source.Channels);Buffer.BlockCopy(source.Alpha,(y+5)*100+50,crop.Alpha,y*50,50);}
            var regional=Options(new RasterProtectionMask(new[]{Box(5.8,.7,.2,.8)}));regional.PrintPreparation=new DtfPrintPreparationOptions{OriginXmm=5,OriginYmm=.25};
            var tile=NativeRasterFilters.Process(crop,regional);
            check(Alpha(tile,5.95,1.025,5,.25)==0 && Alpha(tile,5.75,1.025,5,.25)>0,"Regional preview shifted the outline mask");
            check(source.Colors.SequenceEqual(colors) && source.Alpha.SequenceEqual(alpha),"Masked outline mutated the original raster");
        }
        var ordered=new RasterProtectionMask(new[]{Box(0,0,10,10),new RasterMaskShape(RasterMaskTool.Eraser,new[]{new RasterMaskPoint(1,1),new RasterMaskPoint(3,3)},.2),new RasterMaskShape(RasterMaskTool.Brush,new[]{new RasterMaskPoint(2,2)},.1)});
        var bits=ordered.Rasterize(100,100,254,254,0,0,CancellationToken.None);
        check(!bits[10*100+10] && bits[20*100+20] && !bits[30*100+30] && bits[10*100+50],"Brush/eraser order lost a diagonal hole or repainted region");
    }
}
