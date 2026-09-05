using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DotNetDebuggerMcp.Client;

/// <summary>
/// 端到端验证执行器：连接 MCP server、列出工具、逐个执行场景，按断言字段判定 PASS/FAIL 并统计失败数。
/// </summary>
public sealed class ClientRunner
{
    private readonly McpClient _mcp;

    private ClientRunner(McpClient mcp) => _mcp = mcp;

    /// <summary>
    /// 累计未通过场景数（含 ListToolsAsync 断言失败）；成功场景不计。
    /// </summary>
    public int Failures { get; private set; }

    /// <summary>
    /// 以 Release 方式启动 server 项目并建立 stdio 连接。
    /// </summary>
    /// <param name="serverProject">server 项目文件路径。</param>
    /// <returns>执行器实例。</returns>
    public static async Task<ClientRunner> ConnectAsync(string serverProject)
    {
        var transport = new StdioClientTransport(new()
        {
            Name = "DotNetDebuggerMcp test client",
            Command = "dotnet",
            Arguments = ["run", "--project", serverProject, "-c", "Release"],
        });
        var mcp = await McpClient.CreateAsync(transport);
        Console.WriteLine($"=== ServerInstructions: {mcp.ServerInstructions} ===");
        return new ClientRunner(mcp);
    }

    /// <summary>
    /// 打印 server 暴露的工具列表，并断言工具数量及关键工具名；不满足时计入失败数。
    /// </summary>
    public async Task ListToolsAsync()
    {
        Console.WriteLine("=== TOOLS ===");
        var tools = await _mcp.ListToolsAsync();
        foreach (var tool in tools) Console.WriteLine($"- {tool.Name}");

        var names = tools.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var pass = tools.Count >= 13
            && names.Contains("decompile")
            && names.Contains("decompile_member")
            && names.Contains("list_types")
            && names.Contains("decompile_to_dir")
            && names.Contains("decompile_to_project")
            && names.Contains("signature")
            && names.Contains("hierarchy")
            && names.Contains("dependencies")
            && names.Contains("call_graph")
            && names.Contains("call_chain")
            && names.Contains("assembly_info")
            && names.Contains("search_string")
            && names.Contains("field_access");
        if (pass)
        {
            Console.WriteLine("[PASS] 工具数量 >= 13 且含 decompile/decompile_member/list_types/decompile_to_dir/decompile_to_project/signature/hierarchy/dependencies/call_graph/call_chain/assembly_info/search_string/field_access");
        }
        else
        {
            Console.WriteLine($"[FAIL] 工具列表不完整：共 {tools.Count} 个：{string.Join(", ", names)}");
            Failures++;
        }
    }

    /// <summary>
    /// 按顺序执行全部验证场景，每个场景由断言字段判定 PASS/FAIL。
    /// </summary>
    /// <param name="cases">场景集合。</param>
    public async Task RunAsync(IEnumerable<ToolCallCase> cases)
    {
        foreach (var c in cases) await CallAsync(c);
    }

    /// <summary>
    /// 截取文本前 200 字符用于错误提示，避免刷屏。
    /// </summary>
    /// <param name="text">完整结果文本。</param>
    /// <returns>截断后的预览文本。</returns>
    private static string Preview(string text)
        => text.Length <= 200 ? text : text[..200] + "...";

    /// <summary>
    /// 执行单个场景：调用工具、提取文本结果、按断言字段检查并打印 PASS/FAIL。
    /// </summary>
    /// <param name="c">场景定义。</param>
    /// <returns>断言通过返回 true。</returns>
    private async Task<bool> CallAsync(ToolCallCase c)
    {
        Console.WriteLine($"{Environment.NewLine}=== CALL {c.Tool}：{c.Label} ===");
        var result = await _mcp.CallToolAsync(c.Tool, c.Args, cancellationToken: CancellationToken.None);

        // 提取文本块结果；非文本块（如图片）只打印类型名
        var textBlocks = result.Content.OfType<TextContentBlock>().ToList();
        foreach (var block in result.Content)
        {
            if (block is not TextContentBlock) Console.WriteLine($"({block.GetType().Name})");
        }
        var text = string.Join(Environment.NewLine, textBlocks.Select(b => b.Text));

        // 断言：ExpectSuccess 为 true 时要求 IsError 为 false；错误提示场景（ExpectSuccess=false）的
        // 语义是「预期返回错误提示文本」，server 端一切错误均以中文提示文本返回（IsError 恒为 false）， 故错误场景额外要求 IsError 不得为
        // true——若回归为框架级 Tool Error，即使文本命中也判定失败
        var pass = true;
        if (c.ExpectSuccess && result.IsError == true)
        {
            pass = false;
            Console.WriteLine("[FAIL] 预期成功，但调用结果被标记为错误（IsError=true）。");
        }
        if (!c.ExpectSuccess && result.IsError == true)
        {
            pass = false;
            Console.WriteLine("[FAIL] 预期返回中文错误提示，但结果为框架级 Tool Error（IsError=true）。");
        }
        if (c.ExpectedContains is not null && !text.Contains(c.ExpectedContains, StringComparison.Ordinal))
        {
            pass = false;
            Console.WriteLine($"[FAIL] 预期包含 \"{c.ExpectedContains}\"；实际（前 200 字符）：{Preview(text)}");
        }
        if (c.MustNotContain is not null && text.Contains(c.MustNotContain, StringComparison.Ordinal))
        {
            pass = false;
            Console.WriteLine($"[FAIL] 不应包含 \"{c.MustNotContain}\"；实际（前 200 字符）：{Preview(text)}");
        }

        // 打印调用结果文本，便于人工核对
        if (!string.IsNullOrEmpty(text)) Console.WriteLine(text);

        Console.WriteLine(pass ? "[PASS]" : "[FAIL]");
        if (!pass) Failures++;
        return pass;
    }
}