namespace ILSpyMcp.Client;

/// <summary>
/// decompile_member 工具的全部端到端验证场景：按名搜索单成员/多成员/分隔头/访问器排除/相近名/无匹配/类型不存在/缺参校验。
/// 匹配数超上限（&gt;20）仅返回签名清单的分支不在本文件设场景——TestSamples 无超过 20 个方法的类，该分支已由 CLI 冒烟覆盖。
/// </summary>
public static class DecompileMemberCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // 按名搜到 BigClass.BigMethod，应输出其签名与行号（600+ 行超 200 触发截断）
        new ToolCallCase("decompile_member", "memberName 单匹配（BigMethod 超 200 行触发截断）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "BigMethod" },
            ExpectedContains: "已截断", MustNotContain: "at System"),
        // 多个成员命中（BigMethod/BigHelper/BigHelper2）：合并输出，头部标注匹配数
        new ToolCallCase("decompile_member", "memberName 多匹配（Big 命中 3 个成员合并输出）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "Big" },
            ExpectedContains: "3 个匹配", MustNotContain: "at System"),
        // 多匹配合并输出的各成员体前应有 === 名字 (token) === 分隔行（同参数，经管道缓存命中前序结果）
        new ToolCallCase("decompile_member", "多匹配分隔头（=== 名字 (token) ===）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "Big" },
            ExpectedContains: "===", MustNotContain: "at System"),
        // 默认排除属性/事件访问器：Members 的 get_Count 被排除后无名称含 "get" 的成员，返回未找到提示
        new ToolCallCase("decompile_member", "访问器排除（get_Count 不参与匹配）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.MembersTypeName, ["memberName"] = "get" },
            ExpectedContains: "未找到名称包含", MustNotContain: "at System", ExpectSuccess: false),
        // 拼错成员名：无匹配时返回相近成员名提示（BigMethd 编辑距离 1 → BigMethod）
        new ToolCallCase("decompile_member", "相近名提示（BigMethd → BigMethod）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "BigMethd" },
            ExpectedContains: "相近成员", MustNotContain: "at System", ExpectSuccess: false),
        // 大小写不敏感：bigmethod 应命中 BigMethod
        new ToolCallCase("decompile_member", "memberName 大小写不敏感",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "bigmethod" },
            ExpectedContains: "1\t", MustNotContain: "at System"),
        // 无匹配成员应返回中文提示而非异常堆栈
        new ToolCallCase("decompile_member", "无匹配成员（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "NoSuchMethod" },
            ExpectedContains: "未找到名称包含", MustNotContain: "at System", ExpectSuccess: false),
        // 类型不存在应返回中文提示
        new ToolCallCase("decompile_member", "类型不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "No.Such.Type", ["memberName"] = "X" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
        // 缺 typeName 应返回中文校验提示
        new ToolCallCase("decompile_member", "缺 typeName（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["memberName"] = "X" },
            ExpectedContains: "请指定 typeName", MustNotContain: "at System", ExpectSuccess: false),
        // 缺 memberName 应返回中文校验提示
        new ToolCallCase("decompile_member", "缺 memberName（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName },
            ExpectedContains: "请指定 memberName", MustNotContain: "at System", ExpectSuccess: false),
    };
}