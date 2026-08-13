namespace ILSpyMcp.Client;

/// <summary>
/// assembly_info 工具的全部端到端验证场景：程序集概览各字段（程序集名/目标框架/类型计数/引用清单）、
/// 缺 assembly 校验提示与 lines 分页。
/// </summary>
public static class AssemblyInfoCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // 概览包含程序集名
        new ToolCallCase("assembly_info", "程序集概览含程序集名",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "程序集: ", MustNotContain: "at System"),
        // 概览包含目标框架
        new ToolCallCase("assembly_info", "程序集概览含目标框架",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "目标框架: ", MustNotContain: "at System"),
        // 概览包含类型计数
        new ToolCallCase("assembly_info", "程序集概览含类型计数",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "类型总数:", MustNotContain: "at System"),
        // 概览包含引用的程序集清单
        new ToolCallCase("assembly_info", "程序集概览含引用清单",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "引用的程序集:", MustNotContain: "at System"),
        // 概览包含入口点行
        new ToolCallCase("assembly_info", "程序集概览含入口点",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "入口点:", MustNotContain: "at System"),
        // 缺 assembly 应返回中文校验提示
        new ToolCallCase("assembly_info", "缺 assembly（应返回校验提示）",
            new Dictionary<string, object?>(),
            ExpectedContains: "请指定 assembly", MustNotContain: "at System", ExpectSuccess: false),
        // lines 分页：概览首行即程序集名，lines="1-1" 应返回带行号切片
        new ToolCallCase("assembly_info", "lines=\"1-1\" 分页",
            new Dictionary<string, object?> { ["assembly"] = dll, ["lines"] = "1-1" },
            ExpectedContains: "程序集: ", MustNotContain: "at System"),
    };
}
