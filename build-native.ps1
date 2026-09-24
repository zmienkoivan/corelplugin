param(
    [string]$Configuration = "Release",
    [switch]$SkipSign
)

$script = Join-Path $PSScriptRoot "scripts\Build-Native.ps1"
& $script -Configuration $Configuration -SkipSign:$SkipSign