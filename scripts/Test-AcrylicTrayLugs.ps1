[CmdletBinding()]
param([string]$OutputDirectory=(Join-Path $env:TEMP 'VanyaTrayLugChecks'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase,System.Web.Extensions
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$optionsType=$assembly.GetType('VanyaTools.Native.AcrylicTrayOptions',$true)
$patternType=$assembly.GetType('VanyaTools.Native.AcrylicTrayPattern',$true)
$checks=0
function Check($ok,[string]$message){if(-not $ok){throw $message};$script:checks++}
function Near([double]$a,[double]$b){Check ([Math]::Abs($a-$b)-lt 0.003) "Expected $b, got $a"}
function Build([double]$t,[double]$a){$o=[Activator]::CreateInstance($optionsType,$true);$o.UseWedges=$true;$o.Thickness=$t;$o.AngleDegrees=$a;$patternType.GetMethod('Build').Invoke($null,@($o))}
Add-Type -ReferencedAssemblies @([Windows.Media.Geometry].Assembly.Location,[Windows.Point].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
public static class LugMaterialWeb {
    static List<Point> Points(PathFigure f){var p=new List<Point>{f.StartPoint};foreach(var s in f.Segments){if(s is PolyLineSegment)p.AddRange(((PolyLineSegment)s).Points);else if(s is LineSegment)p.Add(((LineSegment)s).Point);}return p;}
    static double Area(List<Point> p){double a=0;for(int i=0;i<p.Count;i++){var q=p[(i+1)%p.Count];a+=p[i].X*q.Y-q.X*p[i].Y;}return Math.Abs(a)/2;}
    static double Distance(Point p,Point a,Point b){var d=b-a;double s=d.LengthSquared<1e-18?0:Math.Max(0,Math.Min(1,Vector.Multiply(p-a,d)/d.LengthSquared));return (p-(a+d*s)).Length;}
    public static double Minimum(Geometry sheet,Geometry opening){
        List<Point> outer=null;double largest=0;
        foreach(var f in sheet.GetFlattenedPathGeometry(.0001,ToleranceType.Absolute).Figures){var points=Points(f);double a=Area(points);if(a>largest){largest=a;outer=points;}}
        var hole=Points(opening.GetFlattenedPathGeometry(.0001,ToleranceType.Absolute).Figures[0]);
        double min=double.MaxValue;
        for(int i=0;i<outer.Count;i++)for(int j=0;j<hole.Count;j++){
            var a=outer[i];var b=outer[(i+1)%outer.Count];var c=hole[j];var d=hole[(j+1)%hole.Count];
            min=Math.Min(min,Math.Min(Math.Min(Distance(a,c,d),Distance(b,c,d)),Math.Min(Distance(c,a,b),Distance(d,a,b))));}
        return min;
    }
}
'@
$sizes=@()
foreach($t in @(2,3)){
    foreach($angle in @(0,20,45,60)){
        $p=Build $t $angle;$hole=$p.SideLugOpeningBounds()
        Near $p.SideLugProjection (2*$t+$p.Options.RetainerThicknessClearance);Near $p.SideLugWidth 20
        Near $p.SideLugRadius ($t/2);Near $hole.Width ($t+$p.Options.RetainerThicknessClearance);Near $hole.Height 10
        foreach($key in $p.Wedges){
            $parent=$p.Parts|Where-Object Id -eq $key.ParentId
            $web=[LugMaterialWeb]::Minimum($parent.AssemblyGeometry,$key.Opening)
            Check ($web-ge $t-0.02) "Thin free-edge web at $($key.Id): $web mm; material $t, angle $angle"
            $headWeb=[LugMaterialWeb]::Minimum($key.Shape,$key.GripOpening)
            Check ($headWeb-ge $t-0.02) "Thin grip-window web at $($key.Id): $headWeb mm; material $t, angle $angle"
        }
    }
    $sizes+= [pscustomobject]@{thicknessMm=$t;projectionMm=$p.SideLugProjection;widthMm=$p.SideLugWidth;openingWidthMm=$hole.Width;openingHeightMm=$hole.Height;radiusMm=$p.SideLugRadius;nominalWebMm=$t}
}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$sizes|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'lug-dimensions.json') -Encoding UTF8
$p=Build 3 20;$t=$p.Options.Thickness;$w=$p.SideLugWidth;$length=$p.SideLugLength;$hole=$p.SideLugOpeningBounds()
$ear=$p.SideLugCutout();$core=[Windows.Media.RectangleGeometry]::new([Windows.Rect]::new(-3,-$w/2-1.5,3.02,$w+3))
$body=[Windows.Media.Geometry]::Combine($core,$ear,[Windows.Media.GeometryCombineMode]::Union,$null,0.001,[Windows.Media.ToleranceType]::Absolute)
$drawing=[Windows.Media.DrawingVisual]::new();$dc=$drawing.RenderOpen();$ox=130.0;$oy=250.0;$scale=[Math]::Min(26.0,300.0/$w)
$ink=[Windows.Media.BrushConverter]::new().ConvertFromString('#315f76');$pen=[Windows.Media.Pen]::new($ink,1.2)
$light=[Windows.Media.Pen]::new([Windows.Media.Brushes]::Gray,0.7)
function P([double]$u,[double]$v){[Windows.Point]::new($ox+$u*$scale,$oy+$v*$scale)}
function Fmt([double]$v){$v.ToString('0.#',[Globalization.CultureInfo]::GetCultureInfo('ru-RU'))}
function Text([string]$s,[double]$x,[double]$y,[double]$size=16){
    $text=[Windows.Media.FormattedText]::new($s,[Globalization.CultureInfo]::GetCultureInfo('ru-RU'),[Windows.FlowDirection]::LeftToRight,[Windows.Media.Typeface]::new('Segoe UI'),$size,$ink)
    $dc.DrawText($text,[Windows.Point]::new($x,$y))
}
function Dim($a,$b,[string]$label,[double]$dx,[double]$dy){
    $dc.DrawLine($pen,$a,$b);$v=$b-$a;$v.Normalize();$n=[Windows.Vector]::new(-$v.Y,$v.X)
    $dc.DrawLine($pen,$a,$a+$v*6+$n*3);$dc.DrawLine($pen,$a,$a+$v*6-$n*3)
    $dc.DrawLine($pen,$b,$b-$v*6+$n*3);$dc.DrawLine($pen,$b,$b-$v*6-$n*3)
    Text $label (($a.X+$b.X)/2+$dx) (($a.Y+$b.Y)/2+$dy)
}
$dc.DrawRectangle([Windows.Media.Brushes]::White,$null,[Windows.Rect]::new(0,0,600,540))
Text 'Боковое ушко · акрил 3 мм' 25 14 21
$dc.PushTransform([Windows.Media.TranslateTransform]::new($ox,$oy));$dc.PushTransform([Windows.Media.ScaleTransform]::new($scale,$scale))
$dc.DrawGeometry([Windows.Media.BrushConverter]::new().ConvertFromString('#cfe8ed'),[Windows.Media.Pen]::new($ink,0.045),$body)
$wall=[Windows.Media.SolidColorBrush]::new([Windows.Media.Color]::FromArgb(70,90,100,110))
$dc.DrawRectangle($wall,$null,[Windows.Rect]::new(0,-$w/2-1.2,$t,$w+2.4))
$dc.Pop();$dc.Pop()
$dimY=-$w/2-2
$dc.DrawLine($light,(P $t (-$w/2)),(P $t ($dimY-0.2)));$dc.DrawLine($light,(P $length (-$w/2)),(P $length ($dimY-0.2)))
Dim (P $t $dimY) (P $length $dimY) ((Fmt $p.SideLugProjection)+' мм — выступ') -59 -25
$dc.DrawLine($light,(P ($length-$p.SideLugRadius) (-$w/2)),(P ($length+2.3) (-$w/2)))
$dc.DrawLine($light,(P ($length-$p.SideLugRadius) ($w/2)),(P ($length+2.3) ($w/2)))
Dim (P ($length+2) (-$w/2)) (P ($length+2) ($w/2)) ((Fmt $w)+' мм') 9 -12
Dim (P $hole.Right 0) (P $length 0) '3 мм' -20 -27
Text 'Боковина 3 мм' 25 79 15;$dc.DrawLine($light,[Windows.Point]::new(112,101),(P 1.5 (-$w/2+0.5)))
Text ('Окно '+(Fmt $hole.Width)+' × '+(Fmt $hole.Height)+' мм') 70 435 17;$dc.DrawLine($light,(P ($hole.X+$hole.Width/2) $hole.Bottom),[Windows.Point]::new(133,429))
Text 'R1,5' 421 401 16;$dc.DrawLine($light,[Windows.Point]::new(421,400),(P ($length-0.4) ($w/2-0.4)))
Text ('Перемычки не тоньше 3 мм. Зазор окна по толщине — '+(Fmt $p.Options.RetainerThicknessClearance)+' мм.') 25 481 14
Text 'Ушко вырезается вместе с дном.' 25 508 14
$dc.Close();$bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(600,540,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bitmap.Render($drawing)
$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream=[IO.File]::Create((Join-Path $OutputDirectory 'side-lug-dimensions-3mm.png'));try{$encoder.Save($stream)}finally{$stream.Dispose()}
Write-Output "Passed $checks compact lug checks. Dimensioned drawing: $OutputDirectory"
