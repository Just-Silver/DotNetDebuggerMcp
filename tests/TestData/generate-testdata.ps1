# Generates ILSpyMcp.TestSamples.dll (the tests/TestData test assembly). Both the
# dll and this script are tracked in the repo so the fixture can be regenerated.
# Coverage:
#   - 600 classes (Class0001..Class0600) + BigClass = 601 total
#     -> list_types triggers both the 200-line default truncation and the
#        500-line per-call cap
#   - BigClass.BigMethod with 600 statements
#     -> decompile emits 600+ lines, exercising truncation + line slicing
# Usage: powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1
param(
    [string]$OutDir = $PSScriptRoot,
    [string]$Tfm = "net10.0"
)

$ErrorActionPreference = 'Stop'
$tmp = Join-Path $env:TEMP ("ilspymcp-testdata-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp | Out-Null

try {
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine("using System;")
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("namespace ILSpyMcp.Samples;")
    [void]$sb.AppendLine("")

    for ($i = 1; $i -le 600; $i++) {
        $name = "Class{0:D4}" -f $i
        [void]$sb.AppendLine("public class $name { public void M() { } }")
    }

    [void]$sb.AppendLine("public class BigClass")
    [void]$sb.AppendLine("{")
    [void]$sb.AppendLine("    public static void BigMethod(int seed)")
    [void]$sb.AppendLine("    {")
    [void]$sb.AppendLine("        int[] v = new int[600];")
    [void]$sb.AppendLine("        v[0] = seed;")
    for ($i = 1; $i -lt 600; $i++) {
        [void]$sb.AppendLine("        v[$i] = v[$($i - 1)] + 1;")
    }
    [void]$sb.AppendLine("        Console.WriteLine(v[599]);")
    [void]$sb.AppendLine("    }")
    [void]$sb.AppendLine("    public static void BigHelper() { }")
    [void]$sb.AppendLine("    public static void BigHelper2() { }")
    [void]$sb.AppendLine("}")

    Set-Content -Path (Join-Path $tmp "Samples.cs") -Value $sb.ToString() -Encoding UTF8

    $csproj = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>$Tfm</TargetFramework>
    <OutputType>Library</OutputType>
    <AssemblyName>ILSpyMcp.TestSamples</AssemblyName>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="Samples.cs" />
  </ItemGroup>
</Project>
"@
    Set-Content -Path (Join-Path $tmp "TestSamples.csproj") -Value $csproj -Encoding UTF8

    dotnet build (Join-Path $tmp "TestSamples.csproj") -c Release --nologo -v q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed, exit code $LASTEXITCODE" }

    $dll = Join-Path $tmp "bin\Release\$Tfm\ILSpyMcp.TestSamples.dll"
    $target = Join-Path $OutDir "ILSpyMcp.TestSamples.dll"
    Copy-Item $dll $target -Force
    $len = (Get-Item $target).Length
    Write-Host "Generated $target ($len bytes)"
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}
