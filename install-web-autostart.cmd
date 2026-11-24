@echo off
setlocal
cd /d "%~dp0"

set "MODE=Boot"
if /i "%~1"=="logon" set "MODE=Logon"

if /i "%MODE%"=="Logon" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-autostart.ps1" -Mode Logon
    echo.
    pause
    exit /b
)

net session >nul 2>&1
if not "%errorlevel%"=="0" (
    echo Requesting administrator privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs -ArgumentList '%MODE%'"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\install-autostart.ps1" -Mode %MODE%
echo.
pause
