using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace DotNetDebugger.Decompiler.Metadata;

/// <summary>
/// 泛型实例化扫描结果：两段使用点行列表（类型::成员签名 → 实例化），均已去重排序。
/// </summary>
/// <param name="SignatureHits">成员签名中的泛型实例化命中行。</param>
/// <param name="CallHits">方法体调用中的泛型实例化命中行。</param>
public readonly record struct GenericInstantiationResult(IReadOnlyList<string> SignatureHits, IReadOnlyList<string> CallHits);

/// <summary>
/// 纯元数据「泛型实例化使用点」：反扫程序集全部非编译器生成类型，收集指定泛型类型被以具体类型参数实例化的位置， 供 generic_instantiations
/// 工具使用。两遍扫描：成员签名遍历（字段/方法/属性/事件签名经 <see cref="ISignatureTypeProvider{TType,TGenericContext}"/> 解码，凡
/// GENERICINST 的泛型类型等于目标时记录命中）与 方法体 MethodSpec 调用点（扫描方法体 IL 调用指令，MethodSpec
/// 操作数解码泛型实参，泛型方法的声明类型为目标时记录 方法级实例化，成员引用 parent 为目标的 TypeSpec 实例化时经签名解码记录类型级实例化）。 方法体读取经
/// PEReader.GetMethodBody，IL 解码经共享 IlScanHelper；解码异常安全中止并累计降级计数。
/// </summary>
public sealed class GenericInstantiationScanner
{
    private static readonly GenericContext s_emptyContext = new(Array.Empty<string>(), Array.Empty<string>());
    private readonly PEReader _pe;
    private readonly MetadataReader _reader;
    private readonly RenderingProvider _provider;
    private readonly HashSet<string> _capture = new(StringComparer.Ordinal);
    private int _abortedBodies;

    /// <summary>
    /// 以已打开的 PE 读取器构建扫描器（复用其元数据读取器）。
    /// </summary>
    public GenericInstantiationScanner(PEReader pe)
    {
        _pe = pe;
        _reader = pe.GetMetadataReader();
        _provider = new RenderingProvider(_reader);
    }

    /// <summary>
    /// 解码中止计数：方法体 IL 解码遇损坏（IlScanHelper 解码异常）时累加，供调用方感知解码完整性。
    /// </summary>
    public int AbortedBodies => _abortedBodies;

