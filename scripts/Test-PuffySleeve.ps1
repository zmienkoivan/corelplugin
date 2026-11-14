[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $env:TEMP 'VanyaSleeveChecks'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xml.Linq, System.Web.Extensions
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dll = Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'
$assembly = [Reflection.Assembly]::LoadFrom($dll)
$optionType = $assembly.GetType('VanyaTools.Native.PuffySleeveOptions', $true)
$patternType = $assembly.GetType('VanyaTools.Native.PuffySleevePattern', $true)
$outputType = $assembly.GetType('VanyaTools.Native.PuffySleeveOutput', $true)
$tabType = $assembly.GetType('VanyaTools.Native.PuffySleeveTab', $true)
$build = $patternType.GetMethod('Build')
$checks = 0
function Options { [Activator]::CreateInstance($optionType, $true) }
function Build($o) { $build.Invoke($null, @($o)) }
function Check($value, [string]$message) {
    if (-not $value) { throw $message }
    $script:checks++
}
function Near([double]$actual, [double]$expected, [double]$tolerance = 0.00001) {
    Check ([Math]::Abs($actual - $expected) -le $tolerance) "Expected $expected, got $actual"
}
function Reject($o) {
    $failed = $false
    try { $null = Build $o } catch { $failed = $true }
    Check $failed 'Invalid options accepted'
}
function PathLength([string]$path) {
    $geometry = [Windows.Media.Geometry]::Parse($path).GetFlattenedPathGeometry(0.0001, [Windows.Media.ToleranceType]::Absolute)
    $length = 0.0
    foreach ($figure in $geometry.Figures) {
        $last = $figure.StartPoint
        foreach ($segment in $figure.Segments) {
            foreach ($point in $segment.Points) {
                $length += ($point - $last).Length
                $last = $point
            }
        }
    }
    $length
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Check ($assembly.GetManifestResourceNames() -contains 'VanyaTools.Native.Assets.puffy-sleeve-jigs.html') 'Missing built-in manufacturing guide'
$o = Options
$p = Build $o
Near $p.SeamWidth 335
Near $p.SeamHeight 235
Near $p.RingDepth 30
Near $p.RequiredRingDepth 34
Near $p.CutWidth 345
Near $p.CutHeight 245
Near $p.FoamWidth 331
Near $p.FoamHeight 231
Near $p.ZipperDrop (235 / 4)
Near (PathLength $p.ZipperPath) $p.ZipperLength 0.01
Near (PathLength $p.SeamPath) $p.PerimeterLength 0.1
Near ($p.ZipperLength + $p.BottomBandLength) $p.PerimeterLength
Near ($p.ZipperLength + 2 * $o.SeamAllowance + 2 * $o.ZipperTail) $p.ZipperTapeLength
Near ($p.ZipperTapeWidth - 2 * $o.SeamAllowance) $p.RingDepth
Near $p.ZipperTapeWidth 40
$o.ZipperTapeWidth = 60; $wideZip = Build $o
Near $wideZip.RingDepth 50
$o.ZipperTapeWidth = 0; $autoZip = Build $o
Near $autoZip.RingDepth $autoZip.RequiredRingDepth
$o.ZipperTapeWidth = 8; Reject $o
$o = Options

$o.DeviceThickness = 28; $o.FoamThickness = 5
$thick = Build $o
Near $thick.RingDepth $p.RingDepth
Near $thick.RequiredRingDepth ($p.RequiredRingDepth + 11)
Near $thick.SeamWidth $p.SeamWidth
Near $thick.PerimeterLength $p.PerimeterLength
$o = Options; $o.ShrinkX = 3; $o.ShrinkY = 7
$shrunk = Build $o
Near ($shrunk.BlankWidth * 0.97) ($p.CutWidth + 2 * $p.TrimMargin)
Near ($shrunk.BlankHeight * 0.93) ($p.CutHeight + 2 * $p.TrimMargin)
Near $shrunk.ZipperLength $p.ZipperLength
Near $shrunk.RingDepth $p.RingDepth

$o = Options; $o.CommonBlankWidth = 450; $o.CommonBlankHeight = 350
$common = Build $o
Near $common.BlankWidth 450; Near $common.BlankHeight 350
$o.DeviceWidth = 280; $o.DeviceHeight = 190
$smaller = Build $o
Check ($common.QuiltPaths.Count -eq $smaller.QuiltPaths.Count) 'Common template changes its stitch count'
for ($i = 0; $i -lt $common.QuiltPaths.Count; $i++) {
    $first = [Windows.Media.Geometry]::Parse($common.QuiltPaths[$i]).Bounds
    $second = [Windows.Media.Geometry]::Parse($smaller.QuiltPaths[$i]).Bounds
    Near ($first.X + $common.BlankMarginX) ($second.X + $smaller.BlankMarginX)
    Near ($first.Y + $common.BlankMarginY) ($second.Y + $smaller.BlankMarginY)
    Near $first.Width $second.Width
}
$o.CommonBlankWidth = 200; Reject $o

$o = Options; $o.UseDiagonal = $true
$diag = Build $o
Near $diag.DeviceWidth (14 * 25.4 * 16 / [Math]::Sqrt(356) + 16)
Near $diag.DeviceHeight (14 * 25.4 * 10 / [Math]::Sqrt(356) + 16)

$quiltType = $assembly.GetType('VanyaTools.Native.SleeveQuilting', $true)
foreach ($kind in @('Straight', 'Arcs', 'Chevrons', 'None')) {
    foreach ($corner in @(0, 12, 55)) {
        $o = Options; $o.Quilting = [Enum]::Parse($quiltType, $kind); $o.CornerRadius = $corner; $o.QuiltInset = 0
        $case = Build $o
        Near (PathLength $case.ZipperPath) $case.ZipperLength 0.1
        if ($kind -eq 'None') { Check ($case.QuiltPaths.Count -eq 0) 'None contains stitch paths'; continue }
        $seam = [Windows.Media.RectangleGeometry]::new([Windows.Rect]::new(-$case.TrimMargin, -$case.TrimMargin,
            $case.CutWidth + 2 * $case.TrimMargin, $case.CutHeight + 2 * $case.TrimMargin))
        $boundary = [Windows.Media.Pen]::new([Windows.Media.Brushes]::Black, 0.02)
        foreach ($path in $case.QuiltPaths) {
            $flat = [Windows.Media.Geometry]::Parse($path).GetFlattenedPathGeometry()
            foreach ($figure in $flat.Figures) {
                Check ($seam.FillContains($figure.StartPoint) -or $seam.StrokeContains($boundary, $figure.StartPoint)) "Quilting starts outside panel: $kind, r=$corner"
                foreach ($segment in $figure.Segments) {
                    foreach ($point in $segment.Points) {
                        Check ($seam.FillContains($point) -or $seam.StrokeContains($boundary, $point)) "Quilting leaves panel: $kind, r=$corner"
                    }
                }
            }
        }
    }
}
foreach ($entry in @(@('DeviceWidth', [double]::NaN), @('DeviceThickness', [double]::PositiveInfinity),
    @('FoamThickness', -1), @('ShrinkX', 100), @('QuiltStep', 0), @('SeamAllowance', 0), @('CornerRadius', 100))) {
    $o = Options; $optionType.GetField($entry[0]).SetValue($o, [double]$entry[1]); Reject $o
}

$o = Options
$folder = $outputType.GetMethod('Save').Invoke($null, @($OutputDirectory, $o, $null, $null, $false))
Check (-not (Test-Path (Join-Path $folder 'НЕ_ЗАВЕРШЕНО.txt'))) 'Export not completed'
Check (-not (Test-Path (Join-Path $folder 'print-front.svg'))) 'Missing artwork became printable output'
[xml]$svg = Get-Content (Join-Path $folder '01-patterns.svg') -Raw
$ns = [Xml.XmlNamespaceManager]::new($svg.NameTable); $ns.AddNamespace('s', 'http://www.w3.org/2000/svg')
Check ($svg.svg.width.EndsWith('mm')) 'SVG lost physical units'
Check ($svg.SelectNodes('//s:g', $ns).Count -eq 7) 'Missing garment pieces or unexpected zipper strips'
Check (-not $svg.OuterXml.Contains('zipper-strip')) 'Bare zipper gained fabric strips'
Check ($svg.SelectNodes('//s:path[@data-role="zipper"]', $ns).Count -eq 2) 'Missing zipper marking'
[xml]$shapeSvg = Get-Content (Join-Path $folder '04-shaping.svg') -Raw
$shapeNs = [Xml.XmlNamespaceManager]::new($shapeSvg.NameTable); $shapeNs.AddNamespace('s', 'http://www.w3.org/2000/svg')
Check ($shapeSvg.SelectNodes('//s:path[@data-role="fixing-stitch"]', $shapeNs).Count -eq 2) 'Missing separate shaping contours'

# Asymmetric fixture detects accidental double mirroring and wrong raster DPI.
$pixels = [byte[]]::new(200 * 160 * 4)
for ($y = 0; $y -lt 160; $y++) {
    for ($x = 0; $x -lt 200; $x++) {
        $index = ($y * 200 + $x) * 4
        if ($x -lt 100) { $pixels[$index + 2] = 255 } else { $pixels[$index] = 255 }
        $pixels[$index + 3] = 255
    }
}
$bitmap = [Windows.Media.Imaging.BitmapSource]::Create(200, 160, 96, 96, [Windows.Media.PixelFormats]::Bgra32, $null, $pixels, 800)
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$fixture = Join-Path $OutputDirectory 'red-blue.png'
$stream = [IO.File]::Create($fixture); try { $encoder.Save($stream) } finally { $stream.Dispose() }
$o = Options; $o.DeviceWidth = 100; $o.DeviceHeight = 80; $o.ShrinkX = 3; $o.ShrinkY = 7
foreach ($mirror in @($false, $true)) {
    $printed = $outputType.GetMethod('Save').Invoke($null, @($OutputDirectory, $o, [string]$fixture, $null, [bool]$mirror))
    $raster = $outputType.GetMethod('ReadBitmap').Invoke($null, @([string](Join-Path $printed 'print-front-300dpi.png')))
    Near $raster.DpiX 300 0.1; Near $raster.DpiY 300 0.1
    $printPattern = Build $o
    Near $raster.PixelWidth ([Math]::Ceiling($printPattern.PrintWidth / 25.4 * 300))
    Near $raster.PixelHeight ([Math]::Ceiling($printPattern.PrintHeight / 25.4 * 300))
    $sample = [byte[]]::new(4)
    $sampleRect = [Windows.Int32Rect]::new([int]($raster.PixelWidth / 4), [int]($raster.PixelHeight / 2), 1, 1)
    $raster.CopyPixels($sampleRect, $sample, 4, 0)
    Check ($(if ($mirror) { $sample[0] -gt 240 } else { $sample[2] -gt 240 })) 'Wrong mirror direction'
    [xml]$printSvg = Get-Content (Join-Path $printed 'print-front.svg') -Raw
    Check ($printSvg.svg.ChildNodes.Count -eq 2) 'Print contains technical geometry'
    Check ($printSvg.svg.image.href.StartsWith('data:image/png;base64,')) 'SVG artwork depends on external file'
}

# Instantiate the real tab and exercise live updates in the same WPF runtime.
$tab = [Activator]::CreateInstance($tabType, [object[]]@([Action[string,bool]]{ param($m, $e) }))
$flags = [Reflection.BindingFlags]'Instance, NonPublic'
$summary = $tabType.GetField('_summary', $flags).GetValue($tab)
Check ($summary.Text.Contains('335')) 'Tab failed to calculate initial model'
foreach ($kind in @(0, 1, 2, 3)) {
    $tabType.GetField('_quilting', $flags).GetValue($tab).SelectedIndex = $kind
    Check ($null -ne $tabType.GetField('_preview', $flags).GetValue($tab).Source) 'Preview failed'
}
$tabType.GetField('_mode', $flags).GetValue($tab).SelectedIndex = 1
Check ($summary.Text.Contains('≈')) 'Diagonal mode failed'
$tabType.GetField('_mode', $flags).GetValue($tab).SelectedIndex = 0
$tabType.GetField('_width', $flags).GetValue($tab).Text = 'NaN'
Check (-not $tabType.GetField('_save', $flags).GetValue($tab).IsEnabled) 'Invalid model remains exportable'
$tabType.GetField('_width', $flags).GetValue($tab).Text = '325'
$tabType.GetField('_quilting', $flags).GetValue($tab).SelectedIndex = 1
Check ($tabType.GetField('_save', $flags).GetValue($tab).IsEnabled) 'Validation did not recover'

$tab.Width = 360; $tab.Height = 980
$assembly.GetType('VanyaTools.Native.DockerTheme').GetMethod('Apply').Invoke($null, @($tab)) | Out-Null
$frame = [Windows.Controls.Border]::new()
$frame.Background = [Windows.Media.BrushConverter]::new().ConvertFromString('#ECEFF1'); $frame.Child = $tab
$frame.Measure([Windows.Size]::new(360, 980)); $frame.Arrange([Windows.Rect]::new(0, 0, 360, 980)); $frame.UpdateLayout()
$render = [Windows.Media.Imaging.RenderTargetBitmap]::new(360, 980, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
$render.Render($frame)
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($render))
$stream = [IO.File]::Create((Join-Path $OutputDirectory 'tab-preview.png'))
try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Output "Puffy sleeve checks passed: $checks. Example: $folder. Preview: $OutputDirectory\tab-preview.png"
