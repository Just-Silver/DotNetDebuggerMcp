$ErrorActionPreference = 'Stop'
$sentinel = 'C:\SandboxSpike\persistent.txt'
$previous = if (Test-Path $sentinel) { [IO.File]::ReadAllText($sentinel) } else { '' }
[IO.File]::WriteAllText($sentinel, "instance=$env:SANDBOX_SPIKE_INSTANCE; pid=$PID")
[pscustomobject]@{
    instanceId = $env:SANDBOX_SPIKE_INSTANCE
    machine = $env:COMPUTERNAME
    user = $env:USERNAME
    powershell = $PSVersionTable.PSVersion.ToString()
    processId = $PID
    sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    previousCommandState = $previous
    dotnetAvailable = [bool](Get-Command dotnet -ErrorAction SilentlyContinue)
    networkAdapters = @(Get-NetAdapter | Select-Object Name, Status)
} | ConvertTo-Json -Depth 4
