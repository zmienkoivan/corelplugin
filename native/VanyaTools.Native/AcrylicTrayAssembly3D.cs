using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal sealed class AcrylicTrayMesh3D
    {
        public string Id;
        public MeshGeometry3D Faces, Edges;
    }

    internal sealed class AcrylicTrayAssembly3D
    {
        public AcrylicTrayMesh3D[] Parts;
        public Rect3D Bounds;

        public static AcrylicTrayAssembly3D Build(AcrylicTrayPattern pattern)
        {
            double t = pattern.Options.Thickness;
            var cache = new Dictionary<string, MeshGeometry3D>();
            var result = new List<AcrylicTrayMesh3D>();
            int longitudinal = 0, cross = 0, floor = 0;
            Rect3D bounds = Rect3D.Empty;
            foreach (var part in pattern.Parts)
            {
                if (!cache.TryGetValue(part.Path, out var source))
                {
                    // The existing sheet-mesh builder triangulates concavities
                    // and actual holes, then extrudes all cut edges by t.
                    source = LaserBendMesh.Create(new BendPanel { Shape = part.Geometry,
                        Loops = LaserBendGeometry.Loops(part.Geometry), Area = part.Geometry.GetArea() }, t);
                    cache.Add(part.Path, source);
                }
                Func<Point3D, Point3D> position;
                Func<Vector3D, Vector3D> normal;
                double u0 = part.AssemblyBounds.X, v0 = part.AssemblyBounds.Y;
                if (part.Kind == "side" || part.Kind == "long")
                {
                    double x = part.Kind == "long" ? pattern.Longitudinals[longitudinal++] :
                        part.Id == "side-left" ? -t / 2 : pattern.InnerWidth + t / 2;
                    position = p => new Point3D(x + p.Z, u0 + p.X, p.Y - v0);
                    normal = n => new Vector3D(n.Z, n.X, n.Y);
                }
                else if (part.Kind == "cross")
                {
                    double y = pattern.Crosses[cross++].Y;
                    position = p => new Point3D(u0 + p.X, y - p.Z, p.Y - v0);
                    normal = n => new Vector3D(n.X, -n.Z, n.Y);
                }
                else if (part.Kind == "wedge")
                {
                    var key = pattern.Wedges.First(w => w.Id == part.Id);
                    position = p => key.Point(u0 + p.X, v0 - p.Y, p.Z);
                    normal = n => key.U * n.X - key.V * n.Y + key.W * n.Z;
                }
                else if (part.Kind == "floor")
                {
                    var f = pattern.Floors[floor++];
                    // Native mesh z=-t/2 is the TOP floor face. This is a
                    // proper rotation, preserving winding and outward normals.
                    position = p => new Point3D(u0 + p.X,
                        f.StartY + (v0 - p.Y) * f.Cosine + (p.Z + t / 2) * f.Sine,
                        f.FrontZ + (v0 - p.Y) * f.Sine - (p.Z + t / 2) * f.Cosine);
                    normal = n => new Vector3D(n.X, -n.Y * f.Cosine + n.Z * f.Sine,
                        -n.Y * f.Sine - n.Z * f.Cosine);
                }
                else throw new InvalidOperationException("Неизвестная деталь лотка: " + part.Id);
                var faces = new MeshGeometry3D(); var edges = new MeshGeometry3D();
                for (int i = 0; i < source.TriangleIndices.Count; i += 3)
                {
                    int a = source.TriangleIndices[i];
                    var target = Math.Abs(source.Normals[a].Z) > 0.5 ? faces : edges;
                    for (int k = 0; k < 3; k++)
                    {
                        int index = source.TriangleIndices[i + k];
                        target.TriangleIndices.Add(target.Positions.Count);
                        target.Positions.Add(position(source.Positions[index]));
                        target.Normals.Add(normal(source.Normals[index]));
                    }
                }
                faces.Freeze(); edges.Freeze(); bounds.Union(faces.Bounds); bounds.Union(edges.Bounds);
                result.Add(new AcrylicTrayMesh3D { Id = part.Id, Faces = faces, Edges = edges });
            }
            return new AcrylicTrayAssembly3D { Parts = result.ToArray(), Bounds = bounds };
        }
    }
}
