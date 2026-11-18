[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $env:TEMP 'VanyaAcrylicTrayChecks'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xml.Linq, System.Web.Extensions
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$optionsType = $assembly.GetType('VanyaTools.Native.AcrylicTrayOptions', $true)
$patternType = $assembly.GetType('VanyaTools.Native.AcrylicTrayPattern', $true)
$outputType = $assembly.GetType('VanyaTools.Native.AcrylicTrayOutput', $true)
$tabType = $assembly.GetType('VanyaTools.Native.AcrylicTrayTab', $true)
$modeType = $assembly.GetType('VanyaTools.Native.AcrylicTrayFloorMode', $true)
$checks = 0
function Options { [Activator]::CreateInstance($optionsType, $true) }
function Build($o) { $patternType.GetMethod('Build').Invoke($null, @($o)) }
function Check($value, [string]$message) { if (-not $value) { throw $message }; $script:checks++ }
function Near([double]$actual, [double]$expected, [double]$tolerance = 0.001) {
    Check ([Math]::Abs($actual - $expected) -le $tolerance) "Expected $expected, got $actual"
}
function Reject($o) {
    $rejected = $false
    try { $null = Build $o } catch { $rejected = $true }
    Check $rejected 'Invalid geometry accepted'
}
function Contains($g, [double]$x, [double]$y) {
    # WPF hit testing has its own device-coordinate quantization, independent
    # of curve tolerance. Magnify to sample micrometre-scale fit reliably.
    $sample = [Windows.Media.GeometryGroup]::new()
    $sample.Transform = [Windows.Media.ScaleTransform]::new(100, 100)
    $sample.Children.Add($g)
    $sample.FillContains([Windows.Point]::new($x * 100, $y * 100), 0.001, [Windows.Media.ToleranceType]::Absolute)
}
function Verify($p) {
    $o = $p.Options
    $side = $p.Parts | Where-Object Id -eq 'side-left'
    $sideFigures = $side.AssemblyGeometry.GetFlattenedPathGeometry().Figures.Count
    Check ($sideFigures -eq 1 + $p.Crosses.Count + $o.Rows) "Side window breaks through an outside edge: figures=$sideFigures, t=$($o.Thickness), a=$($p.Angle), mode=$($o.FloorMode), rise=$($o.RowRise), decorative=$($o.DecorativeSides)"
    foreach ($part in $p.Parts) {
        Check ($part.Geometry.GetArea() -gt 0) "Empty part: $($part.Id)"
        Check ($part.Path -notmatch 'NaN|Infinity') 'Non-finite contour'
        $parsed = [Windows.Media.Geometry]::Parse($part.Path)
        Near $parsed.Bounds.Width $part.Width 0.002
        Near $parsed.Bounds.Height $part.Height 0.002
        Near $parsed.GetArea() $part.Geometry.GetArea() 0.1
        $figures = $part.Geometry.GetFlattenedPathGeometry().Figures
        foreach ($figure in $figures) { Check $figure.IsClosed "Open contour in $($part.Id)" }
        # Exclusion must leave one connected outer boundary, not loose islands.
        $areas = @()
        foreach ($figure in $figures) {
            $points = @($figure.StartPoint)
            foreach ($segment in $figure.Segments) {
                if ($segment -is [Windows.Media.PolyLineSegment]) { $points += @($segment.Points) }
                elseif ($segment -is [Windows.Media.LineSegment]) { $points += $segment.Point }
            }
            $area = 0.0
            for ($i = 0; $i -lt $points.Count; $i++) {
                $next = $points[($i + 1) % $points.Count]
                $area += $points[$i].X * $next.Y - $next.X * $points[$i].Y
            }
            $areas += $area
        }
        $largest = $areas | Sort-Object { [Math]::Abs($_) } -Descending | Select-Object -First 1
        $outerCount = @($areas | Where-Object { $_ * $largest -gt 0.00001 }).Count
        Check ($outerCount -eq 1) "Disconnected material in $($part.Id): $outerCount islands; t=$($o.Thickness), a=$($p.Angle), cols=$($o.Columns), mode=$($o.FloorMode); areas=$areas"
    }
    $longPart = $p.Parts | Where-Object Kind -eq 'long' | Select-Object -First 1
    if ($longPart) {
        for ($i = 0; $i -lt $p.Crosses.Count; $i++) {
            $cross = $p.Crosses[$i]
            $crossPart = $p.Parts | Where-Object Id -eq "cross-$($i + 1)"
            foreach ($x in $p.Longitudinals) {
                foreach ($offset in @(-0.4, 0.0, 0.4)) {
                    $y = $cross.Y + $offset * $o.Thickness
                    $lowZ = $cross.JointZ - $o.BoardHeight * 0.15
                    $highZ = $cross.JointZ + $o.BoardHeight * 0.15
                    Check (Contains $longPart.AssemblyGeometry $y (-$lowZ)) 'Lower half-lap lost its material'
                    Check (-not (Contains $crossPart.AssemblyGeometry $x (-$lowZ))) 'Half-lap collision below joint'
                    Check (-not (Contains $longPart.AssemblyGeometry $y (-$highZ))) 'Half-lap collision above joint'
                    Check (Contains $crossPart.AssemblyGeometry $x (-$highZ)) 'Upper half-lap lost its material'
                }
            }
        }
    }
    # Sample the actual intersection through both faces of the thick inclined
    # floor. A t/cos(a)-only slot would fail these independent fit checks.
    for ($i = 0; $i -lt $p.Floors.Count; $i++) {
        $f = $p.Floors[$i]
        $floorPart = $p.Parts | Where-Object Id -eq "floor-$($i + 1)"
        foreach ($cross in @($p.Crosses | Where-Object FloorIndex -eq $i)) {
            $x = $p.CellCenter(0)
            foreach ($n in @(-$o.Thickness, 0.0)) {
                foreach ($edge in @(-0.499, 0.499)) {
                    $s = ($cross.Y + $edge * $o.Thickness - $f.StartY) / $f.Cosine + $n * $f.Slope
                    Check (-not (Contains $floorPart.AssemblyGeometry $x $s)) "Vertical tenon collides with floor: t=$($o.Thickness), a=$($p.Angle), cols=$($o.Columns), y=$($cross.Y), n=$n, edge=$edge, x=$x, s=$s"
                }
            }
        }
    }
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$o = Options; $o.AngleDegrees = 0; $p = Build $o
Near $p.OverallWidth 175; Near $p.Depth 84; Near $p.CrossSlotWidth 3.1
Check ($p.Parts.Count -eq 10) 'Unexpected flat-tray part count'
Check ($p.Floors.Count -eq 1) 'Flat tray has multiple bottoms'
$o.CellWidth = 90
Near $p.Options.CellWidth 40

foreach ($thickness in @(2, 3)) {
    foreach ($angle in @(0, 15, 30, 45, 60)) {
        foreach ($columns in @(1, 4)) {
            $o = Options; $o.Thickness = $thickness; $o.AngleDegrees = $angle; $o.Columns = $columns
            $p = Build $o
            Verify $p
            $a = $angle * [Math]::PI / 180
            Near $p.CrossSlotWidth ($thickness / [Math]::Cos($a) + $thickness * [Math]::Tan($a) + 0.1)
        }
    }
}
foreach ($rise in @(0, 15, 45)) {
    $o = Options; $o.FloorMode = [Enum]::Parse($modeType, 'Steps'); $o.RowRise = $rise; $o.AngleDegrees = [double]::NaN
    $p = Build $o; Verify $p
    Check ($p.Floors.Count -eq $o.Rows) 'Stepped construction lost shelf floors'
    for ($r = 0; $r -lt $o.Rows; $r++) {
        Near ($p.Crosses[2 * $r + 1].Y - $p.Crosses[2 * $r].Y - $o.Thickness) $o.CellDepth
        if ($r) { Near ($p.Floors[$r].FrontZ - $p.Floors[$r - 1].FrontZ) $rise }
    }
}
foreach ($angle in @(0, 60)) {
    $o = Options; $o.DecorativeSides = $false; $o.AngleDegrees = $angle; $o.SlotClearance = 0
    $p = Build $o; Verify $p
    Near $p.OverallDepth ($p.Depth + $o.Thickness * [Math]::Sin($angle * [Math]::PI / 180))
}
$o = Options; $o.AngleDegrees = -1; Reject $o
$o = Options; $o.AngleDegrees = 61; Reject $o
$o = Options; $o.Columns = 0; Reject $o
$o = Options; $o.Rows = 21; Reject $o
$o = Options; $o.Thickness = [double]::NaN; Reject $o
$o = Options; $o.CellWidth = [double]::PositiveInfinity; Reject $o
$o = Options; $o.CellDepth = 5; Reject $o
$o = Options; $o.BoardHeight = 3; Reject $o
$o = Options; $o.SlotClearance = -0.1; Reject $o

$p = Build (Options)
$folder = $outputType.GetMethod('Save').Invoke($null, @($OutputDirectory, $p))
$ns = @{s = 'http://www.w3.org/2000/svg'}
[xml]$cut = Get-Content -LiteralPath (Join-Path $folder '01-cut.svg') -Raw
Check ((Select-Xml -Xml $cut -XPath '//s:text' -Namespace $ns).Count -eq 0) 'Cut file contains engraving text'
Check ((Select-Xml -Xml $cut -XPath '//s:path' -Namespace $ns).Count -eq $p.Parts.Count) 'Cut layout missing parts'
Check ($cut.DocumentElement.width.EndsWith('mm')) 'SVG missing physical dimensions'
[xml]$map = Get-Content -LiteralPath (Join-Path $folder '02-parts-map.svg') -Raw
Check ((Select-Xml -Xml $map -XPath '//s:text' -Namespace $ns).Count -eq 3 * $p.Parts.Count) 'Missing map labels or legend'
Check (-not (Test-Path -LiteralPath (Join-Path $folder 'НЕ_ЗАВЕРШЕНО.txt'))) 'Export not committed'
$parameters = Get-Content -LiteralPath (Join-Path $folder 'parameters.json') -Raw | ConvertFrom-Json
Near $parameters.options.CellWidth 40
Check ($parameters.parts.Count -eq $p.Parts.Count) 'Manifest missing parts'

# Exercise the actual WPF tab, decimal comma input, invalid input and mode switch.
$tab = [Activator]::CreateInstance($tabType, [Reflection.BindingFlags]'Instance,Public,NonPublic', $null, @([Action[string,bool]]{param($m,$e)}), $null)
function Field([string]$name) { $tabType.GetField($name, [Reflection.BindingFlags]'Instance,NonPublic').GetValue($tab) }
function RefreshTab { $null = $tabType.GetMethod('Update', [Reflection.BindingFlags]'Instance,NonPublic').Invoke($tab, @()) }
Check ((Field '_create').IsEnabled) 'New tab starts invalid'
(Field '_clearance').Text = '0,15'; RefreshTab
Check ((Field '_create').IsEnabled) 'Decimal comma rejected'
(Field '_columns').Text = '2.5'; RefreshTab
Check (-not (Field '_create').IsEnabled) 'Fractional cell count accepted'
Check ($null -eq (Field '_side').Source) 'Invalid input leaves stale preview'
(Field '_columns').Text = '4'; (Field '_mode').SelectedIndex = 1; (Field '_angle').Text = 'bad'; RefreshTab
Check ((Field '_create').IsEnabled) 'Inactive angle field blocks stepped tray'
Check ((Field '_angleRow').Visibility -eq 'Collapsed') 'Angle field not hidden for steps'
(Field '_mode').SelectedIndex = 0; (Field '_angle').Text = '30'; RefreshTab
Check ((Field '_create').IsEnabled) 'Angle mode did not recover'
(Field '_decorative').IsChecked = $false; RefreshTab
Check ((Field '_create').IsEnabled) 'Straight side profile did not recover'
(Field '_decorative').IsChecked = $true; RefreshTab
$tab.Background = [Windows.Media.Brushes]::White
$tab.Measure([Windows.Size]::new(360, 1100)); $tab.Arrange([Windows.Rect]::new(0, 0, 360, 1100)); $tab.UpdateLayout()
$bitmap = [Windows.Media.Imaging.RenderTargetBitmap]::new(360, 1100, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$bitmap.Render($tab)
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream = [IO.File]::Create((Join-Path $OutputDirectory 'tray-ui.png'))
try { $encoder.Save($stream) } finally { $stream.Dispose() }
(Field '_timer').Stop()
Write-Host "Passed $checks acrylic tray checks. Artifacts: $OutputDirectory"
Write-Host "Sample SVG bundle: $folder"
