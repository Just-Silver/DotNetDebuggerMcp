# 切换 dotnetdebugger MCP server 的 disabled 状态（opencode V2 检测配置变化自动重启 server）。
# 用法（在 opencode 会话里后台运行）：
#   pwsh scripts/mcp-restart.ps1            # toggle：disabled<->启用（默认）
#   pwsh scripts/mcp-restart.ps1 -Enable    # 强制启用（移除 disabled）
#   pwsh scripts/mcp-restart.ps1 -Disable   # 强制停用（加 disabled:true）
#   pwsh scripts/mcp-restart.ps1 -Restart   # 重启：先停用再启用（等 opencode 断开后重连，拉最新编译）
#
# 背景：代码改完编译后，改 opencode.json 的 disabled 字段即可让 opencode V2 热重载 MCP server，
# 无需手动杀进程/重启 opencode。脚本只做文本级增删 disabled 行，保留其余格式。

param(
    [switch]$Enable,
    [switch]$Disable,
    [switch]$Restart
)

$jsonPath = Join-Path $PSScriptRoot '..\opencode.json'
$jsonPath = (Resolve-Path $jsonPath).Path

function Test-Disabled {
    param([string[]]$Lines)
    return $Lines | Where-Object { $_ -match '"disabled"\s*:\s*true' } | Select-Object -First 1
}

function Set-Disabled {
    param([string[]]$Lines, [bool]$On)
    if ($On) {
        # 已 disabled 则不动
        if (Test-Disabled $Lines) { return $Lines }
        # 在 "command": [...] 行后插入 "disabled": true（server 块内，缩进与 command 一致）
        $out = New-Object System.Collections.Generic.List[string]
        foreach ($line in $Lines) {
            $out.Add($line)
            if ($line -match '"command"\s*:') {
                $indent = ($line -replace '^(\s*).*', '$1')
                $out.Add("${indent}`"disabled`": true")
            }
        }
        return $out.ToArray()
    }
    else {
        return @($Lines | Where-Object { $_ -notmatch '"disabled"\s*:\s*true' })
    }
}

$action = if ($Enable) { 'enable' } elseif ($Disable) { 'disable' } elseif ($Restart) { 'restart' } else { 'toggle' }

$lines = Get-Content $jsonPath
$wasDisabled = [bool](Test-Disabled $lines)

switch ($action) {
    'enable'  { $newLines = Set-Disabled $lines $false }
    'disable' { $newLines = Set-Disabled $lines $true }
    'toggle'  { $newLines = Set-Disabled $lines (-not $wasDisabled) }
    'restart' {
        # 确保先 disabled，等 opencode 断开，再启用
        $newLines = Set-Disabled $lines $true
        Set-Content -Path $jsonPath -Value $newLines -Encoding utf8
        Write-Host "[mcp-restart] 已停用 dotnetdebugger，等待 opencode 断开 (2s)..."
        Start-Sleep -Seconds 2
        $lines2 = Get-Content $jsonPath
        $newLines = Set-Disabled $lines2 $false
        Set-Content -Path $jsonPath -Value $newLines -Encoding utf8
        Write-Host "[mcp-restart] 已重新启用 dotnetdebugger（opencode 将拉起最新编译的 server）"
        exit 0
    }
}

Set-Content -Path $jsonPath -Value $newLines -Encoding utf8

$nowDisabled = [bool](Test-Disabled $newLines)
if ($action -eq 'toggle') {
    Write-Host "[mcp-restart] toggle: dotnetdebugger -> $(if ($nowDisabled) { 'disabled' } else { 'enabled' })"
} else {
    Write-Host "[mcp-restart] dotnetdebugger -> $(if ($nowDisabled) { 'disabled' } else { 'enabled' }) ($action)"
}
