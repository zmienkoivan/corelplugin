using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal static class LaserBendMesh
    {
        // Scanline trapezoids triangulate both concave outlines and real holes.
        // No transparent rectangular textures or filled-in cutouts are used.
        internal static MeshGeometry3D Create(BendPanel panel,double thickness=LaserBendModel.Thickness)
        {
            var mesh=new MeshGeometry3D();var loops=panel.Loops;
            var ys=loops.SelectMany(p=>p).Select(p=>p.Y).OrderBy(y=>y).ToArray();
            var levels=new List<double>();foreach(double y in ys)if(levels.Count==0 || y-levels[levels.Count-1]>.000001)levels.Add(y);
            var edges=loops.SelectMany(loop=>Enumerable.Range(0,loop.Length).Select(i=>Tuple.Create(loop[i],loop[(i+1)%loop.Length]))).ToArray();
            for(int row=1;row<levels.Count;row++)
            {
                double top=levels[row-1],bottom=levels[row],middle=(top+bottom)/2;
                var crossings=edges.Where(e=>Math.Min(e.Item1.Y,e.Item2.Y)<middle && Math.Max(e.Item1.Y,e.Item2.Y)>middle)
                    .OrderBy(e=>X(e,middle)).ToArray();
                if(crossings.Length%2!=0)throw new ArgumentException("Не удалось построить поверхность: проверьте замкнутость контура.");
                for(int i=0;i<crossings.Length;i+=2)
                {
                    var a=new Point(X(crossings[i],top),top);var b=new Point(X(crossings[i+1],top),top);
                    var c=new Point(X(crossings[i+1],bottom),bottom);var d=new Point(X(crossings[i],bottom),bottom);
                    Face(a,b,c);Face(a,c,d);
                }
            }
            foreach(var loop in loops)for(int i=0;i<loop.Length;i++)
            {
                var a=loop[i];var b=loop[(i+1)%loop.Length];
                var mid=new Point((a.X+b.X)/2,(a.Y+b.Y)/2);var v=b-a;v.Normalize();
                bool insideLeft=LaserBendGeometry.Contains(panel.Loops,mid+new Vector(-v.Y,v.X)*.001);
                var at=LaserBendModel.Point3(a,thickness/2);var ab=LaserBendModel.Point3(a,-thickness/2);
                var bt=LaserBendModel.Point3(b,thickness/2);var bb=LaserBendModel.Point3(b,-thickness/2);
                if(insideLeft){Triangle(at,bb,ab);Triangle(at,bt,bb);}else{Triangle(at,ab,bb);Triangle(at,bb,bt);}
            }
            mesh.Freeze();return mesh;
            void Face(Point a,Point b,Point c)
            {
                if(Math.Abs(LaserBendGeometry.Cross(b-a,c-a))<1e-10)return;
                var x=LaserBendModel.Point3(a,thickness/2);var y=LaserBendModel.Point3(b,thickness/2);var z=LaserBendModel.Point3(c,thickness/2);
                if(Vector3D.CrossProduct(y-x,z-x).Z<0){var swap=y;y=z;z=swap;}
                Triangle(x,y,z);x.Z=y.Z=z.Z=-thickness/2;Triangle(x,z,y);
            }
            void Triangle(Point3D a,Point3D b,Point3D c)
            {
                if(mesh.Positions.Count>600000)throw new ArgumentException("Слишком подробная 3D-модель. Упростите исходные кривые.");
                var normal=Vector3D.CrossProduct(b-a,c-a);if(normal.LengthSquared<1e-18)return;normal.Normalize();
                int n=mesh.Positions.Count;mesh.Positions.Add(a);mesh.Positions.Add(b);mesh.Positions.Add(c);
                mesh.Normals.Add(normal);mesh.Normals.Add(normal);mesh.Normals.Add(normal);
                mesh.TriangleIndices.Add(n);mesh.TriangleIndices.Add(n+1);mesh.TriangleIndices.Add(n+2);
            }
        }
        private static double X(Tuple<Point,Point> edge,double y)=>edge.Item1.X+(edge.Item2.X-edge.Item1.X)*(y-edge.Item1.Y)/(edge.Item2.Y-edge.Item1.Y);
    }
}
