using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// #1 进程层验证：宿主 MCP 模式与 --web 同进程共存——MCP stdio 客户端连上可用调试工具，
/// 同时 Web 端口 HTTP 可访问（agent 调试 + 浏览器看现场的基础）。浏览器实时联动人工验收。
/// </summary>
public sealed class McpWebCoexistTests
{
    [Fact]
    public async Task McpAndWeb_同进程_Mcp工具可用且Web可访问()
    {
        var port = FindFreePort();

        // 宿主子进程带 --web 启动（MCP stdio 常驻 + Web 同进程）
        await using var mcp = await ConnectAsync($" --web --web-port {port}");

        // 1. MCP 工具可用：debug_state 无会话提示（说明 MCP 协议正常）
        var st = await CallAsync(mcp, "debug_state", new Dictionary<string, object?>());
        Assert.True(st.IsError != true, st.Text());
        Assert.Contains("无活动调试会话", st.Text());

        // 2. MCP 反编译工具可用（同进程共存验证）
        var dll = Path.Combine(Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, TestDataPaths.TestSamplesAssemblyName + ".dll");
        var dc = await CallAsync(mcp, "decompile_member",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataPaths.SamplesNamespace}.BigClass", ["memberName"] = "BigMethod" });
        Assert.True(dc.IsError != true, dc.Text());
        Assert.Contains("BigMethod", dc.Text());

        // 3. Web 端口 HTTP 可访问（同进程 Web host 在跑）
        var web = await GetWithRetryAsync($"http://127.0.0.1:{port}/", TimeSpan.FromSeconds(15));
        Assert.NotNull(web);
        Assert.Contains("DotNet Debugger Web", web);
    }

    /// <summary>启动宿主子进程（MCP stdio + 可选 --web 参数）并握手。</summary>
    private static async Task<McpClient> ConnectAsync(string extraArgs)
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DotNetDebuggerMcp.dll");
        Assert.True(File.Exists(serverDll), $"server 程序集不存在：{serverDll}");
        var transport = new StdioClientTransport(new()
        {
            Name = "DotNetDebuggerMcp mcp+web test client",
            Command = "dotnet",
            Arguments = [serverDll, .. extraArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries)],
        });
        return await McpClient.CreateAsync(transport).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task<CallToolResult> CallAsync(McpClient mcp, string tool, IReadOnlyDictionary<string, object?> args)
        => await mcp.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);

    private static async Task<string?> GetWithRetryAsync(string url, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                return await hc.GetStringAsync(url);
            }
            catch
            {
                await Task.Delay(300);
            }
        }
        return null;
    }

    private static int FindFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
