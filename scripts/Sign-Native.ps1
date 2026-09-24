#Requires -Version 5.1
<#
.SYNOPSIS
    Signs VanyaTools.Native.dll with a self-signed developer certificate.
.DESCRIPTION
    On first run, creates a code-signing certificate "VanyaTools Dev", adds it to the trusted
    stores (CurrentUser\Root and TrustedPublisher), then signs the DLL. Later runs reuse it.
    This clears the Windows Security / Smart App Control block for the unsigned DLL on THIS PC.
.PARAMETER DllPath
    Path to the DLL. Defaults to the built DLL in addon\VanyaToolsNative.
#>
[CmdletBinding()]
param(
    [string]$DllPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) "..")).Path
if (-not $DllPath) {
    $DllPath = Join-Path $repoRoot "addon\VanyaToolsNative\VanyaTools.Native.dll"
}

if (-not (Test-Path -LiteralPath $DllPath)) {
    Write-Host "ERROR: DLL not found: $DllPath" -ForegroundColor Red
    exit 1
}

$subject = "CN=VanyaTools Dev"

function Get-OrCreateCert {
    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
    if ($existing) {
        Write-Host "Using existing certificate: $($existing.Thumbprint)"
        return $existing
    }

    Write-Host "Creating new certificate '$subject'..."
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $subject `
        -CertStoreLocation Cert:\CurrentUser\My `
        -KeyExportPolicy Exportable `
        -KeyUsage DigitalSignature `
        -NotAfter (Get-Date).AddYears(10)

    # Trust our own certificate: root + publisher (current user only).
    foreach ($store in @("Root", "TrustedPublisher")) {
        $s = New-Object System.Security.Cryptography.X509Certificates.X509Store($store, "CurrentUser")
        $s.Open("ReadWrite")
        $s.Add($cert)
        $s.Close()
    }
    Write-Host "Certificate created and trusted (CurrentUser Root + TrustedPublisher)."
    return $cert
}

$cert = Get-OrCreateCert

Write-Host "Signing: $DllPath"
$result = Set-AuthenticodeSignature -FilePath $DllPath -Certificate $cert -HashAlgorithm SHA256

if ($result.Status -eq "Valid") {
    Write-Host "Done. Signature is valid." -ForegroundColor Green
} else {
    Write-Host "Signature status: $($result.Status) - $($result.StatusMessage)" -ForegroundColor Yellow
}
