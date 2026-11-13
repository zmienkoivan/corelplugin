using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using VanyaTools.Native;

internal static class Program
{
    private static int _checks;
    [STAThread] private static int Main(string[] args)
    {
        try
        {
            var shape=new GeometryGroup{FillRule=FillRule.EvenOdd};
            shape.Children.Add(new RectangleGeometry(new Rect(20,30,120,80)));
            shape.Children.Add(new RectangleGeometry(new Rect(35,42,12,8)));
            var model=LaserBendGeometry.Create(shape,new[]{Fold(20,60,140,60)});
            Check(model.Panels.Length==2,"Rectangle must split in two.");Near(model.Bounds.X,0);Near(model.Bounds.Y,0);
            Near(model.Panels.Sum(p=>p.Area),9504,.1);Meshes(model);
            Check(!model.Panels.Any(p=>p.Shape.FillContains(new Point(20,16))),"Hole was filled or normalization lost.");
            Near(model.Lines[0].A.X,0);Near(model.Lines[0].A.Y,30);
            var zero=model.Poses();Check(zero.All(p=>p.IsIdentity),"Flat poses must be identity.");
            model.Lines[0].Angle=90;var poses=model.Poses();Hinges(model);
            var moving=model.MovingPanels(0);int fixedIndex=Enumerable.Range(0,2).Single(i=>!moving.Contains(i));
            Check(poses[fixedIndex].IsIdentity,"Stationary panel moved.");
            foreach(int i in moving)
            {
                var source=model.Panels[i].Loops.SelectMany(p=>p).OrderByDescending(p=>Math.Abs(p.Y-30)).First();
                var result=poses[i].Transform(LaserBendModel.Point3(source));Near(result.Y,-30,.01);Check(result.Z>0,"Positive angle must fold selected side up.");
            }
            model.Lines[0].MovingSide*=-1;Hinges(model);Check(model.Poses()[moving.First()].IsIdentity,"Flipping side must keep prior moving side fixed.");
            model.ChooseSide(0,moving.First());Check(model.MovingPanels(0).Contains(moving.First()),"Click side failed.");
            var chain=LaserBendGeometry.Create(new RectangleGeometry(new Rect(0,0,100,90)),new[]{Fold(0,30,100,30),Fold(0,60,100,60)});
            foreach(int side0 in new[]{-1,1})foreach(int side1 in new[]{-1,1})
            foreach(double angle0 in new[]{-180d,-90,-30,0,45,90,180})foreach(double angle1 in new[]{-130d,0,60,90})
            {
                chain.Lines[0].MovingSide=side0;chain.Lines[1].MovingSide=side1;chain.Lines[0].Angle=angle0;chain.Lines[1].Angle=angle1;
                Hinges(chain);Rigid(chain);
            }
            // A diagonal flap and a hole straddling a straight hinge remain material, not opaque rectangles.
            var diagonal=LaserBendGeometry.Create(new RectangleGeometry(new Rect(0,0,100,80)),new[]{Fold(0,30,55,80)});Meshes(diagonal);
            var across=new GeometryGroup{FillRule=FillRule.EvenOdd};across.Children.Add(new RectangleGeometry(new Rect(0,0,100,80)));
            across.Children.Add(new EllipseGeometry(new Point(50,40),8,8));
            var holed=LaserBendGeometry.Create(across,new[]{Fold(0,40,100,40)});Meshes(holed);
            var concave=LaserBendGeometry.Path(new[]{new Point(0,0),new Point(120,0),new Point(120,40),new Point(80,40),new Point(80,100),new Point(0,100)});
            var l=LaserBendGeometry.Create(concave,new[]{Fold(0,25,120,25),Fold(0,70,80,70)});Meshes(l);l.Lines[0].Angle=45;l.Lines[1].Angle=-90;Hinges(l);
            Reject(()=>LaserBendGeometry.Create(new RectangleGeometry(new Rect(0,0,100,100)),new[]{Fold(20,40,80,40)}),"partial hinge");
            Reject(()=>LaserBendGeometry.Create(new RectangleGeometry(new Rect(0,0,100,100)),new[]{Fold(0,40,100,40),Fold(50,0,50,100)}),"crossing hinges");
            Reject(()=>LaserBendGeometry.Create(new RectangleGeometry(new Rect(0,0,100,100)),new[]{Fold(0,-20,100,-20)}),"outside hinge");
            Reject(()=>LaserBendGeometry.Create(new RectangleGeometry(new Rect(0,0,100,100)),new[]{Fold(0,40,100,40),Fold(0,40,100,40)}),"duplicate hinge");
            var separate=new GeometryGroup();separate.Children.Add(new RectangleGeometry(new Rect(0,0,10,10)));separate.Children.Add(new RectangleGeometry(new Rect(20,0,10,10)));
            Reject(()=>LaserBendGeometry.Create(separate,new BendLine[0]),"two sheets");
            // Separate collinear flaps must remain independent, even if the axis
            // extension also crosses a bridge elsewhere in the blank.
            var tabs=LaserBendGeometry.Path(new[]{new Point(0,20),new Point(0,0),new Point(25,0),new Point(25,20),new Point(45,20),new Point(45,0),new Point(70,0),new Point(70,20),new Point(100,20),new Point(100,90),new Point(0,90)});
            var twins=LaserBendGeometry.Create(tabs,new[]{Fold(0,20,25,20),Fold(45,20,70,20)});
            Check(twins.Panels.Length==3,"Collinear flaps failed.");twins.Lines[0].Angle=70;twins.Lines[1].Angle=-20;Hinges(twins);Meshes(twins);
            Check(!twins.MovingPanels(0).Overlaps(twins.MovingPanels(1)),"Default side merged independent flaps.");
            var longFolds=Enumerable.Range(1,15).Select(i=>Fold(0,i*10,100,i*10)).ToArray();
            var many=LaserBendGeometry.Create(new RectangleGeometry(new Rect(0,0,100,160)),longFolds);
            Check(many.Panels.Length==16,"Parallel panel chain failed.");
            for(int i=0;i<many.Lines.Length;i++)many.Lines[i].Angle=i%2==0?35:-35;
            Hinges(many);Rigid(many);
            var speed=Stopwatch.StartNew();for(int n=0;n<200;n++){many.Lines[7].Angle=n%180;many.Poses();}
            Check(speed.Elapsed.TotalSeconds<3,"Pose calculation too slow for slider.");
            Capture();
            WindowChecks(args);
            Console.WriteLine("Laser bend checks passed: "+_checks);return 0;
        }
        catch(Exception error){Console.Error.WriteLine(error);return 1;}
    }
    internal static LaserBendModel Demo()
    {
        var body=LaserBendGeometry.Path(new[]{new Point(0,25),new Point(30,25),new Point(30,0),new Point(140,0),new Point(140,25),new Point(180,25),new Point(180,100),new Point(140,100),new Point(140,140),new Point(30,140),new Point(30,100),new Point(0,100)});
        var group=new GeometryGroup{FillRule=FillRule.EvenOdd};group.Children.Add(body);
        group.Children.Add(new RectangleGeometry(new Rect(50,8,35,4)));group.Children.Add(new EllipseGeometry(new Point(90,65),15,15));
        var model=LaserBendGeometry.Create(group,new[]{Fold(30,25,140,25),Fold(30,100,140,100),Fold(30,25,30,100),Fold(140,25,140,100)});
        for(int i=0;i<model.Lines.Length;i++)model.Lines[i].Angle=i<2?60:90;
        return model;
    }
    private static void WindowChecks(string[] args)
    {
        var model=Demo();Meshes(model);Hinges(model);Rigid(model);
        var clock=Stopwatch.StartNew();var window=new LaserBendWindow(model){Width=1220,Height=780};
        var root=(FrameworkElement)window.Content;root.Measure(new Size(1220,780));root.Arrange(new Rect(0,0,1220,780));root.UpdateLayout();
        Check(window.Selected==0,"Window must select first fold.");
        var scenes=Descendants(root).OfType<LaserBendScene>().ToArray();Check(scenes.Length==1,"Missing 3D scene.");
        var viewport=Descendants(root).OfType<Viewport3D>().Single();
        var visual=(ModelVisual3D)viewport.Children[0];var models=((Model3DGroup)visual.Content).Children.OfType<GeometryModel3D>().ToArray();
        var meshes=models.Select(m=>m.Geometry).ToArray();
        for(int n=0;n<1000;n++){window.SetAngle(n%361-180);Hinges(model);}
        Check(models.Select((m,i)=>ReferenceEquals(m.Geometry,meshes[i])).All(s=>s),"Slider rebuilt meshes.");
        Check(clock.Elapsed.TotalSeconds<12,"Interactive angle updates are too slow.");
        var chooser=Descendants(root).OfType<ComboBox>().Single();chooser.SelectedIndex=2;window.SetAngle(37);
        Near(model.Lines[2].Angle,37);Check(window.Selected==2,"Fold selector lost selection.");chooser.SelectedIndex=0;
        window.ChooseSide(0);Check(model.MovingPanels(0).Contains(0),"Window panel selection failed.");
        window.FlipSide();Check(!model.MovingPanels(0).Contains(0),"Window side switch failed.");
        window.Reset();Check(model.Lines.All(f=>f.Angle==0),"Reset must clear all bends.");
        foreach(var fold in model.Lines)fold.Angle=65;
        window.SetAngle(65);scenes[0].Fit();window.UpdateLayout();
        if(args.Length>0)
        {
            var image=new RenderTargetBitmap(1220,780,96,96,PixelFormats.Pbgra32);image.Render(root);
            using(var output=File.Create(args[0])){var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(image));png.Save(output);}
        }
        window.Close();
        var flat=new LaserBendWindow(LaserBendGeometry.Create(new RectangleGeometry(new Rect(0,0,80,40)),new BendLine[0]));
        Check(flat.Selected==-1,"Blank without hinges must explain how to add them.");flat.SetAngle(90);flat.FlipSide();flat.Close();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject item)
    {
        yield return item;for(int i=0;i<VisualTreeHelper.GetChildrenCount(item);i++)foreach(var child in Descendants(VisualTreeHelper.GetChild(item,i)))yield return child;
    }
    private static void Capture()
    {
        var outline=new FakeShape{DisplayCurve=new FakeCurve{SubPaths=new FakeItems<FakePath>(Polygon(true,new[]{new Point(1,1),new Point(5,1),new Point(5,4),new Point(1,4)}))}};
        var hinge=new FakeShape{Name="Fold one",DisplayCurve=new FakeCurve{SubPaths=new FakeItems<FakePath>(Polygon(false,new[]{new Point(1,2),new Point(5,2)}))}};
        var group=new FakeShape{Type=7,Shapes=new FakeItems<FakeShape>(outline,hinge)};
        var app=new FakeApp{ActiveSelectionRange=new FakeItems<FakeShape>(group)};
        var model=LaserBendCapture.Capture(app);Near(model.Bounds.Width,101.6,.01);Near(model.Bounds.Height,76.2,.01);
        Check(model.Lines.Length==1 && model.Panels.Length==2,"Group capture failed: "+model.Lines.Length+" lines, "+model.Panels.Length+" panels.");Check(model.Lines[0].Name=="Fold one","Lost fold name.");
        Check(app.ActiveDocument.Unit==1 && ReferenceEquals(app.ActiveSelectionRange[1],group),"Capture changed source document or selection.");
        Near(model.Lines[0].A.Y,50.8,.01);
        group.Shapes=new FakeItems<FakeShape>(outline,new FakeShape{Type=5});Reject(()=>LaserBendCapture.Capture(app),"raster");
        group.Shapes=new FakeItems<FakeShape>(outline,new FakeShape{DisplayCurve=new FakeCurve{SubPaths=new FakeItems<FakePath>(Polygon(false,new[]{new Point(1,2),new Point(3,3),new Point(5,2)}))}});
        Reject(()=>LaserBendCapture.Capture(app),"curved fold");
        var curved=Polygon(true,new[]{new Point(1,1),new Point(5,1),new Point(5,4),new Point(1,4)});
        curved.Segments[1].Type=1;curved.Segments[1].StartingControlPointX=2;curved.Segments[1].StartingControlPointY=.5;
        curved.Segments[1].EndingControlPointX=4;curved.Segments[1].EndingControlPointY=.5;
        app.ActiveSelectionRange=new FakeItems<FakeShape>(new FakeShape{DisplayCurve=new FakeCurve{SubPaths=new FakeItems<FakePath>(curved)}},hinge);
        var capturedCurve=LaserBendCapture.Capture(app);Check(capturedCurve.Panels.Length==2,"Bezier outline capture failed.");Meshes(capturedCurve);
        app.ActiveSelectionRange=new FakeItems<FakeShape>();Reject(()=>LaserBendCapture.Capture(app),"empty selection");
    }
    private static FakePath Polygon(bool closed,Point[] points)
    {
        var segments=new List<FakeSegment>();for(int i=1;i<points.Length;i++)segments.Add(new FakeSegment(points[i-1],points[i]));
        if(closed)segments.Add(new FakeSegment(points[points.Length-1],points[0]));return new FakePath{Closed=closed,Segments=new FakeItems<FakeSegment>(segments.ToArray())};
    }
    private static void Meshes(LaserBendModel model)
    {
        foreach(var panel in model.Panels)
        {
            var mesh=LaserBendMesh.Create(panel);double area=0;
            Near(mesh.Bounds.SizeZ,1.5);Check(mesh.IsFrozen,"Meshes must be frozen.");
            for(int i=0;i<mesh.TriangleIndices.Count;i+=3)
            {
                int a=mesh.TriangleIndices[i],b=mesh.TriangleIndices[i+1],c=mesh.TriangleIndices[i+2];
                var p=mesh.Positions[a];var q=mesh.Positions[b];var r=mesh.Positions[c];
                if(Math.Abs(p.Z-.75)<1e-6 && Math.Abs(q.Z-.75)<1e-6 && Math.Abs(r.Z-.75)<1e-6)
                {
                    var cross=Vector3D.CrossProduct(q-p,r-p);Check(cross.Z>0,"Front face normal reversed.");area+=cross.Length/2;
                    Check(panel.Shape.FillContains(new Point((p.X+q.X+r.X)/3,-(p.Y+q.Y+r.Y)/3)),"Front mesh filled a cutout or escaped outline.");
                }
                Check(mesh.Normals[a].Length>.99,"Invalid normal.");
                if(Math.Abs(mesh.Normals[a].Z)<.01)
                {
                    var mid=new Point3D((p.X+q.X+r.X)/3,(p.Y+q.Y+r.Y)/3,(p.Z+q.Z+r.Z)/3);
                    var outward=mid+mesh.Normals[a]*.005;var inward=mid-mesh.Normals[a]*.005;
                    Check(!LaserBendGeometry.Contains(panel.Loops,new Point(outward.X,-outward.Y)),"Wall normal points into material.");
                    Check(LaserBendGeometry.Contains(panel.Loops,new Point(inward.X,-inward.Y)),"Wall has no material inside.");
                }
            }
            Near(area,panel.Area,Math.Max(.12,panel.Area*.0001));
        }
    }
    private static void Hinges(LaserBendModel model)
    {
        var poses=model.Poses();foreach(var f in model.Lines)foreach(var point in new[]{f.A,f.B})
        {
            var a=poses[f.PositivePanel].Transform(LaserBendModel.Point3(point));var b=poses[f.NegativePanel].Transform(LaserBendModel.Point3(point));Near((a-b).Length,0,.00001);
        }
    }
    private static void Rigid(LaserBendModel model)
    {
        var poses=model.Poses();for(int i=0;i<poses.Length;i++)
        {
            var p=model.Panels[i].Loops[0];var a=LaserBendModel.Point3(p[0]);var b=LaserBendModel.Point3(p[1]);
            Near((poses[i].Transform(a)-poses[i].Transform(b)).Length,(a-b).Length,.00001);
        }
    }
    private static BendLine Fold(double x,double y,double xx,double yy)=>new BendLine{A=new Point(x,y),B=new Point(xx,yy)};
    private static void Near(double a,double b,double tolerance=.001)=>Check(Math.Abs(a-b)<=tolerance,"Expected "+b+", got "+a);
    private static void Check(bool value,string message){_checks++;if(!value)throw new Exception(message);}
    private static void Reject(Action action,string name){try{action();}catch(ArgumentException){_checks++;return;}catch(InvalidOperationException){_checks++;return;}throw new Exception("Accepted "+name);}
}
public sealed class FakeApp
{
    public FakeDocument ActiveDocument{get;}=new FakeDocument();public FakeItems<FakeShape> ActiveSelectionRange{get;set;}
    public double ConvertUnits(double n,int source,int target){if(source!=1 || target!=3)throw new Exception("Units changed.");return n*25.4;}
}
public sealed class FakeDocument { public int Unit=>1; public string Name=>"Test.cdr"; }
public sealed class FakeItems<T>
{
    private readonly T[] _items;public FakeItems(params T[] items){_items=items;}public int Count=>_items.Length;public T this[int index]=>_items[index-1];
}
public sealed class FakeShape { public int Type{get;set;}=3;public string Name{get;set;} public FakeCurve DisplayCurve{get;set;}public FakeItems<FakeShape> Shapes{get;set;} }
public sealed class FakeCurve { public FakeItems<FakePath> SubPaths{get;set;} }
public sealed class FakePath { public bool Closed{get;set;}public FakeItems<FakeSegment> Segments{get;set;} }
public sealed class FakeNode { public double PositionX{get;set;}public double PositionY{get;set;} }
public sealed class FakeSegment
{
    public FakeSegment(Point a,Point b){StartNode=new FakeNode{PositionX=a.X,PositionY=a.Y};EndNode=new FakeNode{PositionX=b.X,PositionY=b.Y};}
    public int Type{get;set;}
    public double StartingControlPointX{get;set;}public double StartingControlPointY{get;set;}public double EndingControlPointX{get;set;}public double EndingControlPointY{get;set;}
    public FakeNode StartNode{get;}public FakeNode EndNode{get;}
}
