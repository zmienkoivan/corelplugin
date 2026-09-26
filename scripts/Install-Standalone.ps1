#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$CorelAddonsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$installLogPath = Join-Path $env:LOCALAPPDATA "VanyaTools\Install.log"
function Write-InstallLog {
    param([string]$Message)
    try {
        New-Item -ItemType Directory -Path (Split-Path $installLogPath -Parent) -Force | Out-Null
        Add-Content -LiteralPath $installLogPath -Value ("{0:yyyy-MM-dd HH:mm:ss.fff} [PID {1}] {2}" -f (Get-Date), $PID, $Message) -Encoding UTF8
    } catch { }
}
Write-InstallLog "Installer started."

function Find-CorelAddonsPaths {
    $results = @()
    $roots = @($env:ProgramFiles, [Environment]::GetEnvironmentVariable("ProgramFiles(x86)")) |
        Where-Object { $_ } | Select-Object -Unique

    foreach ($root in $roots) {
        $corelRoot = Join-Path $root "Corel"
        if (-not (Test-Path -LiteralPath $corelRoot -PathType Container)) { continue }

        Get-ChildItem -LiteralPath $corelRoot -Directory -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -eq "Programs64" } |
            ForEach-Object {
                $product = Split-Path $_.FullName -Parent
                $results += [pscustomobject]@{
                    Product = Split-Path $product -Leaf
                    Path = Join-Path $_.FullName "Addons"
                }
            }
    }

    return @($results | Sort-Object Path -Unique)
}

function Resolve-InstallPath {
    param([string]$RequestedPath)

    if ($RequestedPath) {
        $fullPath = [IO.Path]::GetFullPath($RequestedPath.Trim().Trim('"'))
        if ((Split-Path $fullPath -Leaf) -ieq "Programs64") {
            $programs64 = $fullPath
            $fullPath = Join-Path $programs64 "Addons"
        } elseif ((Split-Path $fullPath -Leaf) -ieq "Addons") {
            $programs64 = Split-Path $fullPath -Parent
            if ((Split-Path $programs64 -Leaf) -ine "Programs64") {
                throw "Папка Addons должна находиться внутри Programs64: $fullPath"
            }
        } else {
            throw "Укажите путь к папке Programs64 или Programs64\Addons: $fullPath"
        }
        if (-not (Test-Path -LiteralPath $programs64 -PathType Container)) {
            throw "Папка CorelDRAW Programs64 не найдена: $programs64"
        }
        return $fullPath
    }

    $installations = @(Find-CorelAddonsPaths)
    if ($installations.Count -eq 1) {
        Write-Host "Найден CorelDRAW: $($installations[0].Product)"
        return $installations[0].Path
    }

    if ($installations.Count -gt 1) {
        Write-Host "Найдено несколько установок CorelDRAW:`n"
        for ($i = 0; $i -lt $installations.Count; $i++) {
            Write-Host "  $($i + 1). $($installations[$i].Product) — $($installations[$i].Path)"
        }
        Write-Host "  0. Указать путь вручную"
        do {
            $choice = Read-Host "Выберите установку"
            $number = 0
            $validNumber = [int]::TryParse($choice, [ref]$number)
        } while (-not $validNumber -or $number -lt 0 -or $number -gt $installations.Count)

        if ($number -gt 0) { return $installations[$number - 1].Path }
    }
    else {
        Write-Host "CorelDRAW автоматически не найден."
    }

    do {
        $manualPath = Read-Host "Введите путь к Programs64 или Programs64\Addons"
        if (-not $manualPath) { continue }
        try {
            $fullPath = [IO.Path]::GetFullPath($manualPath.Trim().Trim('"'))
            if ((Split-Path $fullPath -Leaf) -ieq "Programs64") {
                if (-not (Test-Path -LiteralPath $fullPath -PathType Container)) { throw "Папка Programs64 не найдена." }
                return (Join-Path $fullPath "Addons")
            }
            if ((Split-Path $fullPath -Leaf) -ieq "Addons" -and
                (Split-Path (Split-Path $fullPath -Parent) -Leaf) -ieq "Programs64" -and
                (Test-Path -LiteralPath (Split-Path $fullPath -Parent) -PathType Container)) { return $fullPath }
        } catch { }
        Write-Host "Путь должен заканчиваться на Programs64 или Programs64\Addons." -ForegroundColor Yellow
    } while ($true)
}

