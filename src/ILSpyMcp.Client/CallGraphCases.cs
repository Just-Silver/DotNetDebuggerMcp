namespace ILSpyMcp.Client;

/// <summary>
/// call_graph 工具的全部端到端验证场景：正向方法体调用 / 反向调用者 / 无内部调用占位 / 类型不存在。
/// </summary>
public static class CallGraphCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // Caller.Run/RunStatic 方法体调用 Callee（构造 + 方法），正向段应含 Callee
        new ToolCallCase("call_graph", "Caller 正向方法体调用含 Callee",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.CallerTypeName },
            ExpectedContains: "ILSpyMcp.Samples.Callee", MustNotContain: "at System"),
        // Callee 的反向段应含 Caller（程序集内方法体调用了它的类型）
        new ToolCallCase("call_graph", "Callee 反向含 Caller",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.Callee" },
            ExpectedContains: "ILSpyMcp.Samples.Caller", MustNotContain: "at System"),
        // Uses 方法体为空（仅默认 ctor 调 Object..ctor，跨程序集），两段均应输出（无）占位而非报错
        new ToolCallCase("call_graph", "Uses 方法体无内部调用（输出（无）占位）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.Uses" },
            ExpectedContains: "（无）", MustNotContain: "at System"),
        // 类型不存在应返回中文提示而非异常堆栈
        new ToolCallCase("call_graph", "类型不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "No.Such.Type" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
        // includeExternal=true：Caller.External 调 System.Console.WriteLine（跨程序集），外部段应含 System.Console 带程序集归属
        new ToolCallCase("call_graph", "Caller includeExternal 外部段含 System.Console",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.CallerTypeName, ["includeExternal"] = true },
            ExpectedContains: "System.Console [System.Console]", MustNotContain: "at System"),
    };
}
