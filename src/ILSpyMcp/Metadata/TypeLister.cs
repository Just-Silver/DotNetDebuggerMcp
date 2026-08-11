using System.Reflection;
using System.Reflection.Metadata;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 纯元数据「类型列表」：按类别组合枚举程序集的实体类型（跳过编译器生成类型），供 list_types 工具使用。
/// 类别判定规则：enum/delegate 按基类全名判定，interface 按元数据标志判定，struct 为基类 System.ValueType 且非 interface（enum 已先排除），其余为 class。
/// </summary>
public static class TypeLister
{
    /// <summary>
    /// 按类别组合枚举程序集的实体类型（跳过编译器生成类型），返回 (类别字母, 全名) 列表。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="categories">实体类别组合（调用方已校验），c/i/s/d/e 可组合：c=class, i=interface, s=struct, d=delegate, e=enum。</param>
    /// <returns>(类别字母, 全名) 列表，按元数据枚举序；无匹配时为空列表。</returns>
    public static IReadOnlyList<(char Category, string FullName)> ListTypes(MetadataReader reader, string categories)
    {
        var result = new List<(char Category, string FullName)>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, type)) continue;
            var category = Classify(reader, type);
            if (categories.Contains(category)) result.Add((category, MetadataNaming.FullName(reader, type)));
        }
        return result;
    }

    /// <summary>
    /// 判定单个类型定义的实体类别字母。
    /// </summary>
    private static char Classify(MetadataReader reader, TypeDefinition type)
    {
        var baseType = ResolveBaseType(reader, type.BaseType);
        if (baseType == "System.Enum") return 'e';
        if (baseType == "System.MulticastDelegate") return 'd';
        if ((type.Attributes & TypeAttributes.Interface) != 0) return 'i';
        if (baseType == "System.ValueType") return 's';
        return 'c';
    }

    /// <summary>
    /// 解析类型基类句柄为全名：TypeDefinition 用 <see cref="MetadataNaming.FullName"/>；TypeReference 取 命名空间.名
    /// （嵌套沿 ResolutionScope 递归拼接，用 + 分隔）；其余（TypeSpecification 等）返回 null。
    /// </summary>
    private static string? ResolveBaseType(MetadataReader reader, EntityHandle handle)
    {
        if (handle.IsNil) return null;
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => MetadataNaming.FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => ResolveTypeReference(reader, (TypeReferenceHandle)handle),
            _ => null,
        };
    }

    /// <summary>
    /// 递归解析 TypeReference 为 命名空间.名；嵌套类型沿 ResolutionScope 递归拼接，与 <see cref="MetadataNaming.FullName"/> 的 + 分隔保持一致。
    /// </summary>
    private static string ResolveTypeReference(MetadataReader reader, TypeReferenceHandle handle)
    {
        var tr = reader.GetTypeReference(handle);
        var name = reader.GetString(tr.Name);
        if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{ResolveTypeReference(reader, (TypeReferenceHandle)tr.ResolutionScope)}+{name}";
        }
        var ns = reader.GetString(tr.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }
}
