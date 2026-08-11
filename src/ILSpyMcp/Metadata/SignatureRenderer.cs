using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 类型成员一行签名渲染：给定 MetadataReader + TypeDefinition，输出该类型全部成员（字段/方法/属性/事件）
/// 每成员一行的 C# 风格签名，供 ilspy_signature 工具做 API 地图。纯元数据解码（SignatureDecoder），
/// 不加载程序集、不反编译 IL。
/// </summary>
public static class SignatureRenderer
{
    /// <summary>
    /// 渲染指定类型全部成员的一行签名（每成员一行，API 地图）。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待渲染的类型定义。</param>
    /// <returns>成员签名行列表，顺序为字段、方法、属性、事件；属性/事件的访问器方法不单独出现。</returns>
    public static IReadOnlyList<string> RenderTypeSignatures(MetadataReader reader, TypeDefinition type)
    {
        var provider = new Provider(reader);
        var results = new List<string>();

        // 类型级泛型参数名（如 GenericBox`1 的 T），成员签名解码按索引取名字；构造函数用「类型名(+泛型参数)」代替 .ctor
        var typeParams = GetGenericParameterNames(reader, type.GetGenericParameters());
        var ctorDisplayName = BuildCtorDisplayName(reader, type, typeParams);

        foreach (var handle in type.GetFields())
        {
            var field = reader.GetFieldDefinition(handle);
            // 自动属性/事件的 backing field（名含 '<'，如 <Count>k__BackingField）是编译器生成物，API 地图不展示
            if (reader.GetString(field.Name).Contains('<')) continue;
            results.Add(RenderField(reader, field, provider, typeParams));
        }

        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            if (IsAccessorName(reader.GetString(method.Name))) continue;
            results.Add(RenderMethod(reader, method, provider, typeParams, ctorDisplayName));
        }

        foreach (var handle in type.GetProperties())
        {
            var property = reader.GetPropertyDefinition(handle);
            results.Add(RenderProperty(reader, property, provider, typeParams));
        }

        foreach (var handle in type.GetEvents())
        {
            var evt = reader.GetEventDefinition(handle);
            results.Add(RenderEvent(reader, evt, provider, new GenericContext(typeParams, Array.Empty<string>())));
        }

