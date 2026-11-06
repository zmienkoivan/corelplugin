using System;
using System.Collections.Generic;
using System.Threading;

namespace VanyaTools.Native
{
    // 8-connected islands, measured by maximum physical bounding-box extent.
    // Two bitsets and a scanline frontier replace a full-image int label/queue.
    // Pixel coordinates are retained only while an island can still be small.
    internal static class DtfIslandCleanup
    {
        private struct Span { internal int Left,Right,Y; internal Span(int l,int r,int y){Left=l;Right=r;Y=y;} }
        internal static NativeRasterFilterResult Process(NativeRasterBuffer source,DtfPrintPreparationOptions options,
            byte[] fill,int maximumPixels,CancellationToken cancel)
        {
            options.Validate();cancel.ThrowIfCancellationRequested();
            int w=source.Width,h=source.Height;double dx=25.4/source.DpiX,dy=25.4/source.DpiY;
            double maximum=Math.Max(options.RemoveSmall?options.RemoveBelowMm:0,options.GrowSmall?options.GrowToMm:0);
            if(maximum/dx>1024 || maximum/dy>1024)
                throw new InvalidOperationException("Порог мелких элементов слишком велик для этого DPI. Уменьшите порог или разрешение.");
            if(fill!=null && fill.Length!=source.Channels)throw new ArgumentException("Цвет обводки не соответствует модели растра.");
            if(fill==null) {fill=new byte[source.Channels];if(source.Mode!=5)for(int c=0;c<fill.Length;c++)fill[c]=255;}
            if(options.RemoveSmall) Visit(source.Alpha,w,h,dx,dy,options.RemoveBelowMm,options.CutEdges,cancel,
                (points,l,r,t,b)=>{foreach(int p in points)source.Alpha[p]=0;});
            NativeRasterBuffer output=source;int padX=0,padY=0;
            if(options.GrowSmall) Visit(source.Alpha,w,h,dx,dy,options.GrowToMm,options.CutEdges,cancel,(points,l,r,t,b)=> {
                if(ReferenceEquals(output,source)) {
                    padX=(int)Math.Ceiling(options.GrowToMm/(2*dx));padY=(int)Math.Ceiling(options.GrowToMm/(2*dy));
                    long ow=(long)w+2*padX,oh=(long)h+2*padY;
                    if(ow*oh>maximumPixels || ow*oh*source.Channels>Int32.MaxValue)
                        throw new InvalidOperationException("Результат увеличения элементов превышает допустимый размер растра.");
                    int nw=(int)ow,nh=(int)oh;var colors=new byte[checked(nw*nh*source.Channels)];var alpha=new byte[checked(nw*nh)];
                    for(int y=0;y<h;y++) {
                        if((y&63)==0)cancel.ThrowIfCancellationRequested();
                        Buffer.BlockCopy(source.Colors,y*w*source.Channels,colors,((y+padY)*nw+padX)*source.Channels,w*source.Channels);
                        Buffer.BlockCopy(source.Alpha,y*w,alpha,(y+padY)*nw+padX,w);
                    }
                    output=new NativeRasterBuffer(nw,nh,source.Mode,source.DpiX,source.DpiY,colors,alpha,source.CmykOrder);
                }
                int rx=(int)Math.Ceiling(Math.Max(0,options.GrowToMm-(r-l+1)*dx)/(2*dx)-1e-9);
                int ry=(int)Math.Ceiling(Math.Max(0,options.GrowToMm-(b-t+1)*dy)/(2*dy)-1e-9);
                foreach(int p in points) {
                    if((p&4095)==0)cancel.ThrowIfCancellationRequested();
                    int px=p%w+padX,py=p/w+padY;
                    for(int y=py-ry;y<=py+ry;y++)for(int x=px-rx;x<=px+rx;x++) {
                        int at=y*output.Width+x;if(output.Alpha[at]!=0)continue;
                        output.Alpha[at]=255;for(int c=0;c<output.Channels;c++)output.Colors[at*output.Channels+c]=fill[c];
                    }
                }
            });
            return new NativeRasterFilterResult {Buffer=output,OffsetMm=Tuple.Create(padX*dx,padY*dy)};
        }

        private static void Visit(byte[] alpha,int w,int h,double dx,double dy,double threshold,int cutEdges,
            CancellationToken cancel,Action<List<int>,int,int,int,int> small)
        {
            int n=alpha.Length;var ink=new uint[(n+31)/32];var seen=new uint[ink.Length];
            for(int i=0;i<n;i++){if((i&65535)==0)cancel.ThrowIfCancellationRequested();if(alpha[i]!=0)ink[i>>5]|=1u<<(i&31);}
            var frontier=new Queue<Span>();var points=new List<int>();
            for(int start=0;start<n;start++) {
                if((start&16383)==0)cancel.ThrowIfCancellationRequested();
                if(!Set(ink,start)||Set(seen,start))continue;
                int left=w,right=-1,top=h,bottom=-1;bool candidate=true;points.Clear();
                Span Add(int x,int y) {
                    int l=x,r=x,row=y*w;
                    while(l>0 && Set(ink,row+l-1) && !Set(seen,row+l-1))l--;
                    while(r+1<w && Set(ink,row+r+1) && !Set(seen,row+r+1))r++;
                    left=Math.Min(left,l);right=Math.Max(right,r);top=Math.Min(top,y);bottom=Math.Max(bottom,y);
                    if(candidate && (Math.Max((right-left+1)*dx,(bottom-top+1)*dy)>=threshold-1e-9 ||
                        ((cutEdges&1)!=0 && left==0) || ((cutEdges&2)!=0 && right==w-1) || ((cutEdges&4)!=0 && top==0) || ((cutEdges&8)!=0 && bottom==h-1)))
                    {candidate=false;points.Clear();}
                    for(int i=row+l;i<=row+r;i++){seen[i>>5]|=1u<<(i&31);if(candidate)points.Add(i);if((i&65535)==0)cancel.ThrowIfCancellationRequested();}
                    var span=new Span(l,r,y);frontier.Enqueue(span);return span;
                }
                Add(start%w,start/w);
                while(frontier.Count>0) {
                    cancel.ThrowIfCancellationRequested();var span=frontier.Dequeue();
                    for(int side=-1;side<=1;side+=2) {
                        int y=span.Y+side;if(y<0||y>=h)continue;
                        for(int x=Math.Max(0,span.Left-1);x<=Math.Min(w-1,span.Right+1);x++) {
                            int p=y*w+x;if(Set(ink,p)&&!Set(seen,p))x=Add(x,y).Right;
                        }
                    }
                }
                if(candidate)small(points,left,right,top,bottom);
            }
        }
        private static bool Set(uint[] bits,int at)=>(bits[at>>5]&(1u<<(at&31)))!=0;
    }
}
