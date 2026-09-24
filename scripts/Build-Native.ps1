[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [switch]$SkipSign
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) "..")).Path
$project = Join-Path $repoRoot "native\VanyaTools.Native\VanyaTools.Native.csproj"
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw "dotnet SDK was not found. Install .NET SDK with Windows Desktop support."
}

& $dotnet.Source build $project --configuration $Configuration -p:Platform=x64 --no-incremental
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$dll = Join-Path $repoRoot "native\VanyaTools.Native\bin\x64\$Configuration\net48\VanyaTools.Native.dll"
if (-not (Test-Path -LiteralPath $dll)) {
    throw "Build finished but DLL was not found: $dll"
}

$addonDir = Join-Path $repoRoot "addon\VanyaToolsNative"
$addonDll = Join-Path $addonDir "VanyaTools.Native.dll"
Copy-Item -LiteralPath $dll -Destination $addonDll -Force

if (-not $SkipSign) {
    # A local development certificate is only used for local development installs.
    $cert = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object { $_.Subject -eq "CN=VanyaTools Dev" -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($cert) {
        $sig = Set-AuthenticodeSignature -FilePath $addonDll -Certificate $cert -HashAlgorithm SHA256
        Write-Host "Signed with VanyaTools Dev cert: $($sig.Status)" -ForegroundColor Green
    } else {
        Write-Host "Not signed (no VanyaTools Dev cert). Run scripts\Sign-Native.ps1 once to enable." -ForegroundColor Yellow
    }
} else {
    Write-Host "Skipping the local development signature for distribution." -ForegroundColor Yellow
}

Write-Host "Built native addon:" -ForegroundColor Green
Write-Host "  $dll"
Write-Host "Copied to:"
Write-Host "  $addonDir"