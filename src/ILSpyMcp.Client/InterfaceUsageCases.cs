namespace ILSpyMcp.Client;

/// <summary>
/// interface_usage 工具的全部端到端验证场景：实现者段 / includeIndirect 间接实现者 / 调用点段 / 类型不存在。
/// </summary>
public static class InterfaceUsageCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // IWorker 直接实现者 WorkerBase：实现者段应含 WorkerBase，且无调用点/引用时输出（无）占位
        new ToolCallCase("interface_usage", "IWorker 实现者段含 WorkerBase",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.IWorker" },
            ExpectedContains: "ILSpyMcp.Samples.WorkerBase", MustNotContain: "at System"),
        // includeIndirect=true：IWorker 的全部（间接）实现者应含 WorkerDerived（经 WorkerBase 间接实现）
        new ToolCallCase("interface_usage", "IWorker includeIndirect=true 含间接实现者 WorkerDerived",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.IWorker", ["includeIndirect"] = true },
            ExpectedContains: "ILSpyMcp.Samples.WorkerDerived", MustNotContain: "at System"),
        // IAnimal 调用点：AnimalCaller.Run 的 a.Speak() 应输出 类型全名::成员名 → 接口成员名 行
        new ToolCallCase("interface_usage", "IAnimal 调用点段含 AnimalCaller::Run → Speak",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.IAnimal" },
            ExpectedContains: "ILSpyMcp.Samples.AnimalCaller::Run → Speak", MustNotContain: "at System"),
        // 类型不存在应返回中文提示而非异常堆栈
        new ToolCallCase("interface_usage", "类型不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "No.Such.Type" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
    };
}
