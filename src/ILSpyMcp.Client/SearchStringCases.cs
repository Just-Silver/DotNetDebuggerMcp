namespace ILSpyMcp.Client;

/// <summary>
/// search_string 工具的全部端到端验证场景：命中 StringHolder 字符串 / 忽略大小写 / 未命中零匹配 / typeName 限定 / 参数校验。
/// </summary>
public static class SearchStringCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // StringHolder.Log 含 "不支持高性能计数器"：全程序集反查应命中并输出 类型::成员签名 + 转义字符串 + token
        new ToolCallCase("search_string", "按中文文案反查命中 StringHolder.Log",
            new Dictionary<string, object?> { ["assembly"] = dll, ["search"] = "不支持高性能计数器" },
            ExpectedContains: "ILSpyMcp.Samples.StringHolder::", MustNotContain: "at System"),
        // 忽略大小写：小写 "order by" 应命中 Query 的大写 "ORDER BY GetDate()"
        new ToolCallCase("search_string", "忽略大小写命中 Query 的 ORDER BY",
            new Dictionary<string, object?> { ["assembly"] = dll, ["search"] = "order by" },
            ExpectedContains: "\"ORDER BY GetDate()\"", MustNotContain: "at System"),
        // 未命中：输出零匹配头部而非报错
        new ToolCallCase("search_string", "未命中返回零匹配",
            new Dictionary<string, object?> { ["assembly"] = dll, ["search"] = "不存在的字符串xyz" },
            ExpectedContains: "匹配实体: 0 个", MustNotContain: "at System"),
        // typeName 限定：仅在 StringHolder 内反查
        new ToolCallCase("search_string", "typeName 限定仅在 StringHolder 内反查",
            new Dictionary<string, object?> { ["assembly"] = dll, ["search"] = "Order", ["typeName"] = "ILSpyMcp.Samples.StringHolder" },
            ExpectedContains: "ILSpyMcp.Samples.StringHolder::", MustNotContain: "at System"),
        // typeName 不存在应返回中文提示而非异常堆栈
        new ToolCallCase("search_string", "typeName 不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["search"] = "Order", ["typeName"] = "No.Such.Type" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
        // 缺 search 应返回中文提示而非异常堆栈
        new ToolCallCase("search_string", "缺 search（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["search"] = "" },
            ExpectedContains: "请指定 search", MustNotContain: "at System", ExpectSuccess: false),
    };
}
