using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Client;

/// <summary>
/// field_access 工具的全部端到端验证场景：typeName+fieldName 定位字段 / fieldToken 定位 / 跨程序集多匹配 #MEMBER 清单 / 参数校验。
/// </summary>
public static class FieldAccessCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll) => new[]
    {
        // FieldHolder.Data 被 FieldUser.Read 读取（ldfld）、FieldWriter.Write 写入（stfld）
        new ToolCallCase("field_access", "typeName+fieldName 追踪 FieldHolder.Data 读写点",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataHelper.SamplesNamespace}.FieldHolder", ["fieldName"] = "Data" },
            ExpectedContains: $"{TestDataHelper.SamplesNamespace}.FieldWriter::", MustNotContain: "at System"),
        // 三段标题齐全
        new ToolCallCase("field_access", "输出读取/写入/取地址三段标题",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataHelper.SamplesNamespace}.FieldHolder", ["fieldName"] = "Data" },
            ExpectedContains: "取地址的成员:", MustNotContain: "at System"),
        // fieldToken 按 token 定位字段（0x04 开头）
        new ToolCallCase("field_access", "fieldToken 按 token 定位字段",
            new Dictionary<string, object?> { ["assembly"] = dll, ["fieldToken"] = FieldTokenOf(dll) },
            ExpectedContains: $"{TestDataHelper.SamplesNamespace}.FieldUser::", MustNotContain: "at System"),
        // 跨程序集字段名多匹配：返回 #MEMBER 签名清单提示用 fieldToken
        new ToolCallCase("field_access", "跨程序集字段名多匹配返回 #MEMBER 清单",
            new Dictionary<string, object?> { ["assembly"] = dll, ["fieldName"] = "D" },
            ExpectedContains: "#MEMBER", MustNotContain: "at System"),
        // 缺 fieldName 应返回中文提示而非异常堆栈
        new ToolCallCase("field_access", "缺 fieldName（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "请指定 fieldName", MustNotContain: "at System", ExpectSuccess: false),
        // 字段名未匹配应返回中文提示而非异常堆栈
        new ToolCallCase("field_access", "字段名未匹配（应返回提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataHelper.SamplesNamespace}.FieldHolder", ["fieldName"] = "NoSuchField" },
            ExpectedContains: "未找到字段名包含", MustNotContain: "at System", ExpectSuccess: false),
    };

    /// <summary>
    /// 取测试程序集 FieldHolder.Data 字段的元数据 token（0x04 开头），供 fieldToken 场景使用。
    /// </summary>
    private static string FieldTokenOf(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != "FieldHolder") continue;
            foreach (var fieldHandle in type.GetFields())
            {
                if (reader.GetString(reader.GetFieldDefinition(fieldHandle).Name) == "Data")
                {
                    return $"0x{MetadataTokens.GetToken(fieldHandle):x8}";
                }
            }
        }
        throw new InvalidOperationException("TestSamples 未找到 FieldHolder.Data 字段");
    }
}