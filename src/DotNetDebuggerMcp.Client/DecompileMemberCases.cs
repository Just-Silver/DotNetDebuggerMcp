using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Client;

/// <summary>
/// decompile_member 工具的全部端到端验证场景：按名搜索单成员/多成员/分隔头/访问器排除/相近名/无匹配/类型不存在/缺参校验。 匹配数超上限（&gt;20）仅返回签名清单由
/// ManyOverloads（21 个 Do 重载）覆盖，清单 token 可经 token 参数直接反编译单个成员（闭环）。
/// </summary>
public static class DecompileMemberCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // 按名搜到 BigClass.BigMethod，应输出其签名与行号（600+ 行超预算触发截断）
        new ToolCallCase("decompile_member", "memberName 单匹配（BigMethod 超预算触发截断）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "BigMethod" },
            ExpectedContains: "已截断", MustNotContain: "at System"),
        // 多个成员命中（BigMethod/BigHelper/BigHelper2）：合并输出，头部标注匹配数
        new ToolCallCase("decompile_member", "memberName 多匹配（Big 命中 3 个成员合并输出）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "Big" },
            ExpectedContains: "3 个匹配", MustNotContain: "at System"),
        // 多匹配合并输出的各成员体前应有 #MEMBER JSON 分隔行（同参数，经管道缓存命中前序结果）
        new ToolCallCase("decompile_member", "多匹配分隔头（#MEMBER JSON）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "Big" },
            ExpectedContains: "#MEMBER", MustNotContain: "at System"),
        // 默认排除属性/事件访问器：Members 的 get_Count 被排除后无名称含 "get" 的成员，返回未找到提示
        new ToolCallCase("decompile_member", "访问器排除（get_Count 不参与匹配）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.MembersTypeName, ["memberName"] = "get" },
            ExpectedContains: "未找到名称包含", MustNotContain: "at System", ExpectSuccess: false),
        // 拼错成员名：无匹配时返回相近成员名提示（BigMethd 编辑距离 1 → BigMethod）
        new ToolCallCase("decompile_member", "相近名提示（BigMethd → BigMethod）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "BigMethd" },
            ExpectedContains: "相近成员", MustNotContain: "at System", ExpectSuccess: false),
        // 大小写不敏感：bigmethod 应命中 BigMethod
        new ToolCallCase("decompile_member", "memberName 大小写不敏感",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "bigmethod" },
            ExpectedContains: "1\t", MustNotContain: "at System"),
        // 无匹配成员应返回中文提示而非异常堆栈
        new ToolCallCase("decompile_member", "无匹配成员（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName, ["memberName"] = "NoSuchMethod" },
            ExpectedContains: "未找到名称包含", MustNotContain: "at System", ExpectSuccess: false),
        // 类型不存在应返回中文提示
        new ToolCallCase("decompile_member", "类型不存在（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "No.Such.Type", ["memberName"] = "X" },
            ExpectedContains: "未找到类型", MustNotContain: "at System", ExpectSuccess: false),
        // 缺 memberName 应返回中文校验提示
        new ToolCallCase("decompile_member", "缺 memberName（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = TestDataHelper.TypeName },
            ExpectedContains: "请指定 memberName", MustNotContain: "at System", ExpectSuccess: false),
        // 匹配数超上限（>20）：ManyOverloads 有 21 个 Do 重载，仅返回签名清单（每行 签名 [token]）不反编译
        new ToolCallCase("decompile_member", "匹配数超限仅返回签名清单（ManyOverloads 21 个 Do）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.ManyOverloads", ["memberName"] = "Do" },
            ExpectedContains: "超过上限", MustNotContain: "at System"),
        // 超限签名清单同样支持 lines 分页：lines="1-2" 应返回前两行清单而非全部 21 行
        new ToolCallCase("decompile_member", "超限签名清单 lines=\"1-2\" 分页",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.ManyOverloads", ["memberName"] = "Do", ["lines"] = "1-2" },
            ExpectedContains: "当前输出: 1-2", MustNotContain: "at System"),
        // token 参数：按元数据 token 直接反编译单个成员（ManyOverloads 第 1 个 Do 重载），验证超限清单 token 可闭环消费
        new ToolCallCase("decompile_member", "token 参数直接反编译单个成员",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = FirstDoToken(dll) },
            ExpectedContains: "按 token 反编译", MustNotContain: "at System"),
        // 非法 token 应返回中文提示
        new ToolCallCase("decompile_member", "非法 token（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["token"] = "0xZZZZ" },
            ExpectedContains: "不是有效的元数据 token", MustNotContain: "at System", ExpectSuccess: false),
        // typeName 带 list_types 行首类别前缀（class Foo.Bar）可直接复制使用
        new ToolCallCase("decompile_member", "typeName 带类别前缀（class BigClass）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "class " + TestDataHelper.TypeName, ["memberName"] = "BigMethod" },
            ExpectedContains: "已截断", MustNotContain: "at System"),
    };

    /// <summary>
    /// 取 TestSamples 中 ManyOverloads 第一个 Do 重载的元数据 token（与超限清单 token 同源，供 token 参数用例）。
    /// </summary>
    private static string FirstDoToken(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != "ManyOverloads") continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) == "Do")
                {
                    return $"0x{MetadataTokens.GetToken(methodHandle):x8}";
                }
            }
        }
        throw new InvalidOperationException("TestSamples 未找到 ManyOverloads.Do 成员");
    }
}