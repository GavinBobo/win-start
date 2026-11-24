@echo off
setlocal
cd /d "%~dp0"
where py >nul 2>nul
if %errorlevel%==0 (
    py -3 manager.py --open
    goto :done
)
python manager.py --open

:done
if errorlevel 1 pause
