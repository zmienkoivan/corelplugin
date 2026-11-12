@echo off
setlocal
set "stationLauncher=%LOCALAPPDATA%\VanyaTools\OrderStation\station.ps1"
if not exist "%stationLauncher%" (
    echo Install the print module with Install.cmd first.
    pause
    exit /b 1
)
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File "%stationLauncher%" -Mode Open
exit /b %errorlevel%
