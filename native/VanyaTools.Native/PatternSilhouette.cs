using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VanyaTools.Native
{
    internal static class PatternSilhouette
    {
        private const double GeometryTolerance = .025;

        internal static Geometry Read(BitmapSource preview, PatternPoint size, out PatternFootprint footprint)
        {
            if (preview != null)
            {
                BitmapSource image = preview.Format == PixelFormats.Bgra32 ? preview :
                    new FormatConvertedBitmap(preview, PixelFormats.Bgra32, null, 0);
                int width = image.PixelWidth, height = image.PixelHeight;
                var pixels = new byte[checked(width * height * 4)];
                image.CopyPixels(pixels, width * 4, 0);
                var visible = new bool[width * height];
                int ink = 0;
                for (int i = 0; i < visible.Length; i++)
                {
                    visible[i] = pixels[i * 4 + 3] != 0;
                    if (visible[i]) ink++;
                }
                if (ink > 0 && ink < visible.Length)
                {
                    var geometry = new StreamGeometry { FillRule = FillRule.EvenOdd };
                    var points = new List<PatternPoint>();
                    using (var context = geometry.Open())
                    {
                        foreach (var loop in AlphaMaskVectorizer.TraceOutline(visible, width, height))
                        {
                            for (int i = 0; i < loop.Length; i++)
                            {
                                var p = new PatternPoint((loop[i].X / (double)width - .5) * size.X,
                                    (.5 - loop[i].Y / (double)height) * size.Y);
                                var vertex = new Point(p.X, p.Y);
                                if (i == 0) context.BeginFigure(vertex, true, true);
                                else context.LineTo(vertex, true, false);
                                points.Add(p);
                            }
                        }
                    }
                    if (points.Count > 0)
                    {
                        geometry.Freeze();
                        footprint = new PatternFootprint(size, points);
                        return geometry;
                    }
                }
            }

            footprint = new PatternFootprint(size);
            double w = Math.Max(.0001, size.X), h = Math.Max(.0001, size.Y);
            var rectangle = new RectangleGeometry(new Rect(-w / 2, -h / 2, w, h));
            rectangle.Freeze();
            return rectangle;
        }

        internal static Geometry At(PatternAsset asset, PatternPlacement item)
        {
            double scale = PatternTransform.Fit(asset.Size, item).Scale;
            var matrix = Matrix.Identity;
            matrix.Scale(scale, scale);
            matrix.Rotate(item.Angle);
            matrix.Translate(item.Center.X, item.Center.Y);
            var silhouette = asset.Silhouette.Clone();
            silhouette.Transform = new MatrixTransform(matrix);
            silhouette.Freeze();
            return silhouette;
        }

        internal static Geometry Reserve(PatternAsset hero, PatternPlacement item, double gap)
        {
            Geometry shape = At(hero, item);
            if (gap <= 0) return shape;
            var pen = new Pen(Brushes.Black, gap * 2) { LineJoin = PenLineJoin.Round };
            var edge = shape.GetWidenedPathGeometry(pen, GeometryTolerance, ToleranceType.Absolute);
            var reserve = Geometry.Combine(shape, edge, GeometryCombineMode.Union, null,
                GeometryTolerance, ToleranceType.Absolute);
            reserve.Freeze();
            return reserve;
        }

        internal static void LeaveRoom(List<PatternPlacement> items, IReadOnlyList<PatternAsset> motifs, Geometry reserve)
        {
            Rect bounds = reserve.Bounds;
            items.RemoveAll(item =>
            {
                PatternAsset motif = motifs[item.Motif];
                var size = PatternTransform.Fit(motif.Size, item);
                var area = new Rect(item.Center.X - size.RotatedWidth / 2, item.Center.Y - size.RotatedHeight / 2,
                    size.RotatedWidth, size.RotatedHeight);
                if (!bounds.IntersectsWith(area)) return false;
                return reserve.FillContainsWithDetail(At(motif, item), GeometryTolerance,
                    ToleranceType.Absolute) != IntersectionDetail.Empty;
            });
        }
    }
}
