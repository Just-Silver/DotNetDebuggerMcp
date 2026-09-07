using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Client;

/// <summary>
/// decompile_il 工具端到端验证场景：方法 token → IL 文本（async 状态机外壳含状态机类型引用）、
/// 空方法体输出、非方法 token（字段）拒绝提示、非法/缺 token 校验提示、lines 分页。
/// </summary>
public static class DecompileIlCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // 空方法体（BigHelper）：断言 ILSpy 风格反汇编骨架（RVA 注释/.maxstack/IL 行号标签）
        new ToolCallCase("decompile_il", "方法 token 返回 IL 文本（空方法体）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = MethodToken(dll, "BigClass", "BigHelper") },
            ExpectedContains: "IL_0000: ret", MustNotContain: "at System"),
        // async 外壳方法（WithAsync.Go）：IL 含状态机类型引用——B② IL 兜底典型场景
        new ToolCallCase("decompile_il", "async 外壳方法 IL 含状态机类型引用",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = MethodToken(dll, "WithAsync", "Go") },
            ExpectedContains: "<Go>d__0", MustNotContain: "at System"),
        // lines 分页：方法 token 反汇编结果按行号切片
        new ToolCallCase("decompile_il", "lines=\"1-5\" 分页",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = MethodToken(dll, "BigClass", "BigHelper"), ["lines"] = "1-5" },
            ExpectedContains: "当前输出: 1-", MustNotContain: "at System"),
        // 非方法 token（Members.Name 字段 0x04 开头）应返回中文提示
        new ToolCallCase("decompile_il", "非方法 token（字段）应返回提示",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = MemberToken(dll, "Members", "Name") },
            ExpectedContains: "不是方法定义", MustNotContain: "at System", ExpectSuccess: false),
        // 非法 token 应返回中文提示
        new ToolCallCase("decompile_il", "非法 token（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = "0xZZZZ" },
            ExpectedContains: "不是有效的元数据 token", MustNotContain: "at System", ExpectSuccess: false),
        // 缺 token 应返回中文校验提示
        new ToolCallCase("decompile_il", "缺 token（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "请指定 token", MustNotContain: "at System", ExpectSuccess: false),
    };

    /// <summary>取 TestSamples 中指定类型指定名方法的方法定义 token（0x06 开头十六进制文本）。</summary>
    private static string MethodToken(string dll, string typeName, string methodName)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != typeName) continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) == methodName)
                {
                    return $"0x{MetadataTokens.GetToken(methodHandle):x8}";
                }
            }
        }
        throw new InvalidOperationException($"TestSamples 未找到类型 {typeName} 的方法 {methodName}");
    }

    /// <summary>取 TestSamples 中指定类型指定名成员的 token（0x 开头十六进制文本；此处取 Members.Name 字段，0x04 开头）。</summary>
    private static string MemberToken(string dll, string typeName, string memberName)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != typeName) continue;
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if (reader.GetString(field.Name) == memberName)
                {
                    return $"0x{MetadataTokens.GetToken(fieldHandle):x8}";
                }
            }
        }
        throw new InvalidOperationException($"TestSamples 未找到类型 {typeName} 的成员 {memberName}");
    }
}
