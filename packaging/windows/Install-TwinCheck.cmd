@echo off
setlocal

fltmc.exe >nul 2>&1
if errorlevel 1 (
    powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
set "result=%errorlevel%"
echo.
if not "%result%"=="0" echo Installation failed with exit code %result%.
pause
exit /b %result%
