using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Client;

/// <summary>
/// call_graph 工具的全部端到端验证场景：正向方法体调用 / 反向调用者 / 无内部调用占位 / 类型不存在 / token 方法级调用点。
/// </summary>
public static class CallGraphCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // Caller.Run/RunStatic 方法体调用 Callee（构造 + 方法），正向段应含 Callee
        new ToolCallCase("call_graph", "Caller 正向方法体调用含 Callee",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.CallerTypeName },
            ExpectedContains: TestDataHelper.CalleeTypeName, MustNotContain: "at System"),
        // Callee 的反向段应含 Caller（程序集内方法体调用了它的类型）
        new ToolCallCase("call_graph", "Callee 反向含 Caller",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.CalleeTypeName },
            ExpectedContains: TestDataHelper.CallerTypeName, MustNotContain: "at System"),
        // Uses 方法体为空（仅默认 ctor 调 Object..ctor，跨程序集），两段均应输出（无）占位而非报错
        new ToolCallCase("call_graph", "Uses 方法体无内部调用（输出（无）占位）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.UsesTypeName },
            ExpectedContains: "（无）", MustNotContain: "at System"),
        // 类型不存在应返回中文提示而非异常堆栈
        new ToolCallCase("call_graph", "类型不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "No.Such.Type" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
        // includeExternal=true：Caller.External 调 System.Console.WriteLine（跨程序集），外部段应含
        // System.Console 带程序集归属
        new ToolCallCase("call_graph", "Caller includeExternal 外部段含 System.Console",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.CallerTypeName, ["includeExternal"] = true },
            ExpectedContains: "System.Console [System.Console]", MustNotContain: "at System"),
        // token 方法级调用点：Callee 首个方法 Help（被 Caller.Run 的 c.Help() 调用）→ 应含 Caller:: 调用点行
        new ToolCallCase("call_graph", "token 方法级调用点含 Caller",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.CalleeTypeName, ["token"] = FirstCalleeMethodToken(dll) },
            ExpectedContains: $"{TestDataHelper.CallerTypeName}::", MustNotContain: "at System"),
        // token 分支缺省 typeName：头部应体现方法级调用点
        new ToolCallCase("call_graph", "token 分支缺省 typeName 仍输出调用点",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = FirstCalleeMethodToken(dll) },
            ExpectedContains: $"{TestDataHelper.CallerTypeName}::", MustNotContain: "at System"),
        // 非法 token 应返回中文提示
        new ToolCallCase("call_graph", "token 非法（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = "0xZZZZ" },
            ExpectedContains: "不是有效的元数据 token", MustNotContain: "at System", ExpectSuccess: false),
    };

    /// <summary>
    /// 取 TestSamples 中 Callee 首个方法（Help）的元数据 token，供 token 方法级调用点用例。 与 Tests 项目
    /// <c>TestDataPaths.FirstCalleeMethodToken</c> 逐字符相同，但 Client 是独立项目、无法引用 Tests，
    /// 故此处保留本地副本（改动时注意与 Tests 侧同步）。
    /// </summary>
    private static string FirstCalleeMethodToken(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != "Callee") continue;
            return $"0x{MetadataTokens.GetToken(type.GetMethods().First()):x8}";
        }
        throw new InvalidOperationException("TestSamples 未找到 Callee 类型");
    }
}