    /// <summary>
    /// 查找指定泛型类型在程序集内的实例化使用点。目标定位： <see cref="MetadataNaming.FindTypes"/> 精确匹配优先， 兜底枚举全部类型按全名/短名（含去
    /// arity 的短名）匹配；多个候选抛歧义异常、无候选抛未找到异常（文本中文）。
    /// </summary>
    /// <param name="genericTypeName">用户输入的泛型类型名，可带 arity（GenericBox`1）或省略 arity（GenericBox，短名亦可）。</param>
    /// <returns>两段实例化使用点行列表；程序集内无使用时为空列表。</returns>
    public GenericInstantiationResult Find(string genericTypeName)
    {
        _abortedBodies = 0;
        var target = ResolveTarget(genericTypeName);
        _provider.SetTarget(target, _capture);

        var signatureHits = new HashSet<string>(StringComparer.Ordinal);
        var callHits = new HashSet<string>(StringComparer.Ordinal);
        ScanMemberSignatures(signatureHits);
        ScanMethodBodies(target, callHits);

        var sig = signatureHits.ToList();
        sig.Sort(StringComparer.Ordinal);
        var calls = callHits.ToList();
        calls.Sort(StringComparer.Ordinal);
        return new GenericInstantiationResult(sig, calls);
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
    /// 去掉类型全名中的泛型 arity 后缀（GenericBox`1 → GenericBox，嵌套 Outer`1+Inner`1 → Outer+Inner）， 供目标定位的短名匹配与实例化渲染。
    /// </summary>
    private static string StripArity(string fullName)
    {
        var sb = new StringBuilder(fullName.Length);
        for (var i = 0; i < fullName.Length; i++)
        {
            if (fullName[i] == '`')
            {
                while (i + 1 < fullName.Length && char.IsAsciiDigit(fullName[i + 1])) i++;
            }
            else sb.Append(fullName[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// 目标类型定位：精确匹配（FindTypes，含 +/· 分隔与类别前缀兼容）→ 兜底按全名/去 arity 短名子串匹配； 多候选抛歧义提示、无候选抛未找到提示。
    /// </summary>
    private TypeDefinitionHandle ResolveTarget(string input)
    {
        var exact = MetadataNaming.FindTypes(_reader, input);
        if (exact.Count > 1) throw new InvalidOperationException(MetadataNaming.BuildAmbiguityMessage(_reader, input, exact, "该类型名在归一化后存在同名类型，请换用不含歧义的完整类型名"));
        if (exact.Count == 1) return exact[0];

        var fallback = new List<TypeDefinitionHandle>();
        foreach (var handle in _reader.TypeDefinitions)
        {
            var fullName = MetadataNaming.FullName(_reader, _reader.GetTypeDefinition(handle));
            var baseName = StripArity(fullName);
            if (fullName == input || baseName == input
                || fullName.EndsWith("." + input, StringComparison.Ordinal) || baseName.EndsWith("." + input, StringComparison.Ordinal)
                || fullName.EndsWith("+" + input, StringComparison.Ordinal) || baseName.EndsWith("+" + input, StringComparison.Ordinal))
            {
                fallback.Add(handle);
            }
        }
        if (fallback.Count > 1) throw new InvalidOperationException(MetadataNaming.BuildAmbiguityMessage(_reader, input, fallback, "该类型名在归一化后存在同名类型，请换用不含歧义的完整类型名"));
        if (fallback.Count == 0) throw new InvalidOperationException(MetadataNaming.BuildNotFoundMessage(_reader, input));
        return fallback[0];
    }

    /// <summary>
    /// 第一遍：遍历全部非编译器生成类型的字段/方法/属性/事件签名，签名解码触发 Provider 捕获目标泛型类型的实例化。
    /// </summary>
    private void ScanMemberSignatures(HashSet<string> hits)
    {
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var type = _reader.GetTypeDefinition(typeHandle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(_reader, type)) continue;
            var typeFullName = MetadataNaming.FullName(_reader, type);
            var typeParams = GetGenericParameterNames(_reader, type.GetGenericParameters());
            var memberCtx = new GenericContext(typeParams, Array.Empty<string>());

            foreach (var fieldHandle in type.GetFields())
            {
                _capture.Clear();
                _reader.GetFieldDefinition(fieldHandle).DecodeSignature(_provider, memberCtx);
                EmitHits(hits, typeFullName, type, fieldHandle, _capture);
            }

            foreach (var methodHandle in type.GetMethods())
            {
                var method = _reader.GetMethodDefinition(methodHandle);
                var methodParams = GetGenericParameterNames(_reader, method.GetGenericParameters());
                _capture.Clear();
                method.DecodeSignature(_provider, new GenericContext(typeParams, methodParams));
                EmitHits(hits, typeFullName, type, methodHandle, _capture);
            }

            foreach (var propHandle in type.GetProperties())
            {
                _capture.Clear();
                _reader.GetPropertyDefinition(propHandle).DecodeSignature(_provider, memberCtx);
                EmitHits(hits, typeFullName, type, propHandle, _capture);
            }

            foreach (var evtHandle in type.GetEvents())
            {
                var evt = _reader.GetEventDefinition(evtHandle);
                _capture.Clear();
                switch (evt.Type.Kind)
                {
                    case HandleKind.TypeDefinition:
                        _provider.GetTypeFromDefinition(_reader, (TypeDefinitionHandle)evt.Type, 0);
                        break;

                    case HandleKind.TypeReference:
                        _provider.GetTypeFromReference(_reader, (TypeReferenceHandle)evt.Type, 0);
                        break;

                    case HandleKind.TypeSpecification:
                        _reader.GetTypeSpecification((TypeSpecificationHandle)evt.Type).DecodeSignature(_provider, memberCtx);
                        break;
                }
                EmitHits(hits, typeFullName, type, evtHandle, _capture);
            }
        }
    }

    /// <summary>
    /// 第二遍：反扫全部非编译器生成类型的方法体调用指令，收集 MethodSpec 方法级实例化与 TypeSpec 类型级实例化。
    /// </summary>
    private void ScanMethodBodies(TypeDefinitionHandle target, HashSet<string> hits)
    {
        var targetName = MetadataNaming.FullName(_reader, _reader.GetTypeDefinition(target));
        var targetBase = StripArity(targetName);
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var type = _reader.GetTypeDefinition(typeHandle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(_reader, type)) continue;
            ScanType(type, targetName, targetBase, hits);
        }
    }

    /// <summary>
    /// 扫描单个类型定义的全部方法体（含访问器方法体，属性 getter 内调用同样属于调用点）。
    /// </summary>
    private void ScanType(TypeDefinition type, string targetName, string targetBase, HashSet<string> hits)
    {
        foreach (var methodHandle in type.GetMethods())
        {
            var method = _reader.GetMethodDefinition(methodHandle);
            if (method.RelativeVirtualAddress == 0) continue; // abstract/pinvoke/internal call 无方法体
            MethodBodyBlock body;
            try
            {
                body = _pe.GetMethodBody(method.RelativeVirtualAddress);
            }
            catch (BadImageFormatException)
            {
                continue; // 单个损坏方法体不影响其余收集
            }
            if (body is null) continue;
            ScanBody(body, method, targetName, targetBase, hits);
        }
    }

    /// <summary>
    /// 解码一个方法体的 IL 字节流（经 IlScanHelper 回调驱动）：调用指令 token 收集实例化命中，方法体扫描结束再统一渲染
    /// 使用点行（免为无命中方法付出全名渲染成本）；解码异常时中止并累计 AbortedBodies，保留已收集部分。
    /// </summary>
    private void ScanBody(MethodBodyBlock body, MethodDefinition sourceMethod, string targetName, string targetBase, HashSet<string> hits)
    {
        _capture.Clear();
        var il = body.GetILReader();
        IlScanHelper.DecodeMethodBody(il, instr =>
        {
            switch (instr.Opcode)
            {
                case ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Jmp
                     or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Calli:
                    ResolveCall(instr.RawToken, targetName, targetBase);
                    break;
            }
        }, () => _abortedBodies++);
        if (_capture.Count == 0) return;

        var declaringType = _reader.GetTypeDefinition(sourceMethod.GetDeclaringType());
        var sourceType = MetadataNaming.FullName(_reader, declaringType);
        var signature = SignatureRenderer.RenderMemberSignature(_reader, declaringType, sourceMethod);
        foreach (var inst in _capture)
        {
            hits.Add($"{sourceType}::{signature} → {inst}");
        }
    }

    /// <summary>
    /// 调用 token 解析：MethodDef 声明类型为非实例化 TypeDef、非泛型方法调用不触发实例化，跳过；MemberRef 走 parent 解析 （TypeSpec
    /// 父类型解码触发类型级实例化捕获）；MethodSpec 解码泛型实参（方法级与实参内类型级实例化）； calli 的函数指针签名解码实参/返回类型中的实例化。
    /// </summary>
    private void ResolveCall(int rawToken, string targetName, string targetBase)
    {
        var handle = MetadataTokens.EntityHandle(rawToken);
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                ResolveMemberParent(_reader.GetMemberReference((MemberReferenceHandle)handle).Parent);
                break;

            case HandleKind.MethodSpecification:
                ResolveMethodSpec(_reader.GetMethodSpecification((MethodSpecificationHandle)handle), targetName, targetBase);
                break;

            case HandleKind.StandaloneSignature:
                ResolveSignatureToken((StandaloneSignatureHandle)handle);
                break;
        }
    }

    /// <summary>
    /// 解析 MethodSpec：泛型方法实参解码（spec.DecodeSignature 触发实参内类型级实例化捕获，返回实参列表供方法级拼接）； spec.Method 为
    /// MethodDef 且声明类型为目标 → 记录 方法名&lt;实参&gt;；为 MemberRef 且 parent 为目标的 TypeSpec 实例化 → 同样记录方法级实例化（跨程序集/经基类接口调用场景）。
    /// </summary>
    private void ResolveMethodSpec(MethodSpecification spec, string targetName, string targetBase)
    {
        switch (spec.Method.Kind)
        {
            case HandleKind.MethodDefinition:
                var methodDef = _reader.GetMethodDefinition((MethodDefinitionHandle)spec.Method);
                if (MetadataNaming.FullName(_reader, _reader.GetTypeDefinition(methodDef.GetDeclaringType())) == targetName)
                {
                    CaptureMethodInstantiation(_reader.GetString(methodDef.Name), spec);
                }
                break;

            case HandleKind.MemberReference:
                var memberRef = _reader.GetMemberReference((MemberReferenceHandle)spec.Method);
                ResolveMemberParent(memberRef.Parent);
                if (memberRef.Parent.Kind == HandleKind.TypeSpecification
                    && ResolveTypeSpecName(memberRef.Parent) is { } parentName
                    && parentName.StartsWith(targetBase + "<", StringComparison.Ordinal))
                {
                    CaptureMethodInstantiation(_reader.GetString(memberRef.Name), spec);
                }
                break;
        }
    }

    /// <summary>
    /// 解码 MethodSpec 泛型实参并记录方法级实例化行（如 Echo&lt;int&gt;）。实参本身为目标的实例化（如
    /// Echo&lt;GenericBox&lt;string&gt;&gt;） 已由 spec.DecodeSignature 经 Provider 捕获到
    /// _capture。任一实参含类型参数（泛型方法/类型内以类型参数调用 Echo&lt;T&gt;） 不是具体化实例化，跳过方法级捕获（实参内的类型级实例化仍保留）。
    /// </summary>
    private void CaptureMethodInstantiation(string methodName, MethodSpecification spec)
    {
        try
        {
            var args = spec.DecodeSignature(_provider, s_emptyContext);
            if (args.Any(a => a.Contains(RenderingProvider.TypeParamMarker))) return;
            _capture.Add($"{methodName}<{string.Join(", ", args)}>");
        }
        catch (BadImageFormatException)
        {
            // 忽略损坏的泛型实参签名
        }
    }

    /// <summary>
    /// 解析 MemberRef 的 parent：TypeSpecification（泛型实例化）解码签名触发类型级实例化捕获；其余作用域非实例化，跳过。
    /// </summary>
    private void ResolveMemberParent(EntityHandle parent)
    {
        if (parent.Kind != HandleKind.TypeSpecification) return;
        try
        {
            _reader.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(_provider, s_emptyContext);
        }
        catch (BadImageFormatException)
        {
            // 忽略损坏的类型规范签名
        }
    }

    /// <summary>
    /// calli 的 StandaloneSignature（函数指针签名）：解码参数/返回类型中的泛型实例化。
    /// </summary>
    private void ResolveSignatureToken(StandaloneSignatureHandle handle)
    {
        var sig = _reader.GetStandaloneSignature(handle);
        if (sig.GetKind() != StandaloneSignatureKind.Method) return;
        try
        {
            sig.DecodeMethodSignature(_provider, s_emptyContext);
        }
        catch (BadImageFormatException)
        {
            // 忽略损坏的函数指针签名
        }
    }

    /// <summary>
    /// 解码 TypeSpec 为渲染字符串（如 ILSpyMcp.Samples.GenericBox&lt;int&gt;），供判定其基类型是否为目标泛型类型。
    /// </summary>
    private string? ResolveTypeSpecName(EntityHandle handle)
    {
        if (handle.Kind != HandleKind.TypeSpecification) return null;
        try
        {
            return _reader.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(_provider, s_emptyContext);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 成员/方法扫描有捕获命中时，渲染成员签名（SignatureRenderer 口径，与 signature 工具一致）后组装使用点行 类型全名::成员签名 → 实例化。
    /// </summary>
    private void EmitHits(HashSet<string> hits, string typeFullName, TypeDefinition type, EntityHandle memberHandle, HashSet<string> capture)
    {
        if (capture.Count == 0) return;
        var signature = SignatureRenderer.RenderSingleMember(_reader, type, memberHandle);
        foreach (var inst in capture)
        {
            hits.Add($"{typeFullName}::{signature} → {inst}");
        }
    }

    /// <summary>
    /// 泛型参数上下文：类型级与方法级泛型参数名数组，签名解码时按索引取名字（与 SignatureRenderer 一致）。
    /// </summary>
    private readonly record struct GenericContext(string[] TypeParameters, string[] MethodParameters);

    /// <summary>
    /// 签名解码器：既渲染签名的类型编码（C# 关键字/全名/泛型实例化/数组/指针等），又在泛型类型等于目标且实参为具体类型
    /// （不含类型参数）时捕获实例化到当前集合（成员签名遍历与方法体扫描共用，每次解码前清空）。捕获判定用带 arity 的 泛型类型全名与 targetName 直比 +
    /// 任一实参含类型参数标记即不算具体化，渲染则去掉 arity（GenericBox`1&lt;int&gt; → GenericBox&lt;int&gt;）。
    /// </summary>
    private sealed class RenderingProvider : ISignatureTypeProvider<string, GenericContext>
    {
        // 类型参数在渲染串内的标记（PUA 区字符，不可能出现在合法类型名/元数据名中）：泛型实例化捕获按「任一实参的
        // 渲染串含类型参数标记」判定是否具体化，标记随组合类型（数组/指针/byref/嵌套实例化）向上传播，避免依赖 last-element 标志在嵌套部分具体化（GenericBox<SomeGeneric<T>>）与泛型方法内以类型参数调用（Echo<T>）时误判
        internal const char TypeParamMarker = '\uE000';

        private readonly MetadataReader _reader;
        private string _targetName = "";
        private HashSet<string>? _sink;

        public RenderingProvider(MetadataReader reader) => _reader = reader;

        /// <summary>
        /// 设置本次扫描的目标泛型类型（取全名带 arity）与捕获集合；Find 每次调用设置一次。
        /// </summary>
        public void SetTarget(TypeDefinitionHandle target, HashSet<string> sink)
        {
            _targetName = MetadataNaming.FullName(_reader, _reader.GetTypeDefinition(target));
            _sink = sink;
        }

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
            => MetadataNaming.TypeReferenceFullName(reader, handle) ?? "<unresolved>";

        public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments)
        {
            var rendered = $"{StripArity(genericType)}<{string.Join(", ", typeArguments)}>";
            // 泛型实参含类型参数（如 GenericBox<T> 自引用、GenericBox<T0>、嵌套部分具体化 GenericBox<SomeGeneric<T>>）时 不是具体化实例化，不捕获；判定按「任一实参渲染串含类型参数标记」，嵌套实参的标记已随组合向上传播
            if (genericType == _targetName && _sink is not null && !typeArguments.Any(a => a.Contains(TypeParamMarker)))
                _sink.Add(rendered);
            return rendered;
        }

        public string GetGenericTypeParameter(GenericContext genericContext, int index)
        {
            var name = index >= 0 && index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : $"T{index}";
            return TypeParamMarker + name;
        }

        public string GetGenericMethodParameter(GenericContext genericContext, int index)
        {
            var name = index >= 0 && index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : $"T{index}";
            return TypeParamMarker + name;
        }

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