using System;
using System.Threading;

namespace VanyaTools.Native
{
    internal sealed class DtfResamplePlan
    {
        internal int Width, Height;
        internal double DpiX, DpiY;
        internal bool Needed;
        internal static DtfResamplePlan Create(int width, int height, double dpiX, double dpiY, int maximumPixels = 70000000)
        {
            if(width<=0 || height<=0 || !Finite(dpiX) || !Finite(dpiY) || dpiX<=0 || dpiY<=0 || maximumPixels<=0)
                throw new ArgumentException("Некорректное разрешение исходника.");
            // PNG pixels/metre metadata slightly perturbs integer DPI. Avoid an
            // extra row/column solely due to that rounding; retain physical mm.
            double refX=Math.Abs(dpiX-Math.Round(dpiX))<.001 ? Math.Round(dpiX) : dpiX;
            double refY=Math.Abs(dpiY-Math.Round(dpiY))<.001 ? Math.Round(dpiY) : dpiY;
            double w = dpiX<299.99 ? Math.Ceiling(width*300d/refX) : width;
            double h = dpiY<299.99 ? Math.Ceiling(height*300d/refY) : height;
            if(!Finite(w) || !Finite(h) || w>Int32.MaxValue || h>Int32.MaxValue || w*h>maximumPixels)
                throw new InvalidOperationException("При 300 DPI получится слишком большой растр (лимит "+(maximumPixels/1000000)+" Мп). Уменьшите область обработки.");
            return new DtfResamplePlan { Width=(int)w,Height=(int)h,DpiX=dpiX*w/width,DpiY=dpiY*h/height,Needed=w!=width || h!=height };
        }
        private static bool Finite(double v) => !Double.IsNaN(v) && !Double.IsInfinity(v);
    }

    internal static class DtfRasterResample
    {
        // Premultiplied-alpha bilinear interpolation, written as straight samples.
        // Separate native planes and interleaved BGRA use exactly the same math.
        internal static void Run(byte[] colors,int channels,int stride,byte[] alpha,int alphaStride,int alphaOffset,
            int width,int height,byte[] target,int targetStride,byte[] targetAlpha,int targetAlphaStride,int targetAlphaOffset,
            DtfResamplePlan plan,CancellationToken cancel)
        {
            if(plan==null || plan.Width<=0 || plan.Height<=0 || width<=0 || height<=0 || channels<1 || channels>4 || stride<channels || targetStride<channels ||
                colors==null || colors.LongLength<(long)width*height*stride ||
                alphaStride<1 || alphaOffset<0 || alphaOffset>=alphaStride || alpha==null || alpha.LongLength<(long)width*height*alphaStride ||
                target==null || target.LongLength<(long)plan.Width*plan.Height*targetStride ||
                targetAlphaStride<1 || targetAlphaOffset<0 || targetAlphaOffset>=targetAlphaStride || targetAlpha==null || targetAlpha.LongLength<(long)plan.Width*plan.Height*targetAlphaStride)
                throw new ArgumentException("Неполные данные для ресемпла.");
            cancel.ThrowIfCancellationRequested();
            double scaleX=width/(double)plan.Width,scaleY=height/(double)plan.Height;
            var x0s=new int[plan.Width];var x1s=new int[plan.Width];var fractions=new double[plan.Width];
            for(int x=0;x<plan.Width;x++) { double sx=Math.Max(0,Math.Min(width-1,(x+.5)*scaleX-.5));x0s[x]=(int)sx;x1s[x]=Math.Min(width-1,x0s[x]+1);fractions[x]=sx-x0s[x]; }
            for(int y=0;y<plan.Height;y++) {
                if((y&15)==0)cancel.ThrowIfCancellationRequested();
                double sy=Math.Max(0,Math.Min(height-1,(y+.5)*scaleY-.5));int y0=(int)sy,y1=Math.Min(height-1,y0+1);double fy=sy-y0;
                for(int x=0;x<plan.Width;x++) {
                    if((x&8191)==0)cancel.ThrowIfCancellationRequested();
                    double fx=fractions[x];int p00=y0*width+x0s[x],p10=y0*width+x1s[x],p01=y1*width+x0s[x],p11=y1*width+x1s[x];
                    double w00=(1-fx)*(1-fy)*alpha[p00*alphaStride+alphaOffset],w10=fx*(1-fy)*alpha[p10*alphaStride+alphaOffset],
                        w01=(1-fx)*fy*alpha[p01*alphaStride+alphaOffset],w11=fx*fy*alpha[p11*alphaStride+alphaOffset],sum=w00+w10+w01+w11;
                    int output=y*plan.Width+x;targetAlpha[output*targetAlphaStride+targetAlphaOffset]=Byte(sum);
                    for(int c=0;c<channels;c++) target[output*targetStride+c]=sum>0 ? Byte((colors[p00*stride+c]*w00+colors[p10*stride+c]*w10+colors[p01*stride+c]*w01+colors[p11*stride+c]*w11)/sum) : colors[p00*stride+c];
                }
            }
        }
        private static byte Byte(double v) => (byte)Math.Max(0,Math.Min(255,(int)Math.Round(v)));
    }
}
