using DotNetDebuggerMcp.Services;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// V1 VerifyBuildRunner：对临时 console 工程（真实 dotnet build）验证——成功路径 Ok+ExitCode0+「编译成功」、
/// 坏工程 → 失败摘要含编译错误、GetTargetPathAsync 返回 .exe/.dll 结尾存在的绝对路径、工程不存在中文提示；
/// 纯函数 ArtifactFileMatches / ResolveLaunchExecutable / TailText。真实 build 每测试一次（秒级），不挂 AppServices。
/// </summary>
public sealed class VerifyBuildRunnerTests
{
    [Fact]
    public async Task RunAsync_GoodProject_OkTrueExitZeroSummaryCompileSuccess()
    {
        var dir = CreateConsoleProject("RunOk", "System.Console.WriteLine(\"[RunOk] hi\");");
        try
        {
            var outcome = await VerifyBuildRunner.RunAsync(Path.Combine(dir, "RunOk.csproj"), "Debug", 120, TestContext.Current.CancellationToken);
            Assert.True(outcome.Ok, outcome.Summary);
            Assert.Equal(0, outcome.ExitCode);
            Assert.Contains("编译成功", outcome.Summary);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task RunAsync_BrokenSource_FailWithErrorTailAndNoOk()
    {
        var dir = CreateConsoleProject("RunFail", "class P { void M() { int x = ; } }");
        try
        {
            var outcome = await VerifyBuildRunner.RunAsync(Path.Combine(dir, "RunFail.csproj"), "Debug", 120, TestContext.Current.CancellationToken);
            Assert.False(outcome.Ok);
            Assert.NotEqual(0, outcome.ExitCode);
            Assert.Contains("编译失败", outcome.Summary);
            Assert.Contains("CS", outcome.Summary);   // 编译错误行（error CSxxxx）进摘要尾部
            Assert.Contains("RunFail", outcome.Summary);
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task RunAsync_MissingProject_ChineseProjectMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid() + ".csproj");
        var outcome = await VerifyBuildRunner.RunAsync(missing, "Debug", 30, TestContext.Current.CancellationToken);
        Assert.False(outcome.Ok);
        Assert.Contains("工程文件不存在", outcome.Summary);
    }

    [Fact]
    public async Task GetTargetPathAsync_AfterBuild_ReturnsExistingExeOrDllAbsolute()
    {
        var dir = CreateConsoleProject("PathSmoke", "System.Console.WriteLine(\"[PathSmoke] hi\");");
        try
        {
            var build = await VerifyBuildRunner.RunAsync(Path.Combine(dir, "PathSmoke.csproj"), "Debug", 120, TestContext.Current.CancellationToken);
            Assert.True(build.Ok, build.Summary);

            var target = await VerifyBuildRunner.GetTargetPathAsync(Path.Combine(dir, "PathSmoke.csproj"), "Debug", TestContext.Current.CancellationToken);
            Assert.NotNull(target);
            Assert.True(Path.IsPathRooted(target), $"TargetPath 应绝对路径：{target}");
            Assert.True(target!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || target.EndsWith(".dll", StringComparison.OrdinalIgnoreCase), $"TargetPath 应 .exe/.dll 结尾：{target}");
            Assert.True(File.Exists(target), $"TargetPath 产物应存在：{target}");
            // console 工程：产物 dll 存在 + 可启动的 apphost exe 同目录
            var launch = VerifyBuildRunner.ResolveLaunchExecutable(target);
            Assert.True(File.Exists(launch));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public async Task GetTargetPathAsync_MissingProject_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid() + ".csproj");
        var target = await VerifyBuildRunner.GetTargetPathAsync(missing, "Debug", TestContext.Current.CancellationToken);
        Assert.Null(target);
    }

    [Fact]
    public void ArtifactFileMatches_StemIgnoreCaseAndExtensionAndQuotes()
    {
        // commandLine 首段可带相对/绝对前缀、写 .exe/.dll、产物是 .dll——比文件名主干忽略大小写
        Assert.True(VerifyBuildRunner.ArtifactFileMatches("SmokeApp.exe", @"C:\out\bin\Debug\net10.0\SmokeApp.dll"));
        Assert.True(VerifyBuildRunner.ArtifactFileMatches(@"bin\Debug\net10.0\smokeapp.dll", @"C:\out\bin\Debug\net10.0\SmokeApp.exe"));
        Assert.True(VerifyBuildRunner.ArtifactFileMatches("\"C:\\My Dir\\SmokeApp.exe\"", @"C:\out\SmokeApp.dll"));
        Assert.False(VerifyBuildRunner.ArtifactFileMatches("WrongName.exe", @"C:\out\SmokeApp.dll"));
        Assert.False(VerifyBuildRunner.ArtifactFileMatches("", @"C:\out\SmokeApp.dll"));
    }

    [Fact]
    public void ResolveLaunchExecutable_DllWithApphostExe_UsesExe()
    {
        var dir = Path.Combine(Path.GetTempPath(), "verify-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dll = Path.Combine(dir, "App.dll");
            var exe = Path.Combine(dir, "App.exe");
            File.WriteAllText(dll, "");
            File.WriteAllText(exe, "");

            Assert.Equal(exe, VerifyBuildRunner.ResolveLaunchExecutable(dll));
            // 无 apphost 兄弟（纯库）：原样返回 dll
            File.Delete(exe);
            Assert.Equal(dll, VerifyBuildRunner.ResolveLaunchExecutable(dll));
            // 已是 exe（TargetPath 即 exe 的项目形态）：原样返回
            Assert.Equal(exe, VerifyBuildRunner.ResolveLaunchExecutable(exe));
        }
        finally { TryDelete(dir); }
    }

    [Fact]
    public void TailText_TakesLastLinesOfBothStreams()
    {
        var stdout = "a\nb\nc\nd\ne";
        var stderr = "x\ny";
        var tail = VerifyBuildRunner.TailText(stdout, stderr);
        // 末尾（各流尾部，stderr 在后）应含最新行
        Assert.Contains("e", tail);
        Assert.Contains("y", tail);
        // 不会带整段超长：前面 b/c 可能被截（stdout 5 行 < 40 全保留——此处验证拼接正确）
        Assert.StartsWith(Environment.NewLine, tail);
    }

    /// <summary>建临时 console 工程（csproj + Program.cs）；返回工程目录。</summary>
    private static string CreateConsoleProject(string name, string source)
    {
        var dir = Path.Combine(Path.GetTempPath(), "verify-build-tests", name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>{name}</AssemblyName>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), source);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* 残留系统临时目录 */ }
    }
}
