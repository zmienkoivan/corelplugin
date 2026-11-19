[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $env:TEMP 'VanyaTrayLayoutChecks'),[Alias('SteepLocksOnly')][switch]$LocksOnly,[ValidateRange(0,60)][double]$LockAngle=60)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xml.Linq
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$optionsType=$assembly.GetType('VanyaTools.Native.AcrylicTrayOptions',$true)
$patternType=$assembly.GetType('VanyaTools.Native.AcrylicTrayPattern',$true)
$layoutType=$assembly.GetType('VanyaTools.Native.AcrylicTrayCutLayout',$true)
$outputType=$assembly.GetType('VanyaTools.Native.AcrylicTrayOutput',$true)
$modeType=$assembly.GetType('VanyaTools.Native.AcrylicTrayFloorMode',$true)
$checks=0
function Check($ok,[string]$message) { if(-not $ok){throw $message};$script:checks++ }
function Near([double]$a,[double]$b,[double]$tolerance=0.001){Check ([Math]::Abs($a-$b)-le $tolerance) "Expected $b, got $a"}
function Options { [Activator]::CreateInstance($optionsType,$true) }
function Pattern($o){$patternType.GetMethod('Build').Invoke($null,@($o))}
function Layout($p){$layoutType.GetMethod('Build').Invoke($null,@($p))}
Add-Type -ReferencedAssemblies @([Windows.Media.Geometry].Assembly.Location,[Windows.Point].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
public static class TrayCutDistances {
    static Geometry Enlarged(Geometry g){var scaled=new GeometryGroup{Transform=new ScaleTransform(100,100)};scaled.Children.Add(g);return scaled;}
    // WPF can report zero fill area for a healthy small rounded part after
    // translation far from the origin. Measure at enlarged coordinates, as
    // with the fit checks, instead of relying on device-scale tessellation.
    public static double Area(Geometry g){return Enlarged(g).GetArea()/10000;}
    public static double OverlapArea(Geometry a,Geometry b){
        if(!a.Bounds.IntersectsWith(b.Bounds))return 0;
        return Geometry.Combine(Enlarged(a),Enlarged(b),GeometryCombineMode.Intersect,null,.001,ToleranceType.Absolute).GetArea()/10000;
    }
    static List<Point[]> Edges(Geometry geometry) {
        var result=new List<Point[]>();var flat=geometry.GetFlattenedPathGeometry(.0001,ToleranceType.Absolute);
        foreach(var f in flat.Figures){var points=new List<Point>{f.StartPoint};
            foreach(var segment in f.Segments){var line=segment as LineSegment;var poly=segment as PolyLineSegment;
                if(line!=null)points.Add(line.Point);if(poly!=null)points.AddRange(poly.Points);}
            if(flat.Transform!=null)for(int i=0;i<points.Count;i++)points[i]=flat.Transform.Transform(points[i]);
            for(int i=0;i<points.Count;i++)result.Add(new[]{points[i],points[(i+1)%points.Count]});}
        return result;
    }
    static double Cross(Vector a,Vector b){return a.X*b.Y-a.Y*b.X;}
    static double PointSegment(Point p,Point a,Point b){var v=b-a;double t=v.LengthSquared<1e-20?0:Math.Max(0,Math.Min(1,Vector.Multiply(p-a,v)/v.LengthSquared));return (p-(a+v*t)).LengthSquared;}
    public static double Between(Geometry a,Geometry b){double best=double.MaxValue;
        foreach(var ea in Edges(a))foreach(var eb in Edges(b)){
            var p=ea[0];var q=ea[1];var r=eb[0];var s=eb[1];
            double dx=Math.Max(0,Math.Max(Math.Min(p.X,q.X),Math.Min(r.X,s.X))-Math.Min(Math.Max(p.X,q.X),Math.Max(r.X,s.X)));
            double dy=Math.Max(0,Math.Max(Math.Min(p.Y,q.Y),Math.Min(r.Y,s.Y))-Math.Min(Math.Max(p.Y,q.Y),Math.Max(r.Y,s.Y)));
            if(dx*dx+dy*dy>=best)continue;
            if(Cross(q-p,r-p)*Cross(q-p,s-p)<=0 && Cross(s-r,p-r)*Cross(s-r,q-r)<=0 && dx==0 && dy==0)return 0;
            best=Math.Min(best,Math.Min(Math.Min(PointSegment(p,r,s),PointSegment(q,r,s)),Math.Min(PointSegment(r,p,q),PointSegment(s,p,q))));}
        return Math.Sqrt(best);
    }
}
'@
function Verify($p,$layout){
    Check ($layout.Parts.Count-eq $p.Parts.Count) 'Missing or duplicate cut pieces'
    Check ($layout.Parts[0].Part.Id-eq 'side-left') 'First side is not at the left edge of the top band'
    Check ($layout.Parts[$p.Options.Columns].Part.Id-eq 'side-right') 'Second side does not close the top band'
    $seen=@{}
    for($i=0;$i-lt $layout.Parts.Count;$i++){
        $place=$layout.Parts[$i];$part=$place.Part
        Check (-not $seen.ContainsKey($part.Id)) 'Duplicated part';$seen[$part.Id]=$true
        Near ([TrayCutDistances]::Area($place.Geometry)) $part.Geometry.GetArea() ([Math]::Max(0.05,$part.Geometry.GetArea()*0.000002))
        Check ($place.Bounds.Left-ge 2.999 -and $place.Bounds.Top-ge 2.999) 'Cut geometry outside page margin'
        Check ($place.Bounds.Right-le $layout.Width -and $place.Bounds.Bottom-le $layout.Height) 'Page clips cut geometry'
        if($part.Kind-eq 'long' -or $part.Kind-eq 'side'){Near $place.Bounds.Top 3;Check ($place.Rotation-eq 90 -or $place.Rotation-eq 270) 'Sloping part was not turned upright'}
        for($j=0;$j-lt $i;$j++){
            $other=$layout.Parts[$j]
            Check ([TrayCutDistances]::OverlapArea($place.Geometry,$other.Geometry)-lt 0.0001) "Parts overlap: $($part.Id) and $($other.Part.Id)"
            $distance=[TrayCutDistances]::Between($place.Geometry,$other.Geometry)
            Check ($distance-ge 2.998) "Clearance below 3 mm: $($part.Id) / $($other.Part.Id): $distance"
        }
    }
    $crosses=@($layout.Parts|Where-Object {$_.Part.Kind-eq 'cross'})
    $floors=@($layout.Parts|Where-Object {$_.Part.Kind-eq 'floor'})
    Check ($floors[0].Bounds.Top-gt $crosses[-1].Bounds.Bottom) 'Floor not below the strip stack'
    for($i=1;$i-lt $crosses.Count;$i++){Near ($crosses[$i].Bounds.Top-$crosses[$i-1].Bounds.Bottom) 3;Near $crosses[$i].Bounds.Left $crosses[0].Bounds.Left}
}
if(-not $LocksOnly){
foreach($angle in @(0,20,60)){
    $o=Options;$o.AngleDegrees=$angle;$p=Pattern $o;$layout=Layout $p;Verify $p $layout
}
$o=Options;$o.Columns=1;$o.Rows=1;$p=Pattern $o;Verify $p (Layout $p)
$o=Options;$o.FloorMode=[Enum]::Parse($modeType,'Steps');$p=Pattern $o;Verify $p (Layout $p)
$o=Options;$o.UseWedges=$true;$p=Pattern $o;Verify $p (Layout $p)
}
$o=Options;$o.UseWedges=$true;$o.AngleDegrees=$LockAngle;$p=Pattern $o;$layout=Layout $p;Verify $p $layout
if(-not $LocksOnly){
$o=Options;$o.Columns=18;$o.Rows=8;$o.BoardHeight=30;$o.Thickness=2
$p=Pattern $o;$layout=Layout $p;Verify $p $layout
$top=@($layout.Parts|Where-Object {$_.Part.Kind-eq 'side' -or $_.Part.Kind-eq 'long'})
$boxRowWidth=0;foreach($piece in $top){$boxRowWidth+=$piece.Bounds.Width}
$actualWidth=($top|ForEach-Object {$_.Bounds.Right}|Measure-Object -Maximum).Maximum-3
Check ($actualWidth-lt $boxRowWidth*0.8) 'Contour packing did not remove the triangular waste'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$cut=$outputType.GetMethod('Layout',[type[]]@($patternType,[bool])).Invoke($null,@($p,$false))
$map=$outputType.GetMethod('Layout',[type[]]@($patternType,[bool])).Invoke($null,@($p,$true))
$cut.Save((Join-Path $OutputDirectory 'reference-layout-cut.svg'));$map.Save((Join-Path $OutputDirectory 'reference-layout-map.svg'))
$svgNs=[Xml.Linq.XNamespace]'http://www.w3.org/2000/svg'
$cutPaths=@($cut.Root.Descendants($svgNs+'path'));$mapPaths=@($map.Root.Descendants($svgNs+'path'))
for($i=0;$i-lt $cutPaths.Count;$i++){Check ($cutPaths[$i].ToString()-ceq $mapPaths[$i].ToString()) 'Map and cutting file use different positions'}
Check (@($cut.Root.Descendants($svgNs+'text')).Count-eq 0) 'Cut file contains labels'

# Render exactly the placed red vectors for visual review, without using a
# separate packing or drawing implementation.
$scale=[Math]::Min(900/$layout.Width,1100/$layout.Height)
$visual=[Windows.Media.DrawingVisual]::new();$dc=$visual.RenderOpen()
$dc.DrawRectangle([Windows.Media.BrushConverter]::new().ConvertFromString('#3d3d3d'),$null,[Windows.Rect]::new(0,0,$layout.Width*$scale,$layout.Height*$scale))
$dc.PushTransform([Windows.Media.ScaleTransform]::new($scale,$scale))
foreach($piece in $layout.Parts){$dc.DrawGeometry($null,[Windows.Media.Pen]::new([Windows.Media.Brushes]::Red,0.5/$scale),$piece.Geometry)}
$dc.Pop();$dc.Close()
$bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new([int][Math]::Ceiling($layout.Width*$scale),[int][Math]::Ceiling($layout.Height*$scale),96,96,[Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($visual);$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream=[IO.File]::Create((Join-Path $OutputDirectory 'reference-layout.png'));try{$encoder.Save($stream)}finally{$stream.Dispose()}
Write-Output "Passed $checks cutting layout checks. Gap: 3 mm. Artifacts: $OutputDirectory"
