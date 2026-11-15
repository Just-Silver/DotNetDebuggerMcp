$ErrorActionPreference = 'Stop'
if (Test-Path 'C:\SandboxSpike\gui.json') { throw 'GUI 已有快照，请先确认旧进程，不重复启动。' }
$process = Start-Process 'C:\SandboxSpike\Bin\SandboxProbe.exe' -ArgumentList 'gui' -PassThru
$clock = [Diagnostics.Stopwatch]::StartNew()
while ($clock.Elapsed.TotalSeconds -lt 20) {
    if ($process.HasExited) { throw "GUI 提前退出：$($process.ExitCode)" }
    if (Test-Path 'C:\SandboxSpike\gui.json') {
        [pscustomobject]@{ processId = $process.Id; snapshot = 'C:\SandboxSpike\gui.json' } | ConvertTo-Json
        exit 0
    }
    Start-Sleep -Milliseconds 100
}
throw '等待 GUI 快照超时。'
