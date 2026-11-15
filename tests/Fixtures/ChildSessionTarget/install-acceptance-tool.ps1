param([string]$RunDirectory = "")
$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))
if (!$RunDirectory) {
    $RunDirectory = Join-Path $root ("tests/TestData/ChildSessionAcceptance/" + (Get-Date -Format "yyyyMMdd-HHmmss"))
}
$run = [IO.Path]::GetFullPath($RunDirectory)
$project = Join-Path $root "src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj"
[xml]$xml = Get-Content $project
$version = $xml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$packages = Join-Path $run "packages"
$tool = Join-Path $run "tool"
New-Item -ItemType Directory -Force $run, $packages | Out-Null
$config = Join-Path $run "NuGet.Config"
$source = [Security.SecurityElement]::Escape($packages)
Set-Content $config "<configuration><packageSources><clear/><add key=`"acceptance`" value=`"$source`"/></packageSources></configuration>"
dotnet pack $project -c Release -o $packages
if ($LASTEXITCODE) { throw "pack失败：$LASTEXITCODE" }
$oldCache = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = Join-Path $run "nuget-cache"
    dotnet tool install DotNetDebuggerMcp --tool-path $tool --configfile $config --version $version --no-cache
    if ($LASTEXITCODE) { throw "tool install失败：$LASTEXITCODE" }
} finally { $env:NUGET_PACKAGES = $oldCache }
$dll = Get-ChildItem (Join-Path $tool ".store") -Recurse -Filter DotNetDebuggerMcp.dll |
    Where-Object { $_.Directory.Name -eq "any" } | Select-Object -First 1
if (!$dll) { throw "安装目录未找到宿主dll。" }
$installed = @{
    runDirectory = $run
    version = "$version"
    tool = Join-Path $tool "DotNetDebuggerMcp.exe"
    dll = $dll.FullName
    sha256 = (Get-FileHash $dll.FullName -Algorithm SHA256).Hash
}
$installed | ConvertTo-Json | Set-Content (Join-Path $run "installed.json")
$installed | ConvertTo-Json
