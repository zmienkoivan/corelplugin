$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $env:LOCALAPPDATA 'VanyaTools\order-server.bin'
if (!(Test-Path -LiteralPath $settingsPath)) {
    $settingsPath = Join-Path $env:LOCALAPPDATA 'VanyaTools\preview-publisher.bin'
}
$savedSettings = [Text.Encoding]::UTF8.GetString(
    [Security.Cryptography.ProtectedData]::Unprotect(
        [IO.File]::ReadAllBytes($settingsPath), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)) | ConvertFrom-Json
if (!$savedSettings.server -or !$savedSettings.key) { throw 'В плагине не сохранено подключение к серверу.' }
$env:PREVIEW_ORDER_SERVER = $savedSettings.server.TrimEnd('/')
$env:PREVIEW_ORDER_SERVER_KEY = $savedSettings.key
$env:PREVIEW_HOST = '127.0.0.1'
$env:PREVIEW_PORT = '4173'
$env:PREVIEW_PUBLIC_URL = 'http://127.0.0.1:4173'
$env:PREVIEW_DATA_DIR = Join-Path $PSScriptRoot 'data'
$env:PREVIEW_ADMIN_KEY = ''
Push-Location $PSScriptRoot
try {
    Write-Host "Просмотр заказов: $env:PREVIEW_ORDER_SERVER → http://127.0.0.1:4173/orders"
    & node server/index.mjs | ForEach-Object {
        if ($_ -like 'Admin key:*') { 'Ключ локального доступа сохранён в data/admin-key.txt' } else { $_ }
    }
} finally {
    Remove-Item Env:PREVIEW_ORDER_SERVER_KEY -ErrorAction SilentlyContinue
    Remove-Item Env:PREVIEW_ORDER_SERVER -ErrorAction SilentlyContinue
    Pop-Location
}
