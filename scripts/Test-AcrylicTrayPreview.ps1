[CmdletBinding()]
param([string]$OutputDirectory=(Join-Path $env:TEMP 'VanyaTrayPreviewChecks'))
$ErrorActionPreference='Stop'
Add-Type -AssemblyName PresentationFramework,PresentationCore,WindowsBase
$repo=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$assembly=[Reflection.Assembly]::LoadFrom((Join-Path $repo 'native\VanyaTools.Native\bin\x64\Release\net48\VanyaTools.Native.dll'))
$tabType=$assembly.GetType('VanyaTools.Native.AcrylicTrayTab',$true)
$sceneType=$assembly.GetType('VanyaTools.Native.AcrylicTrayScene',$true)
$flags=[Reflection.BindingFlags]'Instance,Public,NonPublic'
$checks=0
function Check($ok,[string]$message){if(-not $ok){throw $message};$script:checks++}
$tab=[Activator]::CreateInstance($tabType,[object[]]@([Action[string,bool]]{param($m,$e)}))
$assembly.GetType('VanyaTools.Native.DockerTheme').GetMethod('Apply').Invoke($null,@($tab))|Out-Null
function Field([string]$name){$tabType.GetField($name,$flags).GetValue($tab)}
function SetField([string]$name,$value){$tabType.GetField($name,$flags).SetValue($tab,$value)}
function Pump{
    $frame=[Windows.Threading.DispatcherFrame]::new()
    $timer=[Windows.Threading.DispatcherTimer]::new();$timer.Interval=[TimeSpan]::FromMilliseconds(650);$timer.Tag=$frame
    $timer.Add_Tick({param($sender,$eventArgs)$sender.Stop();$sender.Tag.Continue=$false})
    $timer.Start();[Windows.Threading.Dispatcher]::PushFrame($frame)
    $tab.Measure([Windows.Size]::new(380,900));$tab.Arrange([Windows.Rect]::new(0,0,380,900));$tab.UpdateLayout()
}
function Notice{Field '_previewMessage'}
function Valid{
    Check ($null-ne (Field '_pattern')) 'Current geometry was rejected'
    Check ($null-ne (Field '_scene').Assembly) 'Valid model is invisible'
    Check ((Notice).Visibility-eq [Windows.Visibility]::Collapsed) 'Old preview warning survived a valid rebuild'
    Check ((Field '_create').IsEnabled-and (Field '_assembly').IsEnabled-and (Field '_save').IsEnabled) 'Valid model cannot be exported'
}
function Rejected($lastAssembly){
    Check ($null-eq (Field '_pattern')) 'Rejected dimensions remain exportable'
    Check ([Object]::ReferenceEquals($lastAssembly,(Field '_scene').Assembly)) 'Rejected dimensions erased the last 3D model'
    Check ((Notice).Visibility-eq [Windows.Visibility]::Visible) 'Validation error is hidden below the settings'
    Check ((Notice).Text.Contains((Field '_summary').Text)) 'Pinned warning does not identify the invalid dimension'
    Check (-not (Field '_create').IsEnabled-and -not (Field '_assembly').IsEnabled-and -not (Field '_save').IsEnabled) 'Stale preview can be exported as current dimensions'
    Check ([Object]::ReferenceEquals($lastAssembly,$large.Assembly)) 'Large preview was erased'
    Check ($largeMessage.Text-eq (Notice).Text-and $largeMessage.Visibility-eq [Windows.Visibility]::Visible) 'Large preview hides its stale-state warning'
}
function Save([Windows.Media.Imaging.BitmapSource]$bitmap,[string]$name){
    $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream=[IO.File]::Create((Join-Path $OutputDirectory $name));try{$encoder.Save($stream)}finally{$stream.Dispose()}
}
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
Pump;Valid
(Field '_wedges').IsChecked=$true;Pump;Valid
Check (@((Field '_scene').Assembly.Parts|Where-Object {$_.Id.StartsWith('wedge-')}).Count-gt 0) 'Checkbox did not add the actual 3D retainers'
$scene=Field '_scene';Save ($scene.RenderPng(900)) 'tray-with-retainers.png'
$large=[Activator]::CreateInstance($sceneType,$true)
$large.Set((Field '_displayedPattern'),$scene.Assembly)
$largeMessage=[Windows.Controls.TextBlock]::new();SetField '_largeScene' $large;SetField '_largeMessage' $largeMessage
(Field '_wedges').IsChecked=$false;Pump;Valid
(Field '_width').Text='30';Pump;Valid
$last=$scene.Assembly
(Field '_wedges').IsChecked=$true
Check ($null-eq (Field '_pattern')-and -not (Field '_create').IsEnabled) 'Pending checkbox change permits an export of old geometry'
Pump;Rejected $last
Check ((Field '_width').Text-eq '30') 'Invalid user dimensions were silently changed'
$panelBitmap=[Windows.Media.Imaging.RenderTargetBitmap]::new(380,900,96,96,[Windows.Media.PixelFormats]::Pbgra32)
$panelBitmap.Render($tab);Save $panelBitmap 'tray-preview-warning.png'
(Field '_width').Text='40';Pump;Valid
Check ([Object]::ReferenceEquals($scene.Assembly,$large.Assembly)) 'Large view did not receive the recovered model'
Check ($largeMessage.Visibility-eq [Windows.Visibility]::Collapsed) 'Large-view warning did not clear'
$last=$scene.Assembly;(Field '_depth').Text='19';Pump;Rejected $last
(Field '_depth').Text='20';Pump;Valid
$last=$scene.Assembly;(Field '_angle').Text='';Pump;Rejected $last
(Field '_angle').Text='45';Pump;Valid
$last=$scene.Assembly;(Field '_rows').Text='0';Pump;Rejected $last
(Field '_rows').Text='3';Pump;Valid
(Field '_wedges').IsChecked=$false;Pump;Valid
(Field '_retainerTabWidth').Text='invalid';Pump;Valid
(Field '_timer').Stop();SetField '_largeScene' $null;SetField '_largeMessage' $null
Write-Output "Passed $checks tray preview continuity checks. Artifacts: $OutputDirectory"
