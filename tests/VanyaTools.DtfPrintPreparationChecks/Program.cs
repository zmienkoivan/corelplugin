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
        try { Knockout(); Models(); Modes(); Millimeters(); Phase(); Combined(); Validation();
            Console.WriteLine("DTF print preparation checks passed: " + _checks + ". Synthetic planes only; no Corel or customer images."); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
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
        var full = Screen(source,w,h,dx,dy,options); var cropped = new byte[cw*ch];
        for(int row=0;row<ch;row++) Buffer.BlockCopy(source,(y+row)*w+x,cropped,row*cw,cw);
        var tile = Screen(cropped,cw,ch,dx,dy,options.Offset(x*25.4/dx,y*25.4/dy));
        Check(Enumerable.Range(0,ch).All(row => tile.Skip(row*cw).Take(cw).SequenceEqual(full.Skip((y+row)*w+x).Take(cw))), "Grid phase jumps when preview is cropped");
    }
    private static void Combined()
    {
        var source = Image(4,new byte[] {30,80,120},128);
        var outline = new NativeRasterFilterOptions { Outline = true, RadiusMm = 0, ExpansionMm = .2, OutlineColor = new byte[] {255,255,255} };
        var baseline = NativeRasterFilters.Process(source,outline);
        var prep = new DtfPrintPreparationOptions { Halftone = true, AlphaOnly = false, AngleDegrees = 33 };
        byte[] expected = Screen(baseline.Buffer.Alpha,baseline.Buffer.Width,baseline.Buffer.Height,baseline.Buffer.DpiX,baseline.Buffer.DpiY,
            prep.Offset(-baseline.OffsetMm.Item1,-baseline.OffsetMm.Item2));
        outline.PrintPreparation=prep; var actual=NativeRasterFilters.Process(source,outline);
        Check(actual.Buffer.Alpha.SequenceEqual(expected) && actual.Buffer.Colors.SequenceEqual(baseline.Buffer.Colors), "Grid phase changed after outline padding/trim");
        Check(source.Alpha.All(a=>a==128), "Combined processing changed source alpha");
    }
    private static void Validation()
    {
        foreach (double frequency in new[] {0d,Double.NaN,201d})
            Reject<ArgumentException>(() => Screen(Solid(16,128),4,4,400,400,new DtfPrintPreparationOptions { Halftone=true,FrequencyLpi=frequency }));
        Reject<ArgumentException>(() => Screen(Solid(16,128),4,4,400,400,new DtfPrintPreparationOptions { Halftone=true,AngleDegrees=Double.PositiveInfinity }));
        Reject<ArgumentException>(() => Screen(Solid(16,128),4,4,400,400,new DtfPrintPreparationOptions { Halftone=true,AutoDot=false,DotDiameterMm=0 }));
        var alpha=Solid(16,128); var before=(byte[])alpha.Clone(); var cancel=new CancellationToken(true);
        Reject<OperationCanceledException>(()=>DtfPrintPreparation.Halftone(alpha,1,0,4,4,400,400,new DtfPrintPreparationOptions { Halftone=true },cancel));
        Check(alpha.SequenceEqual(before),"Cancelled job changed alpha before starting");
        Reject<ArgumentException>(()=>DtfPrintPreparation.Halftone(new byte[1],1,0,4,4,400,400,new DtfPrintPreparationOptions { Halftone=true },CancellationToken.None));
        Reject<InvalidOperationException>(()=>DtfPrintPreparation.Knockout(new byte[16*4],4,5,new[]{0,0,2,3},alpha,1,0,4,4,
            new DtfPrintPreparationOptions { Knockout=true },CancellationToken.None));
    }
}