        return results;
    }

    /// <summary>
    /// 渲染单个方法成员的一行签名（供 decompile_member 超限清单等场景）。不做访问器过滤——调用方传的是明确要渲染的成员，
    /// 属性/事件的访问器（get_/set_/add_/remove_）按原名渲染。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">成员所属的类型定义。</param>
    /// <param name="method">待渲染的方法定义。</param>
    /// <returns>该成员的一行签名，如 "public static void BigMethod(int seed);"。</returns>
    public static string RenderMemberSignature(MetadataReader reader, TypeDefinition type, MethodDefinition method)
    {
        // 与 RenderTypeSignatures 共用 typeParams/ctorDisplayName 构造：构造函数展示名依赖类型级泛型参数
        var typeParams = GetGenericParameterNames(reader, type.GetGenericParameters());
        var ctorDisplayName = BuildCtorDisplayName(reader, type, typeParams);
        return RenderMethod(reader, method, new Provider(reader), typeParams, ctorDisplayName);
    }

    /// <summary>
    /// 构造构造函数展示名：类型名（+泛型参数列表），供 .ctor/.cctor 渲染代替元数据名。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">所属类型定义。</param>
    /// <param name="typeParams">类型级泛型参数名数组。</param>
    /// <returns>如 "GenericBox&lt;T&gt;"。</returns>
    private static string BuildCtorDisplayName(MetadataReader reader, TypeDefinition type, string[] typeParams)
        => reader.GetString(type.Name) + (typeParams.Length > 0 ? $"<{string.Join(", ", typeParams)}>" : "");

    /// <summary>
    /// 渲染字段签名：访问级别 + [static/readonly/const] + 类型 + 名字。
    /// </summary>
    private static string RenderField(MetadataReader reader, FieldDefinition field, Provider provider, string[] typeParams)
    {
        var type = field.DecodeSignature(provider, new GenericContext(typeParams, Array.Empty<string>()));
        var attrs = field.Attributes;
        var mods = new List<string>();
        if ((attrs & FieldAttributes.Literal) != 0) mods.Add("const"); // const 隐含 static，不重复输出
        else
        {
            if ((attrs & FieldAttributes.Static) != 0) mods.Add("static");
            if ((attrs & FieldAttributes.InitOnly) != 0) mods.Add("readonly");
        }
        var prefix = mods.Count == 0 ? "" : string.Join(' ', mods) + " ";
        return $"{AccessLevel(attrs & FieldAttributes.FieldAccessMask)} {prefix}{type} {reader.GetString(field.Name)};";
    }

    /// <summary>
    /// 渲染方法签名：访问级别 + [static/abstract/virtual/override/sealed] + 返回类型 + 名字(+泛型参数) + 参数列表。
    /// 构造函数/静态构造函数用类型名代替 .ctor/.cctor（无返回类型）。
    /// </summary>
    private static string RenderMethod(MetadataReader reader, MethodDefinition method, Provider provider, string[] typeParams, string ctorDisplayName)
    {
        var methodParams = GetGenericParameterNames(reader, method.GetGenericParameters());
        var sig = method.DecodeSignature(provider, new GenericContext(typeParams, methodParams));
        var name = reader.GetString(method.Name);

        var isCtor = name is ".ctor" or ".cctor";
        var displayName = isCtor ? ctorDisplayName : name;
        var genericPart = !isCtor && methodParams.Length > 0 ? $"<{string.Join(", ", methodParams)}>" : "";

        var attrs = method.Attributes;
        var mods = new List<string>();
        if ((attrs & MethodAttributes.Static) != 0) mods.Add("static");
        else if ((attrs & MethodAttributes.Virtual) != 0)
        {
            if ((attrs & MethodAttributes.Abstract) != 0) mods.Add("abstract");
            else if ((attrs & MethodAttributes.NewSlot) != 0) mods.Add((attrs & MethodAttributes.Final) != 0 ? "sealed" : "virtual");
            else mods.Add((attrs & MethodAttributes.Final) != 0 ? "sealed override" : "override"); // override 表现为 Virtual 置位而 NewSlot 未置位
        }

        var prefix = mods.Count == 0 ? "" : string.Join(' ', mods) + " ";
        var returnPart = isCtor ? "" : $"{sig.ReturnType} ";
        var paramList = string.Join(", ", sig.ParameterTypes);
        return $"{AccessLevel(attrs & MethodAttributes.MemberAccessMask)} {prefix}{returnPart}{displayName}{genericPart}({paramList});";
    }

    /// <summary>
    /// 渲染属性签名：访问级别（取 get/set 访问器中可见性较高的那个，.NET 元数据属性表本身不存访问级别）+ 类型 + 名字 + { get; set; }，
    /// 按实际存在的访问器输出。
    /// </summary>
    private static string RenderProperty(MetadataReader reader, PropertyDefinition property, Provider provider, string[] typeParams)
    {
        var sig = property.DecodeSignature(provider, new GenericContext(typeParams, Array.Empty<string>()));
        var accessors = property.GetAccessors();
        var body = (!accessors.Getter.IsNil, !accessors.Setter.IsNil) switch
        {
            (true, true) => "{ get; set; }",
            (true, false) => "{ get; }",
            (false, true) => "{ set; }",
            (false, false) => "{ }",
        };
        return $"{AccessorAccessLevel(reader, accessors.Getter, accessors.Setter)} {sig.ReturnType} {reader.GetString(property.Name)} {body}";
    }

    /// <summary>
    /// 渲染事件签名：访问级别（取 add 访问器的可见性）+ event + 类型 + 名字。
    /// </summary>
    private static string RenderEvent(MetadataReader reader, EventDefinition evt, Provider provider, GenericContext ctx)
    {
        var typeName = ResolveTypeHandle(reader, evt.Type, provider, ctx);
        var accessors = evt.GetAccessors();
        return $"{AccessorAccessLevel(reader, accessors.Adder, accessors.Remover)} event {typeName} {reader.GetString(evt.Name)};";
    }

    /// <summary>
    /// 取访问器的访问级别：优先第一个句柄，其次第二个；均空时降级为 internal。
    /// </summary>
    private static string AccessorAccessLevel(MetadataReader reader, MethodDefinitionHandle first, MethodDefinitionHandle second)
    {
        var handle = !first.IsNil ? first : second;
        if (!handle.IsNil)
        {
            return AccessLevel(reader.GetMethodDefinition(handle).Attributes & MethodAttributes.MemberAccessMask);
        }
        return "internal";
    }

    /// <summary>
    /// 解析 EntityHandle 指向的类型为渲染字符串：TypeDefinition 用 FullName，TypeReference 取命名空间.名（+arity），
    /// TypeSpecification 走签名解码。
    /// </summary>
    private static string ResolveTypeHandle(MetadataReader reader, EntityHandle handle, Provider provider, GenericContext ctx)
    {
        return handle.Kind switch
        {
            HandleKind.TypeDefinition => MetadataNaming.FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)handle)),
            HandleKind.TypeReference => provider.GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
            HandleKind.TypeSpecification => reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(provider, ctx),
            _ => "<unknown>",
        };
    }

    /// <summary>
    /// 读取泛型参数句柄集合中每个参数的元数据名（如 T）。
    /// </summary>
    private static string[] GetGenericParameterNames(MetadataReader reader, GenericParameterHandleCollection handles)
    {
        var names = new string[handles.Count];
        for (var i = 0; i < handles.Count; i++)
            names[i] = reader.GetString(reader.GetGenericParameter(handles[i]).Name);
        return names;
    }

    /// <summary>
    /// 判断方法名是否为属性/事件访问器（get_X/set_X/add_/remove_）——此类方法不单独输出，由属性/事件行合并渲染。
    /// C# 编译器保留这些前缀，用户方法名不会误伤。
    /// </summary>
    private static bool IsAccessorName(string name)
        => name.StartsWith("get_", StringComparison.Ordinal)
        || name.StartsWith("set_", StringComparison.Ordinal)
        || name.StartsWith("add_", StringComparison.Ordinal)
        || name.StartsWith("remove_", StringComparison.Ordinal);

    private static string AccessLevel(MethodAttributes access)
        => access switch
        {
            MethodAttributes.Public => "public",
            MethodAttributes.Private => "private",
            MethodAttributes.Family => "protected",
            MethodAttributes.Assembly => "internal",
            MethodAttributes.FamORAssem => "protected internal",
            MethodAttributes.FamANDAssem => "private protected",
            _ => "internal",
        };

    private static string AccessLevel(FieldAttributes access)
        => access switch
        {
            FieldAttributes.Public => "public",
            FieldAttributes.Private => "private",
            FieldAttributes.Family => "protected",
            FieldAttributes.Assembly => "internal",
            FieldAttributes.FamORAssem => "protected internal",
            FieldAttributes.FamANDAssem => "private protected",
            _ => "internal",
        };

    /// <summary>
    /// 泛型参数上下文：类型级与方法级泛型参数名数组，签名解码时按索引取名字。
    /// </summary>
    private readonly record struct GenericContext(string[] TypeParameters, string[] MethodParameters);

    /// <summary>
    /// 签名解码器：把成员签名的类型编码渲染为一行签名所需的字符串（C# 关键字/全名/泛型实例化/数组/指针等）。
    /// </summary>
    private sealed class Provider : ISignatureTypeProvider<string, GenericContext>
    {
        private readonly MetadataReader _reader;

        public Provider(MetadataReader reader) => _reader = reader;

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
                PrimitiveTypeCode.IntPtr => "IntPtr",
                PrimitiveTypeCode.UIntPtr => "UIntPtr",
                PrimitiveTypeCode.TypedReference => "TypedReference",
                _ => $"<{typeCode}>",
            };

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
            => MetadataNaming.FullName(reader, reader.GetTypeDefinition(handle));

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var tr = reader.GetTypeReference(handle);
            var name = reader.GetString(tr.Name);
            if (tr.ResolutionScope.Kind == HandleKind.TypeReference)
            {
                return $"{GetTypeFromReference(reader, (TypeReferenceHandle)tr.ResolutionScope, 0)}+{name}";
            }
            var ns = reader.GetString(tr.Namespace);
            return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        }

        public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            // genericType 形如 System.Collections.Generic.List`1，去掉尾部 arity 后接 <参数列表>
            var backtick = genericType.IndexOf('`');
            var baseName = backtick >= 0 ? genericType[..backtick] : genericType;
            return $"{baseName}<{string.Join(", ", typeArguments)}>";
        }

        public string GetGenericTypeParameter(GenericContext genericContext, int index)
            => index >= 0 && index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : $"T{index}";

        public string GetGenericMethodParameter(GenericContext genericContext, int index)
            => index >= 0 && index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : $"T{index}";

        public string GetArrayType(string elementType, ArrayShape shape)
            => $"{elementType}[{new string(',', Math.Max(0, shape.Rank - 1))}]";

        public string GetSZArrayType(string elementType) => $"{elementType}[]";

        // 元数据无法区分 ref/out（同为 BYREF），统一按 ref 渲染（C# 语法），比 & 更可读
        public string GetByReferenceType(string elementType) => $"ref {elementType}";

        public string GetPointerType(string elementType) => $"{elementType}*";

        public string GetPinnedType(string elementType) => elementType;

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;

        public string GetFunctionPointerType(MethodSignature<string> signature)
            => $"delegate*<{string.Join(", ", signature.ParameterTypes)}{(signature.ParameterTypes.Length > 0 ? ", " : "")}{signature.ReturnType}>";
    }
}
