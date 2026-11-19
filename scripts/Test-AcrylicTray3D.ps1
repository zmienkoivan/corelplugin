[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $env:TEMP 'VanyaAcrylicTray3DChecks'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dll = Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'
$assembly = [Reflection.Assembly]::LoadFrom($dll)
$optionsType = $assembly.GetType('VanyaTools.Native.AcrylicTrayOptions', $true)
$patternType = $assembly.GetType('VanyaTools.Native.AcrylicTrayPattern', $true)
$modelType = $assembly.GetType('VanyaTools.Native.AcrylicTrayAssembly3D', $true)
$tabType = $assembly.GetType('VanyaTools.Native.AcrylicTrayTab', $true)
$modeType = $assembly.GetType('VanyaTools.Native.AcrylicTrayFloorMode', $true)
$flags = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$checks = 0
function Check($ok, [string]$message) { if (-not $ok) { throw $message }; $script:checks++ }
function Near([double]$a, [double]$b, [double]$tolerance = 0.01) { Check ([Math]::Abs($a-$b) -lt $tolerance) "Expected $b, got $a" }
function Options { [Activator]::CreateInstance($optionsType, $true) }
function Build($o) { $patternType.GetMethod('Build').Invoke($null, @($o)) }
function Model($p) { $modelType.GetMethod('Build').Invoke($null, @($p)) }
Add-Type -ReferencedAssemblies @([Windows.Media.Media3D.MeshGeometry3D].Assembly.Location,[Windows.Point].Assembly.Location) -TypeDefinition @'
using System;
using System.Windows.Media.Media3D;
public static class TrayMeshCheck {
    public static double Area(MeshGeometry3D mesh) {
        double area=0;
        for(int i=0;i<mesh.TriangleIndices.Count;i+=3) {
            int a=mesh.TriangleIndices[i],b=mesh.TriangleIndices[i+1],c=mesh.TriangleIndices[i+2];
            var n=Vector3D.CrossProduct(mesh.Positions[b]-mesh.Positions[a],mesh.Positions[c]-mesh.Positions[a]);
            double length=n.Length;
            if(length<=1e-12 || double.IsNaN(length) || double.IsInfinity(length)) throw new Exception("Invalid triangle");
            n/=length;
            if(Vector3D.DotProduct(n,mesh.Normals[a])<.9999) throw new Exception("Inverted face normal");
            area+=length/2;
        }
        return area;
    }
    public static double Distance(MeshGeometry3D mesh, Point3D expected) {
        double distance=double.MaxValue;
        foreach(var p in mesh.Positions) distance=Math.Min(distance,(p-expected).Length);
        return distance;
    }
}
'@
function Verify($p) {
    $m = Model $p
    Check ($m.Parts.Count -eq $p.Parts.Count) 'Assembly dropped a cut part'
    Near $m.Bounds.X (-$p.Options.Thickness)
    Near $m.Bounds.SizeX $p.OverallWidth
    Near $m.Bounds.Y $p.Floors[0].StartY
    Near $m.Bounds.SizeY $p.OverallDepth
    Near $m.Bounds.Z 0
    for ($i=0; $i -lt $m.Parts.Count; $i++) {
        $sheet = $m.Parts[$i]; $part = $p.Parts[$i]
        Check ($sheet.Id -eq $part.Id) 'Wrong assembly part order'
        Check ($sheet.Faces.IsFrozen -and $sheet.Edges.IsFrozen) 'Assembly mesh is not immutable'
        # Independent area comparison detects filled-in slots and bad poses.
        Near ([TrayMeshCheck]::Area($sheet.Faces)) (2 * $part.Geometry.GetArea()) 0.15
        Check ([TrayMeshCheck]::Area($sheet.Edges) -gt 0) 'Sheet thickness missing'
        if ($part.Kind -eq 'side' -or $part.Kind -eq 'long') { Near $sheet.Faces.Bounds.SizeX $p.Options.Thickness }
        if ($part.Kind -eq 'cross') { Near $sheet.Faces.Bounds.SizeY $p.Options.Thickness }
    }
    for ($i=0; $i -lt $p.Floors.Count; $i++) {
        $f = $p.Floors[$i]; $floorMesh = ($m.Parts | Where-Object Id -eq "floor-$($i+1)").Faces
        Near ([TrayMeshCheck]::Distance($floorMesh,[Windows.Media.Media3D.Point3D]::new(0,$f.StartY,$f.FrontZ))) 0
        Near ([TrayMeshCheck]::Distance($floorMesh,[Windows.Media.Media3D.Point3D]::new(0,
            $f.StartY+$p.Options.Thickness*$f.Sine,$f.FrontZ-$p.Options.Thickness*$f.Cosine))) 0
    }
}
$projectVersion=[regex]::Match([IO.File]::ReadAllText((Join-Path $repo 'native\VanyaTools.Native\VanyaTools.Native.csproj')),'<Version>([^<]+)</Version>').Groups[1].Value
Check ($assembly.GetName().Version.ToString(3) -eq $projectVersion) 'Module built against the wrong checkout version'
foreach ($name in @('LetterTransferTab','RasterMaskEditor','CurveMeasureTab','PatternFillTab','FrameBagTab','PuffySleeveTab','Apparel3DTab','LaserBendTab')) {
    Check ($null -ne $assembly.GetType("VanyaTools.Native.$name")) "Latest-release tool missing: $name"
}
foreach ($thickness in @(2,3)) {
    foreach ($angle in @(0,30,60)) { $o=Options; $o.Thickness=$thickness; $o.AngleDegrees=$angle; Verify (Build $o) }
}
$o=Options; $o.Columns=1; $o.Rows=1; Verify (Build $o)
$o=Options; $o.FloorMode=[Enum]::Parse($modeType,'Steps'); $o.RowRise=25; Verify (Build $o)

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$tab = [Activator]::CreateInstance($tabType,[object[]]@([Action[string,bool]]{param($m,$e)}))
function Field([string]$name) { $tabType.GetField($name,$flags).GetValue($tab) }
function Pump {
    $frame = [Windows.Threading.DispatcherFrame]::new()
    $timer = [Windows.Threading.DispatcherTimer]::new(); $timer.Interval=[TimeSpan]::FromMilliseconds(650)
    $timer.Tag = $frame; $timer.Add_Tick({param($sender,$args) $sender.Tag.Continue=$false; $sender.Stop()})
    $timer.Start(); [Windows.Threading.Dispatcher]::PushFrame($frame)
}
function SaveView([string]$name) {
    $bitmap=$scene.RenderPng(900); $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
    $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=[IO.File]::Create((Join-Path $OutputDirectory $name))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
}
$scene=Field '_scene'
$scene.Measure([Windows.Size]::new(720,420)); $scene.Arrange([Windows.Rect]::new(0,0,720,420)); $scene.UpdateLayout()
SaveView 'tray-3d-20deg.png'
$original=$scene.Assembly
(Field '_width').Text='55'; (Field '_depth').Text='28'; (Field '_angle').Text='45'
Pump
Check (-not [Object]::ReferenceEquals($original,$scene.Assembly)) 'Live input did not replace the 3D model'
Near $scene.Assembly.Bounds.SizeX (4*55+5*3)
Near $scene.Assembly.Bounds.SizeY (3*28+4*3+12+3*[Math]::Sin([Math]::PI/4))
SaveView 'tray-3d-45deg-wide.png'
Check ((Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'tray-3d-20deg.png')).Hash -ne
    (Get-FileHash -LiteralPath (Join-Path $OutputDirectory 'tray-3d-45deg-wide.png')).Hash) 'Parameter changes did not affect rendered preview'
