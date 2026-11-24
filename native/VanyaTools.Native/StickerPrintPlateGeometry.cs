using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace VanyaTools.Native
{
    internal static class StickerPrintPlateGeometry
    {
        internal static PathGeometry Build(BitmapSource image,double widthMm,double heightMm)
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
            // The alpha trace already consists of lines. Do not widen/combine
            // thousands of raster-derived loops in WPF's native scanner: a
            // complex sticker can fail there with MILERR_SCANNER_FAILED.
            int nodes=geometry.Figures.Sum(f=>1+f.Segments.Sum(s=>s is PolyLineSegment p?p.Points.Count:1));
            if(nodes>200000)throw new InvalidOperationException("Слишком много узлов печатной плашки. Создайте её частями.");
            geometry.Freeze();return geometry;
        }
        internal static object Curve(object document,PathGeometry geometry,double left,double top,double trapping=0,StickerPrintPlateMemory memory=null)
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
            memory?.Mark("native source curve built");
            if(Math.Abs(trapping)>1e-9)
                curve=curve.Contour(Math.Abs(trapping),trapping>0?CorelConstants.CdrContourOutside:CorelConstants.CdrContourInside,
                    CorelConstants.CdrContourRoundCap,CorelConstants.CdrContourCornerRound,0d);
            if(curve==null || Convert.ToInt32(curve.Nodes.Count)==0 || !Convert.ToBoolean(curve.Closed) || Math.Abs(Convert.ToDouble(curve.Area))<.000001)
                throw new InvalidOperationException("Треппинг полностью убрал плашку. Уменьшите отрицательный треппинг.");
            if(Convert.ToInt32(curve.Nodes.Count)>200000)throw new InvalidOperationException("После треппинга слишком много узлов. Создайте плашку частями.");
            return (object)curve;
        }
    }
}
