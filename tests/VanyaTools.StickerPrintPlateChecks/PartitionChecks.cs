using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using VanyaTools.Native;

internal static class PartitionChecks
{
    internal static void Run(Action<bool,string> check)
    {
        var grid=new PathGeometry{FillRule=FillRule.EvenOdd};
        for(int row=0;row<5;row++)for(int col=0;col<7;col++)
        {
            double x=col*5,y=row*5;
            Rectangle(grid,x,y,3,3);Rectangle(grid,x+1,y+1,1,1);Rectangle(grid,x+1.4,y+1.4,.2,.2);
        }
        foreach(double trap in new[]{-.1,0,.1,.5})
        {
            var app=new PlateApp();
            var batched=(PlateCurve)StickerPrintPlateGeometry.Curve(app.Document,grid,0,0,trap);
            var reference=(PlateCurve)StickerPrintPlateGeometry.Curve(new PlateApp().Document,grid,0,0);
            if(trap!=0)reference=reference.Contour(Math.Abs(trap),trap>0?1:0,1,2,0);
            var actual=batched.Geometry;var expected=reference.Geometry;
            for(int y=0;y<67;y++)for(int x=0;x<97;x++)
            {
                var point=new Point(x*.35-.5,-y*.35+.5);
                check(actual.FillContains(point,.001,ToleranceType.Absolute)==expected.FillContains(point,.001,ToleranceType.Absolute),"Batched trapping changed ink coverage, a hole or an island");
            }
            check(trap==0?app.ContourBatchNodes.Count==0:app.ContourBatchNodes.Count==35 && app.ContourBatchNodes.Max()==12,
                "Separated motifs were sent through one large contour calculation");
        }

        var near=new PathGeometry{FillRule=FillRule.EvenOdd};
        Rectangle(near,0,0,2,2);Rectangle(near,2.3,0,2,2);Rectangle(near,4.6,0,2,2);
        var touching=new PlateApp();var joined=(PlateCurve)StickerPrintPlateGeometry.Curve(touching.Document,near,0,0,.2);
        check(touching.ContourBatchNodes.Count==1,"Neighbouring spreads were calculated separately");
        check(joined.Geometry.FillContains(new Point(2.15,-1)) && joined.Geometry.FillContains(new Point(4.45,-1)),
            "Expanded neighbouring pieces cancelled each other or left a seam");

        var collapse=new PathGeometry{FillRule=FillRule.EvenOdd};
        Rectangle(collapse,0,0,3,3);Rectangle(collapse,10,0,.1,.1);
        var surviving=(PlateCurve)StickerPrintPlateGeometry.Curve(new PlateApp().Document,collapse,0,0,-.2);
        check(surviving.Geometry.FillContains(new Point(1,-1)) && !surviving.Geometry.FillContains(new Point(10.05,-.05)),
            "A collapsed isolated island erased the surviving artwork or remained as ink");

        var fragments=new PathGeometry();
        for(int i=0;i<2050;i++)Rectangle(fragments,i*3,0,1,1);
        var fallback=StickerPrintPlatePartition.Groups(fragments,.1);
        check(fallback.Count==1 && fallback[0].Count==2050,"Fragmented masks lost figures in the bounded partition fallback");
    }
    private static void Rectangle(PathGeometry geometry,double x,double y,double w,double h)
    {
        var figure=new PathFigure{StartPoint=new Point(x,y),IsClosed=true,IsFilled=true};
        figure.Segments.Add(new PolyLineSegment(new[]{new Point(x+w,y),new Point(x+w,y+h),new Point(x,y+h)},true));
        geometry.Figures.Add(figure);
    }
}
