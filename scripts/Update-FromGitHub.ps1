#Requires -Version 5.1
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoFile = Join-Path $env:LOCALAPPDATA "VanyaTools\github-repository.txt"
$defaultRepository = "zmienkoivan/corelplugin"
$workDir = Join-Path $env:TEMP ("VanyaToolsUpdate-" + [guid]::NewGuid().ToString("N"))

try {
    Write-Host "Vanya Tools — проверка обновления" -ForegroundColor Cyan
    if (Get-Process -Name "CorelDRW" -ErrorAction SilentlyContinue) {
        throw "Сначала закройте CorelDRAW и повторно запустите UPDATE.bat."
    }

    $repository = $null
    if (Test-Path -LiteralPath $repoFile) {
        $repository = (Get-Content -LiteralPath $repoFile -Raw).Trim()
    }
    if (-not $repository) { $repository = $defaultRepository }
    if ($repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw "Нужен адрес публичного GitHub репозитория в формате владелец/имя."
    }

    $headers = @{ "User-Agent" = "VanyaTools-Updater"; "Accept" = "application/vnd.github+json" }
    $releaseUri = "https://api.github.com/repos/$repository/releases/latest"
    $release = Invoke-RestMethod -Uri $releaseUri -Headers $headers -Method Get
    $asset = @($release.assets | Where-Object { $_.name -eq "VanyaToolsNative.zip" } | Select-Object -First 1)
    if ($asset.Count -eq 0) {
        throw "В последнем GitHub Release нет файла VanyaToolsNative.zip. Прикрепите архив к опубликованному релизу."
    }

    Write-Host "Найден релиз $($release.tag_name). Загружаю пакет..."
    New-Item -ItemType Directory -Path $workDir -Force | Out-Null
    $zipPath = Join-Path $workDir "VanyaToolsNative.zip"
    Invoke-WebRequest -Uri $asset[0].browser_download_url -Headers $headers -OutFile $zipPath
    Expand-Archive -LiteralPath $zipPath -DestinationPath $workDir -Force

    $installer = Join-Path $workDir "Install.ps1"
    if (-not (Test-Path -LiteralPath $installer -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $workDir "VanyaToolsNative\VanyaTools.Native.dll") -PathType Leaf)) {
        throw "Загруженный архив не похож на пакет Vanya Tools."
    }

    New-Item -ItemType Directory -Path (Split-Path $repoFile -Parent) -Force | Out-Null
    Set-Content -LiteralPath $repoFile -Value $repository -Encoding ASCII

    $installerArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ('"{0}"' -f $installer))
    $process = Start-Process -FilePath "powershell.exe" -ArgumentList $installerArgs -Wait -PassThru
    if ($process.ExitCode -ne 0) { throw "Установщик завершился с кодом $($process.ExitCode)." }

    Write-Host "Обновление установлено из релиза $($release.tag_name). Перезапустите CorelDRAW." -ForegroundColor Green
}
catch {
    Write-Host "Обновление не выполнено: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
finally {
    if (Test-Path -LiteralPath $workDir) {
        Remove-Item -LiteralPath $workDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
