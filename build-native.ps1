param(
    [string]$Configuration = "Release"
)

$script = Join-Path $PSScriptRoot "scripts\Build-Native.ps1"
& $script -Configuration $Configuration
