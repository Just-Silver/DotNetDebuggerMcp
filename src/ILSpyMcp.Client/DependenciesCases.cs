namespace ILSpyMcp.Client;

/// <summary>
/// dependencies 工具的全部端到端验证场景：内部类型正向引用 / 无引用占位 / 类型不存在。
/// </summary>
public static class DependenciesCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // Uses 字段与方法签名引用 DerivedClass 与 Dog（正向）；两类型在不同行，分两条断言
        new ToolCallCase("dependencies", "Uses 正向引用含 DerivedClass",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.UsesTypeName },
            ExpectedContains: "DerivedClass", MustNotContain: "at System"),
        new ToolCallCase("dependencies", "Uses 正向引用含 Dog",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.UsesTypeName },
            ExpectedContains: "Dog", MustNotContain: "at System"),
        // BigClass 成员签名无内部类型引用，应输出（无）占位而非报错
        new ToolCallCase("dependencies", "BigClass 无内部引用（输出（无）占位）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName },
            ExpectedContains: "（无）", MustNotContain: "at System"),
        // 类型不存在应返回中文提示而非异常堆栈
        new ToolCallCase("dependencies", "类型不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "No.Such.Type" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
    };
}
