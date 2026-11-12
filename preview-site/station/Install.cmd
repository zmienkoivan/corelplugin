@echo off
setlocal
set "stationMode=Install"
if /I "%~1"=="--check" set "stationMode=Verify"
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0station.ps1" -Mode %stationMode%
set "stationResult=%errorlevel%"
if /I "%stationMode%"=="Verify" exit /b %stationResult%
if not "%stationResult%"=="0" (
    echo Installation failed. Extract the entire ZIP to a folder and run Install.cmd again.
    pause
)
exit /b %stationResult%
