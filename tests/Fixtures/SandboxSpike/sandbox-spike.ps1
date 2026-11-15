param(
    [ValidateSet('start', 'status', 'exec', 'task', 'cancel', 'artifacts', 'stop')]
    [string]$Action = 'status',
    [string]$File = '',
    [string]$TaskId = '',
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 30,
    [switch]$Detach
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\TestData\SandboxSpike'))
$statePath = Join-Path $root 'current.json'
$sandboxProcessNames = @('WindowsSandbox', 'WindowsSandboxClient', 'WindowsSandboxRemoteSession')

function Write-Json($Path, $Value) {
    $temp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temp, $Path)
}

function Read-State {
    if (-not (Test-Path -LiteralPath $statePath)) { throw '尚未启动此 PoC；先执行 -Action start。' }
    $state = Read-SharedJson $statePath
    if ($state.instanceId -notmatch '^[a-f0-9]{32}$') { throw '实例标识无效。' }
    foreach ($name in @('inputs', 'outputs')) {
        $expected = [IO.Path]::GetFullPath((Join-Path (Join-Path $root $state.instanceId) $name))
        $actual = [IO.Path]::GetFullPath([string]$state.$name)
        if (-not $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "实例 $name 路径不属于当前 PoC，拒绝操作。"
        }
    }
    $state
}

function Read-SharedJson($Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]7)
    $reader = [IO.StreamReader]::new($stream)
    try { $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
}

