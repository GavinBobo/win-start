@echo off
setlocal
cd /d "%~dp0"

echo ============================================
echo   应用管家 - 启动诊断
echo ============================================
echo.

echo [1/3] 检查 .NET Framework...
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release
if errorlevel 1 (
    echo.
    echo 未检测到 .NET Framework 4.x。
    echo 请先安装 .NET Framework 4.8：
    echo https://dotnet.microsoft.com/download/dotnet-framework/net48
    echo.
)

echo [2/3] 解除“来自网络”的阻止标记...
powershell -NoProfile -Command "Get-ChildItem -LiteralPath '%~dp0' -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue"
echo 完成.
echo.

echo [3/3] 运行程序自检（会弹出一个结果窗口，看完点“确定”）...
start /wait "" "%~dp0AppManagerDesktop.exe" --self-test
echo.

echo ---------------- 诊断报告 ----------------
if exist "%~dp0diagnose-report.txt" type "%~dp0diagnose-report.txt"
if exist "%LOCALAPPDATA%\AppManagerDesktop\diagnose-report.txt" type "%LOCALAPPDATA%\AppManagerDesktop\diagnose-report.txt"
if exist "%TEMP%\AppManagerDesktop-error.log" (
    echo.
    echo ---------------- 启动错误 ----------------
    type "%TEMP%\AppManagerDesktop-error.log"
)
echo -----------------------------------------
echo.
echo 如果还是打不开，请把上面的内容（或 diagnose-report.txt）发给我。
pause
