@echo off
powershell.exe -NoProfile -STA -ExecutionPolicy Bypass -File "%~dp0Add-Zebra.ps1"
if errorlevel 1 pause
