$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot '../../TestData/ChildSessionTarget'
dotnet build (Join-Path $PSScriptRoot 'ChildSessionTarget.csproj') -c Debug -o $output
exit $LASTEXITCODE
