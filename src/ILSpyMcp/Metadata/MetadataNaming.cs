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
    /// 渲染 TypeReference 的全限定名（命名空间.名，嵌套沿 ResolutionScope 递归用 + 连接），与
    /// <see cref="FullName(MetadataReader, TypeDefinition)"/> 的格式一致（供跨程序集外部类型渲染）。
    /// 纯元数据读取，不加载外部程序集。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="handle">待渲染的类型引用句柄。</param>
    /// <returns>如 "System.Console"、"System.Collections.Generic.List`1"；无法解析时返回 null。</returns>
    public static string? TypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var tr = reader.GetTypeReference(handle);
        var name = reader.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            var outer = TypeReferenceFullName(reader, (TypeReferenceHandle)tr.ResolutionScope);
            return outer is null ? null : $"{outer}+{name}";
        }
        var ns = reader.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    /// <summary>
    /// 判定 TypeReference 归属：沿 ResolutionScope 上溯到最外层，最外层为 AssemblyReference 时返回该程序集名
    /// （跨程序集外部类型，纯元数据读取 AssemblyReference.Name，不加载外部程序集）；ModuleDefinition 为本模块
    /// （程序集内部）；ModuleReference 等其余作用域归属未知（外部，程序集名为 null）。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="handle">待判定的类型引用句柄。</param>
    /// <returns>IsInternal 表示本程序集类型；否则 AssemblyName 为归属程序集名（未知时为 null）。</returns>
    public static (bool IsInternal, string? AssemblyName) TypeReferenceScope(MetadataReader reader, TypeReferenceHandle handle)
    {
        var scope = reader.GetTypeReference(handle).ResolutionScope;
        while (true)
        {
            switch (scope.Kind)
            {
                case HandleKind.ModuleDefinition:
                    return (true, null);
                case HandleKind.AssemblyReference:
                    return (false, reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name));
                case HandleKind.TypeReference: // 嵌套类型：沿外层继续上溯
                    scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
                    continue;
                default: // ModuleReference 等其余作用域：外部且归属未知
                    return (false, null);
            }
        }
    }

    /// <summary>
    /// 渲染跨程序集外部类型的归属条目：<c>全名 [程序集名]</c>（如 <c>System.Console [System.Console]</c>）；
    /// 归属未知（如 ModuleReference 作用域）时输出 <c>全名 [&lt;外部&gt;]</c>。供 dependencies/call_graph 的外部段使用。
    /// </summary>
    /// <param name="fullName">类型全名（建议来自 <see cref="TypeReferenceFullName"/>）。</param>
    /// <param name="assemblyName">归属程序集名；未知时传 null。</param>
    /// <returns>如 "System.Console [System.Console]"、"Ns.Type [&lt;外部&gt;]"。</returns>
    public static string FormatExternal(string fullName, string? assemblyName)
        => $"{fullName} [{(assemblyName ?? "<外部>")}]";

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
    /// 在程序集全部非编译器生成类型中查找与输入相近的类型：类型全名或短名（最后一段）与查询名编辑距离 ≤ 2、
    /// 或共享 ≥ 4 字符公共前缀即视为相近。返回全名（可直接复制用于定位），按名序排序取前 max 个。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="input">用户输入的类型名（兼容 list_types 行首类别前缀，匹配前剥离）。</param>
    /// <param name="max">最多返回个数（默认 5）。</param>
    /// <returns>相近类型全名列表，可能为空。</returns>
    public static List<string> FindSimilarTypeNames(MetadataReader reader, string input, int max = 5)
    {
        var query = StripCategoryPrefix(input);
        var result = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, type)) continue;
            var fullName = FullName(reader, type);
            // 全名与短名分别判定：短名命中便于用户只输短名（如 "BigClas"→"BigClass"）也能给出提示
            if (SimilarNameMatcher.IsSimilar(fullName, query)
                || SimilarNameMatcher.IsSimilar(reader.GetString(type.Name), query))
            {
                result.Add(fullName);
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result.Count > max ? result.GetRange(0, max) : result;
    }

    /// <summary>
    /// 组装「未找到类型」提示：有相近类型时输出 <c>未找到类型 X。相近类型：A、B、C</c>（全名、可直接复制用于定位），
    /// 否则保持 <c>未找到类型 X</c> 原文本（提示文本保留原始输入）。供各工具/反编译入口的未找到分支统一使用。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="input">用户输入的类型名。</param>
    /// <returns>未找到提示文本。</returns>
    public static string BuildNotFoundMessage(MetadataReader reader, string input)
    {
        var similar = FindSimilarTypeNames(reader, input);
        return similar.Count > 0 ? $"未找到类型 {input}。相近类型：{string.Join("、", similar)}" : $"未找到类型 {input}";
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