$addonSource = Join-Path $PSScriptRoot "VanyaToolsNative"
$requiredFiles = @("VanyaTools.Native.dll", "VanyaTools.CutPalette.xml", "AppUI.xslt", "UserUI.xslt", "Coreldrw.addon")
if (-not (Test-Path -LiteralPath $addonSource -PathType Container)) {
    Write-Host "Ошибка: рядом с установщиком не найдена папка VanyaToolsNative." -ForegroundColor Red
    exit 1
}
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $addonSource $file) -PathType Leaf)) {
        Write-Host "Ошибка: пакет неполный, отсутствует $file." -ForegroundColor Red
        exit 1
    }
}

try {
    Write-Host ""
    Write-Host "Vanya Tools — установка для CorelDRAW" -ForegroundColor Cyan
    $CorelAddonsPath = Resolve-InstallPath $CorelAddonsPath
    $CorelAddonsPath = [IO.Path]::GetFullPath($CorelAddonsPath)
    $installPath = Join-Path $CorelAddonsPath "VanyaToolsNative"
    Write-InstallLog "Resolved Corel add-ons path: $CorelAddonsPath"
    try {
        # Install the update helper in the signed-in user profile before a UAC relaunch.
        $updaterExeSource = Join-Path $PSScriptRoot "VanyaTools.Updater.exe"
        $updaterSource = Join-Path $PSScriptRoot "Update.ps1"
        $updaterBatSource = Join-Path $PSScriptRoot "UPDATE.bat"
        $updaterHome = Join-Path $env:LOCALAPPDATA "VanyaTools"
        $updaterTarget = $null
        New-Item -ItemType Directory -Path $updaterHome -Force | Out-Null

        if (Test-Path -LiteralPath $updaterExeSource -PathType Leaf) {
            $updaterTarget = Join-Path $updaterHome "VanyaTools.Updater.exe"
            # The Replicate worker uses a separate filename so replacing or
            # running the updater cannot leave the AI tab bound to an old binary.
            $workerTarget = Join-Path $updaterHome "VanyaTools.ReplicateWorker.exe"
            Copy-Item -LiteralPath $updaterExeSource -Destination $workerTarget -Force

            if (@(Get-Process -Name "VanyaTools.Updater" -ErrorAction SilentlyContinue).Count -eq 0) {
                Copy-Item -LiteralPath $updaterExeSource -Destination $updaterTarget -Force
            } else {
                # The updater is installing this package and has its own EXE
                # loaded, so stage the replacement and apply it after exit.
                $stagedUpdater = Join-Path $updaterHome "VanyaTools.Updater.pending.exe"
                $replaceScriptPath = Join-Path $updaterHome "Replace-VanyaUpdater.ps1"
                Copy-Item -LiteralPath $updaterExeSource -Destination $stagedUpdater -Force
                $replaceScript = @'
param([string]$PendingPath, [string]$TargetPath)
for ($i = 0; $i -lt 3600; $i++) {
    if (@(Get-Process -Name "VanyaTools.Updater" -ErrorAction SilentlyContinue).Count -eq 0) {
        try {
            Move-Item -LiteralPath $PendingPath -Destination $TargetPath -Force
            exit 0
        } catch { }
    }
    Start-Sleep -Seconds 1
}
exit 1
'@
                Set-Content -LiteralPath $replaceScriptPath -Value $replaceScript -Encoding UTF8
                $replaceArgs = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $replaceScriptPath + '" -PendingPath "' + $stagedUpdater + '" -TargetPath "' + $updaterTarget + '"'
                Start-Process -FilePath "powershell.exe" -ArgumentList $replaceArgs -WindowStyle Hidden
            }
        } elseif ((Test-Path -LiteralPath $updaterSource -PathType Leaf) -and
                  (Test-Path -LiteralPath $updaterBatSource -PathType Leaf)) {
            # Legacy package fallback for older releases.
            Copy-Item -LiteralPath $updaterSource -Destination (Join-Path $updaterHome "Update.ps1") -Force
            Copy-Item -LiteralPath $updaterBatSource -Destination (Join-Path $updaterHome "UPDATE.bat") -Force
            $updaterTarget = Join-Path $updaterHome "UPDATE.bat"
        }

        if ($updaterTarget) {
            $startMenu = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Vanya Tools"
            New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
            $shortcutPath = Join-Path $startMenu "Проверить обновления.lnk"
            $shell = New-Object -ComObject WScript.Shell
            $shortcut = $shell.CreateShortcut($shortcutPath)
            $shortcut.TargetPath = $updaterTarget
            $shortcut.WorkingDirectory = $updaterHome
            $shortcut.Description = "Проверить обновления Vanya Tools на GitHub"
            $shortcut.Save()
        }
    } catch {
        Write-Host "Не удалось подготовить пункт обновления: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    # CorelDRAW is normally installed under Program Files. Elevate only when
    # the current user cannot write to the selected Addons directory.
    $canWrite = $false
    try {
        if (-not (Test-Path -LiteralPath $CorelAddonsPath)) {
            New-Item -ItemType Directory -Path $CorelAddonsPath -Force | Out-Null
        }
        $probe = Join-Path $CorelAddonsPath (".vanyatools-write-{0}" -f [guid]::NewGuid().ToString("N"))
        [IO.File]::WriteAllText($probe, "")
        Remove-Item -LiteralPath $probe -Force
        $canWrite = $true
    } catch [System.UnauthorizedAccessException] {
        $canWrite = $false
    } catch [System.Security.SecurityException] {
        $canWrite = $false
    }
    Write-InstallLog "Write access check: $canWrite"

    if (-not $canWrite) {
        Write-InstallLog "Requesting administrator elevation for the installer."
        Write-Host "Для установки в папку CorelDRAW нужны права администратора. Откроется запрос Windows." -ForegroundColor Yellow
        $arguments = @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"{0}"' -f $PSCommandPath),
            "-CorelAddonsPath", ('"{0}"' -f $CorelAddonsPath)
        )
        $process = Start-Process -FilePath "powershell.exe" -ArgumentList $arguments -Verb RunAs -Wait -PassThru
        Write-InstallLog "Elevated installer returned exit code $($process.ExitCode)."
        exit $process.ExitCode
    }

    if (-not (Test-Path -LiteralPath $installPath)) {
        New-Item -ItemType Directory -Path $installPath -Force | Out-Null
    }
    foreach ($file in $requiredFiles) {
        Copy-Item -LiteralPath (Join-Path $addonSource $file) -Destination (Join-Path $installPath $file) -Force
    }
    Write-InstallLog "Copied add-on files to $installPath"


    Write-Host ""
    Write-Host "Установка завершена:" -ForegroundColor Green
    Write-Host "  $installPath"
    Write-Host "Обновление: Пуск > Vanya Tools > Проверить обновления"
    Write-Host "Если ярлыка нет: $env:LOCALAPPDATA\VanyaTools\VanyaTools.Updater.exe"
    Write-Host "Перезапустите CorelDRAW и откройте: Окно > Окна настройки (Dockers) > Vanya Tools Native."
    Write-InstallLog "Installer completed successfully."
}
catch {
    Write-InstallLog "Installer failed: $($_.Exception.Message)"
    Write-Host ""
    Write-Host "Установка не выполнена: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
