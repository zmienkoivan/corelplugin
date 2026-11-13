function Get-StationFolder([string]$name) {
    try { return [Environment]::GetFolderPath([Environment+SpecialFolder]$name) } catch { return '' }
}
function Get-StationStartupCommand {
    return '"' + (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') + '" -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $stationRoot 'station.ps1') + '" -Mode Start'
}
function Get-StationRunEntry {
    try { return (Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'VanyaOrderStation' -ErrorAction Stop).VanyaOrderStation }
    catch { return $null }
}
function Test-StationAutostart {
    try {
        $folder = Get-StationFolder 'Startup'
        if ($folder -and (Test-Path -LiteralPath (Join-Path $folder 'Ель — модуль печати.lnk'))) { return $true }
    } catch { }
    return (Get-StationRunEntry) -eq (Get-StationStartupCommand)
}
function Remove-StationRunEntry {
    if ((Get-StationRunEntry) -eq (Get-StationStartupCommand)) {
        Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'VanyaOrderStation' -ErrorAction Stop
    }
}
function New-Shortcut([string]$destination, [string]$shortcutMode) {
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($destination))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $shell = $null; $link = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $link = $shell.CreateShortcut($destination)
        $link.TargetPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $link.Arguments = '-NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + (Join-Path $stationRoot 'station.ps1') + '" -Mode ' + $shortcutMode
        $link.WorkingDirectory = $stationRoot; $link.WindowStyle = 7
        $link.Description = 'Заказы с рабочего сервера и печать на принтер этого компьютера'
        $link.Save()
    } finally {
        if ($link -and [Runtime.InteropServices.Marshal]::IsComObject($link)) { [Runtime.InteropServices.Marshal]::ReleaseComObject($link) | Out-Null }
        if ($shell -and [Runtime.InteropServices.Marshal]::IsComObject($shell)) { [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null }
    }
}
function Set-StationShortcuts([bool]$autoStart,
    [string]$desktopDirectory = (Get-StationFolder 'DesktopDirectory'),
    [string]$startupDirectory = (Get-StationFolder 'Startup')) {
    $warnings = @(); $desktopCreated = $false; $startupCreated = $false
    try {
        if (!$desktopDirectory) { throw 'Windows не вернула папку рабочего стола.' }
        New-Shortcut (Join-Path $desktopDirectory 'Заказы.lnk') 'Open'
        New-Shortcut (Join-Path $desktopDirectory 'Настройки печати заказов.lnk') 'Configure'
        $desktopCreated = $true
    } catch { $warnings += 'Ярлык рабочего стола: ' + $_.Exception.Message }
    if ($autoStart) {
        try {
            if (!$startupDirectory) { throw 'Windows не вернула папку автозагрузки.' }
            New-Shortcut (Join-Path $startupDirectory 'Ель — модуль печати.lnk') 'Start'
            $startupCreated = $true
            Remove-StationRunEntry
        } catch {
            $warnings += 'Ярлык автозапуска: ' + $_.Exception.Message
            try {
                $existing = Get-StationRunEntry
                $command = Get-StationStartupCommand
                if ($existing -and $existing -ne $command) { throw 'Запись VanyaOrderStation уже указывает на другое приложение.' }
                $run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
                New-Item -Path $run -Force | Out-Null
                New-ItemProperty -LiteralPath $run -Name 'VanyaOrderStation' -Value $command -PropertyType String -Force | Out-Null
                $startupCreated = $true
            } catch { $warnings += 'Резервный автозапуск: ' + $_.Exception.Message }
        }
    } else {
        try {
            if ($startupDirectory) {
                $link = Join-Path $startupDirectory 'Ель — модуль печати.lnk'
                if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link -ErrorAction Stop }
            }
            Remove-StationRunEntry
        } catch { $warnings += 'Отключение автозапуска: ' + $_.Exception.Message }
    }
    try {
        [IO.Directory]::CreateDirectory($stationData) | Out-Null
        [IO.File]::WriteAllText((Join-Path $stationData 'shortcut-warnings.txt'),($warnings -join [Environment]::NewLine),[Text.Encoding]::UTF8)
    } catch { Write-Warning 'Не удалось сохранить журнал ярлыков. Модуль продолжит запуск.' }
    if ($warnings.Count) {
        Write-Warning 'Не все ярлыки удалось создать. Модуль продолжит запуск. Для открытия заказов используйте Open.cmd из распакованного пакета.'
        if ($autoStart -and !$startupCreated) { Write-Warning 'Автозапуск недоступен: после входа в Windows запускайте Open.cmd вручную.' }
    }
    return @{ desktop=$desktopCreated; autoStart=$startupCreated; warnings=$warnings.Count }
}
