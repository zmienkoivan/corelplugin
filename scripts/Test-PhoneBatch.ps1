[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $env:TEMP 'VanyaPhoneBatchChecks'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xml.Linq, System.Web.Extensions
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$optionsType = $assembly.GetType('VanyaTools.Native.PuffySleeveOptions', $true)
$patternType = $assembly.GetType('VanyaTools.Native.PuffySleevePattern', $true)
$jigType = $assembly.GetType('VanyaTools.Native.SleeveJigPlan', $true)
$jigOptionsType = $assembly.GetType('VanyaTools.Native.SleeveJigOptions', $true)
$jigOutputType = $assembly.GetType('VanyaTools.Native.SleeveJigOutput', $true)
$outputType = $assembly.GetType('VanyaTools.Native.PuffySleeveOutput', $true)
$checks = 0
function Check($ok, [string]$message) { if (-not $ok) { throw $message }; $script:checks++ }
function Near([double]$actual, [double]$expected, [double]$epsilon = 0.02) { Check ([Math]::Abs($actual - $expected) -le $epsilon) "Expected $expected, got $actual" }
function Phone { $optionsType.GetMethod('Phone').Invoke($null, @()) }
function Pattern($o) { $patternType.GetMethod('Build').Invoke($null, @($o)) }
function Jig($p, [bool]$quilt) { $jigType.GetMethod('Build').Invoke($null, @($p, [Activator]::CreateInstance($jigOptionsType, $true), $quilt)) }
function PointAt($path, [double]$fraction) {
    $point = [Windows.Point]::new(0, 0); $tangent = [Windows.Vector]::new(0, 0)
    $path.GetPointAtFractionLength($fraction, [ref]$point, [ref]$tangent)
    $point
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$o = Phone; $p = Pattern $o
Near $p.SeamWidth 85; Near $p.SeamHeight 160
Near $p.BlankWidth 125; Near $p.BlankHeight 200
Near $p.BatchWidth 250; Near $p.BatchHeight 400
Check ($p.PanelCount -eq 4) 'Expected four panels per pass'
Near $p.QuiltStep 20
foreach ($kind in @('Straight', 'Arcs', 'Chevrons')) {
    $o.Quilting = [Enum]::Parse($assembly.GetType('VanyaTools.Native.SleeveQuilting'), $kind)
    $o.ShrinkX = 3; $o.ShrinkY = 7
    $p = Pattern $o
    foreach ($quilting in @($true, $false)) {
        $j = Jig $p $quilting
        $perCell = if ($quilting) { $p.QuiltPaths.Count } else { 4 }
        Check ($j.Axes.Count -eq 4 * $perCell) 'Missing repeated stitch axes'
        Check ($j.Cuts.Count -eq $j.Axes.Count + 3) 'Unexpected through-cuts, possibly division guides'
        Near $j.SlotWidth 3.3
        Near $j.Width $(if ($quilting) { $p.BatchWidth + 50 } else { $p.BatchWidth / $p.ScaleX + 50 })
        foreach ($cut in $j.Cuts) {
            Check ($cut.Geometry.Figures.Count -eq 1) 'Extra cut islands'
            Check $cut.Geometry.Figures[0].IsClosed 'Open laser contour'
        }
        for ($cell = 1; $cell -lt 4; $cell++) {
            $dx = ($cell % 2) * $j.BlankWidth / 2
            $dy = [Math]::Floor($cell / 2) * $j.BlankHeight / 2
            for ($n = 0; $n -lt $perCell; $n++) {
                foreach ($fraction in @(0, 0.25, 0.5, 0.75, 1)) {
                    $a = PointAt $j.Axes[$n] $fraction
                    $b = PointAt $j.Axes[$cell * $perCell + $n] $fraction
                    Near ($b.X - $a.X) $dx 0.001
                    Near ($b.Y - $a.Y) $dy 0.001
                }
            }
        }
    }
}
$o = Phone; $o.Jigs = [Activator]::CreateInstance($jigOptionsType, $true)
$p = Pattern $o
$kit = $outputType.GetMethod('Save').Invoke($null, @($OutputDirectory, $o, $null, $null, $false))
$meta = Get-Content -LiteralPath (Join-Path $kit 'parameters.json') -Raw | ConvertFrom-Json
Check ($meta.product -eq 'puffy-phone-pouch-with-gusset' -and $meta.panelsPerPass -eq 4) 'Missing phone/batch metadata'
Check (@(Get-ChildItem -LiteralPath $kit -Filter '*-CUT.svg').Count -eq 4) 'Missing laser sheets'
foreach ($name in @('02-quilting.svg', '04-shaping.svg', '03-print-layout.svg')) {
    [xml]$xml = Get-Content -LiteralPath (Join-Path $kit $name) -Raw
    $ns = [Xml.XmlNamespaceManager]::new($xml.NameTable); $ns.AddNamespace('s', 'http://www.w3.org/2000/svg')
    Check ($xml.SelectNodes('//s:path[@data-role="separate-fabric"]', $ns).Count -eq 1) 'Missing division guides'
}
[xml]$xml = Get-Content -LiteralPath (Join-Path $kit '04-shaping.svg') -Raw
$ns = [Xml.XmlNamespaceManager]::new($xml.NameTable); $ns.AddNamespace('s', 'http://www.w3.org/2000/svg')
Check ($xml.SelectNodes('//s:path[@data-role="fixing-stitch"]', $ns).Count -eq 4) 'Missing four garment forms'
$quilt = Jig $p $true; $form = Jig $p $false
Near $quilt.Width 300; Near $quilt.Height 450
Near $form.Width 300; Near $form.Height 450
$jigOutputType.GetMethod('Save').Invoke($null, @($OutputDirectory, $quilt)) | Out-Null
$jigOutputType.GetMethod('Save').Invoke($null, @($OutputDirectory, $form)) | Out-Null

# Asymmetric front and a distinct back detect double mirroring, misplaced cells,
# image bleed crossing a shared border, and missing second-row artwork.
function Fixture([string]$name, [bool]$green) {
    $pixels = [byte[]]::new(100 * 200 * 4)
    for ($y = 0; $y -lt 200; $y++) { for ($x = 0; $x -lt 100; $x++) {
        $k = ($y * 100 + $x) * 4
        if ($green) { $pixels[$k + 1] = 255 } elseif ($x -lt 50) { $pixels[$k + 2] = 255 } else { $pixels[$k] = 255 }
        $pixels[$k + 3] = 255
    } }
    $bitmap = [Windows.Media.Imaging.BitmapSource]::Create(100, 200, 96, 96, [Windows.Media.PixelFormats]::Bgra32, $null, $pixels, 400)
    $encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $path = Join-Path $OutputDirectory $name
    $stream = [IO.File]::Create($path); try { $encoder.Save($stream) } finally { $stream.Dispose() }
    $path
}
$front = Fixture 'front-red-blue.png' $false; $back = Fixture 'back-green.png' $true
$o.ShrinkX = 3; $o.ShrinkY = 7; $p = Pattern $o
foreach ($mirror in @($false, $true)) {
    $saved = $outputType.GetMethod('Save').Invoke($null, @($OutputDirectory, $o, [string]$front, [string]$back, $mirror))
    $image = $outputType.GetMethod('ReadBitmap').Invoke($null, @([string](Join-Path $saved 'print-batch-4-300dpi.png')))
    Near $image.DpiX 300 0.1; Near $image.DpiY 300 0.1
    Near $image.PixelWidth ([Math]::Ceiling($p.BatchPrintWidth / 25.4 * 300))
    Near $image.PixelHeight ([Math]::Ceiling($p.BatchPrintHeight / 25.4 * 300))
    for ($row = 0; $row -lt 2; $row++) {
        foreach ($sample in @(@(($p.Bleed + $p.BlankWidth / 4), 2), @(($p.Bleed + 3 * $p.BlankWidth / 4), 0), @(($p.Bleed + 1.5 * $p.BlankWidth), 1))) {
            $x = [double]$sample[0]; if ($mirror) { $x = $p.BatchPrintWidth - $x }
            $y = $p.Bleed + ($row + 0.5) * $p.BlankHeight
            $pixel = [byte[]]::new(4)
            $rect = [Windows.Int32Rect]::new([int]($x / 25.4 * 300), [int]($y / 25.4 * 300), 1, 1)
            $image.CopyPixels($rect, $pixel, 4, 0)
            Check ($pixel[[int]$sample[1]] -gt 240) 'Incorrect cell artwork or mirror'
        }
    }
    [xml]$printSvg = Get-Content -LiteralPath (Join-Path $saved 'print-batch-4.svg') -Raw
    Check ($printSvg.svg.ChildNodes.Count -eq 2 -and $printSvg.svg.image.href.StartsWith('data:image/png;base64,')) 'Batch print contains technical marks or external images'
}
$tabType = $assembly.GetType('VanyaTools.Native.PuffySleeveTab')
$tab = [Activator]::CreateInstance($tabType, [object[]]@([Action[string,bool]]{ param($m, $e) }))
$flags = [Reflection.BindingFlags]'Instance, NonPublic'
function Field($name) { $tabType.GetField($name, $flags).GetValue($tab) }
function FieldNumber($name) { [double]::Parse((Field $name).Text.Replace(',', '.'), [Globalization.CultureInfo]::InvariantCulture) }
(Field '_width').Text = '330.125'; (Field '_step').Text = '46.5'
(Field '_product').SelectedIndex = 1
Check ((Field '_fourUp').IsChecked -and -not (Field '_mode').IsEnabled -and (Field '_save').IsEnabled) 'Phone preset/4-up mode failed'
Near (FieldNumber '_width') 75; Near (FieldNumber '_height') 150
Near (FieldNumber '_step') 20
(Field '_step').Text = '17.5'
(Field '_product').SelectedIndex = 0
Near (FieldNumber '_width') 330.125 0.00001
Near (FieldNumber '_step') 46.5
Check (-not (Field '_fourUp').IsChecked) 'Phone batch mode leaked into laptop draft'
(Field '_mode').SelectedIndex = 1
(Field '_diagonal').Text = '15.6'; (Field '_bezel').Text = '7.5'
(Field '_product').SelectedIndex = 1; (Field '_product').SelectedIndex = 0
Near (FieldNumber '_width') 330.125 0.00001
Near (FieldNumber '_diagonal') 15.6
Near (FieldNumber '_bezel') 7.5
Check ((Field '_mode').SelectedIndex -eq 1) 'Laptop diagonal mode was lost'
(Field '_product').SelectedIndex = 1
Near (FieldNumber '_step') 17.5
Check ((Field '_jigShape').IsEnabled -and (Field '_jigQuilt').IsEnabled) 'Phone jig export disabled'
$tab.Width = 380; $tab.Height = 1150
$assembly.GetType('VanyaTools.Native.DockerTheme').GetMethod('Apply').Invoke($null, @($tab)) | Out-Null
$frame = [Windows.Controls.Border]::new(); $frame.Background = [Windows.Media.Brushes]::White; $frame.Child = $tab
$frame.Measure([Windows.Size]::new(380, 1150)); $frame.Arrange([Windows.Rect]::new(0, 0, 380, 1150)); $frame.UpdateLayout()
$render = [Windows.Media.Imaging.RenderTargetBitmap]::new(380, 1150, 96, 96, [Windows.Media.PixelFormats]::Pbgra32); $render.Render($frame)
$encoder = [Windows.Media.Imaging.PngBitmapEncoder]::new(); $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($render))
$stream = [IO.File]::Create((Join-Path $OutputDirectory 'phone-tab.png')); try { $encoder.Save($stream) } finally { $stream.Dispose() }
Write-Output "Phone four-up checks passed: $checks. Specimens: $OutputDirectory"
