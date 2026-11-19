[CmdletBinding()]
param([string]$OutputDirectory=(Join-Path $env:TEMP 'VanyaTrayCardChecks'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$optionsType=$assembly.GetType('VanyaTools.Native.AcrylicTrayOptions',$true)
$patternType=$assembly.GetType('VanyaTools.Native.AcrylicTrayPattern',$true)
$cardsType=$assembly.GetType('VanyaTools.Native.AcrylicTrayCards',$true)
$tabType=$assembly.GetType('VanyaTools.Native.AcrylicTrayTab',$true)
$modeType=$assembly.GetType('VanyaTools.Native.AcrylicTrayFloorMode',$true)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic';$checks=0
function Check($ok,[string]$message){if(-not $ok){throw $message};$script:checks++}
function Near([double]$a,[double]$b){Check ([Math]::Abs($a-$b)-lt 0.0001) "Expected $b, got $a"}
function Options{[Activator]::CreateInstance($optionsType,$true)}
function Build($o){$patternType.GetMethod('Build').Invoke($null,@($o))}
function Pose($p,[int]$row,[int]$col){$cardsType.GetMethod('Place').Invoke($null,@($p,$row,$col))}
function Verify($p){
    for($row=0;$row-lt $p.Options.Rows;$row++){
        $plane=$p.Options.FloorMode.ToString()-eq 'Plane';$f=$p.Floors[$(if($plane){0}else{$row})]
        $front=$p.Crosses[$(if($plane){$row}else{2*$row})];$rear=$p.Crosses[$(if($plane){$row+1}else{2*$row+1})]
        for($col=0;$col-lt $p.Options.Columns;$col++){
            $pose=Pose $p $row $col;$direction=$pose.Top-$pose.Bottom
            Near $direction.Length 30;Near $pose.Bottom.X $p.CellCenter($col)
            Near $pose.Bottom.Y ($front.Y+$p.Options.Thickness/2+0.12)
            Near $pose.Bottom.Z ($f.Top($pose.Bottom.Y)+0.12)
            foreach($s in @(0,0.25,0.5,0.75,1)){$point=$pose.Bottom+$direction*$s;Check ($point.Z-ge $f.Top($point.Y)-0.0001) 'Card penetrates the cell floor'}
            if($pose.Rest.ToString()-eq 'RearTop'){
                $support=$pose.RearTop-$pose.Bottom
                Near ([Windows.Media.Media3D.Vector3D]::CrossProduct($support,$direction).Length) 0
                Check ($support.Length-le 30.0001) 'Card stretched to reach the rear rim'
                Near $pose.RearTop.Y ($rear.Y-$p.Options.Thickness/2-0.12)
                Near $pose.RearTop.Z ($rear.Bottom+$rear.Height+0.12)
            }elseif($pose.Rest.ToString()-eq 'RearFace'){
                Near $pose.Top.Y ($rear.Y-$p.Options.Thickness/2-0.12)
                Check ($pose.Top.Z-ge $rear.Bottom+0.119) 'Card rests in air below the rear wall'
                Check ($pose.Top.Z-le $rear.Bottom+$rear.Height+0.121) 'Card passes through rear rim'
            }else{Near $pose.Top.Z ($f.Top($pose.Top.Y)+0.12)}
        }
    }
}
foreach($angle in @(0,20,30,45,60)){foreach($depth in @(20,40)){$o=Options;$o.AngleDegrees=$angle;$o.CellDepth=$depth;Verify (Build $o)}}
$o=Options;$o.UseWedges=$true;Verify (Build $o)
$o=Options;$o.FloorMode=[Enum]::Parse($modeType,'Steps');Verify (Build $o)
$o=Options;$o.CellWidth=20;Check ($null-eq (Pose (Build $o) 0 0)) 'Oversized card forced into narrow cell'
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$tab=[Activator]::CreateInstance($tabType,[object[]]@([Action[string,bool]]{param($m,$e)}))
function Field([string]$name){$tabType.GetField($name,$flags).GetValue($tab)}
(Field '_angle').Text='0';(Field '_wedges').IsChecked=$true
$tabType.GetMethod('Update',$flags).Invoke($tab,@())|Out-Null
$scene=Field '_scene';$scene.Height=400;$scene.Measure([Windows.Size]::new(800,400));$scene.Arrange([Windows.Rect]::new(0,0,800,400));$scene.View(2)
$samples=$scene.GetType().GetField('_samples',$flags).GetValue($scene)
Check ($samples.Children.Count-eq 12) 'Realistic sample meshes missing'
foreach($card in $samples.Children){Near ($card.Geometry.Positions[3]-$card.Geometry.Positions[0]).Length 30;Near ($card.Geometry.Positions[1]-$card.Geometry.Positions[0]).Length 30}
$bitmap=$scene.RenderPng(1000);$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
$stream=[IO.File]::Create((Join-Path $OutputDirectory 'cards-resting-side.png'));try{$encoder.Save($stream)}finally{$stream.Dispose()}
$scene.View(0);$bitmap=$scene.RenderPng(1000);$encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap));$stream=[IO.File]::Create((Join-Path $OutputDirectory 'cards-and-wedges-3d.png'));try{$encoder.Save($stream)}finally{$stream.Dispose()}
(Field '_timer').Stop()
Write-Output "Passed $checks realistic card support checks. Artifacts: $OutputDirectory"
