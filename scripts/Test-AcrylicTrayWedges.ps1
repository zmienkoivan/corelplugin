[CmdletBinding()]
param([string]$OutputDirectory=(Join-Path $env:TEMP 'VanyaTrayWedgeChecks'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Xml.Linq
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$optionsType=$assembly.GetType('VanyaTools.Native.AcrylicTrayOptions',$true)
$patternType=$assembly.GetType('VanyaTools.Native.AcrylicTrayPattern',$true)
$modelType=$assembly.GetType('VanyaTools.Native.AcrylicTrayAssembly3D',$true)
$layoutType=$assembly.GetType('VanyaTools.Native.AcrylicTrayCutLayout',$true)
$outputType=$assembly.GetType('VanyaTools.Native.AcrylicTrayOutput',$true)
$tabType=$assembly.GetType('VanyaTools.Native.AcrylicTrayTab',$true)
$modeType=$assembly.GetType('VanyaTools.Native.AcrylicTrayFloorMode',$true)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$checks=0
function Check($value,[string]$message){if(-not $value){throw $message};$script:checks++}
function Near([double]$a,[double]$b,[double]$tolerance=0.002){Check ([Math]::Abs($a-$b)-le $tolerance) "Expected $b, got $a"}
function Options{$o=[Activator]::CreateInstance($optionsType,$true);$o.UseWedges=$true;$o}
function Build($o){$patternType.GetMethod('Build').Invoke($null,@($o))}
function Vertices($key,[double]$thickness,[Windows.Point[]]$outline){
    $vertices=[Collections.Generic.List[Windows.Media.Media3D.Point3D]]::new()
    foreach($w in @((-$thickness/2),($thickness/2))){foreach($point in $outline){$vertices.Add($key.Point($point.X,$point.Y,$w))}}
    ,$vertices.ToArray()
}
Add-Type -ReferencedAssemblies @([Windows.Media.Geometry].Assembly.Location,[Windows.Point].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
public static class TrayWedgeFit {
    static List<int[]> Edges(int vertexCount){var edges=new List<int[]>();int n=vertexCount/2;
        for(int i=0;i<n;i++){edges.Add(new[]{i,(i+1)%n});edges.Add(new[]{i+n,(i+1)%n+n});edges.Add(new[]{i,i+n});}return edges;}
    static double Cross(Point a,Point b,Point c){return (b.X-a.X)*(c.Y-a.Y)-(b.Y-a.Y)*(c.X-a.X);}
    static Geometry Hull(List<Point> points){
        points.Sort((a,b)=>a.X==b.X?a.Y.CompareTo(b.Y):a.X.CompareTo(b.X));
        var h=new List<Point>();foreach(var p in points){while(h.Count>=2 && Cross(h[h.Count-2],h[h.Count-1],p)<=1e-9)h.RemoveAt(h.Count-1);h.Add(p);}
        int lower=h.Count;for(int i=points.Count-2;i>=0;i--){var p=points[i];while(h.Count>lower && Cross(h[h.Count-2],h[h.Count-1],p)<=1e-9)h.RemoveAt(h.Count-1);h.Add(p);}
        if(h.Count<4)return Geometry.Empty;h.RemoveAt(h.Count-1);
        var g=new StreamGeometry();using(var c=g.Open()){c.BeginFigure(h[0],true,true);for(int i=1;i<h.Count;i++)c.LineTo(h[i],true,false);}return g;
    }
    // Clip the actual convex wedge prism against a structural sheet's finite
    // thickness, then intersect its projected footprint with the cut material.
    // This catches collisions through either face of an inclined acrylic sheet.
    public static double Collision(Point3D[] prism,Geometry sheet,int kind,double center,double t,double y0,double z0,double cos,double sin){
        Func<Point3D,double> n=p=>kind==0?p.X:kind==1?p.Y:(p.Y-y0)*sin-(p.Z-z0)*cos;
        Func<Point3D,Point> uv=p=>kind==0?new Point(p.Y,-p.Z):kind==1?new Point(p.X,-p.Z):new Point(p.X,(p.Y-y0)*cos+(p.Z-z0)*sin);
        double lo=kind==2?0:center-t/2,hi=kind==2?t:center+t/2;
        double minNormal=double.MaxValue,maxNormal=double.MinValue;
        foreach(var p in prism){double d=n(p);minNormal=Math.Min(minNormal,d);maxNormal=Math.Max(maxNormal,d);}
        if(maxNormal<=lo+1e-8 || minNormal>=hi-1e-8)return 0; // Touching faces have no intersecting volume.
        var points=new List<Point>();foreach(var p in prism)if(n(p)>=lo-1e-8 && n(p)<=hi+1e-8)points.Add(uv(p));
        foreach(var edge in Edges(prism.Length)){var a=prism[edge[0]];var b=prism[edge[1]];double na=n(a),nb=n(b);
            if(Math.Abs(nb-na)<1e-12)continue;
            foreach(double plane in new[]{lo,hi}){double s=(plane-na)/(nb-na);if(s>=-1e-8 && s<=1+1e-8)points.Add(uv(a+(b-a)*s));}}
        if(points.Count<3)return 0;
        var aScaled=new GeometryGroup{Transform=new ScaleTransform(100,100)};aScaled.Children.Add(Hull(points));
        var bScaled=new GeometryGroup{Transform=new ScaleTransform(100,100)};bScaled.Children.Add(sheet);
        return Geometry.Combine(aScaled,bScaled,GeometryCombineMode.Intersect,null,.001,ToleranceType.Absolute).GetArea()/10000;
    }
    public static bool WedgesOverlap(Point3D[] a,Point3D[] b){
        var ba=Rect3D.Empty;var bb=Rect3D.Empty;foreach(var p in a)ba.Union(p);foreach(var p in b)bb.Union(p);
        if(!ba.IntersectsWith(bb))return false;
        var axes=new List<Vector3D>();foreach(var p in new[]{a,b}){
            int n=p.Length/2;var thick=p[n]-p[0];axes.Add(thick);
            for(int i=0;i<n;i++)axes.Add(Vector3D.CrossProduct(p[(i+1)%n]-p[i],thick));}
        foreach(var ea in Edges(a.Length))foreach(var eb in Edges(b.Length))axes.Add(Vector3D.CrossProduct(a[ea[1]]-a[ea[0]],b[eb[1]]-b[eb[0]]));
        foreach(var axis0 in axes){if(axis0.LengthSquared<1e-14)continue;var axis=axis0;axis.Normalize();
            double amin=double.MaxValue,amax=double.MinValue,bmin=double.MaxValue,bmax=double.MinValue;
            foreach(var p in a){double d=Vector3D.DotProduct((Vector3D)p,axis);amin=Math.Min(amin,d);amax=Math.Max(amax,d);}
            foreach(var p in b){double d=Vector3D.DotProduct((Vector3D)p,axis);bmin=Math.Min(bmin,d);bmax=Math.Max(bmax,d);}
            if(amax<=bmin+.0001 || bmax<=amin+.0001)return false;}
        return true;
    }
    public static double PartsArea(Point[][] pieces){Geometry union=null;
        foreach(var points in pieces){var g=new StreamGeometry();using(var c=g.Open()){c.BeginFigure(points[0],true,true);for(int i=1;i<points.Length;i++)c.LineTo(points[i],true,false);}
            union=union==null?(Geometry)g:Geometry.Combine(union,g,GeometryCombineMode.Union,null,.0001,ToleranceType.Absolute);}
        return union.GetArea();
    }
    public static double SurfaceArea(MeshGeometry3D mesh){double area=0;
        for(int i=0;i<mesh.TriangleIndices.Count;i+=3){var a=mesh.Positions[mesh.TriangleIndices[i]];var b=mesh.Positions[mesh.TriangleIndices[i+1]];var c=mesh.Positions[mesh.TriangleIndices[i+2]];
            area+=Vector3D.CrossProduct(b-a,c-a).Length/2;}return area;
    }
    public static int Islands(Geometry shape){
        var flat=shape.GetFlattenedPathGeometry(.0001,ToleranceType.Absolute);var areas=new List<double>();
        foreach(var f in flat.Figures){var points=new List<Point>{f.StartPoint};foreach(var s in f.Segments){if(s is PolyLineSegment)points.AddRange(((PolyLineSegment)s).Points);else if(s is LineSegment)points.Add(((LineSegment)s).Point);}
            double area=0;for(int i=0;i<points.Count;i++){var a=points[i];var b=points[(i+1)%points.Count];area+=a.X*b.Y-b.X*a.Y;}areas.Add(area);}
        double largest=0;foreach(double a in areas)if(Math.Abs(a)>Math.Abs(largest))largest=a;int count=0;foreach(double a in areas)if(a*largest>1e-8)count++;return count;
    }
}
'@
function Verify($p){
    $model=$modelType.GetMethod('Build').Invoke($null,@($p));$t=$p.Options.Thickness
    Check ($model.Parts.Count-eq $p.Parts.Count) 'Missing assembled wedge or panel'
    Near $model.Bounds.SizeX $p.OverallWidth;Near $model.Bounds.SizeY $p.OverallDepth
    Check ($model.Bounds.Z-ge -0.001) 'Fastener passes through the table'
    foreach($part in $p.Parts){Check ([TrayWedgeFit]::Islands($part.AssemblyGeometry)-eq 1) "Disconnected cut material: $($part.Id)"}
    $vertices=@();foreach($key in $p.Wedges){$pieces=@();foreach($loop in $key.ConvexPieces){$pieces+=,(Vertices $key $t $loop)};$vertices+=,$pieces}
    for($i=0;$i-lt $p.Wedges.Count;$i++){
        $key=$p.Wedges[$i];Check ($key.Opening.GetArea()-gt 0) 'Missing locking window'
        Near ([TrayWedgeFit]::PartsArea($key.ConvexPieces)-$key.GripOpening.GetArea()) $key.Shape.GetArea() 0.02
        $keyMesh=$model.Parts|Where-Object Id -eq $key.Id
        Near ([TrayWedgeFit]::SurfaceArea($keyMesh.Faces)) (2*$key.Shape.GetArea()) 0.15
        Near $key.HeadWidth ($p.Options.RetainerWindowLength+2*$t)
        Check ($key.StemLength-ge $key.PassingThickness+0.8*$t-0.002) 'Stem is too short for its receiving panel'
        Near $key.Shape.Bounds.Width ($key.StemLength+$key.HeadLength)
        Near $key.HeadLength (2*$t+$key.GripHeight)
        if($key.Side){Near $key.Shape.Bounds.Width (3.8*$t+$key.GripHeight)}
        Check ($key.Shape.GetFlattenedPathGeometry().Figures.Count-eq 2) 'Missing closed grip window or unexpected cuts'
        Check ($key.GripOpening.GetArea()-gt 0) 'Grip window has no area'
        $grip=$key.GripOpening.Bounds
        Near $grip.Width $key.GripHeight;Near $grip.Height $key.GripWidth
        Check (-not $key.Shape.FillContains([Windows.Point]::new($grip.X+$grip.Width/2,$grip.Y+$grip.Height/2))) 'Grip window is filled'
        $stemCenter=($key.ConvexPieces[0][0].X+$key.ConvexPieces[0][1].X)/2
        Check ($key.Shape.FillContains([Windows.Point]::new($stemCenter,$key.HeadWidth/2))) 'Grip window cuts into the stem'
        Check ($key.HeadWidth-gt $key.NeckWidth+0.8*$t) 'Head has no gripping shoulders'
        Check ($key.NeckWidth-gt $key.TipWidth -and $key.NeckWidth-$key.TipWidth-lt 1.3) 'Stem must widen gently under the head'
        Check ($key.ConvexPieces[0][1].X-ge $key.PassingThickness/2+0.4*$t-0.001) 'Stem does not exit the far face'
        $start=$key.ConvexPieces[0][0].X;$end=$key.ConvexPieces[0][1].X
        $frontWidth=$key.NeckWidth-($key.NeckWidth-$key.TipWidth)*(-$key.PassingThickness/2-$start)/($end-$start)
        Near $frontWidth ($p.Options.RetainerWindowLength-0.01)
        if($p.Angle-eq 0){
            Near $key.Opening.Bounds.Width $(if($key.Side){$t+$p.Options.RetainerThicknessClearance}else{$p.Options.RetainerWindowLength})
            Near $key.Opening.Bounds.Height $(if($key.Side){$p.Options.RetainerWindowLength}else{$t+$p.Options.RetainerThicknessClearance})
        }
        if($key.Side){Near ([Math]::Abs($key.W.X)) 1;Near $key.U.X 0;Near $key.V.X 0}
        else {
            $f=$p.Floors[$key.FloorIndex]
            $normal=[Windows.Media.Media3D.Vector3D]::new(0,$f.Sine,(-$f.Cosine))
            Near ([Windows.Media.Media3D.Vector3D]::DotProduct($key.W,$normal)) 1
            Near ([Windows.Media.Media3D.Vector3D]::DotProduct($key.U,$normal)) 0
            Near ([Windows.Media.Media3D.Vector3D]::DotProduct($key.V,$normal)) 0
            $depths=@();foreach($loop in $key.ConvexPieces){foreach($point in (Vertices $key $t $loop)){
                $depths+=($point.Y-$f.StartY)*$f.Sine-($point.Z-$f.FrontZ)*$f.Cosine
            }}
            Near (($depths|Measure-Object -Minimum).Minimum) ($t+$p.Options.RetainerThicknessClearance/2)
            Near (($depths|Measure-Object -Maximum).Maximum) (2*$t+$p.Options.RetainerThicknessClearance/2)
        }
        for($j=0;$j-lt $i;$j++){foreach($pa in $vertices[$i]){foreach($pb in $vertices[$j]){
            Check (-not [TrayWedgeFit]::WedgesOverlap($pa,$pb)) "Keys collide: $($key.Id) / $($p.Wedges[$j].Id)"
        }}}
        $long=0;$cross=0;$floor=0
        foreach($part in $p.Parts){
            if($part.Kind-eq 'wedge'){continue}
            $kind=0;$center=0;$fy=0;$fz=0;$cos=1;$sin=0
            if($part.Kind-eq 'side'){$center=if($part.Id-eq 'side-left'){- $t/2}else{$p.InnerWidth+$t/2}}
            elseif($part.Kind-eq 'long'){$center=$p.Longitudinals[$long];$long++}
            elseif($part.Kind-eq 'cross'){$kind=1;$center=$p.Crosses[$cross].Y;$cross++}
            else{$kind=2;$f=$p.Floors[$floor];$floor++;$fy=$f.StartY;$fz=$f.FrontZ;$cos=$f.Cosine;$sin=$f.Sine}
            foreach($piece in $vertices[$i]){
                $area=[TrayWedgeFit]::Collision($piece,$part.AssemblyGeometry,$kind,$center,$t,$fy,$fz,$cos,$sin)
                Check ($area-lt 0.003) "Key hits panel: $($key.Id) / $($part.Id), area $area; t=$t angle=$($p.Angle)"
            }
        }
    }
}
$o=Options;$p=Build $o;Check ($p.Wedges.Count-eq 18) 'Wrong default lock count'
foreach($thickness in @(2,3)){foreach($angle in @(0,20,45,60)){$o=Options;$o.Thickness=$thickness;$o.AngleDegrees=$angle;Verify (Build $o)}}
foreach($case in @(@(2,28),@(3,35))){$o=Options;$o.Thickness=$case[0];$o.CellWidth=$case[1];Verify (Build $o)}
$o=Options;$o.RetainerTabWidth=18;$o.RetainerWindowLength=8;$o.RetainerThicknessClearance=0.3;Verify (Build $o)
$o=Options;$o.RetainerThicknessClearance=0;Verify (Build $o)
$o=Options;$o.Columns=1;$o.Rows=1;Verify (Build $o)
foreach($rise in @(0,15)){$o=Options;$o.FloorMode=[Enum]::Parse($modeType,'Steps');$o.RowRise=$rise;Verify (Build $o)}
foreach($interval in @(1,20)){$o=Options;$o.AngleDegrees=60;$o.WedgeEvery=$interval;Verify (Build $o)}
$o=Options;$o.Columns=8;$o.Rows=7;$p=Build $o
foreach($parent in @($p.Wedges|Select-Object -ExpandProperty ParentId -Unique)){
    $items=@($p.Wedges|Where-Object ParentId -eq $parent)
    if($parent.StartsWith('divider-')){Check (@($items|Where-Object TabIndex -eq 0).Count-eq 1) 'Front end unlocked';Check (@($items|Where-Object TabIndex -eq 6).Count-eq 1) 'Rear end unlocked'}
    if($parent.StartsWith('cross-')){Check (@($items|Where-Object TabIndex -eq 0).Count-eq 1) 'Left end unlocked';Check (@($items|Where-Object TabIndex -eq 7).Count-eq 1) 'Right end unlocked'}
}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$p=Build (Options)
$folder=$outputType.GetMethod('Save').Invoke($null,@($OutputDirectory,$p))
[xml]$manifestSvg=Get-Content -LiteralPath (Join-Path $folder '01-cut.svg') -Raw
$ns=@{s='http://www.w3.org/2000/svg'}
Check ((Select-Xml -Xml $manifestSvg -XPath '//s:path' -Namespace $ns).Count-eq $p.Parts.Count) 'Wedges missing from cutting file'
$tab=[Activator]::CreateInstance($tabType,[object[]]@([Action[string,bool]]{param($m,$e)}))
function Field([string]$name){$tabType.GetField($name,$flags).GetValue($tab)}
function Refresh{$tabType.GetMethod('Update',$flags).Invoke($tab,@())|Out-Null}
(Field '_wedges').IsChecked=$true;Refresh
Check ((Field '_create').IsEnabled) 'Wedge UI mode failed'
Check ((Field '_scene').Assembly.Parts.Count-eq 28) '3D preview omitted keys'
$scene=Field '_scene';$scene.Height=420;$scene.Measure([Windows.Size]::new(720,420));$scene.Arrange([Windows.Rect]::new(0,0,720,420));$scene.View(2)
function SavePng([string]$name){$bitmap=$scene.RenderPng(1000);$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap));$stream=[IO.File]::Create((Join-Path $OutputDirectory $name));try{$encoder.Save($stream)}finally{$stream.Dispose()}}
SavePng 'wedge-side.png';$scene.GetType().GetField('_pitch',$flags).SetValue($scene,-35.0);$scene.Fit();SavePng 'wedges-underneath.png'
# Draw the actual cut profile upright, with no independent illustration shape.
$profile=$p.Wedges[0].Shape.Clone();$transform=[Windows.Media.TransformGroup]::new();$transform.Children.Add([Windows.Media.RotateTransform]::new(90));$profile.Transform=$transform
$bounds=$profile.Bounds;$scale=26.0;$drawing=[Windows.Media.DrawingVisual]::new();$dc=$drawing.RenderOpen()
$dc.DrawRectangle([Windows.Media.Brushes]::White,$null,[Windows.Rect]::new(0,0,600,340))
$dc.PushTransform([Windows.Media.TranslateTransform]::new(300-($bounds.X+$bounds.Width/2)*$scale,35-$bounds.Y*$scale));$dc.PushTransform([Windows.Media.ScaleTransform]::new($scale,$scale))
$dc.DrawGeometry([Windows.Media.BrushConverter]::new().ConvertFromString('#d3b57a'),[Windows.Media.Pen]::new([Windows.Media.Brushes]::SaddleBrown,0.06),$profile);$dc.Pop();$dc.Pop()
$caption=[Windows.Media.FormattedText]::new('3 mm acrylic | 20 mm tab | 3.2 x 10 mm window | rigid broad stem',[Globalization.CultureInfo]::InvariantCulture,[Windows.FlowDirection]::LeftToRight,[Windows.Media.Typeface]::new('Segoe UI'),14,[Windows.Media.Brushes]::DarkSlateGray)
$dc.DrawText($caption,[Windows.Point]::new(40,300));$dc.Close()
$bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(600,340,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($drawing)
$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap));$stream=[IO.File]::Create((Join-Path $OutputDirectory 'mushroom-profile.png'));try{$encoder.Save($stream)}finally{$stream.Dispose()}
(Field '_wedgeEvery').Text='0';Refresh;Check (-not (Field '_create').IsEnabled) 'Invalid lock interval accepted'
(Field '_retainerTabWidth').Text='14';(Field '_wedgeEvery').Text='3';Refresh
Check (-not (Field '_create').IsEnabled) 'Window leaves insufficient material in the tab'
(Field '_wedges').IsChecked=$false;Refresh;Check ((Field '_create').IsEnabled) 'Inactive lock interval blocks ordinary mode'
(Field '_timer').Stop()
Write-Output "Passed $checks wedge-fit checks. Prototype bundle: $folder"
