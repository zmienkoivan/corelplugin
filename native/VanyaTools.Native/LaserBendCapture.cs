using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace VanyaTools.Native
{
    internal static class LaserBendCapture
    {
        private static object P(object target,string name,params object[] args)=>target.GetType().InvokeMember(name,BindingFlags.GetProperty,null,target,args,CultureInfo.InvariantCulture);
        private static double N(object target,string name)=>Convert.ToDouble(P(target,name),CultureInfo.InvariantCulture);
        internal static LaserBendModel Capture()=>Capture(CorelApp.Get());
        internal static LaserBendModel Capture(object app)
        {
            object doc=P(app,"ActiveDocument");if(doc==null)throw new InvalidOperationException("Откройте документ CorelDRAW.");
            object selected=P(app,"ActiveSelectionRange");int count=selected==null?0:Convert.ToInt32(P(selected,"Count"));
            if(count==0)throw new InvalidOperationException("Выделите внешний контур, отверстия и прямые линии сгиба.");
            double scale=Convert.ToDouble(app.GetType().InvokeMember("ConvertUnits",BindingFlags.InvokeMethod,null,app,new object[]{1d,Convert.ToInt32(P(doc,"Unit")),3},CultureInfo.InvariantCulture));
            if(scale<=0 || Double.IsNaN(scale) || Double.IsInfinity(scale))throw new InvalidOperationException("Не удалось определить единицы документа.");
            var outline=new GeometryGroup{FillRule=FillRule.EvenOdd};var folds=new List<BendLine>();int segments=0,shapes=0;
            for(int i=1;i<=count;i++)Read(P(selected,"Item",i),0);
            outline.Freeze();string name="Развёртка";try{name=Convert.ToString(P(doc,"Name"));}catch{}
            return LaserBendGeometry.Create(outline,folds,name);
            void Read(object shape,int depth)
            {
                if(depth>32 || ++shapes>2000)throw new InvalidOperationException("Слишком много вложенных объектов в выделении.");
                int type=Convert.ToInt32(P(shape,"Type"));
                if(type==7)
                {
                    object children=P(shape,"Shapes");int total=Convert.ToInt32(P(children,"Count"));
                    for(int i=1;i<=total;i++)Read(P(children,"Item",i),depth+1);return;
                }
                if(type==5)throw new InvalidOperationException("3D-гибка работает с векторными контурами. Уберите растры из выделения.");
                string label="Линия "+(folds.Count+1);try{string value=Convert.ToString(P(shape,"Name"));if(!String.IsNullOrWhiteSpace(value))label=value;}catch{}
                object curve;
                try{curve=P(shape,"DisplayCurve");}catch(Exception error){throw new InvalidOperationException("Не удалось прочитать векторный объект. Преобразуйте его в кривые.",error);}
                object paths=P(curve,"SubPaths");int pathCount=Convert.ToInt32(P(paths,"Count"));
                for(int pathIndex=1;pathIndex<=pathCount;pathIndex++)
                {
                    object source=P(paths,"Item",pathIndex),parts=P(source,"Segments");int total=Convert.ToInt32(P(parts,"Count"));
                    bool closed=Convert.ToBoolean(P(source,"Closed"));if(total==0)continue;
                    var geometry=new StreamGeometry();
                    using(var path=geometry.Open())
                    {
                        for(int i=1;i<=total;i++)
                        {
                            if(++segments>20000)throw new InvalidOperationException("В выделении слишком много сегментов. Упростите кривые.");
                            object segment=P(parts,"Item",i);object a=P(segment,"StartNode"),b=P(segment,"EndNode");
                            if(i==1)path.BeginFigure(Point(N(a,"PositionX"),N(a,"PositionY")),true,closed);
                            var end=Point(N(b,"PositionX"),N(b,"PositionY"));
                            if(Convert.ToInt32(P(segment,"Type"))==0)path.LineTo(end,true,false);
                            else path.BezierTo(Point(N(segment,"StartingControlPointX"),N(segment,"StartingControlPointY")),
                                Point(N(segment,"EndingControlPointX"),N(segment,"EndingControlPointY")),end,true,false);
                        }
                    }
                    geometry.Freeze();
                    if(closed){outline.Children.Add(geometry);continue;}
                    var flat=geometry.GetFlattenedPathGeometry(.03,ToleranceType.Absolute);var points=new List<Point>();
                    foreach(var figure in flat.Figures){points.Add(figure.StartPoint);foreach(var part in figure.Segments){if(part is PolyLineSegment poly)points.AddRange(poly.Points);else if(part is LineSegment line)points.Add(line.Point);}}
                    if(points.Count<2)continue;var start=points[0];var finish=points[points.Count-1];var direction=finish-start;
                    if(direction.Length<.1)throw new InvalidOperationException(label+": линия сгиба слишком короткая.");
                    foreach(var p in points)if(Math.Abs(LaserBendGeometry.Cross(direction,p-start))/direction.Length>.05)
                        throw new InvalidOperationException(label+": для сгиба нужна прямая линия. Криволинейные сгибы пока не поддерживаются.");
                    folds.Add(new BendLine{Name=label,A=start,B=finish});
                }
            }
            Point Point(double x,double y)
            {
                if(Double.IsNaN(x+y) || Double.IsInfinity(x+y))throw new InvalidOperationException("Некорректные координаты контура.");
                return new Point(x*scale,-y*scale);
            }
        }
    }
}
