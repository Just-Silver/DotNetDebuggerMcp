using ILSpyMcp.Infrastructure;
using ILSpyMcp.Tools;

using System.Net;
using System.Net.Http;
using System.Text;

using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// 串行化使用 <see cref="AppServices"/> 静态状态的测试类（CheckToolTests / ToolPreflightTests），
/// 避免跨类并行执行时相互覆盖注入的 fake 造成竞态。
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
            JsonHandler("{\"versions\":[]}"),
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
            JsonHandler("{\"versions\":[]}"),
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
            JsonHandler("{\"versions\":[]}"),
            async text =>
            {
                Assert.Contains("环境状态: 就绪", text);
                Assert.Contains("11.0.0.9335 >= 11.0", text);
                Assert.Contains("成员反编译（-m）: 可用", text);
            });
    }

    [Fact]
    public async Task NuGet网络失败_静默跳过该检查项()
    {
        var handler = new FakeHandler { Responder = _ => throw new HttpRequestException("network down") };
        await RunWithAsync(
            new FakeProcessRunner { Code = 0, Stdout = "ilspycmd: 11.0.0.9335\n" },
            handler,
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
            JsonHandler("{\"versions\":[\"1.0.0\",\"2.0.0\"]}"),
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
            JsonHandler("{\"versions\":[\"1.1.0\"]}"),
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
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[\"1.1.0\"]}") };
        AppServices.ConfigureForTest(fake);
        AppServices.NuGet = new NuGetClient(handler);
        try
        {
            await CheckTool.CheckStatus();
            var second = await CheckTool.CheckStatus();

            Assert.Equal(1, fake.CallCount);
            Assert.Equal(1, handler.CallCount);
            Assert.Contains("环境状态: 就绪", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    private static async Task RunWithAsync(FakeProcessRunner fake, HttpMessageHandler handler, Func<string, Task> assert)
    {
        AppServices.ConfigureForTest(fake);
        AppServices.NuGet = new NuGetClient(handler);
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

    private static FakeHandler JsonHandler(string json) => new() { Responder = _ => Json(json) };

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
