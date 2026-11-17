using System;
using System.Collections.Generic;
using System.Linq;

namespace VanyaTools.Native
{
    internal static class PatternMotifRotation
    {
        internal static PatternPlacement Copy(PatternPlacement item) => new PatternPlacement {
            Motif=item.Motif, Size=item.Size, Angle=item.Angle, Center=item.Center, Cell=item.Cell };

        internal static PatternPlacement Fit(PatternPlacement original, PatternFootprint motif, PatternFillOptions options, double angle)
        {
            if(original.Cell==null || original.Cell.Length<3 || Double.IsNaN(angle) || Double.IsInfinity(angle))return null;
            var fit=PatternFillLayout.PlaceInCells(new List<PatternPoint[]>{original.Cell},new[]{motif},
                new PatternFillOptions{Mode=options.Mode,SizeMm=options.SizeMm,Angle=angle,Seed=options.Seed},fixedAngle:true).SingleOrDefault();
            if(fit==null)return null;fit.Motif=original.Motif;
            return Resize(fit,motif,Math.Min(PatternTransform.Fit(motif.Size,original).Scale,PatternTransform.Fit(motif.Size,fit).Scale));
        }

        internal static PatternPlacement Resize(PatternPlacement item,PatternFootprint motif,double scale)
        {
            var center=InkCenter(item,motif);double radians=item.Angle*Math.PI/180,c=Math.Cos(radians),s=Math.Sin(radians);
            var rotated=PatternTransform.RotatedSize(motif.Size.X,motif.Size.Y,item.Angle);
            return new PatternPlacement{Motif=item.Motif,Cell=item.Cell,Angle=item.Angle,Size=scale*Math.Max(rotated.X,rotated.Y),
                Center=new PatternPoint(center.X-scale*(motif.Center.X*c-motif.Center.Y*s),center.Y-scale*(motif.Center.X*s+motif.Center.Y*c))};
        }

        internal static PatternPoint InkCenter(PatternPlacement item,PatternFootprint motif)
        {
            double scale=PatternTransform.Fit(motif.Size,item).Scale,radians=item.Angle*Math.PI/180,c=Math.Cos(radians),s=Math.Sin(radians);
            return new PatternPoint(item.Center.X+scale*(motif.Center.X*c-motif.Center.Y*s),item.Center.Y+scale*(motif.Center.X*s+motif.Center.Y*c));
        }

        internal static bool Contains(PatternPoint[] polygon,PatternPoint point)
        {
            bool inside=false;
            for(int i=0,j=polygon.Length-1;i<polygon.Length;j=i++)
            {
                var a=polygon[i];var b=polygon[j];
                if((a.Y>point.Y)!=(b.Y>point.Y) && point.X<(b.X-a.X)*(point.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
            }
            return inside;
        }

        internal static bool Separated(PatternPoint[] a,PatternPoint[] b,double gap)
        {
            double leftA=a.Min(p=>p.X),rightA=a.Max(p=>p.X),bottomA=a.Min(p=>p.Y),topA=a.Max(p=>p.Y),
                leftB=b.Min(p=>p.X),rightB=b.Max(p=>p.X),bottomB=b.Min(p=>p.Y),topB=b.Max(p=>p.Y);
            if(leftA>=rightB+gap || leftB>=rightA+gap || bottomA>=topB+gap || bottomB>=topA+gap)return true;
            // Convex hulls overlap unless at least one edge separates them.
            bool separated=false;
            foreach(var hull in new[]{a,b})for(int i=0;i<hull.Length;i++)
            {
                var p=hull[i];var q=hull[(i+1)%hull.Length];double nx=q.Y-p.Y,ny=p.X-q.X;
                double minA=Double.PositiveInfinity,maxA=Double.NegativeInfinity,minB=minA,maxB=maxA;
                foreach(var v in a){double t=v.X*nx+v.Y*ny;minA=Math.Min(minA,t);maxA=Math.Max(maxA,t);}
                foreach(var v in b){double t=v.X*nx+v.Y*ny;minB=Math.Min(minB,t);maxB=Math.Max(maxB,t);}
                if(maxA<=minB+1e-9 || maxB<=minA+1e-9)separated=true;
                if(maxA+gap*Math.Sqrt(nx*nx+ny*ny)<=minB+1e-9 || maxB+gap*Math.Sqrt(nx*nx+ny*ny)<=minA+1e-9)return true;
            }
            if(!separated)return false;
            double square=Math.Max(0,gap-1e-7)*Math.Max(0,gap-1e-7);
            foreach(var pair in new[]{Tuple.Create(a,b),Tuple.Create(b,a)})foreach(var p in pair.Item1)
                for(int i=0;i<pair.Item2.Length;i++)if(DistanceSquared(p,pair.Item2[i],pair.Item2[(i+1)%pair.Item2.Length])<square)return false;
            return true;
        }

        private static double DistanceSquared(PatternPoint p,PatternPoint a,PatternPoint b)
        {
            double dx=b.X-a.X,dy=b.Y-a.Y,length=dx*dx+dy*dy,t=length>0?Math.Max(0,Math.Min(1,((p.X-a.X)*dx+(p.Y-a.Y)*dy)/length)):0;
            dx=p.X-a.X-t*dx;dy=p.Y-a.Y-t*dy;return dx*dx+dy*dy;
        }
    }
}
