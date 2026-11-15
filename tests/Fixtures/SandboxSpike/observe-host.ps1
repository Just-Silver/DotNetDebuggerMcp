<#
.SYNOPSIS
    只读采样宿主指针、前台窗口与 Windows Sandbox 外层窗口状态（P0 输入隔离探测）。
.DESCRIPTION
    从 tests/TestData/SandboxSpike/current.json 读取本 PoC 实例，按固定间隔记录宿主侧的
    GetCursorPos / GetForegroundWindow / Sandbox 客户端窗口状态与工作集；仅在实例所有权可确认时，
    可选对 Sandbox 外层窗口执行一次 ShowWindowAsync 最小化/还原。
    不做：点击、SendInput、SetForegroundWindow、读取窗口标题或键盘文本、启动或停止 Sandbox。
    观测文件写入 sessionRoot（与 inputs/outputs 同级，guest 未映射该目录），避免 guest 伪造。
.PARAMETER Seconds
    采样时长秒数，默认 15，范围 1..120。
.PARAMETER WindowState
    Keep（默认）不改变窗口；Minimize 在采样开始时最小化、结束时还原到开始前状态；
    Restore 仅还原已最小化的窗口，不主动最小化；Background 最小化后不激活显示并置底。
.NOTES
    光标变化可能来自用户本人操作，本观测不能证明输入隔离，也不能把状态不变判为隔离成功。
#>
param(
    [ValidateRange(1, 120)][int]$Seconds = 15,
    [ValidateSet('Keep', 'Minimize', 'Restore', 'Background')][string]$WindowState = 'Keep'
)

$ErrorActionPreference = 'Stop'
$spikeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\TestData\SandboxSpike'))
$statePath = Join-Path $spikeRoot 'current.json'
$sampleIntervalMs = 500
$swMinimize = 6
$swRestore = 9
$sandboxProcessNames = @('WindowsSandbox', 'WindowsSandboxClient', 'WindowsSandboxRemoteSession', 'WindowsSandboxServer')
$sandboxWindowProcessNames = @('WindowsSandboxClient', 'WindowsSandboxRemoteSession')

if (-not ('SandboxHostNative' -as [type])) {
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class SandboxHostNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hWnd, int command);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    public static int[] Bounds(IntPtr hWnd)
    {
        RECT rect;
        return GetWindowRect(hWnd, out rect) ? new[] { rect.Left, rect.Top, rect.Right, rect.Bottom } : null;
    }

    public static long[] FindTopLevelWindows(uint processId)
    {
        var handles = new List<long>();
        EnumWindows(delegate (IntPtr hWnd, IntPtr lParam)
        {
            uint owner;
            GetWindowThreadProcessId(hWnd, out owner);
            if (owner == processId) { handles.Add(hWnd.ToInt64()); }
            return true;
        }, IntPtr.Zero);
        return handles.ToArray();
    }
}
'@
}

function Read-PocState {
    if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) {
        throw "未找到 $statePath；尚未启动 PoC，无法确定观测会话。"
    }
    $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
    if ($state.instanceId -notmatch '^[a-f0-9]{32}$') { throw 'current.json 的 instanceId 无效。' }
    $expected = [IO.Path]::GetFullPath((Join-Path (Join-Path $spikeRoot $state.instanceId) 'outputs'))
    $actual = [IO.Path]::GetFullPath([string]$state.outputs)
    if (-not $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) {
        throw "current.json 的 outputs 不属于该实例，拒绝继续：$actual"
    }
    if (-not (Test-Path -LiteralPath $actual -PathType Container)) { throw "实例 outputs 目录不存在：$actual" }
    $state
}

function Format-IsoUtc($Value) {
    if ($null -eq $Value) { return $null }
    ([DateTimeOffset]$Value).ToUniversalTime().ToString('o')
}

function Get-ProcessStartTimeText($Process) {
    try { $Process.StartTime.ToUniversalTime().ToString('o') } catch { $null }
}

function Test-ProcessStartMatches($Process, [DateTimeOffset]$StartedAt) {
    try {
        $start = [DateTimeOffset]$Process.StartTime
        return ($start -ge $StartedAt.AddSeconds(-2) -and $start -le $StartedAt.AddSeconds(120))
    } catch { return $false }
}

