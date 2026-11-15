$ErrorActionPreference = 'Stop'
if (Get-Process WindowsSandbox, WindowsSandboxClient, WindowsSandboxRemoteSession -ErrorAction SilentlyContinue) {
    throw '请先停止 Sandbox 再发布，避免覆盖沙盒正在使用的只读二进制。'
}
$output = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\TestData\SandboxSpike\bin'))
dotnet publish (Join-Path $PSScriptRoot 'SandboxProbe\SandboxProbe.csproj') -c Release -r win-x64 --self-contained true -o $output
if ($LASTEXITCODE -ne 0) { throw "SandboxProbe publish 失败：$LASTEXITCODE" }
