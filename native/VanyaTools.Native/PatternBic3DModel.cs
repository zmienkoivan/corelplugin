using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    // An illustrative lighter profile, with its body perimeter tied to the
    // actual print template. The metal head is outside the printable height.
    internal static class PatternBic3DModel
    {
        internal static Point Profile(double width,double depth,double u)
        {
            double r=depth/2,l=Math.Max(0,width-depth),c=2*l+2*Math.PI*r,s=u*c;
            if(s<=l/2)return new Point(s,-r);
            s-=l/2;if(s<=Math.PI*r){double a=-Math.PI/2+s/r;return new Point(l/2+r*Math.Cos(a),r*Math.Sin(a));}
            s-=Math.PI*r;if(s<=l)return new Point(l/2-s,r);
            s-=l;if(s<=Math.PI*r){double a=Math.PI/2+s/r;return new Point(-l/2+r*Math.Cos(a),r*Math.Sin(a));}
            return new Point(-l/2+s-Math.PI*r,-r);
        }
        internal static double BodyWidth(double diameter)=>diameter*.6+Math.PI*diameter*.2;

        internal static MeshGeometry3D Tube(double width,double depth,double bottom,double top,double x=0,int segments=128,bool cap=false)
        {
            var mesh=new MeshGeometry3D();
            for(int j=0;j<2;j++)for(int i=0;i<=segments;i++)
            {double u=(double)i/segments;var p=Profile(width,depth,u);mesh.Positions.Add(new Point3D(p.X+x,j==0?bottom:top,p.Y));mesh.TextureCoordinates.Add(new Point(u,j==0?1:0));}
            for(int i=0;i<segments;i++){int a=i,b=i+1,c=i+segments+1,d=c+1;Tri(mesh,a,c,b);Tri(mesh,b,c,d);}
            if(cap)for(int side=0;side<2;side++)
            {
                int center=mesh.Positions.Count;mesh.Positions.Add(new Point3D(x,side==0?bottom:top,0));mesh.TextureCoordinates.Add(new Point(.5,.5));
                for(int i=0;i<segments;i++)if(side==0)Tri(mesh,center,i,i+1);else Tri(mesh,center,segments+2+i,segments+1+i);
            }
            mesh.Freeze();return mesh;
        }
        internal static MeshGeometry3D Ellipsoid(Point3D center,double x,double y,double z,int rings=12,int segments=40)
        {
            var mesh=new MeshGeometry3D();
            for(int j=0;j<=rings;j++)for(int i=0;i<=segments;i++)
            {
                double v=(double)j/rings*Math.PI,u=(double)i/segments*2*Math.PI;
                mesh.Positions.Add(center+new Vector3D(x*Math.Sin(v)*Math.Cos(u),y*Math.Cos(v),z*Math.Sin(v)*Math.Sin(u)));
            }
            for(int j=0;j<rings;j++)for(int i=0;i<segments;i++)
            {int a=j*(segments+1)+i,b=a+1,c=a+segments+1;Tri(mesh,a,b,c);Tri(mesh,b,c+1,c);}
            mesh.Freeze();return mesh;
        }
        internal static MeshGeometry3D Wheel(Point3D center,double radius,double depth)
        {
            var mesh=new MeshGeometry3D();const int n=96;
            for(int j=0;j<2;j++)for(int i=0;i<=n;i++)
            {double a=i*2*Math.PI/n,r=radius*(i%2==0?1:.90);mesh.Positions.Add(center+new Vector3D(r*Math.Cos(a),r*Math.Sin(a),(j-.5)*depth));}
            for(int i=0;i<n;i++){int a=i,b=i+1,c=i+n+1;Tri(mesh,a,b,c);Tri(mesh,b,c+1,c);}
            for(int side=0;side<2;side++)
            {int c=mesh.Positions.Count;mesh.Positions.Add(center+new Vector3D(0,0,(side-.5)*depth));for(int i=0;i<n;i++)if(side==0)Tri(mesh,c,i+1,i);else Tri(mesh,c,n+1+i,n+2+i);}
            mesh.Freeze();return mesh;
        }
        internal static Material Material(Color color,bool shiny=false)
        {
            var material=new MaterialGroup();material.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));
            if(shiny)material.Children.Add(new SpecularMaterial(new SolidColorBrush(Color.FromRgb(120,120,120)),65));
            material.Freeze();return material;
        }
        private static void Tri(MeshGeometry3D mesh,int a,int b,int c)
        {mesh.TriangleIndices.Add(a);mesh.TriangleIndices.Add(b);mesh.TriangleIndices.Add(c);}
    }
}
