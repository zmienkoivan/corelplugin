using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace VanyaTools.Native
{
    internal static class StickerPrintPlateGeometry
    {
        internal static PathGeometry Build(BitmapSource image,double widthMm,double heightMm,double trapping)
        {
            if(image==null || !(widthMm>0) || !(heightMm>0))throw new ArgumentException("Нет силуэта рисунка.");
            int width=image.PixelWidth,height=image.PixelHeight;
            var pixels=new byte[checked(width*height*4)];new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0).CopyPixels(pixels,width*4,0);
            var visible=new bool[width*height];int count=0;
            for(int i=0;i<visible.Length;i++){visible[i]=pixels[i*4+3]>10;if(visible[i])count++;}
            if(count==0)throw new InvalidOperationException("В выделении нет видимых пикселей для печатной плашки.");
            int edges=0;
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)if(visible[y*width+x])
            {
                if(x==0 || !visible[y*width+x-1])edges++;if(x==width-1 || !visible[y*width+x+1])edges++;
                if(y==0 || !visible[(y-1)*width+x])edges++;if(y==height-1 || !visible[(y+1)*width+x])edges++;
                if(edges>400000)throw new InvalidOperationException("Силуэт слишком сложный. Очистите мелкий мусор или создайте плашки частями.");
            }
            double sx=widthMm/width,sy=heightMm/height;var geometry=new PathGeometry{FillRule=FillRule.EvenOdd};
            // Keep every loop, including counters and nested transparent holes.
            foreach(var loop in AlphaMaskVectorizer.TraceOutline(visible,width,height,.25))
            {
                if(loop.Length<3)continue;
                var figure=new PathFigure{StartPoint=new Point(loop[0].X*sx,loop[0].Y*sy),IsClosed=true,IsFilled=true};
                figure.Segments.Add(new PolyLineSegment(loop.Skip(1).Select(p=>new Point(p.X*sx,p.Y*sy)),true));geometry.Figures.Add(figure);
            }
            Geometry result=geometry;
            if(Math.Abs(trapping)>1e-9)
            {
                var pen=new Pen(Brushes.Black,Math.Abs(trapping)*2){LineJoin=PenLineJoin.Round,StartLineCap=PenLineCap.Round,EndLineCap=PenLineCap.Round};
                var rim=geometry.GetWidenedPathGeometry(pen,.0025,ToleranceType.Absolute);
                result=Geometry.Combine(geometry,rim,trapping>0?GeometryCombineMode.Union:GeometryCombineMode.Exclude,null,.0025,ToleranceType.Absolute);
            }
            var flattened=result.GetFlattenedPathGeometry(.0025,ToleranceType.Absolute);
            if(flattened.GetArea(.0025,ToleranceType.Absolute)<.000001)throw new InvalidOperationException("Ужатие полностью убрало плашку. Уменьшите отрицательный треппинг.");
            int nodes=flattened.Figures.Sum(f=>1+f.Segments.Sum(s=>s is PolyLineSegment p?p.Points.Count:1));
            if(nodes>200000)throw new InvalidOperationException("Слишком много узлов печатной плашки. Создайте её частями.");
            flattened.Freeze();return flattened;
        }
        internal static object Curve(object document,PathGeometry geometry,double left,double top)
        {
            dynamic curve=((dynamic)document).CreateCurve();
            foreach(var figure in geometry.Figures)
            {
                dynamic path=curve.CreateSubPath(left+figure.StartPoint.X,top-figure.StartPoint.Y);
                foreach(var segment in figure.Segments)
                {
                    if(segment is PolyLineSegment polyline)foreach(var point in polyline.Points)path.AppendLineSegment(left+point.X,top-point.Y);
                    else if(segment is LineSegment line)path.AppendLineSegment(left+line.Point.X,top-line.Point.Y);
                    else throw new InvalidOperationException("Не удалось подготовить контур печатной плашки.");
                }
                path.Closed=true;
            }
            return (object)curve;
        }
    }
}
