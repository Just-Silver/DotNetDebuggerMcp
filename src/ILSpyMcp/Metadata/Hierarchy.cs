using System.Reflection.Metadata;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 纯元数据「继承/接口关系」：给定类型，输出基类链、实现的接口与程序集内直接继承/实现它的类型，供 hierarchy 工具使用，
/// 不依赖 ilspycmd 安装。基类链/接口的解析句柄可能为 TypeDefinition（同程序集）或 TypeReference（外部），统一按全名比较。
/// </summary>
public static class Hierarchy
{
    /// <summary>
    /// 基类链：从 type 沿 BaseType 上溯到 System.Object（含两端），返回全名列表。顶层类型链以 System.Object 为终点，
    /// 解析不到时（接口 BaseType 为 nil、跨程序集基类不可再上溯）在已解析处停止；畸形程序集基类循环时提前终止。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待查询的类型定义。</param>
    /// <returns>全名列表，第一个元素为 type 自身；接口/无基类类型仅含自身。</returns>
    public static IReadOnlyList<string> GetBaseChain(MetadataReader reader, TypeDefinition type)
    {
        var chain = new List<string>();
        var seen = new HashSet<string>();
        var current = type;
        while (true)
        {
            var name = MetadataNaming.FullName(reader, current);
            if (!seen.Add(name)) break; // 畸形程序集防循环
            chain.Add(name);

            var baseHandle = current.BaseType;
            if (baseHandle.IsNil) break;
            // 基类在程序集内（TypeDef）继续上溯；TypeReference（如 System.Object）即链终点，加入后停止
            if (baseHandle.Kind != HandleKind.TypeDefinition)
            {
                var baseName = ResolveType(reader, baseHandle);
                if (baseName is not null) chain.Add(baseName);
                break;
            }
            current = reader.GetTypeDefinition((TypeDefinitionHandle)baseHandle);
        }
        return chain;
    }

    /// <summary>
    /// 类型实现的接口全名列表（InterfaceImplementations 表，含显式实现）。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待查询的类型定义。</param>
    /// <returns>接口全名列表，按元数据枚举序；无接口时为空列表。</returns>
    public static IReadOnlyList<string> GetInterfaces(MetadataReader reader, TypeDefinition type)
    {
        var result = new List<string>();
        foreach (var handle in type.GetInterfaceImplementations())
        {
            var impl = reader.GetInterfaceImplementation(handle);
            var resolved = ResolveType(reader, impl.Interface);
            if (resolved is not null) result.Add(resolved);
        }
        return result;
    }

    /// <summary>
    /// 程序集内「直接继承 type 或实现其接口」的类型全名列表（跳过编译器生成类型与 type 自身），按元数据枚举序。
    /// 注意语义：仅收集直接基类/直接接口等于 type 的类型；基类链上更深的上游不在此列（调用方可用 GetBaseChain 判定）。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待查询的类型定义。</param>
    /// <param name="typeFullName">type 的规范全名（<see cref="MetadataNaming.FullName"/> 输出），用于基类/接口全名比较。</param>
    /// <returns>后代类型全名列表；无匹配时为空列表。</returns>
    public static IReadOnlyList<string> GetDescendants(MetadataReader reader, TypeDefinition type, string typeFullName)
    {
        var result = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var candidate = reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, candidate)) continue;
            var candidateName = MetadataNaming.FullName(reader, candidate);
            if (candidateName == typeFullName) continue;

            if (ResolveType(reader, candidate.BaseType) == typeFullName)
            {
                result.Add(candidateName);
                continue;
            }
            foreach (var implHandle in candidate.GetInterfaceImplementations())
            {
                if (ResolveType(reader, reader.GetInterfaceImplementation(implHandle).Interface) == typeFullName)
                {
                    result.Add(candidateName);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 解析类型句柄为全名：TypeDefinition 用 <see cref="MetadataNaming.FullName"/>；TypeReference 取 命名空间.名
    /// （嵌套沿 ResolutionScope 递归拼接，用 + 分隔）；其余（TypeSpecification 等）返回 null。
    /// </summary>
    private static string? ResolveType(MetadataReader reader, EntityHandle handle)
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