function Test-SandboxOwnership($State) {
    $result = [ordered]@{
        established = $false
        exclusive = $false
        basis = 'current.json + host process identity/start time（不读取 guest 心跳或任务状态）'
        reason = ''
        launcherAlive = $false
        windowPid = $null
        trackedPids = @()
        processes = @()
    }
    $all = @(Get-Process -Name $sandboxProcessNames -ErrorAction SilentlyContinue)
    $result.processes = @($all | ForEach-Object {
        [ordered]@{ pid = $_.Id; name = $_.ProcessName; startTime = (Get-ProcessStartTimeText $_) }
    })
    if ($all.Count -eq 0) {
        $result.reason = '未发现 WindowsSandbox/WindowsSandboxClient 进程。'
        return [pscustomobject]$result
    }
    $startedAt = [DateTimeOffset]$State.startedAt
    $launchers = @($all | Where-Object { $_.ProcessName -eq 'WindowsSandbox' })
    $windowCandidates = @($all | Where-Object { $_.ProcessName -in $sandboxWindowProcessNames })
    $servers = @($all | Where-Object { $_.ProcessName -eq 'WindowsSandboxServer' })
    $result.launcherAlive = @($launchers | Where-Object { $_.Id -eq [int]$State.sandboxPid }).Count -eq 1
    $foreign = @($launchers | Where-Object { $_.Id -ne [int]$State.sandboxPid })
    if ($foreign.Count -gt 0) {
        $result.reason = '存在非本 PoC sandboxPid 的 WindowsSandbox 进程，无法确认所有权。'
        return [pscustomobject]$result
    }
    $matchedWindows = @($windowCandidates | Where-Object { Test-ProcessStartMatches $_ $startedAt })
    if ($matchedWindows.Count -gt 1) {
        $result.reason = "多个窗口进程的启动时间都接近本实例（$($matchedWindows.Count) 个），无法确认所有权。"
        return [pscustomobject]$result
    }
    if ($matchedWindows.Count -eq 1) {
        $windowProcess = $matchedWindows[0]
    } elseif ($result.launcherAlive) {
        $launcher = $launchers | Where-Object { $_.Id -eq [int]$State.sandboxPid } | Select-Object -First 1
        if (-not (Test-ProcessStartMatches $launcher $startedAt)) {
            $result.reason = 'sandboxPid 进程启动时间与 current.json 不匹配，无法确认所有权。'
            return [pscustomobject]$result
        }
        $windowProcess = $launcher
    } else {
        $result.reason = '未找到与本实例启动时间匹配的唯一窗口进程（launch.exe 已退出且客户端未出现？）。'
        return [pscustomobject]$result
    }
    $matchedServers = @($servers | Where-Object { Test-ProcessStartMatches $_ $startedAt })
    $extraWindows = @($windowCandidates | Where-Object { $_.Id -ne $windowProcess.Id })
    $result.established = $true
    $result.exclusive = ($extraWindows.Count -eq 0)
    $result.windowPid = $windowProcess.Id
    $result.trackedPids = @($windowProcess.Id) + @($matchedServers | ForEach-Object { $_.Id })
    if ($result.exclusive) {
        $result.reason = '窗口进程启动时间与 current.json 匹配，Sandbox 进程唯一。'
    } else {
        $result.reason = "窗口进程 (pid=$($windowProcess.Id)) 与本实例启动时间匹配，但存在其他 Sandbox 窗口进程，未证明独占；仅做只读采样。"
    }
    [pscustomobject]$result
}

function Resolve-SandboxWindow($Ownership) {
    if (-not $Ownership.established -or $null -eq $Ownership.windowPid) {
        return @{ handle = [IntPtr]::Zero; pid = $null }
    }
    $handles = @([SandboxHostNative]::FindTopLevelWindows([uint32]$Ownership.windowPid) |
        ForEach-Object { [IntPtr]$_ })
    if ($handles.Count -eq 0) { return @{ handle = [IntPtr]::Zero; pid = $Ownership.windowPid } }
    $visible = $handles | Where-Object { [SandboxHostNative]::IsWindowVisible($_) } | Select-Object -First 1
    if ($visible) { return @{ handle = $visible; pid = $Ownership.windowPid } }
    @{ handle = $handles[0]; pid = $Ownership.windowPid }
}

function Get-WindowDescriptor($Handle) {
    if ($null -eq $Handle -or $Handle -eq [IntPtr]::Zero) { return $null }
    [ordered]@{
        hwnd = [long]$Handle.ToInt64()
        exists = [SandboxHostNative]::IsWindow($Handle)
        visible = [SandboxHostNative]::IsWindowVisible($Handle)
        minimized = [SandboxHostNative]::IsIconic($Handle)
        bounds = [SandboxHostNative]::Bounds($Handle)
    }
}

