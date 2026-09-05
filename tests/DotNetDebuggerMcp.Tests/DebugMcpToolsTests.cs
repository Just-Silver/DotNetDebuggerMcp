using DotNetDebuggerMcp.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Reflection.Metadata;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// MCP 调试工具端到端测试：真实子进程宿主 + DebugTarget 目标，
/// 验证 debug_launch → breakpoint → continue → 命中 → stack/variables 闭环。
/// </summary>
public sealed class DebugMcpToolsTests
{
    private static string DebugTargetExe => Path.Combine(
        Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, "DebugTarget.exe");

    [Fact]
    public async Task DebugTools_LaunchBreakpointContinueInspect_ClosesLoop()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        // Work 的 token 从 DebugTarget.dll 元数据读（0x06000003 稳定，但用元数据解析更稳）
        var dll = Path.ChangeExtension(exe, ".dll");
        var workToken = ReadMethodToken(dll, "Work");
        Assert.True(workToken > 0);

        await using var mcp = await ConnectAsync();

        // 1. debug_launch：启动 DebugTarget（3 迭代 + 8s delay 供操作窗口），异步返回
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 3 8", ["timeoutSeconds"] = 20 });
        if (launch.IsError == true) Console.WriteLine($"[diag] launch error: {launch.Text()}");
        Assert.True(launch.IsError != true, launch.Text());
        Assert.Contains("已启动", launch.Text());

        // 2. pending 断点：错误模块名不再报错，登记待绑定；list 可见、remove 可删
        var pending = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "NoSuchModule.dll", ["methodToken"] = $"0x{workToken:x8}", ["ilOffset"] = 0 });
        Assert.True(pending.IsError != true, pending.Text());
        Assert.Contains("断点已登记", pending.Text());
        var listPending = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("未绑定", listPending.Text());
        var pendingId = ParseBreakpointId(pending.Text());
        var rmPending = await CallAsync(mcp, "debug_breakpoint_remove",
            new Dictionary<string, object?> { ["breakpointId"] = pendingId });
        Assert.True(rmPending.IsError != true, rmPending.Text());

        // 3. 设断点：Work 入口（模块已加载 → 已绑定）
        var bp = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["methodToken"] = $"0x{workToken:x8}", ["ilOffset"] = 0 });
        Assert.True(bp.IsError != true, bp.Text());
        Assert.Contains("断点已设", bp.Text());
        var list = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("断点列表（1 个）", list.Text());
        Assert.Contains("已绑定", list.Text());

        // 4. debug_continue：进程运行（delay 结束进 Work 命中）
        var cont = await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        Assert.True(cont.IsError != true, cont.Text());
        Assert.Contains("已继续", cont.Text());

        // 5. debug_wait：阻塞等停点（进程 delay 8s 后 Work 命中；上限 20s 内应返回已停下）
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("已停下", wait.Text());
        Assert.Contains("breakpoint", wait.Text());

        // 6. debug_state 确认 Stopped
        var st = await CallAsync(mcp, "debug_state", new Dictionary<string, object?>());
        Assert.Contains("已停止", st.Text());

        // 7. debug_stack：读调用栈（应含 Work 帧）
        var stack = await CallAsync(mcp, "debug_stack", new Dictionary<string, object?>());
        Assert.True(stack.IsError != true, stack.Text());
        Assert.Contains("调用栈", stack.Text());
        Assert.Contains("DebugTarget.dll", stack.Text());

        // 8. debug_variables：读局部变量
        var vars = await CallAsync(mcp, "debug_variables", new Dictionary<string, object?>());
        Assert.True(vars.IsError != true, vars.Text());
        Assert.Contains("局部变量", vars.Text());

        // 9. debug_disconnect 清理
        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task DebugState_ConcurrentQueries_AllReturn()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe));

        await using var mcp = await ConnectAsync();
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 3 6", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());

        // 单会话内并发 12 路 debug_state：验证 Session 管理器线程安全 + stdio 不撕帧
        var tasks = Enumerable.Range(0, 12).Select(async _ =>
        {
            var r = await CallAsync(mcp, "debug_state", new Dictionary<string, object?>());
            return r;
        }).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.All(results, r => Assert.True(r.IsError != true));
        Assert.All(results, r => Assert.Contains("会话状态", r.Text()));

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    private static async Task<CallToolResult> CallAsync(McpClient mcp, string tool, IReadOnlyDictionary<string, object?> args)
        => await mcp.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>从断点设置结果文本（"断点已设: id=N ..."）解析断点 id。</summary>
    private static int ParseBreakpointId(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"id=(\d+)");
        Assert.True(m.Success, $"结果文本中未找到断点 id: {text}");
        return int.Parse(m.Groups[1].Value);
    }

    private static async Task<McpClient> ConnectAsync()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DotNetDebuggerMcp.dll");
        var transport = new StdioClientTransport(new()
        {
            Name = "DotNetDebuggerMcp debug tools test client",
            Command = "dotnet",
            Arguments = [serverDll],
        });
        return await McpClient.CreateAsync(transport).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static int ReadMethodToken(string dllPath, string methodName)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                var md = mr.GetMethodDefinition(mh);
                if (mr.GetString(md.Name) == methodName)
                    return System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }
}

internal static class CallToolResultExtensions
{
    public static string Text(this CallToolResult result)
        => string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}

