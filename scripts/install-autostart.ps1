<#
  注册「应用管家」为 Windows 计划任务，实现开机 / 登录自启。
  用法：
    # 登录后启动（普通权限即可）
    powershell -ExecutionPolicy Bypass -File .\scripts\install-autostart.ps1 -Mode Logon
    # 开机时启动（需要管理员）
    powershell -ExecutionPolicy Bypass -File .\scripts\install-autostart.ps1 -Mode Boot
#>
param(
    [ValidateSet('Boot', 'Logon')]
    [string]$Mode = 'Boot',
    [string]$Python = '',
    [string]$Script = '',
    [string]$TaskName = 'AppManager'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

function Fail([string]$Message, [int]$Code = 1) {
    Write-Output ("ERROR: " + $Message)
    exit $Code
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principalCheck = New-Object Security.Principal.WindowsPrincipal($identity)
$isAdmin = $principalCheck.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if ($Mode -eq 'Boot' -and -not $isAdmin) {
    Fail '“开机时启动”需要管理员权限。请右键点击 install-autostart.cmd，选择“以管理员身份运行”；或改用“登录后启动”模式（不需要管理员）。' 3
}

if (-not $Script) {
    $Script = Join-Path $PSScriptRoot '..\manager.py'
}
if (-not (Test-Path -LiteralPath $Script)) {
    Fail "找不到 manager.py：$Script" 4
}
$Script = (Resolve-Path -LiteralPath $Script).Path
$workDir = Split-Path -Parent $Script

$usePyLauncher = $false
if (-not $Python) {
    $found = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($found) { $Python = $found.Source }
}
if ($Python -like '*\WindowsApps\python*.exe') {
    $launcher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($launcher) {
        $Python = $launcher.Source
        $usePyLauncher = $true
    }
}
if (-not $Python -or -not (Test-Path -LiteralPath $Python)) {
    Fail '找不到 Python，请先安装 Python 3.8 或更高版本。' 4
}
$Python = (Resolve-Path -LiteralPath $Python).Path

# 登录模式使用无控制台窗口的 pythonw.exe，避免每次登录弹出一个黑框
$taskExe = $Python
if ($Mode -eq 'Logon') {
    $wName = if ((Split-Path -Leaf $Python) -ieq 'py.exe') { 'pyw.exe' } else { 'pythonw.exe' }
    $candidate = Join-Path (Split-Path -Parent $Python) $wName
    if (Test-Path -LiteralPath $candidate) {
        $taskExe = $candidate
    }
}

$argLine = '"' + $Script + '"'
if ($usePyLauncher) {
    $argLine = '-3 ' + $argLine
}

$action = New-ScheduledTaskAction -Execute $taskExe -Argument $argLine -WorkingDirectory $workDir

if ($Mode -eq 'Boot') {
    $trigger = New-ScheduledTaskTrigger -AtStartup
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $modeText = '开机时启动（SYSTEM 账户，无需登录 Windows）'
} else {
    $currentUser = $identity.Name
    if ($currentUser -match '^(NT AUTHORITY|NT SERVICE)\\' -or $currentUser -eq 'SYSTEM') {
        $interactive = $null
        try {
            $interactive = (Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).UserName
        } catch {
            $interactive = $null
        }
        if ($interactive) {
            $currentUser = $interactive
        } else {
            Fail '当前管理服务以 SYSTEM 运行，无法确定要登录的用户。请改用管理员 PowerShell 运行 install-autostart.cmd Logon。' 6
        }
    }
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
    # RunLevel Highest 需要管理员；普通权限下用默认级别
    $principalArgs = @{
        UserId    = $currentUser
        LogonType = 'Interactive'
    }
    if ($isAdmin) { $principalArgs['RunLevel'] = 'Highest' }
    $principal = New-ScheduledTaskPrincipal @principalArgs
    $modeText = "用户登录后启动（$currentUser）"
}

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description '应用管家：自动运行受托管的应用' `
    -Force | Out-Null

$task = Get-ScheduledTask -TaskName $TaskName
Write-Output "OK: 已注册计划任务 $TaskName"
Write-Output "模式: $modeText"
Write-Output "程序: $taskExe"
Write-Output "脚本: $Script"
Write-Output "状态: $($task.State)"
Write-Output "说明: 端口与监听地址读取 config.json；修改后请重启管理服务。"
