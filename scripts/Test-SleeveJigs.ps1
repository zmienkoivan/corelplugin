[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $env:TEMP 'VanyaSleeveJigChecks'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Xml.Linq, System.Web.Extensions
Add-Type -Path (Join-Path $PSScriptRoot 'fixtures\SleeveJigCorelFake.cs')
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly = [Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$optionType = $assembly.GetType('VanyaTools.Native.PuffySleeveOptions', $true)
$patternType = $assembly.GetType('VanyaTools.Native.PuffySleevePattern', $true)
$jigOptionType = $assembly.GetType('VanyaTools.Native.SleeveJigOptions', $true)
$jigType = $assembly.GetType('VanyaTools.Native.SleeveJigPlan', $true)
$outputType = $assembly.GetType('VanyaTools.Native.SleeveJigOutput', $true)
$build = $jigType.GetMethod('Build')
$checks = 0
function Check($value, [string]$message) { if (-not $value) { throw $message }; $script:checks++ }
function Near([double]$a, [double]$b, [double]$tolerance = 0.02) { Check ([Math]::Abs($a - $b) -le $tolerance) "Expected $b, got $a" }
function Length($geometry) {
    $total = 0.0
    foreach ($figure in $geometry.Figures) {
        $previous = $figure.StartPoint
        foreach ($point in $jigType.GetMethod('Points').Invoke($null, @($figure))) {
            $total += ($point - $previous).Length; $previous = $point
        }
        if ($figure.IsClosed) { $total += ($previous - $figure.StartPoint).Length }
    }
    $total
}
function CleanSlots($plan) {
    for ($i = 0; $i -lt $plan.Axes.Count; $i++) {
        $outline = $plan.Cuts[$i + 1].Geometry
        Check ($outline.Figures.Count -eq 1) 'Slot has extra cut islands'
        # The two edges and two round ends form one perimeter. Internal joins
        # left by widening a curved path would add unwanted laser travel.
        Near (Length $outline) (2 * (Length $plan.Axes[$i]) + [Math]::PI * $plan.DrawnSlotWidth) 0.3
    }
}
function Options { [Activator]::CreateInstance($optionType, $true) }
function JigOptions {
    $j = [Activator]::CreateInstance($jigOptionType, $true)
    $j.GuideDiameter = 4; $j.GuideHeight = 0.7; $j.CenteredGuideConfirmed = $true; $j.Kerf = 0.15
    $j.PlasticThickness = 1
    $j
}
function Pattern($o) { $patternType.GetMethod('Build').Invoke($null, @($o)) }
function Jig($p, $j, [bool]$quilt) { $build.Invoke($null, @($p, $j, $quilt)) }
function Reject($p, $j, [bool]$quilt) {
    $failed = $false; try { $null = Jig $p $j $quilt } catch { $failed = $true }
    Check $failed 'Unsafe or invalid jig geometry accepted'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$o = Options; $p = Pattern $o; $j = JigOptions
$quilt = Jig $p $j $true; $form = Jig $p $j $false
CleanSlots $quilt; CleanSlots $form
Near $quilt.Width 425; Near $quilt.Height 325
Near $quilt.SlotWidth 4.3; Near $quilt.DrawnSlotWidth 4.15
Near $quilt.Cuts[0].Geometry.Bounds.Width ($quilt.Width + 0.15)
Near $quilt.Cuts[1].Geometry.Bounds.Height 4.15
Near $quilt.Cuts[1].Geometry.Bounds.Width ($quilt.Axes[0].Bounds.Width + 4.15)
Check ($form.Axes.Count -eq 4) 'Form template must retain four bridges'
Check ($form.Cuts.Count -eq 7) 'Expected exterior, four slots, two alignment holes'
Near ($form.Cuts[1].Geometry.Bounds.X - $form.Cuts[4].Geometry.Bounds.Right) ($j.Bridge + $j.Kerf)
foreach ($plan in @($quilt, $form)) {
    foreach ($cut in $plan.Cuts) {
        foreach ($figure in $cut.Geometry.Figures) {
            Check $figure.IsClosed 'Open CUT path'
            Check ($figure.Segments.Count -ge 3) 'Degenerate CUT polygon'
        }
    }
}
# The centre stays joined to the frame along the vertical bridge lane.
$material = [Windows.Media.Geometry]::Parse($jigType.GetMethod('Data').Invoke($null, @($form.Cuts[0].Geometry)))
for ($i = 1; $i -lt $form.Cuts.Count; $i++) {
    $material = [Windows.Media.Geometry]::Combine($material, $form.Cuts[$i].Geometry, [Windows.Media.GeometryCombineMode]::Exclude, $null)
}
for ($y = 1; $y -lt $form.Height; $y += 3) {
    Check ($material.FillContains([Windows.Point]::new($form.Width / 2, $y))) 'Centre island detached or bridge crossed by cut'
}

$o.ShrinkX = 3; $o.ShrinkY = 7
$shrunk = Jig (Pattern $o) $j $true
Near $shrunk.Cuts[1].Geometry.Bounds.Height $quilt.Cuts[1].Geometry.Bounds.Height
Near $shrunk.Width ($p.BlankWidth / 0.97 + 2 * $j.Frame)
$o = Options; $o.CommonBlankWidth = 450; $o.CommonBlankHeight = 350
$common = Jig (Pattern $o) $j $true
$o.DeviceWidth = 280; $o.DeviceHeight = 190
$small = Jig (Pattern $o) $j $true
Check ($small.Axes.Count -eq $common.Axes.Count) 'Common jig changed stitch count'
for ($i = 0; $i -lt $common.Axes.Count; $i++) {
    Near $small.Axes[$i].Bounds.X $common.Axes[$i].Bounds.X
    Near $small.Axes[$i].Bounds.Y $common.Axes[$i].Bounds.Y
    Near $small.Axes[$i].Bounds.Width $common.Axes[$i].Bounds.Width
}
foreach ($kind in @('Arcs', 'Chevrons')) {
    $o = Options
    $o.Quilting = [Enum]::Parse($assembly.GetType('VanyaTools.Native.SleeveQuilting'), $kind)
    $case = Jig (Pattern $o) $j $true
    CleanSlots $case
    foreach ($cut in $case.Cuts) { foreach ($figure in $cut.Geometry.Figures) { Check $figure.IsClosed 'Curved slot is open' } }
}
$bad = JigOptions; $bad.CenteredGuideConfirmed = $false; Reject $p $bad $true
$bad = JigOptions; $bad.GuideHeight = 1.2; Reject $p $bad $true
$bad = JigOptions; $bad.GuideDiameter = [double]::NaN; Reject $p $bad $false
$bad = JigOptions; $bad.Kerf = 0.9; $bad.RegistrationHole = 0.5; Reject $p $bad $false
$o = Options; $o.CornerRadius = 0; Reject (Pattern $o) $j $false
$o = Options; $o.QuiltStep = 10
$bad = JigOptions; $bad.GuideDiameter = 9; Reject (Pattern $o) $bad $true

foreach ($plan in @($quilt, $form)) {
    $outputType.GetMethod('Save').Invoke($null, @($OutputDirectory, $plan)) | Out-Null
    $a = $outputType.GetMethod('SvgDocument').Invoke($null, @($plan, $false, $false))
    $b = $outputType.GetMethod('SvgDocument').Invoke($null, @($plan, $true, $false))
    Check ($a.Root.Attribute('width').Value.EndsWith('mm')) 'SVG lost physical units'
    Check ($a.Root.Element([Xml.Linq.XName]::Get('g', 'http://www.w3.org/2000/svg')).ToString() -eq
        $b.Root.Element([Xml.Linq.XName]::Get('g', 'http://www.w3.org/2000/svg')).ToString()) 'Top/bottom CUT paths do not match'
    Check (-not $a.ToString().Contains('<text')) 'Text leaked into laser CUT file'
    Check (-not $a.ToString().Contains('stroke-dasharray')) 'Dashes leaked into laser CUT file'
}
$o = Options; $o.Jigs = $j
$kit = $assembly.GetType('VanyaTools.Native.PuffySleeveOutput').GetMethod('Save').Invoke($null,
    @($OutputDirectory, $o, $null, $null, $false))
Check (@(Get-ChildItem -LiteralPath $kit -Filter '*-CUT.svg').Count -eq 4) 'Complete kit is missing the four plastic sheets'
$metadata = Get-Content (Join-Path $kit 'parameters.json') -Raw | ConvertFrom-Json
Near $metadata.options.Jigs.GuideDiameter 4
Near $metadata.options.Jigs.Kerf 0.15

# Verify the native Corel adapter: mm pages, matched closed curves, isolated
# printable CUT layer, and rollback on a failed creation.
$native = @($outputType.GetMethods() | Where-Object { $_.Name -eq 'CreateInCorel' -and $_.GetParameters().Count -eq 2 })[0]
$app = [JigTestApp]::new()
$doc = $native.Invoke($null, @($app, $form))
Check ($doc.Pages.Count -eq 2 -and $doc.Unit -eq 3) 'Expected two millimetre pages'
Check ($doc.Rulers.HUnits -eq 3 -and $doc.Rulers.VUnits -eq 3) 'Expected millimetre rulers in saved CDR'
Check $doc.Ended 'Undo group left open'
Check (-not $app.Optimization) 'Corel optimization was not restored'
foreach ($page in $doc.Pages.Items) {
    Near $page.Width $form.PageWidth; Near $page.Height $form.PageHeight
    Check ($page.Layers[0].Name.StartsWith('CUT') -and $page.Layers[0].Printable) 'Missing printable CUT layer'
    Check (-not $page.Layers[1].Printable -and -not $page.Layers[2].Printable) 'Engraving or labels unexpectedly print'
    Check ($page.Layers[0].Shapes.Count -eq $form.Cuts.Count) 'Missing Corel cut contours'
    foreach ($shape in $page.Layers[0].Shapes) {
        Check ($shape.Fill.Empty -and $shape.Outline.Color.R -eq 255 -and $shape.Outline.Color.G -eq 0) 'Cut style is not unfilled red'
        foreach ($path in $shape.Curve.Paths) { Check $path.Closed 'Native Corel cut curve is open' }
    }
}
$app = [JigTestApp]::new(); $app.FailCurve = $true
$failed = $false; try { $null = $native.Invoke($null, @($app, $form)) } catch { $failed = $true }
Check ($failed -and $app.LastDocument.Closed -and $app.LastDocument.Ended -and -not $app.Optimization) 'Failed jig document was not rolled back'

# Missing machine measurements must not block garment patterns or allow CUT.
$tabType = $assembly.GetType('VanyaTools.Native.PuffySleeveTab')
$tab = [Activator]::CreateInstance($tabType, [object[]]@([Action[string,bool]]{ param($m, $e) }))
$flags = [Reflection.BindingFlags]'Instance, NonPublic'
function Field($name) { $tabType.GetField($name, $flags).GetValue($tab) }
Near ([double](Field '_guideDiameter').Text) 3
Near ([double](Field '_guideHeight').Text) 1
Near ([double](Field '_plasticThickness').Text) 2
Check ((Field '_guideCentered').IsChecked -and (Field '_jigShape').IsEnabled) 'Confirmed machine preset did not enable jig export'
$preset = [Activator]::CreateInstance($jigOptionType, $true)
Near (Jig $p $preset $false).SlotWidth 3.3
(Field '_guideDiameter').Text = ''
Check (-not (Field '_jigShape').IsEnabled -and (Field '_save').IsEnabled) 'Unmeasured jig enabled or ordinary export blocked'
(Field '_guideDiameter').Text = '4'; (Field '_guideHeight').Text = '0,7'; (Field '_guideCentered').IsChecked = $true
Check ((Field '_jigShape').IsEnabled -and (Field '_jigQuilt').IsEnabled) 'Measured jig did not become available'
(Field '_includeJigs').IsChecked = $true
(Field '_guideHeight').Text = '3'
Check (-not (Field '_save').IsEnabled) 'Invalid jig included in complete export'
(Field '_includeJigs').IsChecked = $false
Check ((Field '_save').IsEnabled) 'Ordinary export failed to recover'
Write-Output "Sleeve laser jig checks passed: $checks. SVG specimens: $OutputDirectory (test guide 4 mm)."
