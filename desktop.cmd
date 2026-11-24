@echo off
setlocal
cd /d "%~dp0"
if not exist "%~dp0AppManagerDesktop.exe" (
    echo AppManagerDesktop.exe not found.
    echo Run: powershell -ExecutionPolicy Bypass -File desktop\build-desktop.ps1
    pause
    exit /b 1
)
start "" "%~dp0AppManagerDesktop.exe"
