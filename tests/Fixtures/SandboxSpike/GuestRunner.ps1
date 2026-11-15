param(
    [Parameter(Mandatory = $true)][string]$InstanceId,
    [Parameter(Mandatory = $true)][string]$HostMachine
)

$ErrorActionPreference = 'Stop'
$inputRoot = 'C:\SandboxSpike\Inputs'
$outputRoot = 'C:\SandboxSpike\Outputs'
if ($env:USERNAME -ne 'WDAGUtilityAccount' -or $env:COMPUTERNAME -eq $HostMachine) {
    throw '拒绝在宿主或非 Sandbox 账户运行 GuestRunner。'
}
$env:SANDBOX_SPIKE_INSTANCE = $InstanceId
$env:SANDBOX_SPIKE_HOST = $HostMachine
Set-Content -LiteralPath 'C:\SandboxSpike\instance.txt' -Value $InstanceId -Encoding UTF8
$runnerId = [guid]::NewGuid().ToString('N')
$startedAt = [DateTimeOffset]::UtcNow
$seen = @{}
$script:heartbeatSeq = 0
$script:lastHeartbeat = [DateTimeOffset]::MinValue
$script:lastState = ''
[IO.Directory]::CreateDirectory("$outputRoot\heartbeats") | Out-Null

function Write-CommittedJson($Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    $marker = [IO.File]::Open("$Path.ready", [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $marker.Dispose()
}

function Write-Heartbeat($State, $TaskId = '') {
    $now = [DateTimeOffset]::UtcNow
    if (($now - $script:lastHeartbeat).TotalSeconds -lt 1 -and $State -eq $script:lastState) { return }
    $script:heartbeatSeq++
    $path = "$outputRoot\heartbeats\{0:D12}.json" -f $script:heartbeatSeq
    Write-CommittedJson $path @{
        instanceId = $InstanceId; runnerId = $runnerId; state = $State; taskId = $TaskId
        startedAt = $startedAt.ToString('o'); heartbeatAt = [DateTimeOffset]::UtcNow.ToString('o')
        machine = $env:COMPUTERNAME; user = $env:USERNAME; pid = $PID
        sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
        workingSetBytes = [Diagnostics.Process]::GetCurrentProcess().WorkingSet64
    }
    $script:lastHeartbeat = $now
    $script:lastState = $State
}

function Read-CappedLog($Path) {
    if (-not (Test-Path $Path)) { return @{ text = ''; truncated = $false } }
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]7)
    $reader = [IO.StreamReader]::new($stream)
    try {
        $buffer = [char[]]::new(32769)
        $count = $reader.ReadBlock($buffer, 0, $buffer.Length)
        return @{ text = [string]::new($buffer, 0, [Math]::Min(32768, $count)); truncated = ($count -gt 32768) }
    } finally { $reader.Dispose() }
}

function Stop-TaskTree($Process) {
    if (-not $Process.HasExited) {
        & "$env:WINDIR\System32\taskkill.exe" /PID $Process.Id /T /F 2>&1 | Out-Null
        if (-not $Process.WaitForExit(5000)) { throw "无法结束任务进程树：$($Process.Id)" }
    }
}

function Invoke-GuestTask($Command) {
    $taskId = $Command.taskId
    $taskRoot = "$outputRoot\$taskId"
    [IO.Directory]::CreateDirectory($taskRoot) | Out-Null
    $resultPath = "$taskRoot\result.json"
    $stdoutPath = "$taskRoot\stdout.txt"
    $stderrPath = "$taskRoot\stderr.txt"
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $state = 'failed'
    $exitCode = $null
    $errorText = ''
    $process = $null
    Write-CommittedJson "$taskRoot\running.json" @{ instanceId = $InstanceId; runnerId = $runnerId; taskId = $taskId; state = 'running' }
    try {
        if (Test-Path "$inputRoot\$taskId.cancel") { $state = 'cancelled'; return }
        $script = "$inputRoot\$taskId.ps1"
        $process = Start-Process "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -ArgumentList @('-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $script) `
            -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
        # Windows PowerShell 的重定向启动路径若未保留句柄，进程退出后 ExitCode 可能为 null。
        $null = $process.Handle
        while (-not $process.WaitForExit(100)) {
            Write-Heartbeat 'executing' $taskId
            if (Test-Path "$inputRoot\$taskId.cancel") { $state = 'cancelled'; break }
            if ($clock.Elapsed.TotalSeconds -ge $Command.timeoutSeconds) { $state = 'timed_out'; break }
            $size = (Get-Item $stdoutPath).Length + (Get-Item $stderrPath).Length
            if ($size -gt 1MB) { $errorText = '输出超过 1 MiB，终止任务。'; break }
        }
        if ($process.HasExited) {
            $exitCode = $process.ExitCode
            $state = if ($exitCode -eq 0) { 'completed' } else { 'failed' }
        } else { Stop-TaskTree $process }
    } catch {
        $errorText = $_.Exception.Message
        if ($process) { Stop-TaskTree $process }
    } finally {
        if ($process) { $process.Dispose() }
        $stdout = Read-CappedLog $stdoutPath
        $stderr = Read-CappedLog $stderrPath
        Write-CommittedJson $resultPath @{
            instanceId = $InstanceId; runnerId = $runnerId; taskId = $taskId; state = $state
            exitCode = $exitCode; stdout = $stdout.text; stderr = $stderr.text; error = $errorText
            outputTruncated = ($stdout.truncated -or $stderr.truncated)
            durationMs = $clock.ElapsedMilliseconds
        }
    }
}

try {
    Write-Heartbeat 'ready'
    while (-not (Test-Path "$inputRoot\stop")) {
        foreach ($file in (Get-ChildItem $inputRoot -Filter '*.command.json.ready' | Sort-Object Name)) {
            if ($seen.ContainsKey($file.Name)) { continue }
            $commandPath = $file.FullName.Substring(0, $file.FullName.Length - '.ready'.Length)
            $command = Get-Content -LiteralPath $commandPath -Raw | ConvertFrom-Json
            if ($command.instanceId -ne $InstanceId -or $command.taskId -notmatch '^[a-f0-9]{32}$') {
                throw "命令实例或任务标识无效：$($file.Name)"
            }
            $seen[$file.Name] = $true
            Invoke-GuestTask $command
        }
        Write-Heartbeat 'ready'
        Start-Sleep -Milliseconds 100
    }
    Write-Heartbeat 'stopped'
    & "$env:WINDIR\System32\shutdown.exe" /s /t 0
} catch {
    $_ | Out-String | Set-Content "$outputRoot\runner-error.txt" -Encoding UTF8
    throw
}
