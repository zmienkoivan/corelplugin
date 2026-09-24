[CmdletBinding()]
param(
    [string]$CorelAddonsPath,
    [switch]$Build
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) "..")).Path

if ($Build) {
    & (Join-Path $repoRoot "build-native.ps1")
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$addonSource = Join-Path $repoRoot "addon\VanyaToolsNative"
$dll = Join-Path $addonSource "VanyaTools.Native.dll"
if (-not (Test-Path -LiteralPath $dll)) {
    throw "VanyaTools.Native.dll was not found. Run .\build-native.ps1 first, or use .\install-native.ps1 -Build."
}

function Find-CorelAddonsPath {
    $roots = @($env:ProgramFiles, [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")) | Where-Object { $_ }
    foreach ($root in $roots) {
        $corelRoot = Join-Path $root "Corel"
        if (Test-Path -LiteralPath $corelRoot) {
            $found = Get-ChildItem -LiteralPath $corelRoot -Directory -Recurse -Depth 3 -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -eq "Programs64" } |
                ForEach-Object {
                    $addons = Join-Path $_.FullName "Addons"
                    if (Test-Path -LiteralPath $addons) { Get-Item -LiteralPath $addons }
                } |
                Sort-Object LastWriteTime -Descending |
                Select-Object -First 1
            if ($found) { return $found.FullName }
        }
    }
    return $null
}

if (-not $CorelAddonsPath) {
    $CorelAddonsPath = Find-CorelAddonsPath
}

if (-not $CorelAddonsPath) {
    throw 'Could not find CorelDRAW Programs64\Addons folder. Pass -CorelAddonsPath explicitly.'
}

$installPath = Join-Path $CorelAddonsPath "VanyaToolsNative"
try {
    if (-not (Test-Path -LiteralPath $installPath)) {
        New-Item -ItemType Directory -Path $installPath | Out-Null
    }

    Get-ChildItem -LiteralPath $addonSource -Force | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $installPath $_.Name) -Recurse -Force
    }
}
catch [UnauthorizedAccessException] {
    Write-Host ""
    Write-Host "Windows denied access to CorelDRAW Addons folder:" -ForegroundColor Red
    Write-Host "  $CorelAddonsPath"
    Write-Host ""
    Write-Host "Run PowerShell as Administrator and repeat:"
    Write-Host "  .\install-native.ps1"
    Write-Host ""
    Write-Host "Or copy this folder manually:"
    Write-Host "  $addonSource"
    Write-Host "to:"
    Write-Host "  $installPath"
    exit 1
}

# Report the installed build identifier (the status string compiled into the docker),
# plus the installed DLL timestamp and signature — so it's always clear what is live.
$installedDll = Join-Path $installPath "VanyaTools.Native.dll"
$buildId = "unknown"
$dockerSrc = Join-Path $repoRoot "native\VanyaTools.Native\VanyaToolsDocker.cs"
if (Test-Path -LiteralPath $dockerSrc) {
    # Match the build date inside the status string (e.g. 2026.06.20.5900), encoding-independent.
    $m = Select-String -LiteralPath $dockerSrc -Pattern '(\d{4}\.\d{2}\.\d{2}\.\d{3,4})' | Select-Object -First 1
    if ($m) { $buildId = $m.Matches[0].Groups[1].Value }
}
$sig = (Get-AuthenticodeSignature -LiteralPath $installedDll).Status

Write-Host ""
Write-Host "Installed native Vanya Tools addon:" -ForegroundColor Green
Write-Host "  Build:     $buildId"
Write-Host "  DLL:       $installedDll"
Write-Host "  Modified:  $((Get-Item $installedDll).LastWriteTime)"
Write-Host "  Signature: $sig"
Write-Host ""
Write-Host "Restart CorelDRAW, then open:"
Write-Host "  Window > Dockers > Vanya Tools Native"
