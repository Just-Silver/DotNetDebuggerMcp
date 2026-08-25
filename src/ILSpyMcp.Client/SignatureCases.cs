namespace ILSpyMcp.Client;

/// <summary>
/// signature 工具的全部端到端验证场景：普通类型（static 方法）/ 属性访问器合并 / 泛型 / 类型不存在 / 缺 typeName。
/// </summary>
public static class SignatureCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // BigClass.BigMethod 是 public static void，单行签名（签名只含参数类型不含参数名）同时覆盖「BigMethod(」与「static」两个断言点
        new ToolCallCase("signature", "BigClass 成员签名（public static void BigMethod）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName },
            ExpectedContains: "public static void BigMethod(int);", MustNotContain: "at System"),
        // Members：属性合并为 { get; set; }，访问器方法（get_/set_）不单独输出
        new ToolCallCase("signature", "Members 属性访问器合并（{ get; set; } 不出现 get_）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.MembersTypeName },
            ExpectedContains: "{ get; set; }", MustNotContain: "get_"),
        // Members：每行行尾附成员 token（方法 0x06），agent 可直接用于 decompile_member 的 token 参数
        new ToolCallCase("signature", "Members 每行行尾附成员 token（0x06 方法）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.MembersTypeName },
            ExpectedContains: "  0x06", MustNotContain: "at System"),
        // 泛型类型：成员签名带类型级泛型参数（T），含泛型方法 First()
        new ToolCallCase("signature", "GenericBox`1 泛型方法 First()",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.GenericTypeName },
            ExpectedContains: "First()", MustNotContain: "at System"),
        // 泛型类型：泛型方法 Add(T item)（与 First() 不同行，单独断言）
        new ToolCallCase("signature", "GenericBox`1 泛型方法 Add(T)",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.GenericTypeName },
            ExpectedContains: "Add(", MustNotContain: "at System"),
        // 类型不存在应返回中文提示而非异常堆栈
        new ToolCallCase("signature", "类型不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "No.Such.Type" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
        // 缺 typeName 应返回中文校验提示
        new ToolCallCase("signature", "缺 typeName（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "请指定 typeName", MustNotContain: "at System", ExpectSuccess: false),
        // lines 分页：BigClass 多个成员签名，lines="1-2" 应返回指定行切片且不报未知参数
        new ToolCallCase("signature", "BigClass lines=\"1-2\" 分页",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["lines"] = "1-2" },
            ExpectedContains: "BigMethod(", MustNotContain: "at System"),
    };
}