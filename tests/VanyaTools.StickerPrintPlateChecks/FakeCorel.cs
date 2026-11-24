using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
public sealed class PlateApp
{
    public string Failure;public PlateDocument Document;public object ActiveDocument=>Document;
    public List<PlateShape> Selected=new List<PlateShape>(),Temporary=new List<PlateShape>(),Outputs=new List<PlateShape>();
    public List<string> CapturedNames=new List<string>();public List<int> InputCounts=new List<int>();public bool Optimization;public PlateWindow ActiveWindow=new PlateWindow();public PlatePalettes Palettes=new PlatePalettes();
    public List<string> RequestedColors = new List<string>();
    public PlateRange ActiveSelectionRange=>new PlateRange(this,Selected.ToList());
    public PlateApp(){Document=new PlateDocument(this);Selected.Add(new PlateShape(this,Document.BaseLayer){Name="Artwork"});}
    public PlateRange CreateShapeRange()=>new PlateRange(this,new List<PlateShape>());
    public PlateColor CreateSpotColorByName(string id,string name,int tint)
    { RequestedColors.Add(name); return new PlateColor{IsSpot=Failure!="spot",SpotColorName=name,Tint=tint,RGBRed=Palettes.LegacyWhite&&name=="WHITE"?255:0,RGBBlue=Palettes.LegacyWhite&&name=="WHITE"?255:0}; }
}
public sealed class PlateDocument
{
    public PlateApp App;public int Unit=1,ReferencePoint=9,Groups;public bool Dirty;public PlateLayer BaseLayer,ActiveLayer;public PlatePage ActivePage;
    public PlateDocument(PlateApp app){App=app;BaseLayer=new PlateLayer(this);ActiveLayer=BaseLayer;ActivePage=new PlatePage(this);}
    public void BeginCommandGroup(string name){Groups++;}public void EndCommandGroup(){Groups--;}
    public PlateCurve CreateCurve()=>new PlateCurve(App);
}
public sealed class PlatePage
{
    public PlateDocument Document;public List<PlateLayer> Layers=new List<PlateLayer>();
    public PlatePage(PlateDocument doc){Document=doc;Layers.Add(doc.BaseLayer);}
    public PlateLayer CreateLayer(string name){var layer=new PlateLayer(Document){Name=name};Layers.Add(layer);Document.ActiveLayer=layer;Document.Dirty=true;return layer;}
}
public sealed class PlateLayer
{
    public PlateDocument Document;public string Name="Artwork";public bool Printable=true,Visible=true,Editable=true,Deleted;
    public bool IsSpecialLayer,Master;
    public PlateLayer Below {get{var layers=Document.ActivePage.Layers;int index=layers.IndexOf(this);return index>0?layers[index-1]:null;}}
    public void MoveBelow(PlateLayer other){if(Document.App.Failure=="layer-order")throw new InvalidOperationException("Synthetic layer order failure");var layers=Document.ActivePage.Layers;layers.Remove(this);layers.Insert(layers.IndexOf(other),this);}
    public PlateLayer(PlateDocument doc){Document=doc;}
    public void Activate(){Document.ActiveLayer=this;}
    public void Delete(){Deleted=true;foreach(var shape in Document.App.Outputs.Where(s=>s.Layer==this))shape.Delete();}
    public PlateShape CreateCurve(PlateCurve curve)
    {
        var points=curve.Paths.SelectMany(p=>p.Points).ToArray();var shape=new PlateShape(Document.App,this){Type=3,Curve=curve,LeftX=points.Min(p=>p.X),TopY=points.Max(p=>p.Y),SizeWidth=points.Max(p=>p.X)-points.Min(p=>p.X),SizeHeight=points.Max(p=>p.Y)-points.Min(p=>p.Y)};
        Document.App.Outputs.Add(shape);Document.Dirty=true;return shape;
    }
}
public sealed class PlateCurve
{
    private readonly PlateApp app;
    public PlateCurve(PlateApp owner=null){app=owner;}
    public List<PlateSubPath> Paths=new List<PlateSubPath>();
    public PlateSubPath CreateSubPath(double x,double y){var path=new PlateSubPath();path.Points.Add(new Point(x,y));Paths.Add(path);return path;}
    public PlateNodes Nodes=>new PlateNodes{Count=Paths.Sum(p=>p.Points.Count)};
    public bool Closed=>Paths.Count>0 && Paths.All(p=>p.Closed);
    public double Area=>Geometry.GetArea();
    public PathGeometry Geometry
    {
        get
        {
            var g=new PathGeometry{FillRule=FillRule.EvenOdd};
            foreach(var path in Paths){var f=new PathFigure{StartPoint=path.Points[0],IsClosed=path.Closed,IsFilled=true};f.Segments.Add(new PolyLineSegment(path.Points.Skip(1),true));g.Figures.Add(f);}return g;
        }
    }
    public PlateCurve Contour(double offset,int direction,int cap,int corner,double miter)
    {
        if(app?.Failure=="contour")throw new InvalidOperationException("Synthetic native contour failure");
        if(offset<=0 || (direction!=0 && direction!=1) || cap!=1 || corner!=2 || miter!=0)throw new Exception("Wrong native contour parameters");
        var g=Geometry;var pen=new Pen(Brushes.Black,offset*2){LineJoin=PenLineJoin.Round};
        var rim=g.GetWidenedPathGeometry(pen,.0025,ToleranceType.Absolute);
        var output=System.Windows.Media.Geometry.Combine(g,rim,direction==1?GeometryCombineMode.Union:GeometryCombineMode.Exclude,null,.0025,ToleranceType.Absolute).GetFlattenedPathGeometry(.0025,ToleranceType.Absolute);
        var result=new PlateCurve(app);
        foreach(var figure in output.Figures){var path=result.CreateSubPath(figure.StartPoint.X,figure.StartPoint.Y);path.Closed=figure.IsClosed;foreach(var segment in figure.Segments.OfType<PolyLineSegment>())path.Points.AddRange(segment.Points);}return result;
    }
}
public sealed class PlateNodes{public int Count;}
public sealed class PlateSubPath{public List<Point> Points=new List<Point>();public bool Closed;public void AppendLineSegment(double x,double y){Points.Add(new Point(x,y));}}
public sealed class PlateShape
{
    public PlateApp App;public PlateLayer Layer;public int Type=5;public string Name;public bool Deleted;public double LeftX=10,TopY=50,SizeWidth=40,SizeHeight=30;public double BottomY=>TopY-SizeHeight;
    public PlateFill Fill;public PlateOutline Outline=new PlateOutline();public List<PlateShape> Children=new List<PlateShape>();public PlateCollection Shapes=>new PlateCollection(Children.Where(s=>!s.Deleted).ToList());
    public object PowerClip=>null;public PlateBitmap Bitmap;public PlateCurve Curve;private bool overprint;
    public bool OverprintFill{get=>overprint;set{if(App.Failure!="overprint")overprint=value;}}
    public PlateShape(PlateApp app,PlateLayer layer){App=app;Layer=layer;Fill=new PlateFill(app);}
    public void Delete(){Deleted=true;foreach(var child in Children)child.Delete();App.Document.Dirty=true;}
    public void CreateSelection(){App.Selected.Clear();App.Selected.Add(this);}public void AddToSelection(){if(!App.Selected.Contains(this))App.Selected.Add(this);}
    public PlateShape Clone(){var clone=new PlateShape(App,Layer){Type=Type,Name=Name,Outline=Outline,LeftX=LeftX,TopY=TopY,SizeWidth=SizeWidth,SizeHeight=SizeHeight};clone.Fill.Type=Fill.Type;clone.Fill.UniformColor=Fill.UniformColor;clone.Children=Children.Select(s=>s.Clone()).ToList();App.Temporary.Add(clone);return clone;}
    private void Record(PlateShape shape){if(shape.Deleted)return;if(shape.Type==7){foreach(var child in shape.Children)Record(child);}else App.CapturedNames.Add(shape.Name);}
    public PlateShape ConvertToBitmapEx(int model,bool transparent,bool aa,int dpi,int quality,bool profile)
    {
        Record(this);Delete();var raster=new PlateShape(App,Layer){Bitmap=new PlateBitmap(App),Name="capture"};App.Temporary.Add(raster);return raster;
    }
}
public sealed class PlateFill
{
    private PlateApp app;public int Type;public PlateColor UniformColor=new PlateColor();
    public PlateFill(PlateApp a){app=a;}public void ApplyUniformFill(PlateColor color){if(app.Failure!="fill"){Type=1;UniformColor=color;}}
}
public sealed class PlateOutline{public int Type;public PlateColor Color=new PlateColor();public void SetNoOutline(){Type=0;}}
public sealed class PlateColor
{
    public bool IsSpot;public string SpotColorName="";public int Tint=100;
    public int RGBRed,RGBGreen=255,RGBBlue;
    public PlateColor GetCopy() => (PlateColor)MemberwiseClone();
    public void ConvertToRGB(){IsSpot=false;}
}
public sealed class PlateCollection:IEnumerable
{
    public List<PlateShape> Values;public int Count=>Values.Count;public PlateShape this[int index]=>Values[index-1];
    public PlateCollection(List<PlateShape> values){Values=values;}public IEnumerator GetEnumerator()=>Values.GetEnumerator();
}
public sealed class PlateRange
{
    public PlateApp App;public List<PlateShape> Values;public PlateCollection Shapes=>new PlateCollection(Values.Where(s=>!s.Deleted).ToList());public int Count=>Shapes.Count;
    public PlateRange(PlateApp app,List<PlateShape> values){App=app;Values=values;}public void Add(PlateShape shape){Values.Add(shape);}
    public void GetBoundingBox(out double x,out double y,out double w,out double h,bool outline){x=10;y=20;w=40;h=30;}
    public PlateRange Duplicate(double x,double y){App.InputCounts.Add(Count);return new PlateRange(App,App.Failure=="duplicate"?new List<PlateShape>():Values.Select(s=>s.Clone()).ToList());}
    public PlateShape Group(){var group=new PlateShape(App,App.Document.ActiveLayer){Type=7,Children=Values.ToList()};App.Temporary.Add(group);return group;}
}
public sealed class PlateBitmap
{
    public bool Transparent=>true;public PlateImage Image,ImageAlpha;
    public PlateBitmap(PlateApp app){Image=new PlateImage(false,false);ImageAlpha=new PlateImage(true,app.Failure=="alpha");}
}
public sealed class PlateImage
{
    public int Width=80,Height=60;public PlateTiles Tiles;
    public PlateImage(bool alpha,bool empty){Tiles=new PlateTiles(alpha,empty);}
}
public sealed class PlateTiles
{
    public int Count;public PlateTile Value;public PlateTileIndex Item=>new PlateTileIndex(Value);
    public PlateTiles(bool alpha,bool empty){Count=empty?0:1;Value=new PlateTile(alpha);}
}
public sealed class PlateTileIndex{private PlateTile value;public PlateTileIndex(PlateTile tile){value=tile;}public PlateTile this[int index]=>value;}
public sealed class PlateTile
{
    public int Left=0,Top=59,Width=80,Height=60,BytesPerLine,BytesPerPixel;public byte[] PixelData;
    public PlateTile(bool alpha)
    {
        BytesPerPixel=alpha?1:3;BytesPerLine=80*BytesPerPixel;PixelData=new byte[BytesPerLine*60];
        for(int y=0;y<60;y++)for(int x=0;x<80;x++)for(int c=0;c<BytesPerPixel;c++)PixelData[y*BytesPerLine+x*BytesPerPixel+c]=alpha?(byte)(x>=10 && x<70 && y>=10 && y<50?255:0):(byte)180;
    }
}
public sealed class PlateWindow{public PlateView ActiveView=new PlateView();public void Refresh(){}}
public sealed class PlateView{public bool SimulateOverprints;}
public sealed class PlatePalette
{
    public string Identifier="50af2116-63b7-4f63-8baa-b6bdd41010eb";
    public bool IsOpen, IgnoreOpen;
    public int OpenCalls,CloseCalls;
    public void Open() { OpenCalls++; if (!IgnoreOpen) IsOpen = true; }
    public void Close(){CloseCalls++;IsOpen=false;}
}
public sealed class PlatePalettes
{
    public bool Registered = true;
    public bool LegacyWhite,IgnoreRefresh;
    public int FileOpens;
    public string LastPath;
    public PlatePalette Palette = new PlatePalette();
    public int Count => Registered ? 1 : 0;
    public PlatePalette this[int i] => Palette;
    public PlatePalette Open(string path) { FileOpens++; LastPath = path; Registered = true;if(!IgnoreRefresh)LegacyWhite=false;return Palette; }
}
