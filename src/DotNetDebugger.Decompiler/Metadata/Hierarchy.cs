using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace DotNetDebugger.Decompiler.Metadata;

/// <summary>
/// 纯元数据「继承/接口关系」：给定类型，输出基类链、实现的接口与程序集内直接继承/实现它的类型，供 hierarchy 工具使用。 基类链/接口的解析句柄可能为
/// TypeDefinition（同程序集）或 TypeReference（外部），统一按全名比较。
/// </summary>
public static class Hierarchy
{
    /// <summary>
    /// 基类链：从 type 沿 BaseType 上溯到 System.Object（含两端），返回全名列表。泛型基类（TypeSpecification 实例化， 如
    /// Derived&lt;T&gt; : Base&lt;T&gt;）渲染为带泛型参数的全名，且定义在程序集内时继续沿其上溯； TypeReference（外部类型，如 System.Object）为链终点；畸形程序集基类循环时提前终止。
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
            if (baseHandle.Kind == HandleKind.TypeDefinition)
            {
                // 基类在程序集内（TypeDef）继续上溯
                current = reader.GetTypeDefinition((TypeDefinitionHandle)baseHandle);
                continue;
            }
            // TypeReference（外部类型）与 TypeSpecification（泛型基类实例化）均按全名加入； 泛型基类定义在程序集内时继续上溯其基类链，否则停止
            var (baseName, baseDef) = ResolveType(reader, baseHandle, GetGenericParameterNames(reader, current.GetGenericParameters()));
            if (baseName is not null) chain.Add(baseName);
            if (baseDef is { } defHandle)
            {
                current = reader.GetTypeDefinition(defHandle);
                continue;
            }
            break;
        }
        return chain;
    }

    /// <summary>
    /// 类型实现的接口全名列表（InterfaceImplementations 表，含显式实现）。泛型接口实例化按带泛型参数的全名渲染。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待查询的类型定义。</param>
    /// <returns>接口全名列表，按元数据枚举序；无接口时为空列表。</returns>
    public static IReadOnlyList<string> GetInterfaces(MetadataReader reader, TypeDefinition type)
    {
        var result = new List<string>();
        var typeParams = GetGenericParameterNames(reader, type.GetGenericParameters());
        foreach (var handle in type.GetInterfaceImplementations())
        {
            var impl = reader.GetInterfaceImplementation(handle);
            var (resolved, _) = ResolveType(reader, impl.Interface, typeParams);
            if (resolved is not null) result.Add(resolved);
        }
        return result;
    }

    /// <summary>
    /// 程序集内「直接继承 type 或实现其接口」的类型全名列表（跳过编译器生成类型与 type 自身），按元数据枚举序。 注意语义：仅收集直接基类/直接接口等于 type
    /// 的类型；基类链上更深的上游不在此列（调用方可用 GetBaseChain 判定）。 泛型基类/接口（TypeSpecification 实例化）按底层泛型定义类型比较，因此
    /// QueryableProvider`2 : QueryableProvider`1 能正确归入 QueryableProvider`1 的后代。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待查询的类型定义。</param>
    /// <param name="typeFullName">type 的规范全名（ <see cref="MetadataNaming.FullName"/> 输出），用于基类/接口全名比较。</param>
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

            var candidateParams = GetGenericParameterNames(reader, candidate.GetGenericParameters());
            var (baseName, baseDef) = ResolveType(reader, candidate.BaseType, candidateParams);
            var baseDefName = baseDef is { } bd ? MetadataNaming.FullName(reader, reader.GetTypeDefinition(bd)) : null;
            if (baseName == typeFullName || baseDefName == typeFullName)
            {
                result.Add(candidateName);
                continue;
            }
            foreach (var implHandle in candidate.GetInterfaceImplementations())
            {
                var (ifaceName, ifaceDef) = ResolveType(reader, reader.GetInterfaceImplementation(implHandle).Interface, candidateParams);
                var ifaceDefName = ifaceDef is { } id ? MetadataNaming.FullName(reader, reader.GetTypeDefinition(id)) : null;
                if (ifaceName == typeFullName || ifaceDefName == typeFullName)
                {
                    result.Add(candidateName);
                    break;
                }
            }
        }
        return result;
    }

    /// <summary>
    /// 程序集内「直接或间接继承 type 或实现其接口」的类型全名列表（跳过编译器生成类型与 type 自身），按元数据枚举序。 与 <see cref="GetDescendants"/>
    /// 的差异：不止收集直接子类/直接接口实现者，还沿继承/实现链继续下钻， 收集所有后代（如接口的全部（间接）实现者、基类的所有子孙），一次调用即返回完整后代集合， 供 hierarchy
    /// includeIndirect 使用，免 agent 递归多次调用。 实现：一次遍历构建「全名 → 直接父类/接口全名列表」邻接表
    /// （邻接边同时收录显示名与底层泛型定义全名，保证泛型实例化比较与 <see cref="GetDescendants"/> 一致）， 从 type 出发 BFS 到收敛（HashSet 去重）。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待查询的类型定义。</param>
    /// <param name="typeFullName">type 的规范全名（ <see cref="MetadataNaming.FullName"/> 输出），用于基类/接口全名比较。</param>
    /// <returns>全部（直接+间接）后代类型全名列表；无匹配时为空列表。</returns>
    public static IReadOnlyList<string> GetDescendantsIncludingIndirect(MetadataReader reader, TypeDefinition type, string typeFullName)
    {
        // 一次遍历构建名称索引与邻接表：邻接值同时收录「显示名」（泛型实例化如 GenericBase<int>）与 「底层泛型定义全名」（如 GenericBase`1），使泛型基类/接口实例化能沿定义全名正确连边。
        var adjacency = new Dictionary<string, List<string>>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var candidate = reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, candidate)) continue;
            var candidateName = MetadataNaming.FullName(reader, candidate);
            if (candidateName == typeFullName) continue;

            var parents = new List<string>();
            var candidateParams = GetGenericParameterNames(reader, candidate.GetGenericParameters());
            var (baseName, baseDef) = ResolveType(reader, candidate.BaseType, candidateParams);
            if (baseName is not null) parents.Add(baseName);
            if (baseDef is { } bd) parents.Add(MetadataNaming.FullName(reader, reader.GetTypeDefinition(bd)));
            foreach (var implHandle in candidate.GetInterfaceImplementations())
            {
                var (ifaceName, ifaceDef) = ResolveType(reader, reader.GetInterfaceImplementation(implHandle).Interface, candidateParams);
                if (ifaceName is not null) parents.Add(ifaceName);
                if (ifaceDef is { } id) parents.Add(MetadataNaming.FullName(reader, reader.GetTypeDefinition(id)));
            }
            adjacency[candidateName] = parents;
        }

        // BFS 到收敛：从 typeFullName 出发，逐层把「直接父类/接口全名命中已发现集合」的类型收入结果。
        var discovered = new HashSet<string> { typeFullName };
        var queue = new Queue<string>();
        queue.Enqueue(typeFullName);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var (candidateName, parents) in adjacency)
            {
                if (discovered.Contains(candidateName)) continue;
                if (!parents.Contains(current)) continue;
                discovered.Add(candidateName);
                queue.Enqueue(candidateName);
            }
        }
        discovered.Remove(typeFullName);

        // 按元数据枚举序返回（与 GetDescendants 一致）
        var result = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var candidate = reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, candidate)) continue;
            var candidateName = MetadataNaming.FullName(reader, candidate);
            if (discovered.Contains(candidateName)) result.Add(candidateName);
        }
        return result;
    }

    /// <summary>
    /// 解析类型句柄为全名与底层类型定义句柄：TypeDefinition 返回自身；TypeReference 取 命名空间.名 （嵌套沿 ResolutionScope 递归拼接，用 +
    /// 分隔），底层定义不可得；TypeSpecification（泛型实例化等）解码签名取全名， 若泛型定义是程序集内类型则一并返回其句柄（供基类链继续上溯、后代比较）；其余返回
    /// (null, null)。
    /// </summary>
    private static (string? Name, TypeDefinitionHandle? Definition) ResolveType(MetadataReader reader, EntityHandle handle, string[] typeParams)
    {
        if (handle.IsNil) return (null, null);
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => (MetadataNaming.FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)), (TypeDefinitionHandle)handle),
            HandleKind.TypeReference => (ResolveTypeReference(reader, (TypeReferenceHandle)handle), null),
            HandleKind.TypeSpecification => ResolveTypeSpecification(reader, (TypeSpecificationHandle)handle, typeParams),
            _ => (null, null),
        };
    }

    /// <summary>
    /// 解码 TypeSpecification 签名为类型全名；泛型实例化签名解码期间记录其底层泛型定义句柄（若为程序集内类型）。
    /// </summary>
    private static (string? Name, TypeDefinitionHandle? Definition) ResolveTypeSpecification(MetadataReader reader, TypeSpecificationHandle handle, string[] typeParams)
    {
        try
        {
            var provider = new TypeSignatureProvider(reader);
            var name = reader.GetTypeSpecification(handle).DecodeSignature(provider, typeParams);
            return (name, provider.LastDefinition);
        }
        catch (BadImageFormatException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// 读取类型级泛型参数名数组（如 T），供 TypeSpecification 签名解码按索引渲染参数名。
    /// </summary>
    private static string[] GetGenericParameterNames(MetadataReader reader, GenericParameterHandleCollection handles)
    {
        var names = new string[handles.Count];
        for (var i = 0; i < handles.Count; i++)
            names[i] = reader.GetString(reader.GetGenericParameter(handles[i]).Name);
        return names;
    }

    /// <summary>
    /// 递归解析 TypeReference 为 命名空间.名；嵌套类型沿 ResolutionScope 递归拼接，与 <see
    /// cref="MetadataNaming.FullName"/> 的 + 分隔保持一致。
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

    /// <summary>
    /// 类型签名解码器：把 TypeSpecification 的签名编码渲染为全名字符串，并在遇到程序集内泛型定义（TypeDefinition）时 记录其句柄到 <see
    /// cref="LastDefinition"/>，供基类链上溯与后代比较。只关注泛型实例化等类型编码， 方法级泛型参数按占位渲染（基类/接口签名不涉及）。
    /// </summary>
    private sealed class TypeSignatureProvider : ISignatureTypeProvider<string, string[]>
    {
        private readonly MetadataReader _reader;

        public TypeSignatureProvider(MetadataReader reader) => _reader = reader;

        /// <summary>
        /// 签名解码中最近一次遇到的程序集内类型定义句柄（泛型实例化的底层定义）。
        /// </summary>
        public TypeDefinitionHandle? LastDefinition { get; private set; }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode)
            => typeCode switch
            {
                PrimitiveTypeCode.Void => "void",
                PrimitiveTypeCode.Boolean => "bool",
                PrimitiveTypeCode.Byte => "byte",
                PrimitiveTypeCode.SByte => "sbyte",
                PrimitiveTypeCode.Char => "char",
                PrimitiveTypeCode.Int16 => "short",
                PrimitiveTypeCode.UInt16 => "ushort",
                PrimitiveTypeCode.Int32 => "int",
                PrimitiveTypeCode.UInt32 => "uint",
                PrimitiveTypeCode.Int64 => "long",
                PrimitiveTypeCode.UInt64 => "ulong",
                PrimitiveTypeCode.Single => "float",
                PrimitiveTypeCode.Double => "double",
                PrimitiveTypeCode.String => "string",
                PrimitiveTypeCode.Object => "object",
                _ => $"<{typeCode}>",
            };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            LastDefinition = handle;
            return MetadataNaming.FullName(reader, reader.GetTypeDefinition(handle));
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
            => ResolveTypeReference(reader, handle);

        public string GetTypeFromSpecification(MetadataReader reader, string[] genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            // genericType 形如 ILSpyMcp.Samples.GenericBase`1，去掉尾部 arity 后接 <参数列表>
            var backtick = genericType.IndexOf('`');
            var baseName = backtick >= 0 ? genericType[..backtick] : genericType;
            return $"{baseName}<{string.Join(", ", typeArguments)}>";
        }

        public string GetGenericTypeParameter(string[] genericContext, int index)
            => index >= 0 && index < genericContext.Length ? genericContext[index] : $"T{index}";

        public string GetGenericMethodParameter(string[] genericContext, int index) => $"M{index}";

        public string GetArrayType(string elementType, ArrayShape shape) => $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        public string GetByReferenceType(string elementType) => $"ref {elementType}";

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*";
    }
}