using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class LetterTransferContour
    {
        private sealed class ScanEdge{internal int End;internal double X,Step;}
        internal static BitmapSource Refine(BitmapSource source,double tolerance,double scale,CancellationToken cancel)
        {
            if(Double.IsNaN(tolerance) || tolerance<.25 || tolerance>3 || Double.IsNaN(scale) || Double.IsInfinity(scale) || scale<1)throw new ArgumentOutOfRangeException();
            int w=source.PixelWidth,h=source.PixelHeight;var input=LetterTransferImages.Pixels(source);var mask=new bool[w*h];
            for(int i=0;i<mask.Length;i++)mask[i]=input[i*4+3]>=130;
            scale=Math.Min(scale,Math.Min(9999d/w,Math.Min(9999d/h,Math.Sqrt(24900000d/(w*(double)h)))));scale=Math.Max(1,scale);
            int width=(int)Math.Ceiling(w*scale),height=(int)Math.Ceiling(h*scale);
            var geometry=new StreamGeometry{FillRule=FillRule.EvenOdd};int loops=0;
            using(var path=geometry.Open())
            {
                foreach(var ring in AlphaMaskVectorizer.TraceOutline(mask,w,h,0))
                {
                    cancel.ThrowIfCancellationRequested();if(ring.Length<3)continue;loops++;
                    var points=Smooth(ring.Select(p=>new Point(p.X,p.Y)).ToArray(),tolerance);var entry=new Point[points.Length];var exit=new Point[points.Length];
                    for(int i=0;i<points.Length;i++)
                    {
                        var previous=points[(i+points.Length-1)%points.Length];var current=points[i];var next=points[(i+1)%points.Length];
                        Vector incoming=current-previous,outgoing=next-current;
                        // Sharp serif/counter corners remain corners. Gently
                        // turning sections are joined by short quadratic arcs.
                        bool curved=incoming.Length>0 && outgoing.Length>0 && Vector.Multiply(incoming,outgoing)/(incoming.Length*outgoing.Length)>.81;
                        double fraction=curved?.35:0;entry[i]=current-incoming*fraction;exit[i]=current+outgoing*fraction;
                    }
                    path.BeginFigure(exit[0],true,true);
                    for(int n=1;n<=points.Length;n++){int i=n%points.Length;path.LineTo(entry[i],true,false);if(entry[i]!=exit[i])path.QuadraticBezierTo(points[i],exit[i],true,false);}
                }
            }
            if(loops==0)return source;geometry.Freeze();
            var flat=geometry.GetFlattenedPathGeometry(.15/scale,ToleranceType.Absolute);var buckets=new List<ScanEdge>[height];
            void Edge(Point a,Point b)
            {
                a=new Point(a.X*scale,a.Y*scale);b=new Point(b.X*scale,b.Y*scale);if(Math.Abs(a.Y-b.Y)<1e-9)return;
                if(a.Y>b.Y){var temporary=a;a=b;b=temporary;}
                int first=Math.Max(0,(int)Math.Ceiling(a.Y-.5)),last=Math.Min(height-1,(int)Math.Ceiling(b.Y-.5)-1);if(first>last)return;
                double step=(b.X-a.X)/(b.Y-a.Y);var edge=new ScanEdge{End=last,Step=step,X=a.X+(first+.5-a.Y)*step};
                if(buckets[first]==null)buckets[first]=new List<ScanEdge>();buckets[first].Add(edge);
            }
            foreach(var figure in flat.Figures)
            {
                var first=figure.StartPoint;var previous=first;
                foreach(var segment in figure.Segments)
                {
                    if(segment is PolyLineSegment line)foreach(var point in line.Points){Edge(previous,point);previous=point;}
                    else if(segment is LineSegment single){Edge(previous,single.Point);previous=single.Point;}
                }
                Edge(previous,first);
            }
            var output=new byte[checked(width*height*4)];var active=new List<ScanEdge>();
            for(int y=0;y<height;y++)
            {
                cancel.ThrowIfCancellationRequested();active.RemoveAll(e=>e.End<y);if(buckets[y]!=null)active.AddRange(buckets[y]);active.Sort((a,b)=>a.X.CompareTo(b.X));
                for(int e=0;e+1<active.Count;e+=2)
                {
                    int left=Math.Max(0,(int)Math.Ceiling(active[e].X-.5)),right=Math.Min(width,(int)Math.Ceiling(active[e+1].X-.5));
                    for(int x=left;x<right;x++)Sample(input,w,h,(x+.5)/scale-.5,(y+.5)/scale-.5,output,(y*width+x)*4);
                }
                foreach(var edge in active)edge.X+=edge.Step;
            }
            var result=BitmapSource.Create(width,height,source.DpiX*scale,source.DpiY*scale,PixelFormats.Bgra32,null,output,width*4);result.Freeze();
            return LetterTransferImages.Prepare(result,130,cancel)??source;
        }
        private static Point[] Smooth(Point[] polygon,double strength)
        {
            if(polygon.Max(p=>p.X)-polygon.Min(p=>p.X)<=3 || polygon.Max(p=>p.Y)-polygon.Min(p=>p.Y)<=3)return polygon;
            var sample=new List<Point>();
            for(int i=0;i<polygon.Length;i++)
            {
                Point a=polygon[i],b=polygon[(i+1)%polygon.Length];int steps=Math.Max(1,(int)Math.Ceiling((b-a).Length));
                for(int n=0;n<steps;n++)sample.Add(a+(b-a)*(n/(double)steps));
            }
            int count=sample.Count;if(count<12)return polygon;
            int span=Math.Min(count/6,Math.Max(4,(int)Math.Ceiling(strength*3))),window=Math.Max(2,(int)Math.Ceiling(strength*2));
            var scores=new double[count];var corners=new bool[count];
            for(int i=0;i<count;i++)
            {
                Vector a=sample[i]-sample[(i-span+count)%count],b=sample[(i+span)%count]-sample[i];
                if(a.Length<span*.75 || b.Length<span*.75)continue;
                scores[i]=Math.Acos(Math.Max(-1,Math.Min(1,Vector.Multiply(a,b)/(a.Length*b.Length))));
            }
            // Locate stable corners over several pixels. One-pixel hairs do
            // not have two supported sides and therefore cannot pin the fit.
            for(int i=0;i<count;i++)
            {
                if(scores[i]<.9)continue;bool peak=true;
                for(int d=-window;d<=window;d++){int j=(i+d+count)%count;if(scores[j]>scores[i]+1e-8 || d<0 && Math.Abs(scores[j]-scores[i])<=1e-8){peak=false;break;}}
                corners[i]=peak;
            }
            var points=new Point[count];
            for(int i=0;i<count;i++)
            {
                if(corners[i]){points[i]=sample[i];continue;}double x=sample[i].X,y=sample[i].Y,total=1;
                for(int sign=-1;sign<=1;sign+=2)for(int d=1;d<=window;d++)
                {
                    int j=(i+sign*d+count)%count;double weight=Math.Exp(-.5*d*d/(strength*strength));x+=sample[j].X*weight;y+=sample[j].Y*weight;total+=weight;if(corners[j])break;
                }
                points[i]=new Point(x/total,y/total);
            }
            var anchors=Enumerable.Range(0,count).Where(i=>corners[i]).ToArray();
            if(anchors.Length<2)anchors=new[]{0,Enumerable.Range(1,count-1).OrderByDescending(i=>(points[i]-points[0]).LengthSquared).First()}.OrderBy(i=>i).ToArray();
            var result=new List<Point>();
            for(int n=0;n<anchors.Length;n++)
            {
                int first=anchors[n],last=anchors[(n+1)%anchors.Length];var part=new List<Point>{points[first]};
                for(int i=(first+1)%count;i!=last;i=(i+1)%count)part.Add(points[i]);part.Add(points[last]);
                var simplified=Simplify(part,Math.Max(.4,strength*.9));result.AddRange(simplified.Take(simplified.Count-1));
            }
            return result.Count>=3?result.ToArray():polygon;
        }
        private static List<Point> Simplify(List<Point> points,double tolerance)
        {
            var keep=new bool[points.Count];keep[0]=keep[points.Count-1]=true;var pending=new Stack<Tuple<int,int>>();pending.Push(Tuple.Create(0,points.Count-1));
            while(pending.Count>0)
            {
                var part=pending.Pop();Point a=points[part.Item1],b=points[part.Item2];Vector direction=b-a;double length=direction.LengthSquared,maximum=tolerance*tolerance;int furthest=-1;
                for(int i=part.Item1+1;i<part.Item2;i++)
                {
                    double t=length>0?Math.Max(0,Math.Min(1,Vector.Multiply(points[i]-a,direction)/length)):0;double distance=(points[i]-(a+direction*t)).LengthSquared;
                    if(distance>maximum){maximum=distance;furthest=i;}
                }
                if(furthest<0)continue;keep[furthest]=true;pending.Push(Tuple.Create(part.Item1,furthest));pending.Push(Tuple.Create(furthest,part.Item2));
            }
            return points.Where((point,i)=>keep[i]).ToList();
        }
        private static void Sample(byte[] input,int width,int height,double x,double y,byte[] output,int at)
        {
            int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);double fx=x-ix,fy=y-iy,b=0,g=0,r=0,total=0;
            for(int dy=0;dy<=1;dy++)for(int dx=0;dx<=1;dx++)
            {
                int nx=ix+dx,ny=iy+dy;if(nx<0 || ny<0 || nx>=width || ny>=height)continue;int p=(ny*width+nx)*4;if(input[p+3]==0)continue;
                double weight=(dx==0?1-fx:fx)*(dy==0?1-fy:fy);b+=input[p]*weight;g+=input[p+1]*weight;r+=input[p+2]*weight;total+=weight;
            }
            if(total>1e-8){output[at]=(byte)Math.Round(b/total);output[at+1]=(byte)Math.Round(g/total);output[at+2]=(byte)Math.Round(r/total);output[at+3]=255;return;}
            int nearest=-1;double best=Double.MaxValue;
            for(int dy=-5;dy<=5;dy++)for(int dx=-5;dx<=5;dx++)
            {
                int nx=ix+dx,ny=iy+dy;if(nx<0 || ny<0 || nx>=width || ny>=height)continue;int p=(ny*width+nx)*4;if(input[p+3]==0)continue;
                double distance=(nx-x)*(nx-x)+(ny-y)*(ny-y);if(distance>=best)continue;best=distance;nearest=p;
            }
            if(nearest<0)return;output[at]=input[nearest];output[at+1]=input[nearest+1];output[at+2]=input[nearest+2];output[at+3]=255;
        }
    }
}
