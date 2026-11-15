using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildInstalledToolTests
{
    [Fact]
    public async Task 安装后的两种宿主形式可探测且内部角色不会误入MCP()
    {
        var manifest = Environment.GetEnvironmentVariable("DOTNETDEBUGGERMCP_CHILD_INSTALLED_MANIFEST");
        if (string.IsNullOrEmpty(manifest))
        {
            Assert.Skip("须提供本轮本地安装清单DOTNETDEBUGGERMCP_CHILD_INSTALLED_MANIFEST；不使用开发bin代替安装产物。");
            return;
        }
        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken));
        foreach (var viaDll in new[] { false, true })
        {
            var probe = await RunAsync(file.RootElement, viaDll, ["child", "call", "child_session", "--json", "{\"action\":\"probe\"}"]);
            Assert.Equal(0, probe.ExitCode);
            using var json = JsonDocument.Parse(probe.Output);
            Assert.Equal("completed", json.RootElement.GetProperty("status").GetString());
            Assert.Contains("只读", json.RootElement.GetProperty("message").GetString());
            var role = await RunAsync(file.RootElement, viaDll, ["--child-internal", "invalid-role", Path.GetDirectoryName(manifest)!, "invalid"]);
            Assert.Equal(1, role.ExitCode);
            Assert.Empty(role.Output);
            Assert.Contains("未知内部角色", role.Error);
        }
    }

    [Fact]
    public async Task 安装后的工具可建立并复用内部执行角色()
    {
        if (Environment.GetEnvironmentVariable("DOTNETDEBUGGERMCP_CHILD_INTEGRATION") != "1")
        {
            Assert.Skip("真实安装角色验收须显式授权DOTNETDEBUGGERMCP_CHILD_INTEGRATION=1；默认测试不启用系统。");
            return;
        }
        var manifest = Environment.GetEnvironmentVariable("DOTNETDEBUGGERMCP_CHILD_INSTALLED_MANIFEST");
        Assert.False(string.IsNullOrEmpty(manifest), "须提供本轮安装清单。");
        using var file = JsonDocument.Parse(await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken));
        var run = file.RootElement.GetProperty("runDirectory").GetString()!;
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in new[] { "run", "--no-build", "-c", "Release", "--project",
            Path.Combine(TestDataPaths.RepositoryRoot, "src", "DotNetDebuggerMcp.Client", "DotNetDebuggerMcp.Client.csproj"),
            "--", "--child", file.RootElement.GetProperty("tool").GetString()!, file.RootElement.GetProperty("dll").GetString()!,
            Path.Combine(run, "cross-" + Guid.NewGuid().ToString("N")) })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        var stdout = await output;
        var stderr = await error;
        await File.WriteAllTextAsync(Path.Combine(run, "client-acceptance.log"), stdout + "\n" + stderr, TestContext.Current.CancellationToken);
        Assert.True(process.ExitCode == 0, $"真实安装验收失败(exit={process.ExitCode})，详见{run}/client-acceptance.log：{stderr}");
        Assert.Contains("[PASS] 三轮", stdout);
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(JsonElement manifest, bool viaDll, string[] args)
    {
        var info = new ProcessStartInfo(viaDll ? "dotnet" : manifest.GetProperty("tool").GetString()!)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (viaDll) info.ArgumentList.Add(manifest.GetProperty("dll").GetString()!);
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        return (process.ExitCode, await output, await error);
    }
}
