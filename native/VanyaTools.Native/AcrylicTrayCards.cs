using System;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal enum AcrylicTrayCardRest { RearTop, RearFace, Floor }

    internal sealed class AcrylicTrayCardPose
    {
        public const double Size = 30, SurfaceGap = 0.12;
        public Point3D Bottom, Top, RearTop;
        public AcrylicTrayCardRest Rest;
    }

    internal static class AcrylicTrayCards
    {
        public static AcrylicTrayCardPose Place(AcrylicTrayPattern p, int row, int column)
        {
            if (p.Options.CellWidth < AcrylicTrayCardPose.Size + 0.2) return null;
            bool plane = p.Options.FloorMode == AcrylicTrayFloorMode.Plane;
            var floor = p.Floors[plane ? 0 : row];
            var front = p.Crosses[plane ? row : row * 2];
            var rear = p.Crosses[plane ? row + 1 : row * 2 + 1];
            double gap = AcrylicTrayCardPose.SurfaceGap, size = AcrylicTrayCardPose.Size;
            double y = front.Y + p.Options.Thickness / 2 + gap;
            var result = new AcrylicTrayCardPose {
                Bottom = new Point3D(p.CellCenter(column), y, floor.Top(y) + gap),
                RearTop = new Point3D(p.CellCenter(column), rear.Y - p.Options.Thickness / 2 - gap,
                    rear.Bottom + rear.Height + gap) };
            Vector3D support = result.RearTop - result.Bottom;
            if (support.Length <= size)
            {
                support.Normalize(); result.Top = result.Bottom + support * size;
                result.Rest = AcrylicTrayCardRest.RearTop;
            }
            else
            {
                double dy = result.RearTop.Y - result.Bottom.Y;
                double height = dy <= size ? Math.Sqrt(size * size - dy * dy) : -1;
                if (height >= 0 && result.Bottom.Z + height >= rear.Bottom + gap)
                {
                    result.Top = new Point3D(result.Bottom.X, result.RearTop.Y, result.Bottom.Z + height);
                    result.Rest = AcrylicTrayCardRest.RearFace;
                }
                else
                {
                    result.Top = result.Bottom + new Vector3D(0, floor.Cosine, floor.Sine) * size;
                    result.Rest = AcrylicTrayCardRest.Floor;
                }
            }
            return result;
        }
    }
}
