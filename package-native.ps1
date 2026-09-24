param(
    [string]$Configuration = "Release"
)

$script = Join-Path $PSScriptRoot "scripts\Package-Native.ps1"
& $script -Configuration $Configuration
