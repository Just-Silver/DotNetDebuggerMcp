using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Client;

/// <summary>
/// call_chain 工具的全部端到端验证场景：token 定位 / typeName+memberName 定位 / 多匹配签名清单 / 未找到 / 参数校验。
/// </summary>
public static class CallChainCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // token 定位 ChainTop.Run：输出调用序列（含 ChainMid 成员）与 #MEMBER 反编译分隔行
        new ToolCallCase("call_chain", "token 定位 ChainTop.Run 输出调用序列与反编译",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = ChainTopRunToken(dll) },
            ExpectedContains: "方法体调用序列:", MustNotContain: "at System"),
        // token 定位结果含 #MEMBER JSON 分隔行（各被调用成员体前）
        new ToolCallCase("call_chain", "token 定位结果含 #MEMBER 分隔行",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = ChainTopRunToken(dll) },
            ExpectedContains: "#MEMBER", MustNotContain: "at System"),
        // typeName+memberName 定位起始方法：ChainTop 内 Run 唯一命中
        new ToolCallCase("call_chain", "typeName+memberName 定位起始方法",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.ChainTop", ["memberName"] = "Run" },
            ExpectedContains: "ILSpyMcp.Samples.ChainMid::", MustNotContain: "at System"),
        // includeExternal=true：ChainMid.Mid 仅调 System.Console.WriteLine（外部），序列保留外部调用行带程序集归属
        new ToolCallCase("call_chain", "includeExternal 外部调用行带程序集归属",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = ChainMidMidToken(dll), ["includeExternal"] = true },
            ExpectedContains: "System.Console::WriteLine [System.Console]", MustNotContain: "at System"),
        // 多匹配（BigClass 内 Big 命中 3 个成员）：返回 #MEMBER 签名清单提示用 token 而非反编译
        new ToolCallCase("call_chain", "memberName 多匹配返回 #MEMBER 清单",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.BigClass", ["memberName"] = "Big" },
            ExpectedContains: "#MEMBER", MustNotContain: "at System"),
        // 未找到成员应返回中文提示而非异常堆栈
        new ToolCallCase("call_chain", "未找到成员（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.BigClass", ["memberName"] = "NoSuchMethod" },
            ExpectedContains: "未找到名称包含", MustNotContain: "at System", ExpectSuccess: false),
        // 缺 memberName 应返回中文校验提示
        new ToolCallCase("call_chain", "缺 memberName（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.ChainTop" },
            ExpectedContains: "请指定 memberName", MustNotContain: "at System", ExpectSuccess: false),
    };

    /// <summary>
    /// call_chain 跨程序集展开的端到端验证场景：ExtCaller.Run 的跨程序集调用（TestSamples.dll 的 Callee）经
    /// UniversalAssemblyResolver 定位并展开其方法体子序列。
    /// </summary>
    public static IReadOnlyList<ToolCallCase> CrossAssembly(string extDll) => new[]
    {
        new ToolCallCase("call_chain", "跨程序集调用展开（ExtCaller → Callee）",
            new Dictionary<string, object?> { ["assembly"] = extDll, ["typeName"] = "ILSpyMcp.SamplesExt.ExtCaller", ["memberName"] = "Run", ["includeExternal"] = true },
            ExpectedContains: "ILSpyMcp.TestSamples::ILSpyMcp.Samples.Callee::", MustNotContain: "at System"),
    };

    /// <summary>
    /// 取 TestSamples 中 ChainTop.Run 的元数据 token，供 token 定位用例。
    /// </summary>
    private static string ChainTopRunToken(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != "ChainTop") continue;
            foreach (var methodHandle in type.GetMethods())
            {
                if (reader.GetString(reader.GetMethodDefinition(methodHandle).Name) == "Run")
                    return $"0x{MetadataTokens.GetToken(methodHandle):x8}";
            }
        }
        throw new InvalidOperationException("TestSamples 未找到 ChainTop.Run");
    }

    /// <summary>
    /// 取 TestSamples 中 ChainMid.Mid（仅调 System.Console.WriteLine 的外部方法）的元数据 token，供 includeExternal 用例。
    /// </summary>
    private static string ChainMidMidToken(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != "ChainMid") continue;
            foreach (var methodHandle in type.GetMethods())
            {
                if (reader.GetString(reader.GetMethodDefinition(methodHandle).Name) == "Mid")
                    return $"0x{MetadataTokens.GetToken(methodHandle):x8}";
            }
        }
        throw new InvalidOperationException("TestSamples 未找到 ChainMid.Mid");
    }
}
