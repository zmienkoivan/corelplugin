[CmdletBinding()]
param([string]$OutputDirectory=(Join-Path $env:TEMP 'VanyaTrayRearWallChecks'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$optionsType=$assembly.GetType('VanyaTools.Native.AcrylicTrayOptions',$true)
$patternType=$assembly.GetType('VanyaTools.Native.AcrylicTrayPattern',$true)
$modelType=$assembly.GetType('VanyaTools.Native.AcrylicTrayAssembly3D',$true)
$cardsType=$assembly.GetType('VanyaTools.Native.AcrylicTrayCards',$true)
$tabType=$assembly.GetType('VanyaTools.Native.AcrylicTrayTab',$true)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$checks=0
function Check($ok,[string]$message){if(-not $ok){throw $message};$script:checks++}
function Near([double]$a,[double]$b){Check ([Math]::Abs($a-$b)-lt 0.003) "Expected $b, got $a"}
function Options{[Activator]::CreateInstance($optionsType,$true)}
function Build($o){$patternType.GetMethod('Build').Invoke($null,@($o))}
function Verify($o){
    $baseOptions=$o.Copy();$baseOptions.RearWallHeight=$o.BoardHeight;$original=Build $baseOptions
    $p=Build $o;$rearId='cross-'+$p.Crosses.Count
    Near $p.Crosses[-1].Height $o.RearWallHeight
    Check ($p.Parts.Count-eq $original.Parts.Count) 'Raising the rear wall added extra parts'
    foreach($part in $p.Parts){
        $before=$original.Parts|Where-Object Id -eq $part.Id
        if($part.Id-ne $rearId){Check ($part.Path-ceq $before.Path) "Other cut part changed: $($part.Id)"}
    }
    for($i=0;$i-lt $p.Crosses.Count;$i++){
        Near $p.Crosses[$i].Bottom $original.Crosses[$i].Bottom
        Near $p.Crosses[$i].JointZ $original.Crosses[$i].JointZ
        if($i-lt $p.Crosses.Count-1){Near $p.Crosses[$i].Height $o.BoardHeight}
    }
    $last=$p.Crosses[-1];$top=$last.Bottom+$o.RearWallHeight
    $rear=$p.Parts|Where-Object Id -eq $rearId
    Near (-$rear.AssemblyBounds.Top) $top
    Check ($p.Height-ge $top-0.001) 'Model height clips the rear wall'
    $m=$modelType.GetMethod('Build').Invoke($null,@($p));$mesh=$m.Parts|Where-Object Id -eq $rearId
    Near ($mesh.Faces.Bounds.Z+$mesh.Faces.Bounds.SizeZ) $top
    $pose=$cardsType.GetMethod('Place').Invoke($null,@($p,($o.Rows-1),0))
    Near $pose.RearTop.Z ($top+0.12)
    Near ($pose.Top-$pose.Bottom).Length 30
}
foreach($t in @(2,3)){foreach($angle in @(0,20,60)){
    $o=Options;$o.Thickness=$t;$o.AngleDegrees=$angle;$o.UseWedges=$true;Verify $o
}}
$o=Options;$o.Columns=1;$o.Rows=1;$o.RearWallHeight=60;Verify $o
$o=Options;$o.RearWallHeight=200;Verify $o
$o=Options;$o.FloorMode=[Enum]::Parse($assembly.GetType('VanyaTools.Native.AcrylicTrayFloorMode'),'Steps');Verify $o
foreach($bad in @(-1,14.9,201,[double]::NaN,[double]::PositiveInfinity)){
    $o=Options;$o.RearWallHeight=$bad;$rejected=$false;try{$null=Build $o}catch{$rejected=$true}
    Check $rejected 'Invalid rear wall height accepted'
}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$tab=[Activator]::CreateInstance($tabType,[object[]]@([Action[string,bool]]{param($m,$e)}))
function Field([string]$name){$tabType.GetField($name,$flags).GetValue($tab)}
function Refresh{$tabType.GetMethod('Update',$flags).Invoke($tab,@())|Out-Null}
(Field '_wedges').IsChecked=$true;(Field '_rearHeight').Text='35,5';Refresh
Check ((Field '_create').IsEnabled) 'Rear wall height control rejected decimal comma'
Near (Field '_pattern').Options.RearWallHeight 35.5
(Field '_rearHeight').Text='14';Refresh;Check (-not (Field '_create').IsEnabled) 'Invalid rear wall height can be exported'
(Field '_rearHeight').Text='30';Refresh
Check ((Field '_create').IsEnabled) 'Corrected rear height stays invalid'
$scene=Field '_scene';$scene.ShowSamples=$false;$scene.Height=420
$scene.Measure([Windows.Size]::new(720,420));$scene.Arrange([Windows.Rect]::new(0,0,720,420));$scene.View(0)
$bitmap=$scene.RenderPng(1000);$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream=[IO.File]::Create((Join-Path $OutputDirectory 'raised-rear-wall.png'));try{$encoder.Save($stream)}finally{$stream.Dispose()}
(Field '_timer').Stop()
Write-Output "Passed $checks rear-wall checks. Preview: $OutputDirectory"
