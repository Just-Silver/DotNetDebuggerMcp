using ILSpyMcp.Configuration;
using ILSpyMcp.Services;
using ILSpyMcp.UpdateCheck;

using System.Text.Json;

using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// 串行化使用 <see cref="AppServices"/> 静态状态的测试类（CheckToolTests / ToolPreflightTests）， 避免跨类并行执行时相互覆盖注入的
/// fake 造成竞态。
/// </summary>
[CollectionDefinition("AppServices", DisableParallelization = true)]
public sealed class AppServicesTestCollection;

[Collection("AppServices")]
public class CheckToolTests
{
    [Fact]
    public async Task ilspycmd未安装_报告存在缺口与安装提示()
    {
        await RunWithAsync(
            new FakeProcessRunner { Code = 1 },
            cachedLatest: null,
            async text =>
            {
                Assert.Contains("环境状态: 存在缺口", text);
                Assert.Contains("ilspycmd: 未安装", text);
                Assert.Contains("dotnet tool install --global ilspycmd", text);
            });
    }

    [Fact]
    public async Task 版本低于要求_报告不可用与升级提示()
    {
        await RunWithAsync(
            new FakeProcessRunner { Code = 0, Stdout = "ilspycmd: 10.0.0\n" },
            cachedLatest: null,
            async text =>
            {
                Assert.Contains("环境状态: 存在缺口", text);
                Assert.Contains("10.0.0 < 11.0", text);
                Assert.Contains("dotnet tool update --global ilspycmd", text);
            });
    }

    [Fact]
    public async Task 版本满足要求_报告就绪与可用()
    {
        await RunWithAsync(
            new FakeProcessRunner { Code = 0, Stdout = "ilspycmd: 11.0.0.9335\n" },
            cachedLatest: null,
            async text =>
            {
                Assert.Contains("环境状态: 就绪", text);
                Assert.Contains("11.0.0.9335 >= 11.0", text);
                Assert.Contains("成员反编译（-m）: 可用", text);
            });
    }

    [Fact]
    public async Task 无缓存_NuGet段留白()
    {
        await RunWithAsync(
            new FakeProcessRunner { Code = 0, Stdout = "ilspycmd: 11.0.0.9335\n" },
            cachedLatest: null,
            async text =>
            {
                Assert.DoesNotContain("ilspymcp", text);
                Assert.Contains("环境状态: 就绪", text);
            });
    }

    [Fact]
    public async Task NuGet有新版本_报告升级提示()
    {
        await RunWithAsync(
            new FakeProcessRunner { Code = 0, Stdout = "ilspycmd: 11.0.0.9335\n" },
            cachedLatest: "2.0.0",
            async text =>
            {
                Assert.Contains("ilspymcp: 当前", text);
                Assert.Contains("NuGet 最新 2.0.0", text);
                Assert.Contains("dotnet tool update --global ilspymcp", text);
            });
    }

    [Fact]
    public async Task NuGet已是最新_报告已是最新()
    {
        await RunWithAsync(
            new FakeProcessRunner { Code = 0, Stdout = "ilspycmd: 11.0.0.9335\n" },
            cachedLatest: "1.1.0",
            async text =>
            {
                Assert.Contains("已是最新版本", text);
                Assert.DoesNotContain("可执行 `dotnet tool update", text);
            });
    }

    [Fact]
    public async Task 会话缓存_第二次调用不再执行检查()
    {
        var fake = new FakeProcessRunner { Code = 0, Stdout = "ilspycmd: 11.0.0.9335\n" };
        AppServices.ConfigureForTest(fake);
        var cacheDir = TempDir();
        WriteCacheFile(cacheDir, "1.1.0");
        AppServices.Updater = new UpdateChecker(cacheDir);
        try
        {
            await CheckTool.CheckStatus();
            var second = await CheckTool.CheckStatus();

            Assert.Equal(1, fake.CallCount);
            Assert.Contains("环境状态: 就绪", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    /// <summary>
    /// 注入 fake 进程执行器，并将 Updater 指向预写缓存（或空目录）的临时目录，验证环境自检（CLI -c/握手注入）报告组装。 NuGet 段经 <see
    /// cref="UpdateChecker.GetCachedNuGetLine"/> 同步读缓存，故不注入网络 handler。
    /// </summary>
    private static async Task RunWithAsync(FakeProcessRunner fake, string? cachedLatest, Func<string, Task> assert)
    {
        AppServices.ConfigureForTest(fake);
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