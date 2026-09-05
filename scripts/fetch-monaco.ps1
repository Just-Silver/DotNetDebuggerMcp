# 拉取 monaco-editor 0.55.1 静态资产到 DotNetDebugger.Web/wwwroot/lib/monaco-editor
# （参考 DebuggerExternals\BlazorMonaco 的资产布局；版本如需升级改 $Version）
# 用法: powershell -ExecutionPolicy Bypass -File scripts/fetch-monaco.ps1
$ErrorActionPreference = "Stop"

$Version = "0.55.1"
$Root = Split-Path $PSScriptRoot -Parent
$Dest = Join-Path $Root "src/DotNetDebugger.Web/wwwroot/lib/monaco-editor"
$Tmp = Join-Path $env:TEMP "monaco-fetch"

Write-Host "拉取 monaco-editor $Version -> $Dest"
if (Test-Path $Tmp) { Remove-Item $Tmp -Recurse -Force }
New-Item -ItemType Directory -Path $Tmp | Out-Null

Push-Location $Tmp
try {
    npm pack "monaco-editor@$Version" | Out-Null
    $tgz = Get-ChildItem *.tgz | Select-Object -First 1
    tar -xzf $tgz.Name
    New-Item -ItemType Directory -Path $Dest -Force | Out-Null
    Copy-Item "$Tmp/package/min" "$Dest/min" -Recurse -Force
} finally {
    Pop-Location
    Remove-Item $Tmp -Recurse -Force
}

$files = Get-ChildItem "$Dest/min" -Recurse -File
Write-Host "完成：$($files.Count) 文件，$([math]::Round(($files | Measure-Object Length -Sum).Sum/1MB,1)) MB"
