$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$report = [ordered]@{ computer = $env:COMPUTERNAME; capturedAt = (Get-Date).ToString('o'); printers = @(); notes = @() }
$settingsPath = Join-Path $env:LOCALAPPDATA 'VanyaTools\OrderStation\data\printing\settings.json'
if (Test-Path -LiteralPath $settingsPath) {
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $report.selected = @{ full = $settings.printerName; mini = $settings.miniPrinterName; paperMode = $settings.paperMode; additionalFull = @($settings.additionalFullPrinterNames) }
    if ($settings.miniPrinterName -and ($settings.miniPrinterName -eq $settings.printerName -or $settings.miniPrinterName -in @($settings.additionalFullPrinterNames))) {
        $report.notes += 'Один принтер назначен для 80x120 и 45x22,5 мм. Проверьте выбранный формат и фактический рулон.'
    }
}
foreach ($printer in @(Get-Printer | Where-Object { $_.Name -match '(?i)(Xprinter|XP.?36|XP.?37)' })) {
    $entry = [ordered]@{ name = $printer.Name; driver = $printer.DriverName; port = $printer.PortName; status = [string]$printer.PrinterStatus }
    try {
        $driver = [Drawing.Printing.PrinterSettings]::new()
        $driver.PrinterName = $printer.Name
        $page = $driver.DefaultPageSettings
        $entry.defaultPaper = @{ name = $page.PaperSize.PaperName; widthMm = [Math]::Round($page.PaperSize.Width * 0.254, 2); heightMm = [Math]::Round($page.PaperSize.Height * 0.254, 2); landscape = $page.Landscape }
        $entry.dpi = @{ x = $page.PrinterResolution.X; y = $page.PrinterResolution.Y }
        $entry.jobs = @(Get-PrintJob -PrinterName $printer.Name -ErrorAction Stop | Select-Object Id,JobStatus,TotalPages,PagesPrinted,SubmittedTime)
    } catch { $entry.error = $_.Exception.Message }
    $report.printers += $entry
}
$report.notes += 'Диагностика только читает настройки. Печать, подача бумаги, очистка очереди и калибровка не запускаются.'
$report.notes += 'С модулем отделения проверьте снятие предыдущей этикетки, заправку подложки и режим отделителя в драйвере.'
$output = Join-Path $PSScriptRoot 'printer-diagnostics.json'
$report | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $output -Encoding utf8
Write-Host ('Отчёт сохранён: ' + $output)
$report | ConvertTo-Json -Depth 7