$scene.View(2)
(Field '_width').Text='40'; Pump
Near $scene.GetType().GetField('_yaw',$flags).GetValue($scene) 0
(Field '_samples').IsChecked=$false
Check (-not $scene.ShowSamples) 'Samples toggle ignored'
$lastValid=$scene.Assembly
(Field '_angle').Text='invalid'; Pump
Check ([Object]::ReferenceEquals($lastValid,$scene.Assembly)) 'Invalid input erased the last assembled preview'
Check ((Field '_previewMessage').Visibility -eq [Windows.Visibility]::Visible -and (Field '_previewMessage').Text.Contains('3D')) 'Invalid input is not explained beside the preview'
Check (-not (Field '_create').IsEnabled) 'Invalid model remains exportable'
(Field '_mode').SelectedIndex=1; Pump
Check ($null -ne $scene.Assembly) 'Stepped mode blocked by inactive angle field'
$scene.View(0); SaveView 'tray-3d-steps.png'
(Field '_samples').IsChecked=$true
(Field '_mode').SelectedIndex=0; (Field '_angle').Text='20'; (Field '_depth').Text='20'; Pump
$scene.View(0)
# Also capture the actual docked control to verify the pinned scene layout.
$assembly.GetType('VanyaTools.Native.DockerTheme').GetMethod('Apply').Invoke($null,@($tab)) | Out-Null
$tab.Measure([Windows.Size]::new(380,1050)); $tab.Arrange([Windows.Rect]::new(0,0,380,1050)); $tab.UpdateLayout()
$bitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(380,1050,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($tab); $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap)); $stream=[IO.File]::Create((Join-Path $OutputDirectory 'tray-panel.png'))
try { $encoder.Save($stream) } finally { $stream.Dispose() }
(Field '_timer').Stop()
Write-Output "Passed $checks tray 3D checks against $($assembly.GetName().Version). Artifacts: $OutputDirectory"
