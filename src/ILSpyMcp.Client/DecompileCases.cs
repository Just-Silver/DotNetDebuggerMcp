namespace ILSpyMcp.Client;

/// <summary>
/// decompile 工具的全部端到端验证场景：typeName / member / languageVersion / lines / 缺参与非法参数校验。
/// </summary>
public static class DecompileCases
{
    private const string WhereMember =
        "M:System.Linq.Enumerable.Where``1(System.Collections.Generic.IEnumerable{``0},System.Func{``0,System.Boolean})";

    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // 超 200 行应触发截断提示；结果不得出现异常堆栈
        new ToolCallCase("decompile", "typeName（超 200 行触发截断提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "System.Linq.Enumerable" },
            ExpectedContains: "已截断", MustNotContain: "at System"),
        // 行号分页应定位到 200 行（NumberLines 从起始行号标注）
        new ToolCallCase("decompile", "typeName + lines 按行号分页",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "System.Linq.Enumerable", ["lines"] = "200-400" },
            ExpectedContains: "200\t", MustNotContain: "at System"),
        // member 反编译应输出 Where 方法签名
        new ToolCallCase("decompile", "member（XML 文档 ID）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["member"] = WhereMember },
            ExpectedContains: "Where", MustNotContain: "at System"),
        // 限制输出长度后应定位到第 1 行
        new ToolCallCase("decompile", "typeName + languageVersion + lines（限制输出长度）",
            new Dictionary<string, object?>
            {
                ["assembly"] = dll,
                ["typeName"] = "System.Linq.Enumerable",
                ["languageVersion"] = "Latest",
                ["lines"] = "1-10",
            },
            ExpectedContains: "1\t", MustNotContain: "at System"),
        // 自定义超时参数应被接受
        new ToolCallCase("decompile", "typeName + timeoutSeconds（自定义超时）",
            new Dictionary<string, object?>
            {
                ["assembly"] = dll,
                ["typeName"] = "System.Linq.Enumerable",
                ["timeoutSeconds"] = 60,
            },
            ExpectedContains: "1\t", MustNotContain: "at System"),
        // 非法超时应返回中文校验提示
        new ToolCallCase("decompile", "非法 timeoutSeconds（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "System.Linq.Enumerable", ["timeoutSeconds"] = 0 },
            ExpectedContains: "timeoutSeconds 必须为正整数", MustNotContain: "at System", ExpectSuccess: false),
        // 非法语言版本应返回中文校验提示而非异常堆栈
        new ToolCallCase("decompile", "非法 languageVersion（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "System.Linq.Enumerable", ["languageVersion"] = "Foo" },
            ExpectedContains: "languageVersion 无效", MustNotContain: "at System", ExpectSuccess: false),
        // 缺参：先缺 assembly，返回「请指定 assembly」校验提示
        new ToolCallCase("decompile", "缺参（应返回校验提示）",
            new Dictionary<string, object?>(),
            ExpectedContains: "请指定 assembly", MustNotContain: "at System", ExpectSuccess: false),
    };
}
