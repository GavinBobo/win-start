<#
  移除「应用管家」的自启计划任务。
#>
param(
    [string]$TaskName = 'AppManager'
)

$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [Console]::OutputEncoding

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if (-not $task) {
    Write-Output "INFO: 计划任务 $TaskName 不存在，无需移除。"
    exit 0
}

try {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
} catch {
    Write-Output ("ERROR: 移除失败（可能需要管理员权限）：" + $_.Exception.Message)
    exit 3
}

Write-Output "OK: 已移除计划任务 $TaskName"
Write-Output "说明: 正在运行的管理服务不会被停止；如需结束，请在面板「设置 → 停止管理服务」。"
