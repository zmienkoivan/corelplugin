param([ValidateSet('list', 'print', 'preview', 'raster-preview')][string]$Action, [string]$PayloadPath)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Drawing
$printStage = 'prepare'
$document = $null
$image = $null
$bitmap = $null
$previewGraphics = $null

function Paint-Label([Drawing.Graphics]$Graphics, [double]$LeftMm, [double]$TopMm) {
    $Graphics.PageUnit = [Drawing.GraphicsUnit]::Pixel
    $Graphics.PageScale = 1
    $dx = $Graphics.DpiX / 25.4
    $dy = $Graphics.DpiY / 25.4
    $left = [int][Math]::Round($LeftMm * $dx)
    $top = [int][Math]::Round($TopMm * $dy)
    $Graphics.Clear([Drawing.Color]::White)
    # Text keeps its original rendering; barcode bars are redrawn in native device pixels below.
    $Graphics.InterpolationMode = if ($payload.document) { [Drawing.Drawing2D.InterpolationMode]::NearestNeighbor } else { [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic }
    $Graphics.DrawImage($image, [Drawing.Rectangle]::new($left, $top, [int][Math]::Round($labelWidthMm * $dx), [int][Math]::Round($labelHeightMm * $dy)))
    if ($barcodeLayout) {
        $Graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::None
        $Graphics.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::None
        $x = $left + [int][Math]::Round($barcodeLayout.xMm * $dx)
        $y = $top + [int][Math]::Round($barcodeLayout.yMm * $dy)
        $width = [int][Math]::Round($barcodeLayout.widthMm * $dx)
        $height = [int][Math]::Round($barcodeLayout.heightMm * $dy)
        $paddingY = [int][Math]::Round($barcodeLayout.paddingYmm * $dy)
        $moduleCount = [int](($barcodeLayout.widths | Measure-Object -Sum).Sum + 2 * $barcodeLayout.quietModules)
        $moduleDots = [int][Math]::Floor($width / $moduleCount)
        if ($moduleDots -lt 2) { throw 'The barcode is too wide for this printer resolution.' }
        # Remove the scaled PNG barcode completely, including its interpolated edge pixels.
        $Graphics.FillRectangle([Drawing.Brushes]::White, $x - 1, $y - 1, $width + 2, $height + 2)
        $barX = $x + [int][Math]::Floor(($width - $moduleCount * $moduleDots) / 2) + $barcodeLayout.quietModules * $moduleDots
        for ($i = 0; $i -lt $barcodeLayout.widths.Count; $i++) {
            $run = [int]$barcodeLayout.widths[$i] * $moduleDots
            if ($i % 2 -eq 0) { $Graphics.FillRectangle([Drawing.Brushes]::Black, [int]$barX, [int]($y + $paddingY), [int]$run, [int]($height - 2 * $paddingY)) }
            $barX += $run
        }
    }
}

try {
    if ($Action -eq 'list') {
        $defaultSettings = [Drawing.Printing.PrinterSettings]::new()
        $printers = @([Drawing.Printing.PrinterSettings]::InstalledPrinters | ForEach-Object {
            if ($_ -notmatch '(?i)(PDF|OneNote|Fax|XPS)') {
                @{ name = [string]$_; isDefault = $_ -eq $defaultSettings.PrinterName }
            }
        })
        @{ ok = $true; printers = $printers } | ConvertTo-Json -Compress -Depth 4
        exit 0
    }
    $payload = [IO.File]::ReadAllText($PayloadPath, [Text.Encoding]::UTF8) | ConvertFrom-Json
    $labelWidthMm = if ($payload.document) { [double]$payload.labelWidthMm } elseif ($payload.paperMode -eq 'mini') { 45 } else { 80 }
    $labelHeightMm = if ($payload.paperMode -eq 'mini') { 22.5 } elseif ($payload.labelHeightMm) { [double]$payload.labelHeightMm } else { 110 }
    if ($payload.paperMode -notin @('sheet', 'label', 'mini')) { throw 'Unknown paper mode.' }
    if ($payload.document) {
        if (!(($payload.paperMode -eq 'sheet' -and $labelWidthMm -eq 105 -and $labelHeightMm -eq 148) -or ($payload.paperMode -eq 'label' -and $labelWidthMm -eq 80 -and $labelHeightMm -in @(110,120)))) { throw 'Invalid document size.' }
    } elseif (($payload.paperMode -eq 'mini' -and $labelHeightMm -ne 22.5) -or ($payload.paperMode -ne 'mini' -and $labelHeightMm -notin @(110, 120))) { throw 'Invalid label size.' }
    $imagePaths = @(if ($payload.document) { $payload.imagePaths } else { $payload.imagePath })
    if ($imagePaths.Count -lt 1 -or $imagePaths.Count -gt 8) { throw 'Invalid page count.' }
    $script:printPageIndex = 0
    $barcodeLayout = $payload.barcodeLayout
    if ($barcodeLayout) {
        if ($payload.paperMode -eq 'mini' -or $barcodeLayout.widths.Count -lt 3 -or $barcodeLayout.widths.Count -gt 1000 -or $barcodeLayout.quietModules -lt 10 -or $barcodeLayout.quietModules -gt 100) { throw 'Invalid barcode layout.' }
        foreach ($run in $barcodeLayout.widths) {
            if ([double]$run -notin @(1, 2, 3, 4)) { throw 'Invalid barcode module width.' }
        }
        foreach ($field in @('xMm', 'yMm', 'widthMm', 'heightMm', 'paddingYmm', 'quietModules')) {
            $value = [double]$barcodeLayout.$field
            if ([double]::IsNaN($value) -or [double]::IsInfinity($value) -or $value -lt 0) { throw 'Invalid barcode geometry.' }
        }
        if ($barcodeLayout.widthMm -le 0 -or $barcodeLayout.heightMm -le 2 * $barcodeLayout.paddingYmm -or $barcodeLayout.xMm + $barcodeLayout.widthMm -gt $labelWidthMm -or $barcodeLayout.yMm + $barcodeLayout.heightMm -gt $labelHeightMm) { throw 'Barcode does not fit the label.' }
    }
    $image = [Drawing.Image]::FromFile($imagePaths[0])
    if ($Action -eq 'raster-preview') {
        $dpi = if ($payload.previewDpi) { [single]$payload.previewDpi } else { [single]203.2 }
        if ($dpi -lt 150 -or $dpi -gt 1200 -or [single]::IsNaN($dpi)) { throw 'Invalid preview DPI.' }
        $bitmap = [Drawing.Bitmap]::new([int][Math]::Round($labelWidthMm / 25.4 * $dpi), [int][Math]::Round($labelHeightMm / 25.4 * $dpi))
        $bitmap.SetResolution($dpi, $dpi)
        $previewGraphics = [Drawing.Graphics]::FromImage($bitmap)
        Paint-Label $previewGraphics 0 0
        $bitmap.Save($payload.previewPath, [Drawing.Imaging.ImageFormat]::Png)
        @{ ok = $true; labelWidthMm = $labelWidthMm; labelHeightMm = $labelHeightMm; dpi = $dpi; preview = $true } | ConvertTo-Json -Compress
        exit 0
    }
    $document = [Drawing.Printing.PrintDocument]::new()
    $document.PrinterSettings.PrinterName = $payload.printerName
    if (!$document.PrinterSettings.IsValid) { throw 'The selected Windows printer is unavailable.' }
    if ($payload.printerName -match '(?i)(PDF|OneNote|Fax|XPS)') { throw 'Select a physical printer.' }
    $document.DocumentName = 'Vanya Label ' + $payload.jobId
    $document.PrinterSettings.Copies = 1
    $document.PrinterSettings.PrintToFile = $false
    $document.PrinterSettings.Duplex = [Drawing.Printing.Duplex]::Simplex
    $document.DefaultPageSettings.Landscape = $false
    $document.DefaultPageSettings.Margins = [Drawing.Printing.Margins]::new(0, 0, 0, 0)
    $document.OriginAtMargins = $false
    if ($payload.paperMode -eq 'sheet') {
        $paper = @($document.PrinterSettings.PaperSizes | Where-Object { $_.Kind -eq [Drawing.Printing.PaperKind]::A4 }) | Select-Object -First 1
        if (!$paper) { throw 'This printer driver does not support A4 paper.' }
        $document.DefaultPageSettings.PaperSize = $paper
    } elseif ($payload.paperMode -in @('label', 'mini')) {
        $widthMm = $labelWidthMm
        $heightMm = $labelHeightMm
        $width = [int][Math]::Round($widthMm / 25.4 * 100)
        $height = [int][Math]::Round($heightMm / 25.4 * 100)
        $paper = @($document.PrinterSettings.PaperSizes | Where-Object { [Math]::Abs($_.Width - $width) -le 2 -and [Math]::Abs($_.Height - $height) -le 2 }) | Select-Object -First 1
        if (!$paper) { $paper = [Drawing.Printing.PaperSize]::new("Vanya ${widthMm}x${heightMm} mm", $width, $height) }
        $document.DefaultPageSettings.PaperSize = $paper
    } else { throw 'Unknown paper mode.' }
    $document.add_PrintPage({
        param($sender, $eventArgs)
        if ($script:printPageIndex -gt 0) { $script:image.Dispose(); $script:image = [Drawing.Image]::FromFile($imagePaths[$script:printPageIndex]) }
        $left = if ($payload.paperMode -eq 'sheet') { 10 } else { 0 }
        $eventArgs.Graphics.PageUnit = [Drawing.GraphicsUnit]::Pixel
        $eventArgs.Graphics.TranslateTransform(-[Math]::Round($eventArgs.PageSettings.HardMarginX * $eventArgs.Graphics.DpiX / 100), -[Math]::Round($eventArgs.PageSettings.HardMarginY * $eventArgs.Graphics.DpiY / 100))
        Paint-Label $eventArgs.Graphics $left $left
        $script:printPageIndex++
        $eventArgs.HasMorePages = $script:printPageIndex -lt $imagePaths.Count
    })
    if ($Action -eq 'preview') { $document.PrintController = [Drawing.Printing.PreviewPrintController]::new() }
    else { $document.PrintController = [Drawing.Printing.StandardPrintController]::new() }
    $printStage = 'spooling'
    $document.Print()
    if ($Action -eq 'preview') {
        $pages = $document.PrintController.GetPreviewPageInfo()
        if ($pages.Length -ne $imagePaths.Count) { throw 'Unexpected page count.' }
        $pages[0].Image.Save($payload.previewPath, [Drawing.Imaging.ImageFormat]::Png)
        foreach ($page in $pages) { $page.Image.Dispose() }
    }
    @{ ok = $true; printerName = $payload.printerName; paperMode = $payload.paperMode; labelWidthMm = $labelWidthMm; labelHeightMm = $labelHeightMm; preview = $Action -eq 'preview' } | ConvertTo-Json -Compress
} catch {
    @{ ok = $false; stage = $printStage; error = $_.Exception.Message } | ConvertTo-Json -Compress
    exit 1
} finally {
    if ($previewGraphics) { $previewGraphics.Dispose() }
    if ($bitmap) { $bitmap.Dispose() }
    if ($image) { $image.Dispose() }
    if ($document) { $document.Dispose() }
}
