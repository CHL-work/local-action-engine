@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
if errorlevel 1 (
  echo.
  echo Setup failed. Read the error above and docs\windows-setup.md.
  pause
  exit /b 1
)
echo.
echo Setup finished. Open Play Demo.cmd to start.
pause
