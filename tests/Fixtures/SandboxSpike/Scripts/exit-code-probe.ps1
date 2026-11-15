$ErrorActionPreference = 'Stop'
foreach ($holdHandle in @($false, $true)) {
    $prefix = "C:\SandboxSpike\Outputs\exit-code-$holdHandle"
    $process = Start-Process cmd.exe -ArgumentList '/c exit 7' -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput "$prefix.out" -RedirectStandardError "$prefix.err"
    if ($holdHandle) { $null = $process.Handle }
    $null = $process.WaitForExit(5000)
    [pscustomobject]@{ holdHandle = $holdHandle; hasExited = $process.HasExited; exitCode = $process.ExitCode } | ConvertTo-Json -Compress
    $process.Dispose()
}
