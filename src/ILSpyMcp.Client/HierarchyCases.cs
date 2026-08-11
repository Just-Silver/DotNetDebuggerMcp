namespace ILSpyMcp.Client;

/// <summary>
/// hierarchy 工具的全部端到端验证场景：派生类基类链 / 接口反向实现 / 实现类接口段 / 类型不存在。
/// </summary>
public static class HierarchyCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // DerivedClass 基类链应为 DerivedClass → BaseClass → System.Object；BaseClass 与 System.Object 在不同行，分两条断言
        new ToolCallCase("hierarchy", "DerivedClass 基类链含 BaseClass",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.DerivedTypeName },
            ExpectedContains: "BaseClass", MustNotContain: "at System"),
        new ToolCallCase("hierarchy", "DerivedClass 基类链上溯到 System.Object",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.DerivedTypeName },
            ExpectedContains: "System.Object", MustNotContain: "at System"),
        // IAnimal 反向：程序集内直接实现它的类型应为 Dog
        new ToolCallCase("hierarchy", "IAnimal 反向（程序集内实现者 Dog）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.InterfaceTypeName },
            ExpectedContains: "Dog", MustNotContain: "at System"),
        // Dog 接口段：应列出其实现的 IAnimal
        new ToolCallCase("hierarchy", "Dog 接口段（含 IAnimal）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.Dog" },
            ExpectedContains: "IAnimal", MustNotContain: "at System"),
        // 类型不存在应返回中文提示而非异常堆栈
        new ToolCallCase("hierarchy", "类型不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "No.Such.Type" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
    };
}
