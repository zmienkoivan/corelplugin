param(
    [string]$CorelAddonsPath,
    [switch]$Build
)

$script = Join-Path $PSScriptRoot "scripts\Install-Native.ps1"
& $script -CorelAddonsPath $CorelAddonsPath -Build:$Build
