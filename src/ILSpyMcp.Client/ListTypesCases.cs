namespace ILSpyMcp.Client;

/// <summary>
/// list_types 工具的全部端到端验证场景：list 单值/组合 / lines / 非法值 / 缺参校验。
/// </summary>
public static class ListTypesCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // 单类别 c：结果应含 System.Linq.Enumerable 等 class 类型名
        new ToolCallCase("list_types", "单类别 c",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c" },
            ExpectedContains: "Enumerable", MustNotContain: "at System"),
        // 组合类别 csi：仍应含 class 类型名
        new ToolCallCase("list_types", "组合类别 csi",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "csi" },
            ExpectedContains: "Enumerable", MustNotContain: "at System"),
        // 行号切片应定位到第 1 行
        new ToolCallCase("list_types", "list + lines 按行号切片",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c", ["lines"] = "1-5" },
            ExpectedContains: "1\t", MustNotContain: "at System"),
        // 自定义超时参数应被接受
        new ToolCallCase("list_types", "list + timeoutSeconds（自定义超时）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c", ["timeoutSeconds"] = 45 },
            ExpectedContains: "Enumerable", MustNotContain: "at System"),
        // 非法 list 应返回中文校验提示而非异常堆栈
        new ToolCallCase("list_types", "非法 list（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "xyz" },
            ExpectedContains: "无效的 list 参数", MustNotContain: "at System", ExpectSuccess: false),
        // 缺参：先缺 assembly，返回「请指定 assembly」校验提示
        new ToolCallCase("list_types", "缺参（应返回校验提示）",
            new Dictionary<string, object?>(),
            ExpectedContains: "请指定 assembly", MustNotContain: "at System", ExpectSuccess: false),
    };
}
