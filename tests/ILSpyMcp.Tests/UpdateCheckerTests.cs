using ILSpyMcp.Infrastructure;

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

using Xunit;

namespace ILSpyMcp.Tests;

[Collection("AppServices")]
public class UpdateCheckerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task 首次刷新_命中网络并写缓存()
    {
        var tempDir = TempDir();
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[\"99.0.0\"]}") };
        AppServices.NuGet = new NuGetClient(handler);
        try
        {
            var checker = new UpdateChecker(tempDir);

            var latest = await checker.RefreshIfStaleAsync();

            Assert.Equal("99.0.0", latest);
            Assert.Equal(1, handler.CallCount);
            Assert.True(File.Exists(Path.Combine(tempDir, AppConfig.UpdateCheckCacheFileName)));
            Assert.Equal("99.0.0", ReadCacheLatest(tempDir));
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task TTL内_不再打网络()
    {
        var tempDir = TempDir();
        var fixedTime = T0;
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[\"99.0.0\"]}") };
        AppServices.NuGet = new NuGetClient(handler);
        try
        {
            var checker = new UpdateChecker(tempDir, () => fixedTime);

            var first = await checker.RefreshIfStaleAsync();
            var second = await checker.RefreshIfStaleAsync();

            Assert.Equal("99.0.0", first);
            Assert.Equal("99.0.0", second);
            Assert.Equal(1, handler.CallCount);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task TTL过期_重新打网络()
    {
        var tempDir = TempDir();
        var fixedTime = T0;
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[\"99.0.0\"]}") };
        AppServices.NuGet = new NuGetClient(handler);
        try
        {
            var checker = new UpdateChecker(tempDir, () => fixedTime);

            await checker.RefreshIfStaleAsync();
            Assert.Equal(1, handler.CallCount);

            fixedTime = fixedTime.AddHours(25);
            var latest = await checker.RefreshIfStaleAsync();

            Assert.Equal("99.0.0", latest);
            Assert.Equal(2, handler.CallCount);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 失败_写入退避并保留旧latest()
    {
        var tempDir = TempDir();
        var fixedTime = T0;
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[\"2.0.0\"]}") };
        AppServices.NuGet = new NuGetClient(handler);
        try
        {
            var checker = new UpdateChecker(tempDir, () => fixedTime);

            var first = await checker.RefreshIfStaleAsync();
            Assert.Equal("2.0.0", first);

            fixedTime = fixedTime.AddHours(25);
            handler.Responder = _ => throw new HttpRequestException("network down");
            var result = await checker.RefreshIfStaleAsync();

            Assert.Equal("2.0.0", result);
            Assert.Equal("2.0.0", ReadCacheLatest(tempDir));
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 退避期内_不重试()
    {
        var tempDir = TempDir();
        var fixedTime = T0;
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[\"2.0.0\"]}") };
        AppServices.NuGet = new NuGetClient(handler);
        try
        {
            var checker = new UpdateChecker(tempDir, () => fixedTime);

            await checker.RefreshIfStaleAsync();
            fixedTime = fixedTime.AddHours(25);
            handler.Responder = _ => throw new HttpRequestException("network down");
            await checker.RefreshIfStaleAsync();
            var countAfterFailure = handler.CallCount;

            fixedTime = fixedTime.AddMinutes(30);
            var result = await checker.RefreshIfStaleAsync();

            Assert.Equal(countAfterFailure, handler.CallCount);
            Assert.Equal("2.0.0", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 退避过期_重试()
    {
        var tempDir = TempDir();
        var fixedTime = T0;
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[\"2.0.0\"]}") };
        AppServices.NuGet = new NuGetClient(handler);
        try
        {
            var checker = new UpdateChecker(tempDir, () => fixedTime);

            await checker.RefreshIfStaleAsync();
            fixedTime = fixedTime.AddHours(25);
            handler.Responder = _ => throw new HttpRequestException("network down");
            await checker.RefreshIfStaleAsync();
            var countBeforeRetry = handler.CallCount;

            fixedTime = fixedTime.AddHours(2);
            handler.Responder = _ => Json("{\"versions\":[\"3.0.0\"]}");
            var result = await checker.RefreshIfStaleAsync();

            Assert.Equal(countBeforeRetry + 1, handler.CallCount);
            Assert.Equal("3.0.0", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public void 无缓存时GetCachedInstructions返回状态未知提示()
    {
        var checker = new UpdateChecker(TempDir());

        var result = checker.GetCachedInstructions();

        Assert.NotNull(result);
        Assert.Contains("更新状态未知", result);
        Assert.Contains("check_status", result);
    }

    [Fact]
    public async Task 有更新时返回注入文本()
    {
        var tempDir = TempDir();
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[\"99.0.0\"]}") };
        AppServices.NuGet = new NuGetClient(handler);
        try
        {
            var checker = new UpdateChecker(tempDir);

            var latest = await checker.RefreshIfStaleAsync();
            Assert.Equal("99.0.0", latest);

            var instructions = checker.GetCachedInstructions();

            Assert.NotNull(instructions);
            Assert.Contains("有新版本 99.0.0", instructions);
            Assert.Contains("dotnet tool update --global ilspymcp", instructions);
            Assert.Contains(AppConfig.NuGetPackageId, instructions);
            Assert.Contains("无需调用 check_status 复查", instructions);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 已是最新时返回已是最新提示()
    {
        var tempDir = TempDir();
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[\"0.0.1\"]}") };
        AppServices.NuGet = new NuGetClient(handler);
        try
        {
            var checker = new UpdateChecker(tempDir);

            var latest = await checker.RefreshIfStaleAsync();
            Assert.Equal("0.0.1", latest);

            var instructions = checker.GetCachedInstructions();

            Assert.NotNull(instructions);
            Assert.Contains("已是最新版本", instructions);
            Assert.DoesNotContain("有新版本", instructions);
            Assert.Contains("无需调用 check_status 复查", instructions);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public void 损坏缓存_返回状态未知提示()
    {
        var tempDir = TempDir();
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, AppConfig.UpdateCheckCacheFileName), "{not-json!!!");
        var checker = new UpdateChecker(tempDir);

        var result = checker.GetCachedInstructions();

        Assert.NotNull(result);
        Assert.Contains("更新状态未知", result);
        Assert.Contains("无需调用 check_status 复查", result);
    }

    private static string TempDir() => Path.Combine(Path.GetTempPath(), "ilspymcp-tests", Guid.NewGuid().ToString("N"));

    private static string? ReadCacheLatest(string cacheDir)
    {
        var json = File.ReadAllText(Path.Combine(cacheDir, AppConfig.UpdateCheckCacheFileName));
        return JsonSerializer.Deserialize<UpdateChecker.UpdateCheckCache>(json)?.Latest;
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => Json("{\"versions\":[]}");
        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Responder(request));
        }
    }
}
