param([Parameter(Mandatory=$true)][string]$ResultPath)
$ErrorActionPreference = 'Stop'
try {
    if ($env:COMPUTERNAME -ne 'DESKTOP-AP3VE92') { throw 'Этот пакет предназначен для DESKTOP-AP3VE92.' }
    $driverName = 'ZDesigner GX420d'
    if (!(Get-PrinterDriver -Name $driverName -ErrorAction SilentlyContinue)) {
        $inf = Join-Path $PSScriptRoot 'zebra-driver\zbrn.inf'
        $catalog = Join-Path $PSScriptRoot 'zebra-driver\ZBRN.cat'
        if ((Get-AuthenticodeSignature -LiteralPath $catalog).Status -ne 'Valid') { throw 'Подпись драйвера Zebra не прошла проверку.' }
        & (Join-Path $env:SystemRoot 'System32\pnputil.exe') /add-driver $inf | Out-Null
        if ($LASTEXITCODE -notin @(0,3010)) { throw 'Windows не удалось установить подписанный пакет драйвера Zebra.' }
        $hash = (Get-FileHash -LiteralPath $inf -Algorithm SHA256).Hash
        $installedInf = Get-ChildItem -LiteralPath (Join-Path $env:SystemRoot 'System32\DriverStore\FileRepository') -Directory -Filter 'zbrn.inf_amd64_*' | ForEach-Object {
            $candidate = Join-Path $_.FullName 'zbrn.inf'
            if ((Test-Path -LiteralPath $candidate) -and (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -eq $hash) { $candidate }
        } | Select-Object -First 1
        if (!$installedInf) { throw 'Установленный пакет Zebra не найден в хранилище драйверов.' }
        Add-PrinterDriver -Name $driverName -InfPath $installedInf
    }
    $ports = @(Get-PrinterPort | Where-Object { $_.PrinterHostAddress -eq '192.168.0.107' -and $_.PortNumber -eq 9100 } | ForEach-Object { $_.Name })
    $existing = @(Get-Printer | Where-Object { $_.DriverName -eq $driverName -and $ports -contains $_.PortName } | Sort-Object Name) | Select-Object -First 1
    if ($existing) { $printerName = $existing.Name }
    else {
        $portName = 'Vanya_Zebra_192.168.0.107_9100'
        $port = Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue
        if ($port -and ($port.PrinterHostAddress -ne '192.168.0.107' -or $port.PortNumber -ne 9100)) { throw 'Имя порта Zebra уже используется для другого устройства.' }
        if (!$port) { Add-PrinterPort -Name $portName -PrinterHostAddress '192.168.0.107' -PortNumber 9100 }
        $printerName = 'Zebra GX420d 192.168.0.107'
        if (Get-Printer -Name $printerName -ErrorAction SilentlyContinue) { throw 'Очередь с именем Zebra уже существует, но указывает на другой порт. Она не изменена.' }
        Add-Printer -Name $printerName -DriverName $driverName -PortName $portName
    }
    $result = @{ ok=$true; name=$printerName; hostAddress='192.168.0.107'; port=9100 }
    [IO.File]::WriteAllText($ResultPath,($result | ConvertTo-Json -Compress),[Text.Encoding]::UTF8)
} catch {
    [IO.File]::WriteAllText($ResultPath,(@{ok=$false;error=$_.Exception.Message} | ConvertTo-Json -Compress),[Text.Encoding]::UTF8)
    exit 1
}
