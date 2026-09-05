namespace DotNetDebuggerMcp.Client;

/// <summary>
/// generic_instantiations 工具的全部端到端验证场景：成员签名段 / 无 arity 短名命中 / 方法体调用段 / 类型不存在。
/// </summary>
public static class GenericInstantiationCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // GenericBox 无 arity 短名输入：成员签名段应含 GenericUser 的 GenericBox<int>/GenericBox<string> 实例化
        new ToolCallCase("generic_instantiations", "GenericBox 无 arity 成员签名段含 GenericUser 与 GenericBox<int>",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "GenericBox" },
            ExpectedContains: $"{TestDataHelper.SamplesNamespace}.GenericUser::", MustNotContain: "at System"),
        // GenericBox 全名（带 arity）输入：int 与 string 两种具体参数均输出
        new ToolCallCase("generic_instantiations", "GenericBox 成员签名段含 GenericBox<string>",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.GenericTypeName },
            ExpectedContains: "GenericBox<string>", MustNotContain: "at System"),
        // GenericHelper 泛型方法 Echo<T>：方法体调用段应含 GenericCaller.Run 的 Echo<int>
        new ToolCallCase("generic_instantiations", "GenericHelper 方法体调用段含 Echo<int>",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataHelper.SamplesNamespace}.GenericHelper" },
            ExpectedContains: "Echo<int>", MustNotContain: "at System"),
        // 类型不存在应返回中文提示而非异常堆栈
        new ToolCallCase("generic_instantiations", "类型不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "No.Such.Type" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
    };
}