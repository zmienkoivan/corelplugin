[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $env:TEMP 'VanyaApparel3DChecks'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$static = [Reflection.BindingFlags]'Static,Public,NonPublic'
$instance = [Reflection.BindingFlags]'Instance,Public,NonPublic'
$viewType = $assembly.GetType('VanyaTools.Native.ApparelViewMap', $true)
$modelType = $assembly.GetType('VanyaTools.Native.Apparel3DModel', $true)
$sourceType = $assembly.GetType('VanyaTools.Native.Apparel3DSource', $true)
$boxType = $assembly.GetType('VanyaTools.Native.ApparelShapeBox', $true)
$sceneType = $assembly.GetType('VanyaTools.Native.Apparel3DScene', $true)
$checks = 0
function Check($ok, [string]$message) { if (-not $ok) { throw $message }; $script:checks++ }
function Near([double]$a, [double]$b) { Check ([Math]::Abs($a - $b) -lt 0.000001) "Expected $b, got $a" }
function Demo([bool]$back = $false) { $viewType.GetMethod('Demo').Invoke($null, @($back)) }
function Build($front, $back, [double]$folds) { ,$modelType.GetMethod('Build').Invoke($null, @($front, $back, $folds)) }
function Reject([scriptblock]$action, [string]$message) { $failed = $false; try { & $action | Out-Null } catch { $failed = $true }; Check $failed $message }
function Box([double]$x, [double]$y, [double]$w, [double]$h, [int]$kind = 3) {
    $b = [Activator]::CreateInstance($boxType, $true); $b.Bounds = [Windows.Rect]::new($x, $y, $w, $h); $b.Type = $kind; $b
}
function Detect($boxes) {
    $list = [Activator]::CreateInstance([Collections.Generic.List``1].MakeGenericType($boxType))
    foreach ($b in $boxes) { $list.Add($b) }
    $group = Box 0 0 794 719 7
    $sourceType.GetMethod('Detect', $static).Invoke($null, @($group, $list))
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

Add-Type -TypeDefinition @'
public class ApparelFakePage { public int Index = 1; }
public class ApparelFakeDocument {
    public int Index = 1;
    public string FullFileName = "synthetic.cdr";
    public object ActivePage = new ApparelFakePage();
    public bool Dirty = true;
}
public class ApparelFakeRange { public int Count = 0; }
public class ApparelFakeApplication {
    public ApparelFakeDocument ActiveDocument = new ApparelFakeDocument();
    public object ActivePage = new ApparelFakePage();
    public object ActiveSelectionRange = new ApparelFakeRange();
}
'@
$fakeApp = [ApparelFakeApplication]::new()
$bound = $sourceType.GetMethod('Bind', $static, $null, [type[]]@([object]), $null).Invoke($null, @($fakeApp))
$other = [ApparelFakeDocument]::new(); $other.Index = 2; $other.FullFileName = 'another.cdr'
$fakeApp.ActiveDocument = $other
Reject { $sourceType.GetMethod('Capture').Invoke($bound, @()) } 'An unrelated active document was accepted'
Check $other.Dirty 'Rejected refresh changed the other order state'
Check ($fakeApp.ActiveSelectionRange.Count -eq 0) 'Rejected refresh changed selection'

# Synthetic structure with extra cuff/collar/art shapes. No customer files.
$body = Box 137 9 520 710
$left = Box 0 59 177 207; $right = Box 617 59 177 207
$collar = Box 302 10 190 104
$boxes = @($body, $left, $right, $collar, (Box 10 110 62 156), (Box 50 200 300 80 7))
$candidate = Detect $boxes
Check ($null -ne $candidate) 'Complete shirt was not found'
Near $candidate.Body.Bounds.Width 520; Near $candidate.NeckDepth 104
Check ([Object]::ReferenceEquals($candidate.Left, $left)) 'Cuff selected instead of left sleeve'
Check ([Object]::ReferenceEquals($candidate.Right, $right)) 'Wrong right sleeve'
Check ($null -eq (Detect @($body, $left, $collar))) 'A missing sleeve was accepted'
Check ($null -eq (Detect @($left, $right, $collar))) 'An illustration was accepted as a shirt'

$identity = [Func[Windows.Point,Windows.Point]]{ param($p) $p }
foreach ($side in @(-1, 1)) {
    $nodes = [Collections.Generic.List[Windows.Point]]::new()
    foreach ($p in @(@(159, 266), @(238, 254), @(263, 59), @(101, 111))) {
        $nodes.Add([Windows.Point]::new($p[0] * $side, $p[1]))
    }
    $sleeve = $sourceType.GetMethod('SleeveCorners', $static).Invoke($null, @($nodes, [double](498 * $side), $identity))
    Near $sleeve.RootTop.X (263 * $side); Near $sleeve.RootTop.Y 59
    Near $sleeve.CuffTop.X (101 * $side); Near $sleeve.CuffBottom.Y 266
    $middle = $sleeve.GetType().GetMethod('At').Invoke($sleeve, @([double]0.5, [double]0.5))
    Near $middle.X (190.25 * $side); Near $middle.Y 172.5
    $nodes.RemoveAt(0)
    Reject { $sourceType.GetMethod('SleeveCorners', $static).Invoke($null, @($nodes, [double]498, $identity)) } 'Non-quadrilateral sleeve accepted'
}

$front = Demo; $back = Demo $true
Write-Output 'Checking mesh and print orientation...'
Add-Type -ReferencedAssemblies @([Windows.Media.Media3D.MeshGeometry3D].Assembly.Location, [Windows.Point].Assembly.Location) -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Windows.Media.Media3D;
public static class ApparelMeshChecks {
    public static int Inspect(MeshGeometry3D mesh, MeshGeometry3D original) {
        if (!mesh.IsFrozen || mesh.Positions.Count != mesh.TextureCoordinates.Count || mesh.Positions.Count != mesh.Normals.Count)
            throw new Exception("Incomplete or mutable shirt surface");
        int changed = 0;
        for (int i = 0; i < mesh.Positions.Count; i++) {
            var p = mesh.Positions[i]; var uv = mesh.TextureCoordinates[i]; var n = mesh.Normals[i];
            foreach (double d in new [] { p.X, p.Y, p.Z, uv.X, uv.Y, n.X, n.Y, n.Z })
                if (Double.IsNaN(d) || Double.IsInfinity(d)) throw new Exception("Invalid mesh coordinate");
            if (Math.Abs(n.Length - 1) > .00001) throw new Exception("Non-unit cloth normal");
            if (uv.X < -.001 || uv.X > 1.001 || uv.Y < -.001 || uv.Y > 1.001) throw new Exception("Print chart outside UV space");
            if ((p - original.Positions[i]).Length > .000001) changed++;
        }
        if (mesh.TriangleIndices.Count % 3 != 0) throw new Exception("Incomplete triangles");
        foreach (int index in mesh.TriangleIndices)
            if (index < 0 || index >= mesh.Positions.Count) throw new Exception("Triangle outside mesh");
        return changed;
    }
    public static double Direction(MeshGeometry3D mesh) {
        double x = 0, u = 0, covariance = 0;
        for (int i = 0; i < mesh.Positions.Count; i++) { x += mesh.Positions[i].X; u += mesh.TextureCoordinates[i].X; }
        x /= mesh.Positions.Count; u /= mesh.Positions.Count;
        for (int i = 0; i < mesh.Positions.Count; i++) covariance += (mesh.Positions[i].X-x)*(mesh.TextureCoordinates[i].X-u);
        return covariance;
    }
    public static int Seam(MeshGeometry3D a, MeshGeometry3D b) {
        var points = new Dictionary<Point3D,Vector3D>();
        for (int i = 0; i < a.Positions.Count; i++) points[a.Positions[i]] = a.Normals[i];
        int shared = 0;
        for (int i = 0; i < b.Positions.Count; i++) {
            Vector3D n;
            if (points.TryGetValue(b.Positions[i], out n)) {
                if (Vector3D.DotProduct(n,b.Normals[i]) < .9999) throw new Exception("Broken lighting across a sewn seam");
                shared++;
            }
        }
        return shared;
    }
}
'@
$flat = Build $front $back 0
$folded = Build $front $back 1
Check ($flat.Count -eq 8) 'Missing body, sleeve, collar or inner surface'
$changed = 0
for ($s = 0; $s -lt $flat.Count; $s++) {
    $mesh = $folded[$s].Mesh; $base = $flat[$s].Mesh
    Write-Verbose ("Surface $s : " + $mesh.Positions.Count + ' vertices')
    $changed += [ApparelMeshChecks]::Inspect($mesh, $base)
    Check ($mesh.Positions.Count -gt 100) 'Empty authored surface'
}
Check ($changed -gt 100) 'Fold setting has no effect'
Write-Output 'Checking seams and validation...'
Check ([ApparelMeshChecks]::Direction($flat[0].Mesh) -gt 0) 'Front is mirrored'
Check ([ApparelMeshChecks]::Direction($flat[1].Mesh) -lt 0) 'Back lettering will be mirrored'
foreach ($pair in @(@(0,2),@(0,4),@(1,3),@(1,5),@(0,1))) {
    Check ([ApparelMeshChecks]::Seam($folded[$pair[0]].Mesh,$folded[$pair[1]].Mesh) -gt 8) 'Missing sewn seam'
}
$scaledFront = Demo; $scaledFront.BodyWidthMm = 650; $scaledFront.BodyHeightMm = 852
$scaled = Build $scaledFront $back 1
Near $scaled[0].Mesh.Bounds.SizeX ($folded[0].Mesh.Bounds.SizeX * 1.25)
Near $scaled[0].Mesh.Bounds.SizeY ($folded[0].Mesh.Bounds.SizeY * 1.2)
Near $scaled[0].Mesh.Bounds.SizeZ ($folded[0].Mesh.Bounds.SizeZ * 1.25)
Check ($folded[7].Collar -and $folded[7].Mesh.Bounds.SizeZ -gt .15) 'Missing fabric interior and thickness'
foreach ($folds in @([double]::NaN, [double]::PositiveInfinity, -1, 2)) { Reject { Build $front $back $folds } 'Invalid folds accepted' }
$badView = Demo $true; $badView.NeckDepthMm = [double]::NaN
Reject { Build $front $badView .5 } 'Invalid back neck accepted'
$badView = Demo; $badView.ImageLeft = $null
Reject { Build $badView $back .5 } 'Missing sleeve map accepted'

# Deliberately asymmetric synthetic images catch per-surface UV rescaling.
function Texture($color, $left = [Windows.Media.Brushes]::Lime, $right = [Windows.Media.Brushes]::Magenta) {
    $visual = [Windows.Media.DrawingVisual]::new(); $draw = $visual.RenderOpen()
    try {
        $draw.DrawRectangle($left, $null, [Windows.Rect]::new(0, 0, 256, 256))
        $draw.DrawRectangle($color, $null, [Windows.Rect]::new(44, 0, 168, 256))
        $draw.DrawRectangle($right, $null, [Windows.Rect]::new(218, 0, 38, 256))
    } finally { $draw.Close() }
    $image = [Windows.Media.Imaging.RenderTargetBitmap]::new(256, 256, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $image.Render($visual); $image.Freeze(); $image
}
$front.Image = Texture ([Windows.Media.Brushes]::Red)
Write-Output 'Checking rendered views...'
$back.Image = Texture ([Windows.Media.Brushes]::Blue) ([Windows.Media.Brushes]::Yellow) ([Windows.Media.Brushes]::Cyan)
$snapshot = [Activator]::CreateInstance($assembly.GetType('VanyaTools.Native.ApparelOrderSnapshot'), $true)
$snapshot.Front = $front; $snapshot.Back = $back; $snapshot.SourceName = 'Synthetic order'
$scene = [Activator]::CreateInstance($sceneType, $true)
$sceneType.GetMethod('Set').Invoke($scene, @($snapshot, [double]0.5, $true)) | Out-Null
$textureField = $sceneType.GetField('_textures', $instance)
$textures = $textureField.GetValue($scene)
foreach ($part in @(2,3,4,5)) {
    $pixel = [byte[]]::new(4); $image = $textures[$part]
    $image.CopyPixels([Windows.Int32Rect]::new([int]($image.PixelWidth*.85),[int]($image.PixelHeight*.45),1,1),$pixel,4,0)
    switch ($part) {
        2 { Check ($pixel[1]-gt150-and$pixel[0]-lt10-and$pixel[2]-lt10) 'Right front sleeve did not use the left flat-view quad' }
        3 { Check ($pixel[0]-gt150-and$pixel[1]-gt150-and$pixel[2]-lt10) 'Right back sleeve did not use the right flat-view quad' }
        4 { Check ($pixel[0]-gt150-and$pixel[2]-gt150-and$pixel[1]-lt10) 'Left front sleeve did not use the right flat-view quad' }
        5 { Check ($pixel[1]-gt150-and$pixel[2]-gt150-and$pixel[0]-lt10) 'Left back sleeve did not use the left flat-view quad' }
    }
}
$sceneType.GetMethod('Set').Invoke($scene,@($snapshot,[double]1,$false)) | Out-Null
Check ([Object]::ReferenceEquals($textures,$textureField.GetValue($scene))) 'Fold adjustment regenerated print textures'
$parameters = [Windows.Interop.HwndSourceParameters]::new('Apparel3D checks')
$parameters.Width = 800; $parameters.Height = 800; $parameters.WindowStyle = 0
$hostSource = [Windows.Interop.HwndSource]::new($parameters)
$hashes = @()
try {
    $hostSource.RootVisual = $scene
    $scene.Measure([Windows.Size]::new(800, 800)); $scene.Arrange([Windows.Rect]::new(0, 0, 800, 800)); $scene.UpdateLayout()
    foreach ($pose in @(0, 180, -25)) {
        $sceneType.GetMethod('View').Invoke($scene, @([double]$pose)) | Out-Null
        $frame = [Windows.Threading.DispatcherFrame]::new()
        [Windows.Threading.Dispatcher]::CurrentDispatcher.BeginInvoke([Action]{ $frame.Continue = $false }, [Windows.Threading.DispatcherPriority]::ApplicationIdle) | Out-Null
        [Windows.Threading.Dispatcher]::PushFrame($frame)
        $bitmap = $sceneType.GetMethod('RenderPng').Invoke($scene, @([int]800))
        Check ($bitmap.PixelWidth -eq 800 -and $bitmap.PixelHeight -eq 800) 'Wrong PNG dimensions'
        $pixel = [byte[]]::new(4); $bitmap.CopyPixels([Windows.Int32Rect]::new(400, 400, 1, 1), $pixel, 4, 0)
        if ($pose -eq 0) { Check ($pixel[2] -gt 150 -and $pixel[0] -lt 60 -and $pixel[1] -lt 60) 'Front body samples the wrong texture region' }
        if ($pose -eq 180) { Check ($pixel[0] -gt 150 -and $pixel[2] -lt 60 -and $pixel[1] -lt 60) 'Back body samples the wrong texture region' }
        $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
        $path = Join-Path $OutputDirectory ("synthetic-$pose.png"); $stream = [IO.File]::Create($path)
        try { $encoder.Save($stream) } finally { $stream.Dispose() }
        $hashes += (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
    }
} finally { $hostSource.Dispose() }
Check (@($hashes | Select-Object -Unique).Count -eq 3) 'Export reused the first camera view'

# Exercise the modeless window without contacting Corel.
$windowType = $assembly.GetType('VanyaTools.Native.Apparel3DWindow', $true)
$window = [Activator]::CreateInstance($windowType, $instance, $null, [object[]]@($null), $null)
try {
    $windowType.GetMethod('SetSnapshot', $instance).Invoke($window, @($snapshot)) | Out-Null
    $window.Content.Measure([Windows.Size]::new(1100, 760)); $window.Content.Arrange([Windows.Rect]::new(0, 0, 1100, 760))
    Check ($window.Content.DesiredSize.Width -le 1100) 'Window controls overflow'
    $field = $windowType.GetField('_swapped', $instance); $field.SetValue($window, $true)
    $windowType.GetMethod('Apply', $instance).Invoke($window, @($true)) | Out-Null
    $image = $windowType.GetField('_front', $instance).GetValue($window)
    Check ([Object]::ReferenceEquals($image.Source, $back.Image)) 'Front/back swap did not update source preview'
} finally { $window.Close() }
Write-Output "Apparel 3D checks passed: $checks"
