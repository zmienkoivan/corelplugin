@echo off
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0station.ps1" -Mode Update
if errorlevel 1 pause
