namespace ILSpyMcp.Client;

/// <summary>
/// decompile 工具的全部端到端验证场景：typeName / lines / timeoutSeconds / 缺参与非法参数校验。
/// </summary>
public static class DecompileCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // 超 200 行应触发截断提示；结果不得出现异常堆栈
        new ToolCallCase("decompile", "typeName（超 200 行触发截断提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName },
            ExpectedContains: "已截断", MustNotContain: "at System"),
        // 行号分页应定位到 200 行（NumberLines 从起始行号标注）
        new ToolCallCase("decompile", "typeName + lines 按行号分页",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["lines"] = "200-400" },
            ExpectedContains: "200\t", MustNotContain: "at System"),
        // 自定义超时参数应被接受
        new ToolCallCase("decompile", "typeName + timeoutSeconds（自定义超时）",
            new Dictionary<string, object?>
            {
                ["assembly"] = dll,
                ["typeName"] = TestDataHelper.TypeName,
                ["timeoutSeconds"] = 60,
            },
            ExpectedContains: "1\t", MustNotContain: "at System"),
        // 非法超时应返回中文校验提示
        new ToolCallCase("decompile", "非法 timeoutSeconds（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["timeoutSeconds"] = 0 },
            ExpectedContains: "timeoutSeconds 必须为正整数", MustNotContain: "at System", ExpectSuccess: false),
        // 缺参：先缺 assembly，返回「请指定 assembly」校验提示
        new ToolCallCase("decompile", "缺参（应返回校验提示）",
            new Dictionary<string, object?>(),
            ExpectedContains: "请指定 assembly", MustNotContain: "at System", ExpectSuccess: false),
    };
}