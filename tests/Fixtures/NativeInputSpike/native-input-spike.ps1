param(
    [ValidateSet('Build','Probe','Enable','Disable','Start','Launch','Request','Observe','Exercise','Stop')]
    [string]$Action = 'Probe',
    [string]$Instance = '',
    [string]$Operation = 'status',
    [ValidateRange(1,600)][int]$Seconds = 60
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../..')).Path
$data = Join-Path $repo 'tests/TestData/NativeInputSpike'
$exe = Join-Path $data 'build2/bin/NativeInputSpike/debug/NativeInputSpike.exe'
$currentFile = Join-Path $data 'current-child.json'

function Invoke-Worker([string]$Name) {
    $text = & $exe request $Instance $Name
    if ($LASTEXITCODE -ne 0) { throw "worker 请求失败：$Name" }
    $value = $text | ConvertFrom-Json
    if ($value.error) { throw "${Name}：$($value.error)" }
    return $value
}

function Start-Spike {
    $probe = & $exe probe | ConvertFrom-Json
    if (-not $probe.enabled -or $probe.hasChild) { throw '功能未启用或已有 child；不自动启用或抢占。' }
    $root = Join-Path $data ('child-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $root | Out-Null
    $p = Start-Process -FilePath $exe -ArgumentList @('connect', ('"' + $root + '"')) -PassThru `
        -RedirectStandardOutput (Join-Path $root 'holder.stdout.log') -RedirectStandardError (Join-Path $root 'holder.stderr.log')
    $value = @{root=$root;pid=$p.Id;startedTicks=$p.StartTime.ToUniversalTime().Ticks}
    $value | ConvertTo-Json | Set-Content $currentFile
    $value | ConvertTo-Json
}

function Invoke-Exercise {
    $runRoot = Join-Path $Instance ('run-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runRoot | Out-Null
    $observer = Start-Process -FilePath $exe -ArgumentList @('observe', ('"'+$runRoot+'"'), ($Seconds+5)) -PassThru `
        -RedirectStandardOutput (Join-Path $runRoot 'observer.stdout.log') -RedirectStandardError (Join-Path $runRoot 'observer.stderr.log')
    $rounds = @()
    $until = [DateTime]::UtcNow.AddSeconds($Seconds)
    $failure = $null
    try {
        while ([DateTime]::UtcNow -lt $until) {
            $before = Invoke-Worker 'status'
            $raw = Invoke-Worker 'real'
            $commit = Invoke-Worker 'commit'
            $unicode = Invoke-Worker 'unicode'
            $shortcut = Invoke-Worker 'shortcut'
            $capture = Invoke-Worker 'capture'
            $passed = $commit.strictClicks -gt $before.strictClicks -and $commit.drags -gt $before.drags -and `
                $commit.shortcuts -gt $before.shortcuts -and $shortcut.functionShortcuts -gt $before.functionShortcuts -and `
                $commit.text -eq 'realinput' -and $unicode.text -eq 'realinput隔离Input' -and -not $capture.WasAllBlack
            $rounds += @{passed=$passed;before=$before;raw=$raw;commit=$commit;unicode=$unicode;shortcut=$shortcut;capture=$capture}
            if (-not $passed) { break }
        }
    } catch { $failure = $_.Exception.Message }
    finally {
        if (-not $observer.WaitForExit(($Seconds+10)*1000)) { $observer.Kill(); $observer.WaitForExit() }
        ConvertTo-Json -InputObject $rounds -Depth 12 | Set-Content (Join-Path $runRoot 'exercise-report.json')
    }
    $observationPath = Join-Path $runRoot 'parent-observations.json'
    $observed = if (Test-Path $observationPath) { Get-Content $observationPath -Raw | ConvertFrom-Json } else { $null }
    $inputObserved = $observer.ExitCode -eq 0 -and $null -ne $observed -and `
        $observed.markedKeyboard -eq 0 -and $observed.markedMouse -eq 0
    $all = -not $failure -and $rounds.Count -gt 0 -and @($rounds|Where-Object {-not $_.passed}).Count -eq 0 -and $inputObserved
    $summary = @{rounds=$rounds.Count;allMachineChecksPassed=$all;parentMarkedInputsAbsent=$inputObserved;observerExitCode=$observer.ExitCode;error=$failure;humanAssessment='单独记录';reportDirectory=$runRoot}
    $summary | ConvertTo-Json | Set-Content (Join-Path $runRoot 'exercise-summary.json')
    $summary | ConvertTo-Json
    if (-not $all) { throw '本轮未通过全部机器检查；见保留的报告，不能视为完整人工验收。' }
}

if ($Action -eq 'Build') {
    dotnet build (Join-Path $PSScriptRoot 'NativeInputSpike.csproj') -c Debug --artifacts-path (Join-Path $data 'build2')
    exit $LASTEXITCODE
}
if (-not (Test-Path $exe)) { throw '先执行 -Action Build。' }
if ($Action -eq 'Probe') { & $exe probe; exit $LASTEXITCODE }
if ($Action -in 'Enable','Disable') {
    $p = Start-Process -FilePath $exe -ArgumentList $Action.ToLowerInvariant() -Verb RunAs -PassThru -Wait
    if ($p.ExitCode -ne 0) { throw '管理员操作失败或取消。' }
    & $exe probe
    exit $LASTEXITCODE
}
if ($Action -eq 'Start') { Start-Spike; exit 0 }
if (-not $Instance) { $Instance = (Get-Content $currentFile -Raw | ConvertFrom-Json).root }
switch ($Action) {
    'Launch' { & $exe launch-worker $Instance; exit $LASTEXITCODE }
    'Request' { Invoke-Worker $Operation | ConvertTo-Json -Depth 10 }
    'Observe' { & $exe observe $Instance $Seconds; exit $LASTEXITCODE }
    'Exercise' { Invoke-Exercise }
    'Stop' { & $exe stop-child $Instance; exit $LASTEXITCODE }
}