function Wait-WindowState($Handle, [bool]$ExpectMinimized, [int]$TimeoutMs = 1500) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    do {
        if ([SandboxHostNative]::IsIconic($Handle) -eq $ExpectMinimized) { return }
        Start-Sleep -Milliseconds 100
    } while ($clock.ElapsedMilliseconds -lt $TimeoutMs)
}

function Invoke-SandboxWindowState($Requested, $Ownership, $WindowRef) {
    $before = Get-WindowDescriptor $WindowRef.handle
    $result = [ordered]@{
        requested = $Requested
        performed = $false
        action = 'none'
        reason = ''
        windowBefore = $before
        windowAfter = $null
        undoMinimize = $false
    }
    if ($Requested -eq 'Keep') {
        $result.reason = 'Keep：不改变窗口状态。'
        return [pscustomobject]$result
    }
    if (-not $Ownership.established) {
        $result.reason = '实例所有权未确认，拒绝执行窗口操作。'
        return [pscustomobject]$result
    }
    if (-not $Ownership.exclusive) {
        $result.reason = '存在其他 Sandbox 窗口进程，未证明实例独占，拒绝执行窗口操作。'
        return [pscustomobject]$result
    }
    if (-not $before) {
        $result.reason = '未找到属主进程的顶层窗口，未执行窗口操作。'
        return [pscustomobject]$result
    }
    if (-not $before.exists) {
        $result.reason = '目标窗口句柄已失效，未执行窗口操作。'
        return [pscustomobject]$result
    }
    if ($Requested -eq 'Background') {
        [void][SandboxHostNative]::ShowWindowAsync($WindowRef.handle, $swMinimize)
        Wait-WindowState $WindowRef.handle $true
        [void][SandboxHostNative]::ShowWindowAsync($WindowRef.handle, 4)
        Wait-WindowState $WindowRef.handle $false
        $result.performed = [SandboxHostNative]::SetWindowPos($WindowRef.handle, [IntPtr]1, 0, 0, 0, 0, 0x13)
        $result.action = 'Background'
        $result.windowAfter = Get-WindowDescriptor $WindowRef.handle
        [uint32]$foregroundPid = 0
        [void][SandboxHostNative]::GetWindowThreadProcessId([SandboxHostNative]::GetForegroundWindow(), [ref]$foregroundPid)
        $result.backgroundAchieved = ($foregroundPid -ne $WindowRef.pid)
        $result.reason = if ($result.backgroundAchieved) { '已置底且失焦；未激活其他宿主窗口。' } else { '已请求置底，但仍为宿主前台；此轮不能作为失焦证据。' }
        return [pscustomobject]$result
    }
    if ($Requested -eq 'Minimize') {
        if ($before.minimized) {
            $result.reason = '窗口已最小化，无需操作。'
            return [pscustomobject]$result
        }
        $result.performed = [SandboxHostNative]::ShowWindowAsync($WindowRef.handle, $swMinimize)
        $result.undoMinimize = [bool]$result.performed
    } else {
        if (-not $before.minimized) {
            $result.reason = '窗口未最小化，无需还原。'
            return [pscustomobject]$result
        }
        $result.performed = [SandboxHostNative]::ShowWindowAsync($WindowRef.handle, $swRestore)
    }
    $result.action = $Requested
    Wait-WindowState $WindowRef.handle ($Requested -eq 'Minimize')
    $result.windowAfter = Get-WindowDescriptor $WindowRef.handle
    $result.reason = if ($result.performed) { '已请求状态切换，实际状态以 windowAfter 为准。' } else { 'ShowWindowAsync 返回 false。' }
    [pscustomobject]$result
}

function Restore-ObservedMinimize($WindowOp, $WindowRef) {
    if (-not $WindowOp.performed -or $WindowOp.requested -ne 'Minimize' -or -not $WindowOp.undoMinimize) { return $null }
    $current = Get-WindowDescriptor $WindowRef.handle
    [uint32]$ownerPid = 0
    [void][SandboxHostNative]::GetWindowThreadProcessId($WindowRef.handle, [ref]$ownerPid)
    if ($ownerPid -ne $WindowRef.pid) { return $null }
    if (-not $current -or -not $current.exists -or -not $current.minimized) {
        return [ordered]@{ performed = $false; reason = '窗口已不存在或不再最小化，未再操作。'; windowAfter = $current }
    }
    $ok = [SandboxHostNative]::ShowWindowAsync($WindowRef.handle, $swRestore)
    Wait-WindowState $WindowRef.handle $false
    [ordered]@{
        performed = [bool]$ok
        reason = '采样结束，还原观测开始时未最小化的窗口。'
        windowAfter = (Get-WindowDescriptor $WindowRef.handle)
    }
}

