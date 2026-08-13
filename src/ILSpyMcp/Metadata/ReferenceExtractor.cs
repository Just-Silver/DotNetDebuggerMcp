using System.Collections.Immutable;
using System.Reflection.Metadata;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 纯元数据「成员签名内部类型引用」：解码类型的全部成员签名（方法参数+返回、字段、属性、事件），收集签名中出现的本程序集
/// TypeDefinition 集合（泛型实例化归约到定义，跨程序集 TypeReference 仅当 WithExternal 路径开启时收集），供 dependencies
/// 工具使用。只关注成员签名，不含基类/接口/特性——继承关系由 hierarchy 组件覆盖。
/// </summary>
public static class ReferenceExtractor
{
    /// <summary>
    /// 收集成员签名引用的程序集内部类型全名，按元数据枚举序去重排序。泛型实例化归约到定义（List&lt;Derived&gt; 收集 Derived
    /// 不收集 List）；签名中的泛型参数（T）不是类型，不收集；跨程序集类型不收集。只读成员签名，不改写/加载程序集。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待扫描的类型定义。</param>
    /// <returns>内部类型全名列表；无内部引用时为空列表。</returns>
    public static IReadOnlyList<string> ExtractMemberSignatureReferences(MetadataReader reader, TypeDefinition type)
        => ExtractMemberSignatureReferencesWithExternal(reader, type).Internal;

    /// <summary>
    /// 收集成员签名引用的程序集内部与跨程序集外部类型。内部集合语义与 <see cref="ExtractMemberSignatureReferences"/> 完全一致；
    /// 外部集合为成员签名（含事件类型）中出现的跨程序集 TypeReference，条目格式 <c>全名 [程序集名]</c>（如
    /// <c>System.Console [System.Console]</c>），程序集名取元数据 AssemblyReference.Name（纯元数据，不加载外部程序集），
    /// 未知归属输出 <c>全名 [&lt;外部&gt;]</c>，按全名排序去重。泛型实例化归约到定义：List&lt;Derived&gt; 中外部泛型
    /// 参数（List）不收集、内部实参（Derived）收集。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待扫描的类型定义。</param>
    /// <returns>内部与外部类型集合；对应无引用时为空列表。</returns>
    public static (IReadOnlyList<string> Internal, IReadOnlyList<string> External) ExtractMemberSignatureReferencesWithExternal(
        MetadataReader reader, TypeDefinition type)
    {
        var collected = new HashSet<TypeDefinitionHandle>();
        var external = new HashSet<string>(StringComparer.Ordinal);
        var provider = new Provider(reader, collected, external);
        var typeParams = GetGenericParameterNames(reader, type.GetGenericParameters());

        foreach (var handle in type.GetFields())
        {
            reader.GetFieldDefinition(handle).DecodeSignature(provider, new GenericContext(typeParams, Array.Empty<string>()));
        }

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            var methodParams = GetGenericParameterNames(reader, method.GetGenericParameters());
            method.DecodeSignature(provider, new GenericContext(typeParams, methodParams));
        }

        foreach (var handle in type.GetProperties())
        {
            reader.GetPropertyDefinition(handle).DecodeSignature(provider, new GenericContext(typeParams, Array.Empty<string>()));
        }

        foreach (var handle in type.GetEvents())
        {
            var evt = reader.GetEventDefinition(handle);
            CollectEventType(reader, evt.Type, provider, new GenericContext(typeParams, Array.Empty<string>()));
        }

        var result = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            if (collected.Contains(handle)) result.Add(MetadataNaming.FullName(reader, reader.GetTypeDefinition(handle)));
        }
        var externalResult = external.ToList();
        externalResult.Sort(StringComparer.Ordinal);
        return (result, externalResult);
    }

    /// <summary>
    /// 解析事件类型句柄：TypeDefinition 直接收集；TypeSpecification（泛型实例化/数组等）走签名解码触发回调；
    /// TypeReference 是外部类型，走 Provider 收集外部集合（事件的外部类型签名依赖）——否则事件类型为跨程序集时整体漏掉。
    /// </summary>
    private static void CollectEventType(MetadataReader reader, EntityHandle handle, Provider provider, GenericContext ctx)
    {
        switch (handle.Kind)
        {
            case HandleKind.TypeDefinition:
                provider.GetTypeFromDefinition(reader, (TypeDefinitionHandle)handle, 0);
                break;
            case HandleKind.TypeSpecification:
                reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(provider, ctx);
                break;
            case HandleKind.TypeReference:
                provider.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0);
                break;
        }
    }

    /// <summary>
    /// 读取泛型参数句柄集合中每个参数的元数据名（如 T），供签名解码按索引取名字。
    /// </summary>
    private static string[] GetGenericParameterNames(MetadataReader reader, GenericParameterHandleCollection handles)
    {
        var names = new string[handles.Count];
        for (var i = 0; i < handles.Count; i++)
            names[i] = reader.GetString(reader.GetGenericParameter(handles[i]).Name);
        return names;
    }

    /// <summary>
    /// 泛型参数上下文：类型级与方法级泛型参数名数组，签名解码时按索引取名字（与 SignatureRenderer 一致）。
    /// </summary>
    private readonly record struct GenericContext(string[] TypeParameters, string[] MethodParameters);

    /// <summary>
    /// 签名解码器：只为触发类型解析回调、收集程序集内部 TypeDefinition；返回字符串只是占位，不参与任何展示。
    /// GetTypeFromDefinition 负责收集（本程序集类型），GetTypeFromReference 收集跨程序集外部类型（格式 全名 [程序集名]，
    /// 仅在 WithExternal 路径启用，缺省 API 传 null 保持不收集），TypeSpecification 递归解码，
    /// 泛型实例化/数组等组合结构中的元素类型由上层回调已逐个收集，此处返回占位即可。
    /// </summary>
    private sealed class Provider : ISignatureTypeProvider<string, GenericContext>
    {
        private readonly MetadataReader _reader;
        private readonly HashSet<TypeDefinitionHandle> _collected;
        private readonly HashSet<string>? _external;

        public Provider(MetadataReader reader, HashSet<TypeDefinitionHandle> collected)
            : this(reader, collected, null)
        {
        }

        public Provider(MetadataReader reader, HashSet<TypeDefinitionHandle> collected, HashSet<string>? external)
        {
            _reader = reader;
            _collected = collected;
            _external = external;
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            _collected.Add(handle);
            return MetadataNaming.FullName(reader, reader.GetTypeDefinition(handle));
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            if (_external is not null)
            {
                var name = MetadataNaming.TypeReferenceFullName(reader, handle);
                if (name is not null)
                {
                    _external.Add(MetadataNaming.FormatExternal(name, MetadataNaming.TypeReferenceScope(reader, handle).AssemblyName));
                }
            }
            return ""; // 占位，外部类型不参与内部收集
        }

        public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
            => ""; // 元素类型已由上层解码回调收集

        public string GetGenericTypeParameter(GenericContext genericContext, int index)
            => index >= 0 && index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : $"T{index}";

        public string GetGenericMethodParameter(GenericContext genericContext, int index)
            => index >= 0 && index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : $"T{index}";

        public string GetArrayType(string elementType, ArrayShape shape) => "";
        public string GetSZArrayType(string elementType) => "";
        public string GetByReferenceType(string elementType) => "";
        public string GetPointerType(string elementType) => "";
        public string GetPinnedType(string elementType) => "";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => "";

        public string GetFunctionPointerType(MethodSignature<string> signature)
            => ""; // 参数/返回类型已由上层解码回调收集
    }
}
