using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal static class ApparelShirtMesh
    {
        private sealed class Part
        {
            public int Id;
            public float[][] Vertices;
            public int[] Indices;
        }
        private static readonly Lazy<Part[]> Data = new Lazy<Part[]>(Read);
        private static readonly string[] Names = { "Перед", "Спинка", "Правый рукав · перед", "Правый рукав · спинка",
            "Левый рукав · перед", "Левый рукав · спинка", "Горловина", "Изнанка и края" };

        public static ApparelSurface[] Build(ApparelViewMap front, double folds)
        {
            double sx = front.BodyWidthMm / 520, sy = front.BodyHeightMm / 710;
            var result = new List<ApparelSurface>();
            foreach (var part in Data.Value)
            {
                var mesh = new MeshGeometry3D();
                foreach (var v in part.Vertices)
                {
                    double Mix(int actual, int smooth) => v[smooth] + (v[actual] - v[smooth]) * folds;
                    mesh.Positions.Add(new Point3D(Mix(0, 3) * sx, Mix(1, 4) * sy, Mix(2, 5) * sx));
                    var n = new Vector3D(Mix(6, 9) / sx, Mix(7, 10) / sy, Mix(8, 11) / sx);
                    if (n.LengthSquared > 1e-12) n.Normalize();
                    mesh.Normals.Add(n); mesh.TextureCoordinates.Add(new Point(v[12], v[13]));
                }
                foreach (int i in part.Indices) mesh.TriangleIndices.Add(i);
                mesh.Freeze();
                result.Add(new ApparelSurface { Name = Names[part.Id], PrintPart = part.Id, Back = part.Id == 1 || part.Id == 3 || part.Id == 5,
                    Collar = part.Id >= 6, Mesh = mesh });
            }
            return result.ToArray();
        }
        private static Part[] Read()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("VanyaTools.Native.Assets.Apparel.shirt.mesh.gz"))
            {
                if (stream == null) throw new InvalidOperationException("В установщике отсутствует 3D-модель футболки. Переустановите плагин.");
                using (var gzip = new GZipStream(stream, CompressionMode.Decompress))
                using (var reader = new BinaryReader(gzip, Encoding.ASCII))
                {
                    if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "VTSHIRT1" || reader.ReadInt32() != 8)
                        throw new InvalidDataException("Неверный формат 3D-модели футболки.");
                    var result = new List<Part>();
                    for (int p = 0; p < 8; p++)
                    {
                        int id = reader.ReadInt32(), count = reader.ReadInt32(), indices = reader.ReadInt32();
                        if (id != p || count < 3 || count > 200000 || indices < 3 || indices > 1200000 || indices % 3 != 0)
                            throw new InvalidDataException("Повреждена геометрия 3D-модели.");
                        var part = new Part { Id = id, Vertices = new float[count][], Indices = new int[indices] };
                        for (int i = 0; i < count; i++)
                        {
                            var v = new float[14];
                            for (int j = 0; j < v.Length; j++)
                            {
                                v[j] = reader.ReadSingle();
                                if (Single.IsNaN(v[j]) || Single.IsInfinity(v[j])) throw new InvalidDataException("Неверные координаты 3D-модели.");
                            }
                            part.Vertices[i] = v;
                        }
                        for (int i = 0; i < indices; i++)
                        {
                            int index = reader.ReadInt32();
                            if (index < 0 || index >= count) throw new InvalidDataException("Неверные грани 3D-модели.");
                            part.Indices[i] = index;
                        }
                        result.Add(part);
                    }
                    return result.ToArray();
                }
            }
        }
    }
}
