using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace VanyaTools.Native
{
    internal sealed class BendLine
    {
        internal string Name;
        internal Point A, B;
        internal double Angle;
        internal int MovingSide = 1, PositivePanel, NegativePanel;
    }
    internal sealed class BendPanel
    {
        internal Geometry Shape;
        internal Point[][] Loops;
        internal double Area;
    }
    internal sealed class LaserBendModel
    {
        internal const double Thickness = 1.5;
        internal BendPanel[] Panels;
        internal BendLine[] Lines;
        internal Geometry Outline;
        internal Rect Bounds;
        internal string Name;
        internal HashSet<int> MovingPanels(int index, int? side = null)
        {
            var line = Lines[index];
            int start = (side ?? line.MovingSide) > 0 ? line.PositivePanel : line.NegativePanel;
            var found = new HashSet<int> { start }; var pending = new Queue<int>(); pending.Enqueue(start);
            while (pending.Count != 0)
            {
                int at = pending.Dequeue();
                for (int i = 0; i < Lines.Length; i++)
                {
                    if (i == index) continue;
                    var edge = Lines[i]; int other = edge.PositivePanel == at ? edge.NegativePanel : edge.NegativePanel == at ? edge.PositivePanel : -1;
                    if (other >= 0 && found.Add(other)) pending.Enqueue(other);
                }
            }
            return found;
        }
        internal void ChooseSide(int fold, int panel)
        {
            if (fold < 0 || fold >= Lines.Length || panel < 0 || panel >= Panels.Length) return;
            Lines[fold].MovingSide = MovingPanels(fold, 1).Contains(panel) ? 1 : -1;
        }
        internal Matrix3D[] Poses()
        {
            var result = Enumerable.Repeat(Matrix3D.Identity, Panels.Length).ToArray();
            for (int i = 0; i < Lines.Length; i++)
            {
                var fold = Lines[i];
                if (Double.IsNaN(fold.Angle) || Double.IsInfinity(fold.Angle) || Math.Abs(fold.Angle) > 180)
                    throw new ArgumentException("Угол сгиба: от −180° до 180°.");
                if (Math.Abs(fold.Angle) < .000001) continue;
                int stationary = fold.MovingSide > 0 ? fold.NegativePanel : fold.PositivePanel;
                var a = result[stationary].Transform(Point3(fold.A)); var b = result[stationary].Transform(Point3(fold.B));
                var axis = b - a; axis.Normalize();
                var rotation = Matrix3D.Identity;
                rotation.RotateAt(new Quaternion(axis, -fold.MovingSide * fold.Angle), a);
                foreach (int panel in MovingPanels(i)) { var pose = result[panel]; pose.Append(rotation); result[panel] = pose; }
            }
            return result;
        }
        internal static Point3D Point3(Point p, double z = 0) => new Point3D(p.X, -p.Y, z);
    }

    internal static class LaserBendGeometry
    {
        internal const double Tolerance = .03;
        internal static LaserBendModel Create(Geometry source, IEnumerable<BendLine> folds, string name = "Развёртка")
        {
            if (source == null || source.IsEmpty()) throw new ArgumentException("Выделите замкнутый внешний контур детали и линии сгиба.");
            var outline = source.GetOutlinedPathGeometry(Tolerance, ToleranceType.Absolute).GetFlattenedPathGeometry(Tolerance, ToleranceType.Absolute);
            var bounds = outline.Bounds;
            if (bounds.Width < .1 || bounds.Height < .1 || Math.Max(bounds.Width,bounds.Height) > 10000)
                throw new ArgumentException("Некорректные габариты детали. Размеры должны быть в миллиметрах.");
            var shift = new TranslateTransform(-bounds.Left,-bounds.Top);
            outline.Transform = shift;
            outline = outline.GetFlattenedPathGeometry(Tolerance,ToleranceType.Absolute); outline.Freeze();
            var lines = folds.Select((f,i) => new BendLine { Name=String.IsNullOrWhiteSpace(f.Name)?"Сгиб "+(i+1):f.Name,
                A=shift.Transform(f.A),B=shift.Transform(f.B) }).ToArray();
            if (lines.Length > 32) throw new ArgumentException("В первой версии доступно до 32 линий сгиба на одной детали.");
            foreach(var line in lines) if((line.B-line.A).Length<.1)throw new ArgumentException(line.Name+": слишком короткая линия.");
            var regions=Components(outline);
            if(regions.Length!=1)throw new ArgumentException("Выберите одну цельную деталь. Отверстия внутри её контура допускаются.");
            if(Loops(outline).Sum(p=>p.Length)>12000)throw new ArgumentException("Контур слишком подробный для 3D. Упростите его узлы.");
            for(int i=0;i<lines.Length;i++)for(int j=i+1;j<lines.Length;j++)
            {
                var a=lines[i];var b=lines[j];
                if(Intersection(a.A,a.B,b.A,b.B,out double t,out double u) && t>.0001 && t<.9999 && u>.0001 && u<.9999 &&
                    outline.FillContains(a.A+(a.B-a.A)*t))
                    throw new ArgumentException("Линии сгиба пересекаются внутри детали. Перекрёстные сгибы пока не поддерживаются.");
            }
            var cells=regions.ToList();
            foreach(var line in lines)
            {
                var next=new List<Geometry>();bool split=false;
                foreach(var cell in cells)
                {
                    var parts=SplitCell(cell,line);
                    if(parts.Length==1){next.Add(cell);continue;}
                    if(parts.Length!=2)throw new ArgumentException(line.Name+": сгиб отделяет несколько раздельных клапанов. Задайте для них отдельные линии.");
                    next.AddRange(parts);split=true;
                }
                if(!split)throw new ArgumentException(line.Name+": линия не разделяет деталь. Она должна проходить от края до края участка.");
                cells=next;
            }
            var panels=cells.Select(g=>new BendPanel {Shape=g,Loops=Loops(g),Area=g.GetArea()}).ToArray();
            foreach(var line in lines)
            {
                int pairs=0;
                for(int i=0;i<panels.Length;i++)for(int j=i+1;j<panels.Length;j++)
                {
                    int si=Side(panels[i],line),sj=Side(panels[j],line);
                    if(si*sj>=0)continue;
                    var ai=Intervals(panels[i].Shape,line);var aj=Intervals(panels[j].Shape,line);
                    if(!ai.Any(a=>aj.Any(b=>Math.Min((line.B-line.A).Length,Math.Min(a.Item2,b.Item2))-Math.Max(0,Math.Max(a.Item1,b.Item1))>.05)))continue;
                    pairs++;line.PositivePanel=si>0?i:j;line.NegativePanel=si<0?i:j;
                }
                if(pairs!=1)throw new ArgumentException(line.Name+": неоднозначное соединение граней. Линии должны задавать независимые сгибы без замкнутых петель.");
            }
            if(panels.Length!=lines.Length+1)throw new ArgumentException("Получилась замкнутая цепочка сгибов. Для неё нужна отдельная кинематическая модель.");
            var model=new LaserBendModel {Panels=panels,Lines=lines,Outline=outline,Bounds=outline.Bounds,Name=name};
            foreach(var line in lines)
            {
                int index=Array.IndexOf(lines,line);
                var plus=model.MovingPanels(index,1);var minus=model.MovingPanels(index,-1);
                if(plus.Overlaps(minus) || plus.Count+minus.Count!=panels.Length)throw new ArgumentException("Несвязанные грани или цикл сгибов. Проверьте линии.");
                line.MovingSide=plus.Sum(i=>panels[i].Area)<=minus.Sum(i=>panels[i].Area)?1:-1;
            }
            return model;
        }
        // Split on the finite chord only. Other material crossing the same infinite
        // axis stays joined: two separate flaps can share a horizontal coordinate.
        private static Geometry[] SplitCell(Geometry cell,BendLine line)
        {
            var positive=Clip(cell,line,1);var negative=Clip(cell,line,-1);
            if(positive.GetArea()<.001 || negative.GetArea()<.001)return new[]{cell};
            var plus=Components(positive);var minus=Components(negative);var pieces=plus.Concat(minus).ToArray();
            var roots=Enumerable.Range(0,pieces.Length).ToArray();double length=(line.B-line.A).Length;bool touches=false;
            int Root(int n){while(roots[n]!=n)n=roots[n];return n;}
            for(int i=0;i<plus.Length;i++)for(int j=plus.Length;j<pieces.Length;j++)
            {
                var a=Intervals(pieces[i],line);var b=Intervals(pieces[j],line);
                foreach(var x in a)foreach(var y in b)
                {
                    double start=Math.Max(x.Item1,y.Item1),end=Math.Min(x.Item2,y.Item2);
                    if(end-start<.001)continue;
                    if(Math.Min(end,length)-Math.Max(start,0)>.05)touches=true;
                    if(Math.Min(end,0)-start>.05 || end-Math.Max(start,length)>.05)roots[Root(i)]=Root(j);
                }
            }
            if(!touches)return new[]{cell};
            var result=new List<Geometry>();
            foreach(var group in Enumerable.Range(0,pieces.Length).GroupBy(Root))
            {
                Geometry joined=pieces[group.First()];
                foreach(int index in group.Skip(1))joined=Geometry.Combine(joined,pieces[index],GeometryCombineMode.Union,null,Tolerance,ToleranceType.Absolute);
                joined.Freeze();result.Add(joined);
            }
            return result.ToArray();
        }
        private static int Side(BendPanel panel,BendLine line)
        {
            var u=line.B-line.A;
            double far=panel.Loops.SelectMany(p=>p).Select(p=>Cross(u,p-line.A)).OrderByDescending(Math.Abs).First();
            return Math.Sign(far);
        }
        private static Geometry Clip(Geometry shape,BendLine line,int side)
        {
            var u=line.B-line.A;u.Normalize();var n=new Vector(-u.Y,u.X)*side;
            double size=Math.Sqrt(shape.Bounds.Width*shape.Bounds.Width+shape.Bounds.Height*shape.Bounds.Height)*8+100;
            var half=Path(new[]{line.A-u*size,line.A+u*size,line.A+u*size+n*size,line.A-u*size+n*size});
            var result=Geometry.Combine(shape,half,GeometryCombineMode.Intersect,null,Tolerance,ToleranceType.Absolute);
            result.Freeze();return result;
        }
        private static List<Tuple<double,double>> Intervals(Geometry region,BendLine line)
        {
            var result=new List<Tuple<double,double>>();var u=line.B-line.A;double length=u.Length;u/=length;
            var loops=Loops(region);
            foreach(var loop in loops)for(int i=0;i<loop.Length;i++)
            {
                var a=loop[i]-line.A;var b=loop[(i+1)%loop.Length]-line.A;
                if(Math.Abs(Cross(u,a))>.05 || Math.Abs(Cross(u,b))>.05)continue;
                double t=Vector.Multiply(a,u),s=Vector.Multiply(b,u);
                if(Math.Abs(t-s)>.01)result.Add(Tuple.Create(Math.Min(t,s),Math.Max(t,s)));
            }
            // WPF clipping can retain zero-area, backtracking edges on an axis.
            // Subdivide at every endpoint, then retain only intervals with actual
            // material alongside them, so a flat wing edge is not another hinge.
            var breaks=result.SelectMany(r=>new[]{r.Item1,r.Item2}).OrderBy(t=>t).ToArray();
            var clean=new List<Tuple<double,double>>();var normal=new Vector(-u.Y,u.X)*.002;
            for(int i=1;i<breaks.Length;i++)
            {
                double a=breaks[i-1],b=breaks[i],mid=(a+b)/2;if(b-a<.0001)continue;
                if(!result.Any(r=>mid>=r.Item1 && mid<=r.Item2))continue;
                var p=line.A+u*mid;
                if(Contains(loops,p+normal) || Contains(loops,p-normal))clean.Add(Tuple.Create(a,b));
            }
            return clean;
        }
        internal static Geometry Path(Point[] points)
        {
            var geometry=new StreamGeometry {FillRule=FillRule.EvenOdd};
            using(var path=geometry.Open()) {path.BeginFigure(points[0],true,true);foreach(var p in points.Skip(1))path.LineTo(p,true,false);}
            geometry.Freeze();return geometry;
        }
        internal static Point[][] Loops(Geometry shape)
        {
            var flat=shape.GetFlattenedPathGeometry(Tolerance,ToleranceType.Absolute);var result=new List<Point[]>();
            foreach(var figure in flat.Figures)
            {
                var points=new List<Point>{figure.StartPoint};
                foreach(var segment in figure.Segments)
                {
                    if(segment is PolyLineSegment poly)points.AddRange(poly.Points);
                    else if(segment is LineSegment line)points.Add(line.Point);
                }
                for(int i=points.Count-1;i>0;i--)if((points[i]-points[i-1]).Length<.000001)points.RemoveAt(i);
                if(points.Count>1 && (points[0]-points[points.Count-1]).Length<.000001)points.RemoveAt(points.Count-1);
                if(flat.Transform != null && !flat.Transform.Value.IsIdentity)
                    for(int i=0;i<points.Count;i++)points[i]=flat.Transform.Transform(points[i]);
                // Remove collinear backtracks produced by Boolean clipping. Without
                // this, zero-area whiskers become unwanted vertical mesh walls.
                bool changed=true;
                while(changed && points.Count>=3)
                {
                    changed=false;
                    for(int i=0;i<points.Count;i++)
                    {
                        var a=points[(i+points.Count-1)%points.Count];var b=points[i];var c=points[(i+1)%points.Count];
                        var span=c-a;
                        if(span.Length<.0001 || Math.Abs(Cross(span,b-a))/span.Length<.0001)
                        {points.RemoveAt(i);changed=true;break;}
                    }
                }
                if(points.Count>=3 && Math.Abs(Area(points.ToArray()))>.00001)result.Add(points.ToArray());
            }
            return result.ToArray();
        }
        private static Geometry[] Components(Geometry geometry)
        {
            var loops=Loops(geometry);var paths=loops.Select(Path).ToArray();
            var depth=loops.Select((loop,i)=>Enumerable.Range(0,loops.Length).Count(j=>i!=j && paths[j].FillContains(loop[0]))).ToArray();
            var components=new List<Geometry>();
            for(int i=0;i<loops.Length;i++)if(depth[i]%2==0)
            {
                var combined=new GeometryGroup{FillRule=FillRule.EvenOdd};combined.Children.Add(paths[i]);
                for(int j=0;j<loops.Length;j++)if(depth[j]==depth[i]+1 && paths[i].FillContains(loops[j][0]))combined.Children.Add(paths[j]);
                combined.Freeze();components.Add(combined);
            }
            return components.ToArray();
        }
        internal static bool Contains(Point[][] loops,Point point)
        {
            bool inside=false;
            foreach(var loop in loops)for(int i=0,j=loop.Length-1;i<loop.Length;j=i++)
            {
                var a=loop[i];var b=loop[j];
                if((a.Y>point.Y)!=(b.Y>point.Y) && point.X<(b.X-a.X)*(point.Y-a.Y)/(b.Y-a.Y)+a.X)inside=!inside;
            }
            return inside;
        }
        internal static double Area(Point[] p)=>Enumerable.Range(0,p.Length).Sum(i=>p[i].X*p[(i+1)%p.Length].Y-p[(i+1)%p.Length].X*p[i].Y)/2;
        internal static double Cross(Vector a,Vector b)=>a.X*b.Y-a.Y*b.X;
        private static bool Intersection(Point a,Point b,Point c,Point d,out double t,out double u)
        {
            var v=b-a;var w=d-c;double cross=Cross(v,w);t=u=0;if(Math.Abs(cross)<1e-10)return false;
            t=Cross(c-a,w)/cross;u=Cross(c-a,v)/cross;return t>=0 && t<=1 && u>=0 && u<=1;
        }
    }
}
