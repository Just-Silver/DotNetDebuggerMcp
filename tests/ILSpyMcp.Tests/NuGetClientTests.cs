using ILSpyMcp.Configuration;
using ILSpyMcp.UpdateCheck;

using System.Net;
using System.Net.Http;
using System.Text;

using Xunit;

namespace ILSpyMcp.Tests;

public class NuGetClientTests
{
    [Fact]
    public async Task 查询_排除预发布_返回最大稳定版()
    {
        var client = new NuGetClient(Handler("{\"versions\":[\"1.0.0\",\"1.1.0\",\"1.2.0-beta\"]}"));

        var latest = await client.GetLatestStableVersionAsync("ilspymcp");

        Assert.Equal("1.1.0", latest);
    }

    [Fact]
    public async Task 查询_无预发布_返回最大版本()
    {
        var client = new NuGetClient(Handler("{\"versions\":[\"1.0.0\",\"1.1.0\",\"1.2.0\"]}"));

        var latest = await client.GetLatestStableVersionAsync("ilspymcp");

        Assert.Equal("1.2.0", latest);
    }

    [Fact]
    public async Task 查询_请求URL指向flatcontainer版本清单()
    {
        var handler = new FakeHandler { Responder = _ => Json("{\"versions\":[]}") };
        var client = new NuGetClient(handler);

        await client.GetLatestStableVersionAsync("ilspymcp");

        Assert.Equal("https://api.nuget.org/v3-flatcontainer/ilspymcp/index.json", handler.LastRequest?.RequestUri?.ToString());
    }

    [Fact]
    public async Task 网络异常_返回null()
    {
        var handler = new FakeHandler { Responder = _ => throw new HttpRequestException("network down") };
        var client = new NuGetClient(handler);

        var latest = await client.GetLatestStableVersionAsync("ilspymcp");

        Assert.Null(latest);
    }

    [Fact]
    public async Task JSON结构异常_返回null()
    {
        var client = new NuGetClient(Handler("not-json"));

        var latest = await client.GetLatestStableVersionAsync("ilspymcp");

        Assert.Null(latest);
    }

    [Fact]
    public async Task 空版本列表_返回null()
    {
        var client = new NuGetClient(Handler("{\"versions\":[]}"));

        var latest = await client.GetLatestStableVersionAsync("ilspymcp");

        Assert.Null(latest);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8) };

    private static FakeHandler Handler(string json) => new() { Responder = _ => Json(json) };

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Responder { get; set; } = _ => Json("{\"versions\":[]}");
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Responder(request));
        }
    }
}
