$ErrorActionPreference = 'Stop'
try {
    if ($env:COMPUTERNAME -ne 'DESKTOP-AP3VE92') { throw 'Запустите этот пакет на DESKTOP-AP3VE92 — там установлен рабочий модуль печати.' }
    $stationRoot = Join-Path $env:LOCALAPPDATA 'VanyaTools\OrderStation'
    $stationData = Join-Path $stationRoot 'data'
    $connectionPath = Join-Path $stationData 'connection.bin'
    if (!(Test-Path -LiteralPath $connectionPath)) { throw 'На этом ПК сначала нужно настроить обычный модуль печати.' }
    $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $powershell -NoProfile -NonInteractive -File (Join-Path $PSScriptRoot 'station.ps1') -Mode Verify
    if ($LASTEXITCODE -ne 0) { throw 'Пакет повреждён. Скачайте и распакуйте его заново.' }
    $client = [Net.Sockets.TcpClient]::new()
    try {
        if (!$client.ConnectAsync('192.168.0.107',9100).Wait(5000) -or !$client.Connected) { throw 'Нет связи с Zebra 192.168.0.107:9100.' }
    } finally { $client.Dispose() }
    $backup = Join-Path $stationData ('backups\zebra-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    [IO.Directory]::CreateDirectory($backup) | Out-Null
    Copy-Item -LiteralPath $connectionPath -Destination (Join-Path $backup 'connection.bin')
    $settingsPath = Join-Path $stationData 'printing\settings.json'
    if (Test-Path -LiteralPath $settingsPath) { Copy-Item -LiteralPath $settingsPath -Destination (Join-Path $backup 'settings.json') }
    $resultPath = Join-Path $backup 'printer-setup.json'
    $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $PSScriptRoot 'Setup-Zebra-Printer.ps1') + '" -ResultPath "' + $resultPath + '"'
    Write-Output 'Добавляем сетевую очередь Zebra. Windows может запросить права администратора.'
    $setup = Start-Process -FilePath $powershell -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -Wait -PassThru
    if ($setup.ExitCode -ne 0) {
        $detail = if (Test-Path -LiteralPath $resultPath) { ([IO.File]::ReadAllText($resultPath) | ConvertFrom-Json).error } else { 'Настройка отменена.' }
        throw $detail
    }
    $printer = [IO.File]::ReadAllText($resultPath) | ConvertFrom-Json
    if (!$printer.ok -or !$printer.name -or $printer.hostAddress -ne '192.168.0.107') { throw 'Не удалось подтвердить очередь Zebra.' }
    & $powershell -NoProfile -STA -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'station.ps1') -Mode Update
    if ($LASTEXITCODE -ne 0) { throw 'Обновление модуля не завершено. Сведения о подключении сохранены.' }
    Add-Type -AssemblyName System.Security
    $connectionBytes = [IO.File]::ReadAllBytes($connectionPath)
    $connection = [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect($connectionBytes,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)) | ConvertFrom-Json
    $headers = @{ 'X-Admin-Key' = $connection.adminKey }
    $local = 'http://127.0.0.1:4174/api'
    $before = Invoke-RestMethod -Uri ($local + '/printing/settings?refresh=1') -Headers $headers -TimeoutSec 25
    $additional = @(@($before.additionalFullPrinterNames) + @($printer.name) | Where-Object { $_ } | Select-Object -Unique)
    $settings = @{ printerName = $before.printerName; paperMode = $before.paperMode; miniPrinterName = $before.miniPrinterName; additionalFullPrinterNames = $additional }
    $after = Invoke-RestMethod -Uri ($local + '/printing/settings') -Headers $headers -Method Put -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes(($settings | ConvertTo-Json -Compress))) -TimeoutSec 25
    if ($after.printerName -ne $before.printerName -or $after.miniPrinterName -ne $before.miniPrinterName -or $after.paperMode -ne $before.paperMode -or $after.additionalFullPrinterNames -notcontains $printer.name) { throw 'Не удалось проверить сохранённые настройки принтеров.' }
    if ([Convert]::ToBase64String($connectionBytes) -ne [Convert]::ToBase64String([IO.File]::ReadAllBytes($connectionPath))) { throw 'Параметры подключения изменились во время установки.' }
    $published = $false
    for ($attempt=0; $attempt -lt 20; $attempt++) {
        try {
            $catalog = Invoke-RestMethod -Uri ($local + '/network-printing/printers') -Headers $headers -TimeoutSec 5
            $station = @($catalog.stations | Where-Object { $_.id -eq $connection.instanceId }) | Select-Object -First 1
            if ($station.online -and @($station.printers | Where-Object { $_.name -eq $printer.name -and $_.format -eq 'full' -and $_.labelHeightMm -eq 120 }).Count -eq 1) { $published = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (!$published) { throw 'Zebra сохранена, но общая очередь ещё не подтвердила подключение. Проверьте строку «Общая очередь» в настройках сайта.' }
    Write-Output ('Готово: ' + $printer.name + ' · 80x120 мм. Xprinter и история сохранены. Тестовая печать не запускалась.')
    Start-Process ('http://127.0.0.1:4174/station/start/' + $connection.launchToken)
} catch {
    Write-Output ('Не удалось завершить подключение: ' + $_.Exception.Message)
    exit 1
}
