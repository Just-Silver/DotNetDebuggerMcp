using System.Reflection.Metadata;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 类型全名渲染与定位：统一类型全名格式（命名空间.类型，嵌套用 +，泛型带 arity 如 GenericBox`1），
/// 保证 list_types/signature/hierarchy 输出的名字可直接用于反编译工具与 decompile_member 定位（定位同时接受 + 与 . 两种嵌套分隔符，
/// 并兼容 list_types 行首类别前缀如 "class Foo.Bar"——C# 关键字不可能是合法类型名前缀，直接复制即用）。
/// </summary>
public static class MetadataNaming
{
    /// <summary>
    /// 渲染 TypeDefinition 的全限定名：命名空间.类型，嵌套类型用 + 连接（Outer+Inner），泛型带 arity（GenericBox`1）。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待渲染的类型定义。</param>
    /// <returns>如 "Probe.Outer+Inner"、"Probe.GenericBox`1"。</returns>
    public static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        if (type.IsNested)
        {
            var declaring = reader.GetTypeDefinition(type.GetDeclaringType());
            return $"{FullName(reader, declaring)}+{name}";
        }
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    /// <summary>
    /// 在程序集内按用户输入定位 TypeDefinition；未找到返回 null。
    /// 输入可含两种嵌套分隔符（Probe.Outer+Inner 或 Probe.Outer.Inner）——匹配前将 + 统一归一化为 . 后比较。
    /// 注意：命名空间与嵌套分隔的歧义（A.B.C 是命名空间 A.B 的类型 C，还是命名空间 A 的类型 B 的嵌套 C）取枚举序首个命中。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="input">用户输入的类型全名，如 "Probe.Outer+Inner"，可带 list_types 行首类别前缀（如 "class Probe.Outer"）。</param>
    /// <returns>命中的类型句柄；未找到为 null。</returns>
    public static TypeDefinitionHandle? FindType(MetadataReader reader, string input)
    {
        var normalized = StripCategoryPrefix(input).Replace('+', '.');
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (FullName(reader, type).Replace('+', '.') == normalized) return handle;
        }
        return null;
    }

    /// <summary>
    /// 剥离 list_types 行首类别前缀（"class "/"interface "/"struct "/"delegate "/"enum "，忽略大小写，前缀后需仍有内容）。
    /// C# 关键字不可能作合法类型名前缀，故剥离安全；"interface1"/"structs" 等无空格分隔不受影响。
    /// </summary>
    /// <param name="input">用户输入。</param>
    /// <returns>剥离前缀后的类型名；无前缀时原样返回。</returns>
    private static string StripCategoryPrefix(string input)
    {
        foreach (var prefix in s_categoryPrefixes)
        {
            if (input.Length > prefix.Length && input.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return input[prefix.Length..];
            }
        }
        return input;
    }

    /// <summary>
    /// list_types 行首类别前缀（含尾随空格），与 ListTypesTool 的类别名一致。
    /// </summary>
    private static readonly string[] s_categoryPrefixes = { "class ", "interface ", "struct ", "delegate ", "enum " };
}