function Read-Heartbeat($State) {
    $folder = Join-Path $State.outputs 'heartbeats'
    if (-not (Test-Path -LiteralPath $folder)) { return $null }
    $latest = Get-ChildItem -LiteralPath $folder -Filter '*.json.ready' | Sort-Object Name | Select-Object -Last 1
    if (-not $latest) { return $null }
    $path = $latest.FullName.Substring(0, $latest.FullName.Length - '.ready'.Length)
    $heartbeat = Read-SharedJson $path
    if ($heartbeat.instanceId -ne $State.instanceId) { throw 'Runner 实例不匹配，拒绝继续。' }
    $age = ([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($heartbeat.heartbeatAt)).TotalSeconds
    if ($age -gt 10 -and $heartbeat.state -ne 'stopped') { throw "Runner 心跳已过期 $([int]$age)s，任务结果可能不确定。" }
    $heartbeat
}

function Save-ClientIdentity($State) {
    $start = [DateTimeOffset]$State.startedAt
    $clients = @(Get-Process WindowsSandboxRemoteSession, WindowsSandboxClient -ErrorAction SilentlyContinue |
        Where-Object { [Math]::Abs(($_.StartTime.ToUniversalTime() - $start.UtcDateTime).TotalSeconds) -lt 5 })
    if ($clients.Count -ne 1) { return }
    $client = $clients[0]
    Write-Json (Join-Path (Split-Path $State.outputs) 'client-identity.json') @{
        instanceId = $State.instanceId; pid = $client.Id; processName = $client.ProcessName
        startedTicks = $client.StartTime.ToUniversalTime().Ticks
    }
}

function Start-Spike {
    [IO.Directory]::CreateDirectory($root) | Out-Null
    $mutex = [Threading.Mutex]::new($false, 'Local\DotNetDebuggerMcp.SandboxSpike.Start')
    if (-not $mutex.WaitOne(0)) { $mutex.Dispose(); throw '另一个 start 正在执行。' }
    try {
        if (Test-Path -LiteralPath $statePath) {
            $old = Read-State
            $heartbeat = $null
            try { $heartbeat = Read-Heartbeat $old }
            catch { [Console]::Error.WriteLine($_.Exception.Message) }
            if ($heartbeat -and $heartbeat.state -ne 'stopped') { return $heartbeat }
            if (Get-Process -Name $sandboxProcessNames -ErrorAction SilentlyContinue) {
                throw '已有 Sandbox 但此 PoC 未就绪；拒绝创建第二实例，请先检查旧窗口。'
            }
            Move-Item -LiteralPath $statePath -Destination "$statePath.$([guid]::NewGuid().ToString('N')).previous"
        }
        if (Get-Process -Name $sandboxProcessNames -ErrorAction SilentlyContinue) {
            throw '检测到非本 PoC 的 Sandbox；不会接管或关闭它。'
        }
        $instanceId = [guid]::NewGuid().ToString('N')
        $sessionRoot = Join-Path $root $instanceId
        $inputs = Join-Path $sessionRoot 'inputs'
        $outputs = Join-Path $sessionRoot 'outputs'
        [IO.Directory]::CreateDirectory($inputs) | Out-Null
        [IO.Directory]::CreateDirectory($outputs) | Out-Null
        Copy-Item (Join-Path $PSScriptRoot 'GuestRunner.ps1') $inputs
        $mapped = "<MappedFolder><HostFolder>$([Security.SecurityElement]::Escape($inputs))</HostFolder><SandboxFolder>C:\SandboxSpike\Inputs</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>"
        $mapped += "<MappedFolder><HostFolder>$([Security.SecurityElement]::Escape($outputs))</HostFolder><SandboxFolder>C:\SandboxSpike\Outputs</SandboxFolder><ReadOnly>false</ReadOnly></MappedFolder>"
        $bin = Join-Path $root 'bin'
        if (Test-Path $bin) {
            $mapped += "<MappedFolder><HostFolder>$([Security.SecurityElement]::Escape($bin))</HostFolder><SandboxFolder>C:\SandboxSpike\Bin</SandboxFolder><ReadOnly>true</ReadOnly></MappedFolder>"
        }
        $command = "powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File C:\SandboxSpike\Inputs\GuestRunner.ps1 -InstanceId $instanceId -HostMachine $env:COMPUTERNAME"
        $config = "<Configuration><VGpu>Disable</VGpu><Networking>Disable</Networking><ClipboardRedirection>Disable</ClipboardRedirection><AudioInput>Disable</AudioInput><VideoInput>Disable</VideoInput><PrinterRedirection>Disable</PrinterRedirection><MemoryInMB>4096</MemoryInMB><MappedFolders>$mapped</MappedFolders><LogonCommand><Command>$command</Command></LogonCommand></Configuration>"
        $configPath = Join-Path $sessionRoot 'sandbox.wsb'
        [IO.File]::WriteAllText($configPath, $config, [Text.UTF8Encoding]::new($false))
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $process = Start-Process "$env:WINDIR\System32\WindowsSandbox.exe" -ArgumentList "`"$configPath`"" -PassThru
        $state = @{ instanceId = $instanceId; inputs = $inputs; outputs = $outputs; sandboxPid = $process.Id; startedAt = [DateTimeOffset]::UtcNow.ToString('o') }
        Write-Json $statePath $state
        while ($clock.Elapsed.TotalSeconds -lt 120) {
            if (Test-Path (Join-Path $outputs 'runner-error.txt')) { throw (Get-Content (Join-Path $outputs 'runner-error.txt') -Raw) }
            $heartbeat = Read-Heartbeat $state
            if ($heartbeat) {
                Save-ClientIdentity $state
                return @{ instanceId = $instanceId; readyMs = $clock.ElapsedMilliseconds; runner = $heartbeat; outputs = $outputs }
            }
            Start-Sleep -Milliseconds 200
        }
        throw "等待 Runner 超过 120s；保留窗口和诊断目录：$sessionRoot"
    } finally { $mutex.ReleaseMutex(); $mutex.Dispose() }
}

function Get-Task($State, $Id) {
    if ($Id -notmatch '^[a-f0-9]{32}$') { throw 'TaskId 必须是 32 位十六进制标识。' }
    $path = Join-Path $State.outputs "$Id\result.json"
    if (-not (Test-Path -LiteralPath "$path.ready")) {
        $path = Join-Path $State.outputs "$Id\running.json"
        if (-not (Test-Path -LiteralPath "$path.ready")) { return @{ instanceId = $State.instanceId; taskId = $Id; state = 'queued' } }
    }
    $result = Read-SharedJson $path
    if ($result.instanceId -ne $State.instanceId -or $result.taskId -ne $Id) { throw '任务身份不匹配。' }
    $result
}

function Submit-Script($State) {
    $heartbeat = Read-Heartbeat $State
    if (-not $heartbeat -or $heartbeat.state -eq 'stopped') { throw 'Runner 未就绪；不回退到宿主执行。' }
    if (-not (Test-Path -LiteralPath $File -PathType Leaf)) { throw "找不到脚本：$File" }
    $id = [guid]::NewGuid().ToString('N')
    Copy-Item -LiteralPath $File -Destination (Join-Path $State.inputs "$id.ps1")
    $command = @{ instanceId = $State.instanceId; taskId = $id; timeoutSeconds = $TimeoutSeconds }
    $name = "{0}-{1}.command.json" -f [DateTime]::UtcNow.ToString('yyyyMMddHHmmssfffffff'), $id
    $commandPath = Join-Path $State.inputs $name
    [IO.File]::WriteAllText($commandPath, ($command | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    $marker = [IO.File]::Open("$commandPath.ready", [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $marker.Dispose()
    if ($Detach) { return $command }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt ($TimeoutSeconds + 15)) {
        $result = Get-Task $State $id
        if ($result.state -notin @('running', 'queued')) { return $result }
        Read-Heartbeat $State | Out-Null
        Start-Sleep -Milliseconds 100
    }
    return @{ instanceId = $State.instanceId; taskId = $id; state = 'wait_timed_out'; message = '只停止客户端等待，任务仍可查询或显式取消。' }
}

function Stop-Spike($State) {
    [IO.File]::WriteAllText((Join-Path $State.inputs 'stop'), '')
    $clock = [Diagnostics.Stopwatch]::StartNew()
    while ($clock.Elapsed.TotalSeconds -lt 15) {
        $heartbeat = Read-Heartbeat $State
        if ($heartbeat -and $heartbeat.state -eq 'stopped') {
            $terminated = Stop-OwnedClient $State
            return @{ instanceId = $State.instanceId; state = 'stopped'; ownedClientTerminated = $terminated }
        }
        Start-Sleep -Milliseconds 200
    }
    return @{ instanceId = $State.instanceId; state = 'stop_requested'; message = '尚未结束当前任务；不会强制关闭未知客户端。' }
}

function Stop-OwnedClient($State) {
    $path = Join-Path (Split-Path $State.outputs) 'client-identity.json'
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    $identity = Read-SharedJson $path
    if ($identity.instanceId -ne $State.instanceId -or $identity.processName -notin @('WindowsSandboxRemoteSession', 'WindowsSandboxClient')) { return $false }
    $client = Get-Process -Id ([int]$identity.pid) -ErrorAction SilentlyContinue
    if (-not $client) { return $false }
    try {
        if ($client.ProcessName -ne $identity.processName -or $client.StartTime.ToUniversalTime().Ticks -ne $identity.startedTicks) { return $false }
        # WM_CLOSE 会弹确认框；只在 guest 停止后结束启动时记录的同一个客户端进程。
        $client.Kill()
        return $client.WaitForExit(5000)
    } catch [InvalidOperationException] {
        return $false
    }
}

try {
    if ($Action -eq 'start') { Start-Spike | ConvertTo-Json -Depth 8; exit 0 }
    $state = Read-State
    $value = switch ($Action) {
        'status' {
            $heartbeat = Read-Heartbeat $state
            if ($heartbeat) { $heartbeat } else { @{ instanceId = $state.instanceId; state = 'waiting_for_runner' } }
        }
        'exec' { Submit-Script $state }
        'task' { Read-Heartbeat $state | Out-Null; Get-Task $state $TaskId }
        'cancel' {
            if ($TaskId -notmatch '^[a-f0-9]{32}$') { throw 'TaskId 无效。' }
            [IO.File]::WriteAllText((Join-Path $state.inputs "$TaskId.cancel"), '')
            @{ taskId = $TaskId; state = 'cancel_requested' }
        }
        'artifacts' { Get-ChildItem -LiteralPath $state.outputs -Recurse -File | Select-Object FullName, Length }
        'stop' { Stop-Spike $state }
    }
    $value | ConvertTo-Json -Depth 8
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
