param(
    [switch]$Test
)

$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $csc)) {
    throw '未找到 .NET Framework 编译器 csc.exe'
}

$here = Split-Path -Parent $PSScriptRoot
$desktop = $PSScriptRoot
$ico = Join-Path $desktop 'app.ico'
if (-not (Test-Path -LiteralPath $ico)) {
    powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $desktop 'make-icon.ps1')
}

$refs = @(
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Drawing.dll',
    '/r:System.Web.Extensions.dll'
)

if ($Test) {
    & $csc /nologo /langversion:5 /target:exe /main:AppManager.Tests.Program `
        /out:"$desktop\tests\CoreTests.exe" `
        "$desktop\Core.cs" "$desktop\tests\CoreTests.cs" @refs
    if ($LASTEXITCODE -ne 0) { throw '测试程序编译失败' }
    Write-Output 'CoreTests.exe 编译完成'
    exit 0
}

& $csc /nologo /langversion:5 /target:winexe /main:AppManager.Program /optimize+ `
    /win32manifest:"$desktop\app.manifest" `
    /win32icon:"$ico" `
    /out:"$here\AppManagerDesktop.exe" `
    "$desktop\Core.cs" "$desktop\Ui.cs" "$desktop\Dialogs.cs" @refs
if ($LASTEXITCODE -ne 0) { throw '编译失败' }
Write-Output ("AppManagerDesktop.exe 编译完成: " + (Join-Path $here 'AppManagerDesktop.exe'))
