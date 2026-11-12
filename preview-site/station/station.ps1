param([ValidateSet('Install', 'Update', 'Open', 'Start', 'Configure', 'Stop', 'Verify')][string]$Mode = 'Open')
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$stationRoot = Join-Path $env:LOCALAPPDATA 'VanyaTools\OrderStation'
$stationData = Join-Path $stationRoot 'data'
$connectionPath = Join-Path $stationData 'connection.bin'
$stationApp = Join-Path $stationRoot 'app'
$stationScript = Join-Path $stationApp 'server\print-station.mjs'
$stationNode = Join-Path $stationApp 'node.exe'
$stationUrl = 'http://127.0.0.1:4174'
. (Join-Path $PSScriptRoot 'shortcuts.ps1')

function Read-Connection {
    if (!(Test-Path -LiteralPath $connectionPath)) { return $null }
    $bytes = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($connectionPath), $null,
        [Security.Cryptography.DataProtectionScope]::CurrentUser)
    return [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
}
function Test-Package {
    $manifestPath = Join-Path $PSScriptRoot 'files.json'
    if (!(Test-Path -LiteralPath $manifestPath)) { throw 'Распакуйте весь ZIP перед запуском Install.cmd.' }
    $files = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot) + [IO.Path]::DirectorySeparatorChar
    foreach ($entry in $files.PSObject.Properties) {
        $source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot $entry.Name))
        if (!$source.StartsWith($sourceRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Неверный путь в пакете.' }
        $hasher = [Security.Cryptography.SHA256]::Create()
        $stream = [IO.File]::OpenRead($source)
        try { $fileHash = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $stream.Dispose(); $hasher.Dispose() }
        if ($fileHash -ne $entry.Value) { throw 'Пакет повреждён. Скачайте ZIP заново.' }
    }
    return $files
}
function New-StationToken {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
}
function Check-Connection($server, $key) {
    $uri = [Uri]$server
    if ($uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment) {
        throw 'Укажите адрес сервера HTTPS, без /orders в конце.'
    }
    if (!$key) { throw 'Введите ключ рабочего сервера.' }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $response = Invoke-RestMethod -Uri ($server.TrimEnd('/') + '/api/orders/connection') -Headers @{ 'X-Admin-Key' = $key } -TimeoutSec 25 -MaximumRedirection 0
    if (!$response.mode) { throw 'По этому адресу не найден наш сервер заказов.' }
}
function Configure-Connection {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing
    [Windows.Forms.Application]::EnableVisualStyles()
    $saved = Read-Connection
    $form = New-Object Windows.Forms.Form
    $form.Text = 'Заказы — подключение и печать'
    $form.ClientSize = [Drawing.Size]::new(510, 285)
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'; $form.MaximizeBox = $false
    $form.Font = [Drawing.Font]::new('Segoe UI', 10)
    $caption = [Windows.Forms.Label]::new(); $caption.Text = 'Подключение к рабочему серверу · один раз на этом ПК'
    $caption.Location = [Drawing.Point]::new(18, 16); $caption.Size = [Drawing.Size]::new(475, 24); $form.Controls.Add($caption)
    $serverCaption = [Windows.Forms.Label]::new(); $serverCaption.Text = 'Адрес сервера'
    $serverCaption.Location = [Drawing.Point]::new(18, 49); $serverCaption.AutoSize = $true; $form.Controls.Add($serverCaption)
    $serverField = [Windows.Forms.TextBox]::new(); $serverField.Location = [Drawing.Point]::new(18, 73); $serverField.Size = [Drawing.Size]::new(475, 28)
    $serverField.Text = if ($saved) { $saved.server } else { 'https://evpmerch.com:9443' }; $form.Controls.Add($serverField)
    $keyCaption = [Windows.Forms.Label]::new(); $keyCaption.Text = 'Ключ сервера — тот же, что для сайта'
    $keyCaption.Location = [Drawing.Point]::new(18, 111); $keyCaption.AutoSize = $true; $form.Controls.Add($keyCaption)
    $keyField = [Windows.Forms.TextBox]::new(); $keyField.UseSystemPasswordChar = $true
    $keyField.Location = [Drawing.Point]::new(18, 135); $keyField.Size = [Drawing.Size]::new(475, 28)
    if ($saved) { $keyField.Text = $saved.key }; $form.Controls.Add($keyField)
    $autoStart = [Windows.Forms.CheckBox]::new(); $autoStart.Text = 'Запускать модуль при входе в Windows'
    $autoStart.Location = [Drawing.Point]::new(18, 177); $autoStart.Size = [Drawing.Size]::new(475, 28)
    $autoStart.Checked = if ($saved) { Test-StationAutostart } else { $true }; $form.Controls.Add($autoStart)
    $button = [Windows.Forms.Button]::new(); $button.Text = 'Подключить'; $button.Location = [Drawing.Point]::new(333, 225); $button.Size = [Drawing.Size]::new(160, 40)
    $form.Controls.Add($button); $form.AcceptButton = $button
    $button.Add_Click({
        $button.Enabled = $false; $button.Text = 'Проверяю…'; $form.Refresh()
        try {
            $serverValue = $serverField.Text.Trim().TrimEnd('/'); $keyValue = $keyField.Text.Trim()
            Check-Connection $serverValue $keyValue
            $connection = @{ server = $serverValue; key = $keyValue; instanceId = if ($saved -and $saved.server -eq $serverValue -and $saved.instanceId) { $saved.instanceId } else { [Guid]::NewGuid().ToString() }
                adminKey = if ($saved) { $saved.adminKey } else { New-StationToken }
                launchToken = if ($saved) { $saved.launchToken } else { New-StationToken } }
            [IO.Directory]::CreateDirectory($stationData) | Out-Null
            $bytes = [Text.Encoding]::UTF8.GetBytes(($connection | ConvertTo-Json -Compress))
            $sealed = [Security.Cryptography.ProtectedData]::Protect($bytes, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
            [IO.File]::WriteAllBytes($connectionPath, $sealed)
            $form.Tag = @{ connection = [pscustomobject]$connection; autoStart = $autoStart.Checked }
            $form.DialogResult = [Windows.Forms.DialogResult]::OK; $form.Close()
        } catch {
            [Windows.Forms.MessageBox]::Show('Подключение не удалось. Проверьте интернет, адрес и ключ сервера.' + [Environment]::NewLine + $_.Exception.Message, 'Заказы') | Out-Null
        } finally { $button.Enabled = $true; $button.Text = 'Подключить' }
    })
    try {
        if ($form.ShowDialog() -ne [Windows.Forms.DialogResult]::OK) { return $null }
        return $form.Tag
    } finally { $form.Dispose() }
}
function Stop-Station {
    $pidPath = Join-Path $stationData 'station.pid'
    if (!(Test-Path -LiteralPath $pidPath)) { return }
    $stationProcessId = 0
    if (![int]::TryParse([IO.File]::ReadAllText($pidPath).Trim(), [ref]$stationProcessId)) { return }
    $stationProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $stationProcessId" -ErrorAction SilentlyContinue
    if ($stationProcess -and $stationProcess.ExecutablePath -eq $stationNode -and
        $stationProcess.CommandLine -and $stationProcess.CommandLine.Contains($stationScript)) {
        Stop-Process -Id $stationProcessId -ErrorAction Stop
        Wait-Process -Id $stationProcessId -Timeout 10 -ErrorAction SilentlyContinue
    }
}
function Health {
    try { return Invoke-RestMethod -Uri ($stationUrl + '/api/health') -TimeoutSec 1 } catch { return $null }
}
try {
    if (![Environment]::Is64BitOperatingSystem) { throw 'Этот пакет предназначен для 64-битной Windows.' }
    if ($Mode -eq 'Verify') { Test-Package | Out-Null; Write-Output 'Package verified.'; exit 0 }
    if ($Mode -eq 'Stop') { Stop-Station; exit 0 }
    if ($Mode -in @('Install', 'Update', 'Configure')) {
        if ($Mode -in @('Install', 'Update')) {
            if ($Mode -eq 'Update') {
                if (!(Read-Connection)) { throw 'Модуль ещё не настроен. Сначала запустите Install.cmd.' }
                if (Test-Path -LiteralPath (Join-Path $stationData 'network-print-pending.json')) {
                    throw 'Дождитесь завершения текущего задания печати и повторите обновление.'
                }
                $jobDirectory = Join-Path $stationData 'printing\jobs'
                if (Test-Path -LiteralPath $jobDirectory) {
                    foreach ($jobFile in Get-ChildItem -LiteralPath $jobDirectory -Filter '*.json' -File) {
                        if ($jobFile.Name -notmatch '^[0-9a-f-]{36}\.json$') { continue }
                        $job = [IO.File]::ReadAllText($jobFile.FullName) | ConvertFrom-Json
                        if ($job.status -in @('queued','printing')) { throw 'В очереди есть незавершённая печать. Проверьте её перед обновлением.' }
                    }
                }
            }
            $files = Test-Package
            Stop-Station
            foreach ($entry in $files.PSObject.Properties) {
                $destination = [IO.Path]::GetFullPath((Join-Path $stationRoot $entry.Name))
                if (!$destination.StartsWith([IO.Path]::GetFullPath($stationRoot) + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Неверный путь в пакете.' }
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
                Copy-Item -LiteralPath (Join-Path $PSScriptRoot $entry.Name) -Destination $destination -Force
            }
        }
        if ($Mode -ne 'Update') {
            $configured = Configure-Connection
            if (!$configured) { exit 0 }
            Stop-Station
            Set-StationShortcuts $configured.autoStart | Out-Null
        }
    }
    $connection = Read-Connection
    if (!$connection) {
        if ($Mode -eq 'Start') { exit 0 }
        throw 'Сначала запустите Install.cmd из распакованного ZIP.'
    }
    $stationMutex = [Threading.Mutex]::new($false, 'Local\VanyaOrderStationStartup')
    $stationMutexOwned = $false
    try {
    try { $stationMutexOwned = $stationMutex.WaitOne(30000) }
    catch [Threading.AbandonedMutexException] { $stationMutexOwned = $true }
    if (!$stationMutexOwned) { throw 'Другой запуск модуля ещё выполняется. Повторите через несколько секунд.' }
    $health = Health
    if ($health -and ($health.application -ne 'vanya-order-station' -or $health.instanceId -ne $connection.instanceId)) {
        throw 'Порт 4174 занят другим приложением. Перезапустите модуль через ярлык настроек.'
    }
    if (!$health) {
        $env:STATION_SERVER = $connection.server; $env:STATION_SERVER_KEY = $connection.key
        $env:STATION_ADMIN_KEY = $connection.adminKey; $env:STATION_LAUNCH_TOKEN = $connection.launchToken
        $env:STATION_INSTANCE_ID = $connection.instanceId; $env:STATION_DATA_ROOT = $stationData
        try {
            Start-Process -FilePath $stationNode -ArgumentList ('"' + $stationScript + '"') -WorkingDirectory $stationApp -WindowStyle Hidden `
                -RedirectStandardOutput (Join-Path $stationData 'station.log') -RedirectStandardError (Join-Path $stationData 'station-errors.log') | Out-Null
        } finally {
            foreach ($name in @('STATION_SERVER','STATION_SERVER_KEY','STATION_ADMIN_KEY','STATION_LAUNCH_TOKEN','STATION_INSTANCE_ID','STATION_DATA_ROOT')) {
                [Environment]::SetEnvironmentVariable($name, $null, 'Process')
            }
        }
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            Start-Sleep -Milliseconds 300; $health = Health
            if ($health -and $health.application -eq 'vanya-order-station' -and $health.instanceId -eq $connection.instanceId) { break }
        }
        if (!$health -or $health.application -ne 'vanya-order-station' -or $health.instanceId -ne $connection.instanceId) {
            throw 'Модуль печати не запустился. Журнал: ' + (Join-Path $stationData 'station-errors.log')
        }
    }
    } finally {
        if ($stationMutexOwned) { $stationMutex.ReleaseMutex() }
        $stationMutex.Dispose()
    }
    if ($Mode -notin @('Start','Update')) { Start-Process ($stationUrl + '/station/start/' + $connection.launchToken) }
} catch {
    if ($Mode -eq 'Verify') { Write-Output ('Package verification failed: ' + $_.Exception.Message); exit 1 }
    Add-Type -AssemblyName System.Windows.Forms
    if ($Mode -ne 'Start') { [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Заказы — печать') | Out-Null }
    else {
        [IO.Directory]::CreateDirectory($stationData) | Out-Null
        [IO.File]::WriteAllText((Join-Path $stationData 'startup-error.txt'), $_.Exception.Message)
    }
    exit 1
}
