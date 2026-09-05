using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.UpdateCheck;

using System.Text.Json;

using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// 串行化使用 <see cref="AppServices"/> 静态状态的测试类（CheckToolTests / ToolPipelineTests）， 避免跨类并行执行时相互覆盖注入的
/// fake 造成竞态。
/// </summary>
[CollectionDefinition("AppServices", DisableParallelization = true)]
public sealed class AppServicesTestCollection;

[Collection("AppServices")]
public class CheckToolTests
{
    [Fact]
    public async Task 无缓存记录_返回空报告()
    {
        await RunWithAsync(
            cachedLatest: null,
            async text =>
            {
                Assert.Equal("", text); // 无有效检查记录报告为空，握手不注入
            });
    }

    [Fact]
    public async Task NuGet有新版本_报告升级提示()
    {
        await RunWithAsync(
            cachedLatest: "2.0.0",
            async text =>
            {
                Assert.Contains("dotnet-debugger-mcp: 当前", text);
                Assert.Contains("NuGet 最新 2.0.0", text);
                Assert.Contains("dotnet tool update --global dotnet-debugger-mcp", text);
            });
    }

    [Fact]
    public async Task NuGet已是最新_报告已是最新()
    {
        await RunWithAsync(
            cachedLatest: "1.1.0",
            async text =>
            {
                Assert.Contains("已是最新版本", text);
                Assert.DoesNotContain("可执行 `dotnet tool update", text);
            });
    }

    [Fact]
    public async Task 会话缓存_第二次调用复用同一状态()
    {
        AppServices.ConfigureForTest();
        try
        {
            var first = AppServices.StatusReport.Value;
            var second = AppServices.StatusReport.Value;

            Assert.Same(first, second); // StatusReport 会话内缓存，仅首次真实组装
            Assert.Null(await first); // ConfigureForTest 默认 Updater 无缓存，状态为 null
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public void 握手注入_有新版本_带主动告知指令()
    {
        var status = new UpdateChecker.NuGetUpdateStatus(HasNewVersion: true, Line: "dotnet-debugger-mcp: 当前 1.0.0，NuGet 最新 2.0.0。可执行 `dotnet tool update --global dotnet-debugger-mcp` 升级。");

        var text = EnvironmentChecker.BuildHandshakeText(status);

        Assert.StartsWith("## 更新状态", text);
        Assert.Contains("主动告知用户", text);
        Assert.Contains("NuGet 最新 2.0.0", text);
        Assert.Contains("dotnet tool update --global dotnet-debugger-mcp", text);
    }

    [Fact]
    public void 握手注入_已是最新_仅状态行不带指令()
    {
        var status = new UpdateChecker.NuGetUpdateStatus(HasNewVersion: false, Line: "dotnet-debugger-mcp: 当前 2.0.0，已是最新版本。");

        var text = EnvironmentChecker.BuildHandshakeText(status);

        Assert.StartsWith("## 更新状态", text);
        Assert.DoesNotContain("主动告知用户", text);
        Assert.Contains(status.Line, text);
    }

    [Fact]
    public void 握手注入_无检查记录_返回空串()
    {
        var text = EnvironmentChecker.BuildHandshakeText(null);

        Assert.Equal("", text);
    }

    /// <summary>
    /// 将 Updater 指向预写缓存（或空目录）的临时目录，验证更新检查（CLI -c/握手注入）报告组装。 报告经 <see
    /// cref="UpdateChecker.GetCachedNuGetLine"/> 同步读缓存，故不注入网络 handler。
    /// </summary>
    private static async Task RunWithAsync(string? cachedLatest, Func<string, Task> assert)
    {
        AppServices.ConfigureForTest();
        var cacheDir = TempDir();
        if (cachedLatest is not null) WriteCacheFile(cacheDir, cachedLatest);
        AppServices.Updater = new UpdateChecker(cacheDir);
        try
        {
            var text = await CheckTool.CheckStatus();
            await assert(text);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    private static void WriteCacheFile(string cacheDir, string latest)
    {
        Directory.CreateDirectory(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, AppConfig.UpdateCheckCacheFileName),
            JsonSerializer.Serialize(new UpdateChecker.UpdateCheckCache
            {
                LastAttemptAt = DateTimeOffset.Now,
                LastSuccessAt = DateTimeOffset.Now,
                Latest = latest,
            }));
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "ilspymcp-tests", Guid.NewGuid().ToString("N"));
}