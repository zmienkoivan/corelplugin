using System;
using System.Linq;
using System.Threading;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    private static readonly int[] Order = { 2, 0, 3, 1 };
    private static void Check(bool value, string text) { _checks++; if (!value) throw new Exception(text); }
    private static void Reject<T>(Action action) where T : Exception
    { try { action(); } catch (T) { _checks++; return; } throw new Exception("Expected " + typeof(T).Name); }
    private static byte[] Screen(byte[] source, int w, int h, double dpiX, double dpiY, DtfPrintPreparationOptions options)
    { var result = (byte[])source.Clone(); DtfPrintPreparation.Halftone(result, 1, 0, w, h, dpiX, dpiY, options, CancellationToken.None); return result; }
    private static byte[] Solid(int n, byte a) => Enumerable.Repeat(a, n).ToArray();
    private static double Coverage(byte[] alpha) => alpha.Count(a => a != 0) / (double)alpha.Length;
    private static NativeRasterBuffer Image(int mode, byte[] color, byte alpha = 255)
    {
        int w = 160, h = 120; var colors = new byte[w * h * color.Length];
        for (int p = 0; p < w * h; p++) Buffer.BlockCopy(color, 0, colors, p * color.Length, color.Length);
        return new NativeRasterBuffer(w, h, mode, 400, 300, colors, Solid(w * h, alpha), mode == 5 ? Order : null);
    }
    private static byte[] Cmyk(byte c, byte m, byte y, byte k)
    { var value = new byte[4]; value[Order[0]] = c; value[Order[1]] = m; value[Order[2]] = y; value[Order[3]] = k; return value; }
    private static int Main()
    {
        try { Knockout(); SoftKnockout(); NegativeTolerance(); Islands(); Resample(); Models(); Boost(); Modes(); Shapes(); Millimeters(); Phase(); Combined(); ProtectionMasks(); Validation();
            Console.WriteLine("DTF print preparation checks passed: " + _checks + ". Synthetic planes only; no Corel or customer images."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void ProtectionMasks()
    {
        RasterProtectionMask left = new RasterProtectionMask(new[] {
            new RasterMaskShape(RasterMaskTool.Rectangle,
                new[] { new RasterMaskPoint(0, 0), new RasterMaskPoint(5, 20) }, 0)
        });
        var cmykBlack = Cmyk(0, 0, 0, 255);
        var black = Image(5, cmykBlack);
        var result = NativeRasterFilters.Process(black, new NativeRasterFilterOptions
        {
            KnockoutMask = left, PrintPreparation = new DtfPrintPreparationOptions { Knockout = true, Tolerance = 0 }
        }).Buffer;
        Check(result.Alpha[0] == 255 && result.Alpha[black.Width - 1] == 0 &&
            result.Colors.Take(4).SequenceEqual(cmykBlack), "Knockout protects original CMYK channels and alpha");

        var nearWhite = Image(4, new byte[] { 253, 253, 253 }, 160);
        result = NativeRasterFilters.Process(nearWhite, new NativeRasterFilterOptions
        {
            RemoveSemiTransparent = true, AlphaMask = left, WhitenNearWhite = true, WhiteMask = left
        }).Buffer;
        Check(result.Alpha[0] == 160 && result.Alpha[nearWhite.Width - 1] == 255, "Threshold respects its own mask");
        Check(result.Colors[0] == 253 && result.Colors[(nearWhite.Width - 1) * 3] == 255, "Whitening respects its own mask");

        var print = Image(4, new byte[] { 120, 100, 75 });
        result = NativeRasterFilters.Process(print, new NativeRasterFilterOptions
        {
            HalftoneMask = left, PrintPreparation = new DtfPrintPreparationOptions
            {
                Halftone = true, AlphaOnly = false, ColorBoost = 50,
                RemoveSmall = true, RemoveBelowMm = .3, AutoDot = false, DotDiameterMm = .2
            }
        }).Buffer;
        Check(result.Alpha[0] == 255 && result.Colors.Take(3).SequenceEqual(print.Colors.Take(3)) &&
            result.Alpha.Skip(100).Take(60).Any(a => a == 0), "Halftone protection includes boost and dot cleanup");

        var spot = new RasterProtectionMask(new[] { new RasterMaskShape(RasterMaskTool.Ellipse,
            new[] { new RasterMaskPoint(1, 1), new RasterMaskPoint(2, 2) }, 0) });
        var crop = spot.Rasterize(20, 20, 508, 508, 1, 1, CancellationToken.None);
        Check(!crop[0] && crop[10 * 20 + 10], "Oval stays on the same physical area after resample and viewport crop");
        var brush = new RasterProtectionMask(new[] { new RasterMaskShape(RasterMaskTool.Brush,
            new[] { new RasterMaskPoint(0, 0), new RasterMaskPoint(3, 3) }, .15) });
        var stroke = brush.Rasterize(30, 30, 254, 254, 0, 0, CancellationToken.None);
        Check(stroke[0] && stroke[15 * 30 + 15] && stroke[899] && !stroke[29], "Fast diagonal brush strokes have no gaps");
    }

    private static void NegativeTolerance()
    {
        byte previous=0;
        foreach(int tolerance in new[]{0,-32,-128,-255}) {
            byte[] rgb={0,0,128,0,0,0};byte[] alpha={255,255};
            var options=new DtfPrintPreparationOptions {Knockout=true,SoftKnockout=true,Tolerance=tolerance};
            DtfPrintPreparation.Knockout(rgb,3,4,null,alpha,1,0,2,1,options,CancellationToken.None);
            Check(alpha[0]>=previous && alpha[1]==0,"Negative tolerance does not narrow effect or preserves exact key");
            Check(Math.Abs(alpha[0]/255d*rgb[2]-128)<1.5,"Negative tolerance broke garment recomposition");previous=alpha[0];
        }
        Check(previous==255,"Minus 255 did not retain non-key colour");
        Reject<ArgumentOutOfRangeException>(()=>new DtfPrintPreparationOptions {Knockout=true,SoftKnockout=false,Tolerance=-1}.Validate());
    }
    private static NativeRasterBuffer IslandImage(int w,int h,double dpiX=254,double dpiY=254)
    {
        var colors=new byte[w*h*3];for(int i=0;i<colors.Length;i+=3){colors[i]=10;colors[i+1]=20;colors[i+2]=30;}
        return new NativeRasterBuffer(w,h,4,dpiX,dpiY,colors,new byte[w*h]);
    }
    private static void Rect(NativeRasterBuffer image,int x,int y,int w,int h)
    {for(int row=y;row<y+h;row++)for(int col=x;col<x+w;col++)image.Alpha[row*image.Width+col]=255;}
    private static void Islands()
    {
        var source=IslandImage(100,60);Rect(source,5,5,1,1);Rect(source,20,8,2,2);Rect(source,35,8,3,3);Rect(source,55,5,1,10);Rect(source,70,10,1,1);Rect(source,71,11,1,1);
        var before=(byte[])source.Alpha.Clone();
        var prep=new DtfPrintPreparationOptions {Halftone=true,AlphaOnly=true,RemoveSmall=true,GrowSmall=true,RemoveBelowMm=.3,GrowToMm=.5};
        var result=NativeRasterFilters.Process(source,new NativeRasterFilterOptions {PrintPreparation=prep,OutlineColor=new byte[]{0,0,255}});
        int px=(int)Math.Round(result.OffsetMm.Item1*254/25.4),py=(int)Math.Round(result.OffsetMm.Item2*254/25.4);var image=result.Buffer;
        Func<int,int,int> at=(x,y)=>(y+py)*image.Width+x+px;
        Check(image.Alpha[at(5,5)]==0 && image.Alpha[at(20,8)]==0 && image.Alpha[at(70,10)]==0 && image.Alpha[at(71,11)]==0,"Small islands or diagonal-connected dots survived removal");
        Check(image.Alpha[at(35,8)]==255 && image.Alpha[at(34,7)]==255 && image.Alpha[at(38,11)]==255,"Threshold-sized island was deleted or not grown");
        Check(image.Alpha[at(54,5)]==0 && image.Alpha[at(55,5)]==255,"Long line was treated as a small island");
        Check(image.Colors[at(34,7)*3+2]==255 && image.Colors[at(35,8)*3]==10,"Grow rim did not use selected colour or changed island centre");
        Check(source.Alpha.SequenceEqual(before),"Cleanup changed original alpha");
        Check(image.Alpha.All(a=>a==0 || a==255),"Cleanup introduced semi-transparent pixels");
        var onlyTiny=IslandImage(20,20);Rect(onlyTiny,10,10,1,1);
        var gone=NativeRasterFilters.Process(onlyTiny,new NativeRasterFilterOptions {PrintPreparation=prep,Outline=true,ExpansionMm=.2,RadiusMm=.2});
        Check(gone.Buffer.Alpha.All(a=>a==0),"Removed dot was resurrected by grow or global outline");
        var corner=IslandImage(20,20);Rect(corner,0,0,1,1);var grow=prep.Copy();grow.RemoveSmall=false;
        var expanded=NativeRasterFilters.Process(corner,new NativeRasterFilterOptions {PrintPreparation=grow});
        Check(expanded.Buffer.Alpha.Count(a=>a!=0)==25 && expanded.OffsetMm.Item1>0 && expanded.OffsetMm.Item2>0,"Corner dot was clipped instead of expanded with physical offset");
        var clipped=prep.Copy();clipped.GrowSmall=false;clipped.CutEdges=1;
        Check(NativeRasterFilters.Process(corner,new NativeRasterFilterOptions {PrintPreparation=clipped}).Buffer.Alpha[0]==255,"Artificial cropped edge was classified as a whole small dot");
        var longOnly=IslandImage(20,20);Rect(longOnly,8,2,1,10);
        var unchanged=NativeRasterFilters.Process(longOnly,new NativeRasterFilterOptions {PrintPreparation=grow});
        Check(unchanged.Buffer.Width==20 && unchanged.Buffer.Height==20 && unchanged.OffsetMm.Item1==0,"No eligible islands still added padding");
        var anisotropic=IslandImage(30,30,254,127);Rect(anisotropic,15,15,1,1);grow.GrowToMm=.6;
        var nonSquare=NativeRasterFilters.Process(anisotropic,new NativeRasterFilterOptions {PrintPreparation=grow}).Buffer;
        int minX=nonSquare.Width,maxX=-1,minY=nonSquare.Height,maxY=-1;
        for(int p=0;p<nonSquare.Alpha.Length;p++)if(nonSquare.Alpha[p]!=0){minX=Math.Min(minX,p%nonSquare.Width);maxX=Math.Max(maxX,p%nonSquare.Width);minY=Math.Min(minY,p/nonSquare.Width);maxY=Math.Max(maxY,p/nonSquare.Width);}
        Check((maxX-minX+1)*.1>=.6-1e-9 && (maxY-minY+1)*.2>=.6-1e-9,"Grow ignored per-axis DPI");
        var sharp=NativeRasterFilters.Process(source,new NativeRasterFilterOptions {PrintPreparation=new DtfPrintPreparationOptions {Halftone=true,AlphaOnly=false},Outline=true,RadiusMm=.3,ExpansionMm=.1});
        Check(sharp.Buffer.Alpha.All(a=>a==0 || a==255),"Halftone outline still has Gaussian alpha fringe");
        Reject<OperationCanceledException>(()=>NativeRasterFilters.Process(source,new NativeRasterFilterOptions {PrintPreparation=prep},new CancellationToken(true)));
        Reject<ArgumentException>(()=>new DtfPrintPreparationOptions {Halftone=true,RemoveSmall=true,RemoveBelowMm=0}.Validate());
        Reject<ArgumentException>(()=>new DtfPrintPreparationOptions {Halftone=true,GrowSmall=true,GrowToMm=Double.NaN}.Validate());
        // Exact cropped preview: padding and artificial edges must not change
        // the same visible island compared with processing the complete source.
        var fullSource=IslandImage(160,100);Rect(fullSource,80,50,3,3);Rect(fullSource,10,30,50,1);
        var opts=new DtfPrintPreparationOptions {Halftone=true,AlphaOnly=true,GrowSmall=true,GrowToMm=.5};
        var full=NativeRasterFilters.Process(fullSource,new NativeRasterFilterOptions {PrintPreparation=opts});
        const int left=40,top=20,width=80,height=60;var tileSource=IslandImage(width,height);
        for(int y=0;y<height;y++)Buffer.BlockCopy(fullSource.Alpha,(top+y)*160+left,tileSource.Alpha,y*width,width);
        var tileOpts=opts.Copy();tileOpts.CutEdges=15;
        var tile=NativeRasterFilters.Process(tileSource,new NativeRasterFilterOptions {PrintPreparation=tileOpts});
        int fx=(int)Math.Round(full.OffsetMm.Item1*10),fy=(int)Math.Round(full.OffsetMm.Item2*10),tx=(int)Math.Round(tile.OffsetMm.Item1*10),ty=(int)Math.Round(tile.OffsetMm.Item2*10);
        bool same=true;for(int y=45;y<60;y++)for(int x=75;x<90;x++)same &= full.Buffer.Alpha[(y+fy)*full.Buffer.Width+x+fx]==tile.Buffer.Alpha[(y-top+ty)*tile.Buffer.Width+x-left+tx];
        Check(same,"Cropped cleanup differs from full source");
    }
    private static void Resample()
    {
        foreach(int mode in new[]{4,5,2}) {
            byte[] white=mode==4?new byte[]{255,255,255}:mode==5?Cmyk(0,0,0,0):new byte[]{255};
            int ch=white.Length;var colors=new byte[2*ch];Buffer.BlockCopy(white,0,colors,0,ch);
            for(int c=0;c<ch;c++)colors[ch+c]=mode==5?(byte)255:(byte)0;
            var before=(byte[])colors.Clone();
            var source=new NativeRasterBuffer(2,1,mode,100,400,colors,new byte[]{255,0},mode==5?Order:null);
            var result=NativeRasterFilters.Process(source,new NativeRasterFilterOptions {Resample300=true});var data=result.Buffer;
            Check(data.Mode==mode && data.Width==6 && data.Height==1 && data.DpiX==300 && data.DpiY==400,"Resample model/DPI or high-resolution axis changed");
            Check(Math.Abs(data.Width/data.DpiX-source.Width/source.DpiX)<1e-12 && Math.Abs(data.Height/data.DpiY-source.Height/source.DpiY)<1e-12,"Resample changed physical print size");
            Check(data.Alpha.Any(a=>a>0 && a<255),"Resample did not interpolate alpha");
            Check(Enumerable.Range(0,data.Alpha.Length).Where(p=>data.Alpha[p]>0).All(p=>data.IsPureWhite(p)),"Resample darkened pure white at transparent boundary");
            Check(source.Width==2 && source.Alpha.SequenceEqual(new byte[]{255,0}) && source.Colors.SequenceEqual(before),"Resample modified source data");
            Check(result.OffsetMm.Item1==0 && result.OffsetMm.Item2==0,"Resample moved original location");
            var again=NativeRasterFilters.Process(data,new NativeRasterFilterOptions {Resample300=true}).Buffer;
            Check(again.Width==data.Width && again.Height==data.Height && again.Colors.SequenceEqual(data.Colors) && again.Alpha.SequenceEqual(data.Alpha),"Resample repeated at 300 DPI");
        }
        var unchanged=DtfResamplePlan.Create(123,234,600,400);Check(!unchanged.Needed && unchanged.Width==123 && unchanged.DpiX==600,"High-DPI source was downsampled");
        var rounding=DtfResamplePlan.Create(32,16,99.9998,99.9998);Check(rounding.Width==96 && rounding.Height==48,"DPI metadata rounding added an unnecessary pixel");
        var near300=DtfResamplePlan.Create(1500,1500,299.9994,300.0002);Check(!near300.Needed,"300 DPI metadata caused a repeat resample");
        var arbitrary=DtfResamplePlan.Create(101,73,137.3,209.2);Check(Math.Abs(arbitrary.Width/arbitrary.DpiX-101/137.3)<1e-12 && Math.Abs(arbitrary.Height/arbitrary.DpiY-73/209.2)<1e-12,"Non-integer scaling changed physical size");
        Reject<InvalidOperationException>(()=>DtfResamplePlan.Create(10000,10000,72,72));
        Reject<ArgumentException>(()=>DtfResamplePlan.Create(10,10,Double.NaN,300));
        Reject<OperationCanceledException>(()=>NativeRasterFilters.Process(Image(4,new byte[]{255,255,255}),new NativeRasterFilterOptions {Resample300=true},new CancellationToken(true)));
    }
    private static void SoftKnockout()
    {
        // Recompose arbitrary foreground/key mixtures. The unmixing must keep
        // the displayed colour over that key (not just fade original RGB).
        foreach (var key in new[] {new[]{0,0,0},new[]{255,255,255},new[]{35,90,150}})
        {
            var options=new DtfPrintPreparationOptions {Knockout=true,SoftKnockout=true,Tolerance=0,Red=(byte)key[0],Green=(byte)key[1],Blue=(byte)key[2]};
            byte[] rgb={60,110,190, 190,120,20, (byte)key[2],(byte)key[1],(byte)key[0]};
            byte[] original=(byte[])rgb.Clone(),alpha={255,128,255};
            DtfPrintPreparation.Knockout(rgb,3,4,null,alpha,1,0,3,1,options,CancellationToken.None);
            Check(alpha[2]==0,"Soft knockout retained pure key");
            for(int p=0;p<2;p++)for(int c=0;c<3;c++) {
                double before=(p==0?1:128/255d)*original[p*3+c]+(p==0?0:1-128/255d)*key[2-c];
                double after=alpha[p]/255d*rgb[p*3+c]+(1-alpha[p]/255d)*key[2-c];
                Check(Math.Abs(before-after)<=1.5,"Colour-to-alpha did not recompose over garment colour");
            }
        }
        var black=new DtfPrintPreparationOptions {Knockout=true,SoftKnockout=true,Tolerance=0};
        byte[] red={0,0,64, 0,0,128, 0,0,255};byte[] a={255,255,255};
        DtfPrintPreparation.Knockout(red,3,4,null,a,1,0,3,1,black,CancellationToken.None);
        Check(a.SequenceEqual(new byte[]{64,128,255}) && red[2]==255 && red[5]==255,"Black component was not unmixed from red gradient");
        foreach(int mode in new[]{2,5}) {
            byte[] color=mode==2?new byte[]{128}:Cmyk(0,255,255,127);
            var source=Image(mode,color);var before=(byte[])source.Colors.Clone();
            var result=NativeRasterFilters.Process(source,new NativeRasterFilterOptions {PrintPreparation=black}).Buffer;
            Check(result.Mode==mode && result.Alpha.All(v=>v>0 && v<255),"Soft native opacity missing");
            var view=result.ToPreviewBgra();
            Check(view[2]>=254,"Native unmixing did not remove black contribution");
            Check(source.Colors.SequenceEqual(before) && source.Alpha.All(v=>v==255),"Soft knockout modified source buffer");
        }
        var transparent=Image(4,new byte[]{60,80,120},0);var copy=(byte[])transparent.Colors.Clone();
        Check(NativeRasterFilters.Process(transparent,new NativeRasterFilterOptions {PrintPreparation=black}).Buffer.Colors.SequenceEqual(copy),"Soft knockout recoloured invisible samples");
        var white=new DtfPrintPreparationOptions {Knockout=true,SoftKnockout=true,Tolerance=0,Red=255,Green=255,Blue=255};
        var grayInk=NativeRasterFilters.Process(Image(5,Cmyk(0,0,0,127)),new NativeRasterFilterOptions {PrintPreparation=white}).Buffer;
        Check(grayInk.Colors[Order[3]]==255 && grayInk.Colors[Order[0]]==0 && grayInk.Colors[Order[1]]==0 && grayInk.Colors[Order[2]]==0,"Neutral unmixing created rich black instead of K100");
        var colored=white.Copy();colored.Red=120;
        Reject<InvalidOperationException>(()=>NativeRasterFilters.Process(Image(2,new byte[]{128}),new NativeRasterFilterOptions {PrintPreparation=colored}));
    }
    private static void Boost()
    {
        foreach(int mode in new[]{4,5,2}) {
            byte[] color=mode==4?new byte[]{90,120,180}:mode==5?Cmyk(0,85,128,75):new byte[]{80};
            var source=Image(mode,color);var before=(byte[])source.Colors.Clone();
            var options=new DtfPrintPreparationOptions {Halftone=true,AlphaOnly=false,ColorBoost=65};
            var result=NativeRasterFilters.Process(source,new NativeRasterFilterOptions {PrintPreparation=options}).Buffer;
            Check(result.Mode==mode && !result.Colors.SequenceEqual(before),"Explicit colour boost had no effect or changed model");
            Check(source.Colors.SequenceEqual(before),"Boost changed source colour samples");
            options.AlphaOnly=true;
            Check(NativeRasterFilters.Process(source,new NativeRasterFilterOptions {PrintPreparation=options}).Buffer.Colors.SequenceEqual(before),"Boost applied in alpha-only mode");
            options.AlphaOnly=false;options.ColorBoost=0;
            Check(NativeRasterFilters.Process(source,new NativeRasterFilterOptions {PrintPreparation=options}).Buffer.Colors.SequenceEqual(before),"Zero boost rewrote colour channels");
        }
    }
    private static void Shapes()
    {
        byte[] first=null;
        foreach(DtfDotShape shape in Enum.GetValues(typeof(DtfDotShape))) {
            var options=new DtfPrintPreparationOptions {Halftone=true,Shape=shape,AlphaOnly=true};
            foreach(byte a in new byte[]{64,128,224}) {
                var result=Screen(Solid(360*360,a),360,360,508,508,options);
                Check(Math.Abs(Coverage(result)-a/255d)<.025,"Shape tone calibration drift: "+shape+", "+a+" -> "+Coverage(result));
            }
            options.AlphaOnly=false;var dense=Screen(Solid(360*360,255),360,360,508,508,options);
            Check(Coverage(dense)>.02 && Coverage(dense)<.8,"Shape missing transparent gaps: "+shape);
            if(first!=null)Check(!first.SequenceEqual(dense),"Shape selector had no effect: "+shape);else first=dense;
            options.AutoDot=false;options.DotDiameterMm=.25;
            double small=Coverage(Screen(Solid(360*360,255),360,360,508,508,options));
            options.DotDiameterMm=.5;
            Check(Coverage(Screen(Solid(360*360,255),360,360,508,508,options))>small*2,"Shape size ineffective: "+shape);
        }
    }
    private static void Knockout()
    {
        var bgra = new byte[5 * 5 * 4]; for (int p = 0; p < 25; p++) bgra[p * 4 + 3] = 255;
        bgra[12 * 4] = bgra[12 * 4 + 1] = bgra[12 * 4 + 2] = 255;
        var colors = (byte[])bgra.Clone();
        var options = new DtfPrintPreparationOptions { Knockout = true, Red = 255, Green = 255, Blue = 255, Tolerance = 0 };
        DtfPrintPreparation.Knockout(bgra, 4, 4, null, bgra, 4, 3, 5, 5, options, CancellationToken.None);
        Check(bgra[12 * 4 + 3] == 0 && Enumerable.Range(0, 25).Where(p => p != 12).All(p => bgra[p * 4 + 3] == 255), "Enclosed colour not knocked out");
        Check(Enumerable.Range(0, bgra.Length).Where(i => i % 4 != 3).All(i => bgra[i] == colors[i]), "Knockout changed colour bytes");
        byte[] rgb = { 249,249,249, 248,255,255, 255,255,255 }; byte[] alpha = {128,255,0}; options.Tolerance = 6;
        DtfPrintPreparation.Knockout(rgb, 3, 4, null, alpha, 1, 0, 3, 1, options, CancellationToken.None);
        Check(alpha.SequenceEqual(new byte[] {0,255,0}), "Tolerance boundary or existing transparency changed");
    }
    private static void Models()
    {
        foreach (int mode in new[] {4,5,2})
        {
            byte[] color = mode == 4 ? new byte[] {255,255,255} : mode == 5 ? Cmyk(0,0,0,0) : new byte[] {255};
            var source = Image(mode, color, 128); var before = (byte[])source.Colors.Clone();
            var options = new NativeRasterFilterOptions { PrintPreparation = new DtfPrintPreparationOptions { Knockout = true, Red = 255, Green = 255, Blue = 255, Tolerance = 0 } };
            var result = NativeRasterFilters.Process(source, options).Buffer;
            Check(result.Mode == mode && result.Colors.SequenceEqual(before) && result.Alpha.All(a => a == 0), "Native knockout model/white changed");
            Check(source.Colors.SequenceEqual(before) && source.Alpha.All(a => a == 128), "Knockout edited source planes");
            options.PrintPreparation = new DtfPrintPreparationOptions { Halftone = true, AlphaOnly = false };
            result = NativeRasterFilters.Process(source, options).Buffer;
            Check(result.Mode == mode && result.Colors.SequenceEqual(before) && result.Alpha.All(a => a == 0 || a == 255), "Native halftone rewrote channels");
            Check(result.Width == source.Width && result.Height == source.Height && result.DpiX == 400 && result.DpiY == 300, "Physical size/DPI changed");
            if (mode == 5) Check(result.CmykOrder.SequenceEqual(Order), "CMYK order changed");
        }
    }
    private static void Modes()
    {
        var options = new DtfPrintPreparationOptions { Halftone = true, AlphaOnly = true, AngleDegrees = 22.5 };
        foreach (byte a in new byte[] {32,64,128,192,224})
        {
            byte[] alpha = Screen(Solid(512 * 512, a), 512, 512, 508, 508, options);
            Check(alpha.All(p => p == 0 || p == 255), "Halftone retained semi-transparent samples");
            Check(Math.Abs(Coverage(alpha) - a / 255d) < .015, "Alpha tone coverage drifted: " + a + ": " + Coverage(alpha));
        }
        Check(Screen(Solid(256,255),16,16,400,400,options).All(a => a == 255), "Alpha-only changed dense regions");
        Check(Screen(Solid(256,0),16,16,400,400,options).All(a => a == 0), "Halftone filled empty regions");
        options.AlphaOnly = false;
        var full = Screen(Solid(512 * 512,255),512,512,508,508,options);
        Check(Math.Abs(Coverage(full) - Math.PI * .375 * .375) < .015, "Full-image mode did not put a grid on dense colour");
        options.AngleDegrees = 0; var zero = Screen(Solid(512 * 512,255),512,512,508,508,options);
        Check(!zero.SequenceEqual(full), "Angle has no effect");
        options.AngleDegrees = 180;
        Check(zero.SequenceEqual(Screen(Solid(512 * 512,255),512,512,508,508,options)), "180 degree rotation changed round-grid phase");
    }
    private static void Millimeters()
    {
        var options = new DtfPrintPreparationOptions { Halftone = true, AlphaOnly = false, AutoDot = false,
            FrequencyLpi = 25.4, AngleDegrees = 0, DotDiameterMm = .4 };
        var data = Screen(Solid(300 * 150,255),300,150,2540,1270,options);
        int horizontal = Enumerable.Range(50,100).Count(x => data[50 * 300 + x] != 0);
        int vertical = Enumerable.Range(25,50).Count(y => data[y * 300 + 100] != 0);
        Check(Math.Abs(horizontal - 40) <= 1 && Math.Abs(vertical - 20) <= 1, "Dot diameter does not respect anisotropic DPI");
        double small = Coverage(data); options.DotDiameterMm = .8;
        Check(Coverage(Screen(Solid(300 * 150,255),300,150,2540,1270,options)) > small * 3, "Manual dot diameter has no effect");
    }
    private static void Phase()
    {
        const int w=450,h=280,x=73,y=41,cw=201,ch=127; const double dx=400,dy=300;
        var source = Enumerable.Range(0,w*h).Select(p => (byte)(p % 256)).ToArray();
        var options = new DtfPrintPreparationOptions { Halftone = true, AlphaOnly = false, AngleDegrees = -31.5 };
        var cropped = new byte[cw*ch];
        for(int row=0;row<ch;row++) Buffer.BlockCopy(source,(y+row)*w+x,cropped,row*cw,cw);
        foreach(DtfDotShape shape in Enum.GetValues(typeof(DtfDotShape))) {
            options.Shape=shape;var full=Screen(source,w,h,dx,dy,options);
            var tile = Screen(cropped,cw,ch,dx,dy,options.Offset(x*25.4/dx,y*25.4/dy));
            Check(Enumerable.Range(0,ch).All(row => tile.Skip(row*cw).Take(cw).SequenceEqual(full.Skip((y+row)*w+x).Take(cw))), "Grid phase jumps when preview is cropped: "+shape);
        }
    }
    private static void Combined()
    {
        var source = Image(4,new byte[] {30,80,120},128);
        var outline = new NativeRasterFilterOptions { Outline = true, RadiusMm = 0, ExpansionMm = .2, OutlineColor = new byte[] {255,255,255} };
        var prep = new DtfPrintPreparationOptions { Halftone = true, AlphaOnly = false, AngleDegrees = 33 };
        var screened=NativeRasterFilters.Process(source,new NativeRasterFilterOptions {PrintPreparation=prep});
        var expected=NativeRasterFilters.Process(screened.Buffer,outline);
        outline.PrintPreparation=prep; var actual=NativeRasterFilters.Process(source,outline);
        Check(actual.Buffer.Alpha.SequenceEqual(expected.Buffer.Alpha) && actual.Buffer.Colors.SequenceEqual(expected.Buffer.Colors), "Outline is not applied to screened dots");
        Check(actual.OffsetMm.Equals(expected.OffsetMm),"Screen/outline placement differs");
        int rim=Enumerable.Range(0,actual.Buffer.Alpha.Length).Count(p=>actual.Buffer.Alpha[p]>0 && actual.Buffer.Colors[p*3]==255 && actual.Buffer.Colors[p*3+1]==255 && actual.Buffer.Colors[p*3+2]==255);
        Check(rim>0,"Screened dots have no selected-colour rim");
        Check(source.Alpha.All(a=>a==128), "Combined processing changed source alpha");
        // Interpolation and trim happen before screening. Physical phase must
        // survive both that crop and the final dot-outline expansion.
        var smoothed=NativeRasterFilters.Process(source,new NativeRasterFilterOptions {SmoothStairs=true,StairsScale=2});
        var stagePrep=prep.Offset(-smoothed.OffsetMm.Item1,-smoothed.OffsetMm.Item2);
        var dots=NativeRasterFilters.Process(smoothed.Buffer,new NativeRasterFilterOptions {PrintPreparation=stagePrep});
        var expectedOutline=NativeRasterFilters.Process(dots.Buffer,new NativeRasterFilterOptions {Outline=true,RadiusMm=0,ExpansionMm=.2,OutlineColor=new byte[]{255,255,255}});
        var all=NativeRasterFilters.Process(source,new NativeRasterFilterOptions {SmoothStairs=true,StairsScale=2,Outline=true,RadiusMm=0,ExpansionMm=.2,OutlineColor=new byte[]{255,255,255},PrintPreparation=prep});
        Check(all.Buffer.Alpha.SequenceEqual(expectedOutline.Buffer.Alpha) && all.Buffer.Colors.SequenceEqual(expectedOutline.Buffer.Colors),"Smoothing/trim changed screen or dot outline");
        Check(Math.Abs(all.OffsetMm.Item1-smoothed.OffsetMm.Item1-expectedOutline.OffsetMm.Item1)<1e-10 && Math.Abs(all.OffsetMm.Item2-smoothed.OffsetMm.Item2-expectedOutline.OffsetMm.Item2)<1e-10,"Combined offset wrong after smoothing");
    }
    private static void Validation()
    {
        foreach (double frequency in new[] {0d,Double.NaN,201d})
            Reject<ArgumentException>(() => Screen(Solid(16,128),4,4,400,400,new DtfPrintPreparationOptions { Halftone=true,FrequencyLpi=frequency }));
        Reject<ArgumentException>(() => Screen(Solid(16,128),4,4,400,400,new DtfPrintPreparationOptions { Halftone=true,AngleDegrees=Double.PositiveInfinity }));
        Reject<ArgumentException>(() => Screen(Solid(16,128),4,4,400,400,new DtfPrintPreparationOptions { Halftone=true,AutoDot=false,DotDiameterMm=0 }));
        Reject<ArgumentException>(() => Screen(Solid(16,128),4,4,400,400,new DtfPrintPreparationOptions { Halftone=true,Shape=(DtfDotShape)99 }));
        Reject<ArgumentException>(() => Screen(Solid(16,128),4,4,400,400,new DtfPrintPreparationOptions { Halftone=true,ColorBoost=101 }));
        var alpha=Solid(16,128); var before=(byte[])alpha.Clone(); var cancel=new CancellationToken(true);
        Reject<OperationCanceledException>(()=>DtfPrintPreparation.Halftone(alpha,1,0,4,4,400,400,new DtfPrintPreparationOptions { Halftone=true },cancel));
        Check(alpha.SequenceEqual(before),"Cancelled job changed alpha before starting");
        Reject<ArgumentException>(()=>DtfPrintPreparation.Halftone(new byte[1],1,0,4,4,400,400,new DtfPrintPreparationOptions { Halftone=true },CancellationToken.None));
        Reject<InvalidOperationException>(()=>DtfPrintPreparation.Knockout(new byte[16*4],4,5,new[]{0,0,2,3},alpha,1,0,4,4,
            new DtfPrintPreparationOptions { Knockout=true },CancellationToken.None));
    }
}