function Get-ProcessNameSafe([int]$ProcessId) {
    if ($ProcessId -le 0) { return $null }
    try { (Get-Process -Id $ProcessId -ErrorAction Stop).ProcessName } catch { $null }
}

function Get-WorkingSetSample($Processes) {
    @($Processes | ForEach-Object {
        $entry = [ordered]@{ pid = $_.Id; name = $null; workingSetBytes = $null; alive = $false }
        try { $entry.name = $_.ProcessName } catch { }
        try {
            $_.Refresh()
            if (-not $_.HasExited) {
                $entry.alive = $true
                $entry.workingSetBytes = [long]$_.WorkingSet64
            }
        } catch { }
        [pscustomobject]$entry
    })
}

function New-HostSample($SandboxProcesses, $WindowRef) {
    $point = [SandboxHostNative+POINT]::new()
    $cursorOk = [SandboxHostNative]::GetCursorPos([ref]$point)
    $foreground = [SandboxHostNative]::GetForegroundWindow()
    [uint32]$foregroundPid = 0
    if ($foreground -ne [IntPtr]::Zero) {
        [void][SandboxHostNative]::GetWindowThreadProcessId($foreground, [ref]$foregroundPid)
    }
    [ordered]@{
        at = [DateTimeOffset]::UtcNow.ToString('o')
        cursor = [ordered]@{ available = [bool]$cursorOk; x = $point.X; y = $point.Y }
        foreground = [ordered]@{
            hwnd = [long]$foreground.ToInt64()
            pid = [int]$foregroundPid
            processName = (Get-ProcessNameSafe ([int]$foregroundPid))
            bounds = [SandboxHostNative]::Bounds($foreground)
        }
        sandboxWindow = (Get-WindowDescriptor $WindowRef.handle)
        sandboxProcesses = (Get-WorkingSetSample $SandboxProcesses)
    }
}

function Get-SampleStatistics($Samples, $DurationMs) {
    $positions = New-Object 'System.Collections.Generic.HashSet[string]'
    $foregroundPids = New-Object 'System.Collections.Generic.HashSet[int]'
    $foregroundNames = New-Object 'System.Collections.Generic.HashSet[string]'
    $workingSetByPid = @{}
    $maxStep = 0.0
    $previous = $null
    $foregroundChanges = 0
    $lastForegroundPid = $null
    $windowStateChanges = 0
    $lastWindowKey = $null
    foreach ($sample in $Samples) {
        [void]$positions.Add("$($sample.cursor.x),$($sample.cursor.y)")
        $foregroundPid = [int]$sample.foreground.pid
        if ($null -ne $lastForegroundPid -and $foregroundPid -ne $lastForegroundPid) { $foregroundChanges++ }
        $lastForegroundPid = $foregroundPid
        if ($foregroundPid -gt 0) {
            [void]$foregroundPids.Add($foregroundPid)
            if ($sample.foreground.processName) { [void]$foregroundNames.Add([string]$sample.foreground.processName) }
        }
        if ($previous) {
            $dx = [double]$sample.cursor.x - [double]$previous.cursor.x
            $dy = [double]$sample.cursor.y - [double]$previous.cursor.y
            $step = [Math]::Sqrt(($dx * $dx) + ($dy * $dy))
            if ($step -gt $maxStep) { $maxStep = $step }
        }
        $previous = $sample
        $window = $sample.sandboxWindow
        $windowKey = if ($window) { "exists=$($window.exists);visible=$($window.visible);minimized=$($window.minimized)" } else { 'none' }
        if ($null -ne $lastWindowKey -and $windowKey -ne $lastWindowKey) { $windowStateChanges++ }
        $lastWindowKey = $windowKey
        foreach ($process in $sample.sandboxProcesses) {
            if ($null -eq $process.workingSetBytes) { continue }
            $key = [string]$process.pid
            if (-not $workingSetByPid.ContainsKey($key)) {
                $workingSetByPid[$key] = [ordered]@{
                    pid = $process.pid
                    name = $process.name
                    samples = 0
                    minBytes = [long]::MaxValue
                    maxBytes = [long]::MinValue
                    lastBytes = [long]$process.workingSetBytes
                }
            }
            $entry = $workingSetByPid[$key]
            $bytes = [long]$process.workingSetBytes
            $entry.samples = $entry.samples + 1
            if ($bytes -lt $entry.minBytes) { $entry.minBytes = $bytes }
            if ($bytes -gt $entry.maxBytes) { $entry.maxBytes = $bytes }
            $entry.lastBytes = $bytes
        }
    }
    $workingSetStats = @($workingSetByPid.Values | Sort-Object pid)
    [ordered]@{
        samples = $Samples.Count
        durationMs = $DurationMs
        cursor = [ordered]@{
            distinctPositions = $positions.Count
            moved = ($positions.Count -gt 1)
            maxStepPixels = [Math]::Round($maxStep, 1)
        }
        foreground = [ordered]@{
            transitions = $foregroundChanges
            distinctPids = $foregroundPids.Count
            distinctProcessNames = @($foregroundNames | Sort-Object)
        }
        sandboxWindow = [ordered]@{ stateTransitions = $windowStateChanges }
        sandboxWorkingSet = $workingSetStats
    }
}

