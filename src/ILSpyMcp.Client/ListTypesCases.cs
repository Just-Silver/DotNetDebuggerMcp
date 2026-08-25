namespace ILSpyMcp.Client;

/// <summary>
/// list_types 工具的全部端到端验证场景：list 单值/组合 / lines / 编译器生成类型过滤 / nameContains / namespaceContains / 非法值
/// / 缺参校验。
/// </summary>
public static class ListTypesCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // 单类别 c：结果应含 TestDataHelper.ListedClassName 等 class 类型名
        new ToolCallCase("list_types", "单类别 c",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c" },
            ExpectedContains: TestDataHelper.ListedClassName, MustNotContain: "at System"),
        // 组合类别 csi：仍应含 class 类型名
        new ToolCallCase("list_types", "组合类别 csi",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "csi" },
            ExpectedContains: TestDataHelper.ListedClassName, MustNotContain: "at System"),
        // 行号切片应定位到第 1 行
        new ToolCallCase("list_types", "list + lines 按行号切片",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c", ["lines"] = "1-5" },
            ExpectedContains: "1\t", MustNotContain: "at System"),
        // 编译器生成类型（<Module> 等名含 <）默认过滤，不应出现于输出
        new ToolCallCase("list_types", "编译器生成过滤（<Module> 不出现）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c" },
            ExpectedContains: TestDataHelper.ListedClassName, MustNotContain: "<Module>"),
        // 更严断言：编译器生成类型名均含 < 而 C# 标识符不允许 <，过滤后整段输出不应出现 <
        new ToolCallCase("list_types", "编译器生成类型全过滤（输出不含 <）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c" },
            ExpectedContains: TestDataHelper.ListedClassName, MustNotContain: "<"),
        // nameContains 按类型名子串过滤（忽略大小写）："Generic" 应命中 GenericBox`1
        new ToolCallCase("list_types", "nameContains 按名过滤（命中 GenericBox）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c", ["nameContains"] = "Generic" },
            ExpectedContains: "ILSpyMcp.Samples.GenericBox`1", MustNotContain: "at System"),
        // nameContains 无匹配：过滤后应无结果行，但头部信息块仍在（匹配实体: 0 个）
        new ToolCallCase("list_types", "nameContains 无匹配（返回空列表）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c", ["nameContains"] = "不存在的类型名XYZ" },
            ExpectedContains: "匹配实体: 0 个", MustNotContain: "at System"),
        // namespaceContains 按命名空间子串过滤（忽略大小写）：应命中测试程序集的 ILSpyMcp.Samples 命名空间
        new ToolCallCase("list_types", "namespaceContains 按命名空间过滤（命中 ILSpyMcp.Samples）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c", ["namespaceContains"] = "ILSpyMcp.Samples" },
            ExpectedContains: "ILSpyMcp.Samples.Class0001", MustNotContain: "at System"),
        // namespaceContains 无匹配：过滤后应无结果行，但头部信息块仍在（匹配实体: 0 个）
        new ToolCallCase("list_types", "namespaceContains 无匹配（返回空列表）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c", ["namespaceContains"] = "不存在.Ns" },
            ExpectedContains: "匹配实体: 0 个", MustNotContain: "at System"),
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