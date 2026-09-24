[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $PSCommandPath) "..")).Path
& (Join-Path $repoRoot "build-native.ps1") -Configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$releaseDir = Join-Path $repoRoot "release"
$packageDir = Join-Path $releaseDir "VanyaToolsNative"
$installScript = Join-Path $releaseDir "Install.ps1"
$updaterScript = Join-Path $releaseDir "Update.ps1"
$updaterBat = Join-Path $releaseDir "UPDATE.bat"
$zipPath = Join-Path $releaseDir "VanyaToolsNative.zip"

if (Test-Path -LiteralPath $packageDir) {
    Remove-Item -LiteralPath $packageDir -Recurse -Force
}

New-Item -ItemType Directory -Path $packageDir | Out-Null
Get-ChildItem -LiteralPath (Join-Path $repoRoot "addon\VanyaToolsNative") -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $packageDir $_.Name) -Recurse -Force
}

Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\Install-Standalone.ps1") -Destination $installScript -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\Update-FromGitHub.ps1") -Destination $updaterScript -Force
Copy-Item -LiteralPath (Join-Path $repoRoot "scripts\UPDATE.bat") -Destination $updaterBat -Force

# Удобный батник: даблклик сразу запустит установку с обходом политики.
# Имена файлов латиницей, чтобы кодировка не искажалась в распаковщике Windows.
$batPath = Join-Path $releaseDir "INSTALL.bat"
@'
@echo off
chcp 65001 >nul
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
pause
'@ | Set-Content -LiteralPath $batPath -Encoding UTF8

$readmePath = Join-Path $releaseDir "README.txt"
@'
Vanya Tools — аддон для CorelDRAW

ТРЕБОВАНИЯ
- Windows x64 и CorelDRAW x64.

УСТАНОВКА
1. Распакуйте архив целиком в любую папку.
2. Запустите INSTALL.bat двойным щелчком.
3. Выберите найденную версию CorelDRAW. Если программа не найдена,
   укажите путь к Programs64 или Programs64\Addons.
4. Подтвердите запрос Windows на права администратора, если он появится.
5. Перезапустите CorelDRAW и откройте:
   Окно > Окна настройки (Dockers) > Vanya Tools Native.

ОБНОВЛЕНИЕ ЧЕРЕЗ GITHUB
Для обновления закройте CorelDRAW и запустите «Проверить обновления» в меню
Пуск > Vanya Tools. Updater получает последний публичный релиз
zmienkoivan/corelplugin и переустанавливает аддон. Git не требуется.

Не запускайте CorelDRAW во время установки. Для обновления установите пакет
поверх предыдущей версии тем же способом.
'@ | Set-Content -LiteralPath $readmePath -Encoding UTF8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Compress-Archive -Path @($packageDir, $installScript, $batPath, $readmePath, $updaterScript, $updaterBat) -DestinationPath $zipPath

Write-Host ""
Write-Host "Пакет аддона собран:" -ForegroundColor Green
Write-Host "  $zipPath"
Write-Host ""
Write-Host "Передайте VanyaToolsNative.zip на другой ПК."
Write-Host "Распаковать -> запустить INSTALL.bat -> перезапустить CorelDRAW."
