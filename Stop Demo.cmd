@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0prototype\start-unity-service.ps1" -Stop
if errorlevel 1 pause
