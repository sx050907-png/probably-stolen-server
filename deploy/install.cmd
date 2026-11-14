@echo off
cd /d "%~dp0"
if not exist "%~dp0install.ps1" (
  echo Download and extract the complete Windows installer ZIP first.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
if errorlevel 1 pause
