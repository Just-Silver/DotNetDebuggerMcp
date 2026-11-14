using System.Text.Json;
using DotNetDebuggerMcp.ChildCli;
using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Services.Child;
using SharpSight.ChildSession.Contracts;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildMcpToolsTests
{
    [Fact]
    public async Task 真实MCP的child目录可发现且缺参返回业务提示而非协议错误()
    {
        await using var mcp = await ConnectAsync("child");
        var tools = await mcp.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, tool => tool.Name == "child_session");
        Assert.DoesNotContain(tools, tool => tool.Name is "ui_find" or "screenshot");
        foreach (var name in new[] { "child_process", "child_pointer", "child_keyboard" })
        {
            var result = await mcp.CallToolAsync(name, new Dictionary<string, object?>(),
                cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.IsError != true);
            using var reply = JsonDocument.Parse(string.Concat(result.Content.OfType<TextContentBlock>().Select(b => b.Text)));
            Assert.Equal("not-executed", reply.RootElement.GetProperty("status").GetString());
            Assert.Contains("instance", reply.RootElement.GetProperty("message").GetString());
        }
    }

    [Fact]
    public async Task 真实MCP禁用child时指定实例的UIA和窗口清单都不走父侧()
    {
        await using var mcp = await ConnectAsync("ui,screenshot");
        var tools = await mcp.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.DoesNotContain(tools, tool => tool.Name.StartsWith("child_", StringComparison.Ordinal));
        var input = await mcp.CallToolAsync("ui_input", new Dictionary<string, object?>
        {
            ["process"] = "definitely-not-a-running-process", ["value"] = "不可写入", ["instance"] = "missing"
        }, cancellationToken: TestContext.Current.CancellationToken);
        var windows = await mcp.CallToolAsync("screenshot", new Dictionary<string, object?>
        {
            ["mode"] = "list-windows", ["instance"] = "missing"
        }, cancellationToken: TestContext.Current.CancellationToken);
        foreach (var reply in new[] { input, windows })
        {
            Assert.True(reply.IsError != true);
            var text = string.Concat(reply.Content.OfType<TextContentBlock>().Select(b => b.Text));
            Assert.Contains("未启用", text);
        }
    }

    [Fact]
    public async Task 不同调用入口可复用同一实例()
    {
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken);
        var mcp = new ChildCallPolicy(FeatureFamily.All, host.Client.SendAsync);
        var first = await mcp.CallAsync("ui_input", Args(new { process = "3", value = "MCP" }), TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new ChildCliCommand(new(FeatureFamily.All, host.Client.SendAsync), output, error);
        var exit = await cli.ExecuteAsync(["call", "ui_input", "--json", Args(new { process = "3", value = "CLI" }).GetRawText()],
            TestContext.Current.CancellationToken);
        Assert.Equal("test-instance", first.Instance);
        Assert.Equal(0, exit);
        using var second = JsonDocument.Parse(output.ToString());
        Assert.Equal("test-instance", second.RootElement.GetProperty("instance").GetString());
        Assert.Equal("CLI", await File.ReadAllTextAsync(host.OutputPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 未启用族不能经result或requests取回该族结果()
    {
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken,
            (r, _) => Task.FromResult(ChildReply.Create(r, "completed", "仅UI族可读的机密结果")));
        var full = new ChildCallPolicy(FeatureFamily.All, host.Client.SendAsync);
        var original = await full.CallAsync("ui_get", Args(new { process = "3", what = "value" }),
            TestContext.Current.CancellationToken);
        var restricted = new ChildCallPolicy(FeatureFamily.Child, host.Client.SendAsync);
        var denied = await restricted.CallAsync("child_session", Args(new { action = "result", requestId = original.RequestId }),
            TestContext.Current.CancellationToken);
        var listing = await restricted.CallAsync("child_session", Args(new { action = "requests" }),
            TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", denied.Status);
        Assert.DoesNotContain("机密结果", denied.Message + denied.Data.GetRawText());
        Assert.Empty(listing.Data.GetProperty("items").EnumerateArray());
        var allowed = await full.CallAsync("child_session", Args(new { action = "result", requestId = original.RequestId }),
            TestContext.Current.CancellationToken);
        Assert.Contains("机密结果", allowed.Message);
    }

    [Fact]
    public async Task 指定实例但未启用child时不产生父侧或子侧副作用()
    {
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken);
        var disabled = new ChildCallPolicy(FeatureFamily.Ui, host.Client.SendAsync);
        var reply = await disabled.CallAsync("ui_input", Args(new { process = "3", value = "不允许写入" }),
            TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", reply.Status);
        Assert.Contains("未启用", reply.Message);
        Assert.False(File.Exists(host.OutputPath));
    }

    [Fact]
    public async Task MCP取消后另一客户端能发现并查询原请求()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken, async (r, ct) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return ChildReply.Create(r, "completed", "不应到达");
        });
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var caller = new ChildCallPolicy(FeatureFamily.All, host.Client.SendAsync);
        var pending = caller.CallAsync("ui_input", Args(new { process = "3", value = "不应写入" }), cancel.Token);
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        Assert.Equal("unknown", (await pending).Status);
        var other = new ChildCallPolicy(FeatureFamily.All, host.Client.SendAsync);
        var listing = await other.CallAsync("child_session", Args(new { action = "requests" }),
            TestContext.Current.CancellationToken);
        var item = Assert.Single(listing.Data.GetProperty("items").EnumerateArray());
        var id = item.GetProperty("requestId").GetString()!;
        var found = await other.CallAsync("child_session", Args(new { action = "result", requestId = id, timeoutSeconds = 2 }),
            TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", found.Status);
        Assert.False(File.Exists(host.OutputPath));
    }

    private static JsonElement Args(object extra)
    {
        var fields = JsonSerializer.SerializeToElement(extra).Deserialize<Dictionary<string, JsonElement>>()!;
        fields["instance"] = JsonSerializer.SerializeToElement("test-instance");
        return JsonSerializer.SerializeToElement(fields);
    }

    private static async Task<McpClient> ConnectAsync(string features)
    {
        var transport = new StdioClientTransport(new()
        {
            Name = "child tools contract", Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "DotNetDebuggerMcp.dll"), "--features=" + features]
        });
        return await McpClient.CreateAsync(transport, cancellationToken: TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }
}
