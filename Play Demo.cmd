@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0unity\play.ps1" %*
if errorlevel 1 (
  echo.
  echo Launch failed. Run Setup Demo.cmd first and check .local\unity logs.
  pause
  exit /b 1
)