function Write-Observation($Path, $Value) {
    $temp = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temp, $Path)
}

try {
    $state = Read-PocState
    $sessionRoot = Split-Path -Parent ([string]$state.outputs)
    $pocStartedAt = Format-IsoUtc $state.startedAt
    $ownership = Test-SandboxOwnership $state
    $windowRef = Resolve-SandboxWindow $ownership
    $windowOp = Invoke-SandboxWindowState $WindowState $ownership $windowRef

    $sandboxProcesses = @($ownership.trackedPids |
        ForEach-Object { Get-Process -Id ([int]$_) -ErrorAction SilentlyContinue })

    $clock = [Diagnostics.Stopwatch]::StartNew()
    $samples = New-Object 'System.Collections.Generic.List[object]'
    try {
        do {
            $samples.Add((New-HostSample $sandboxProcesses $windowRef))
            if ($clock.Elapsed.TotalMilliseconds -lt ($Seconds * 1000)) {
                Start-Sleep -Milliseconds $sampleIntervalMs
            }
        } while ($clock.Elapsed.TotalMilliseconds -lt ($Seconds * 1000))
    } finally {
        $restoreResult = Restore-ObservedMinimize $windowOp $windowRef
    }
    $ownershipAtEnd = Test-SandboxOwnership $state
    $observation = [ordered]@{
        schemaVersion = 1
        kind = 'sandbox-host-observation'
        instanceId = [string]$state.instanceId
        sessionRoot = $sessionRoot
        observedAt = [DateTimeOffset]::UtcNow.ToString('o')
        host = [ordered]@{ machine = $env:COMPUTERNAME }
        poc = [ordered]@{
            sandboxPid = [int]$state.sandboxPid
            sandboxPidAlive = $ownership.launcherAlive
            startedAt = $pocStartedAt
        }
        ownership = $ownership
        ownershipAtEnd = [ordered]@{
            established = $ownershipAtEnd.established
            exclusive = $ownershipAtEnd.exclusive
            reason = $ownershipAtEnd.reason
        }
        windowOperation = $windowOp
        windowRestoreAtEnd = $restoreResult
        sampling = [ordered]@{
            requestedSeconds = $Seconds
            intervalMs = $sampleIntervalMs
            samples = $samples
        }
        statistics = (Get-SampleStatistics $samples $clock.ElapsedMilliseconds)
        caveats = @(
            '仅读取宿主侧指针/前台窗口/窗口状态/工作集；未点击、未 SendInput、未 SetForegroundWindow、未读取窗口标题或键盘文本。'
            '光标变化可能来自用户本人操作，不能据此判定隔离失败；状态不变也不构成输入完全隔离的证明。'
            '宿主 GetForegroundWindow 只反映宿主前台窗口，不能说明 guest 内部焦点。'
            '外层 Sandbox 客户端最小化不等于 guest 内被测应用最小化。'
            '不读取 guest 心跳或任务结果文件，也不据此推断任务状态；实例识别只依据 current.json 与宿主进程身份/启动时间。'
        )
        interpretation = 'informational-only'
    }
    $timestamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
    $outputPath = Join-Path $sessionRoot "host-observations-$timestamp.json"
    Write-Observation $outputPath $observation
    Write-Output $outputPath
    Write-Output ("观测完成：{0} 个样本；所有权={1}；窗口操作={2}。" -f $samples.Count, $ownership.established, $windowOp.action)
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
