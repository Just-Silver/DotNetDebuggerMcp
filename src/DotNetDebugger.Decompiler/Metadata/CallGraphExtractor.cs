using System.Collections.Immutable;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebugger.Decompiler.Metadata;

/// <summary>
/// 纯元数据「方法体调用图」：扫描类型全部方法体 IL 的调用指令（call/callvirt/newobj/ldftn/ldvirtftn/jmp/calli），
/// 提取程序集内部被调用的类型（编译器生成类型不计）与跨程序集外部类型（WithExternal 路径，带程序集归属），供 call_graph 工具使用。与 ReferenceExtractor
/// 的签名级引用互补：本类基于方法体执行流而非成员签名。 IL 解码经共享 IlScanHelper（基于 ICSharpCode.Decompiler.Disassembler.ILParser
/// 权威跳表）：只提取调用边 token， 解码异常安全中止并累计降级计数； 同程序集成员调用编译器通常发 MethodDef/FieldDef 直接 token，MemberRef 兜底沿
/// ResolutionScope 回溯判定内部/外部。
/// </summary>
public static class CallGraphExtractor
{
    private static readonly GenericContext EmptyContext = new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// 提取指定类型全部方法体调用的程序集内部类型全名（去重、按元数据枚举序）。
    /// </summary>
    /// <param name="pe">程序集 PE 读取器（方法体位于 PE 数据段，需经 PEReader 读取）。</param>
    /// <param name="type">待扫描的类型定义。</param>
    /// <returns>内部类型全名列表；无内部调用时为空列表。</returns>
    public static IReadOnlyList<string> ExtractMethodBodyCallTypes(PEReader pe, TypeDefinition type)
        => ExtractMethodBodyCallTypesDetailed(pe, type).Internal;

    /// <summary>
    /// 提取指定类型全部方法体调用的程序集内部与跨程序集外部类型。内部集合语义与 <see cref="ExtractMethodBodyCallTypes"/>
    /// 完全一致；外部集合为方法体调用指令（MemberRef 兜底解析）中出现的跨程序集 类型，条目格式 <c>全名 [程序集名]</c>（如 <c>System.Console
    /// [System.Console]</c>），程序集名取元数据 AssemblyReference.Name（纯元数据，不加载外部程序集），未知归属输出 <c>全名
    /// [&lt;外部&gt;]</c>，按全名排序去重。 编译器生成 target 过滤只对内部集合生效（外部类型非编译器生成）。
    /// </summary>
    /// <param name="pe">程序集 PE 读取器（方法体位于 PE 数据段，需经 PEReader 读取）。</param>
    /// <param name="type">待扫描的类型定义。</param>
    /// <returns>内部与外部类型集合；对应无调用时为空列表。</returns>
    public static (IReadOnlyList<string> Internal, IReadOnlyList<string> External) ExtractMethodBodyCallTypesWithExternal(
        PEReader pe, TypeDefinition type)
    {
        var (internalSet, external, _) = ExtractMethodBodyCallTypesDetailed(pe, type);
        return (internalSet, external);
    }

    /// <summary>
    /// 提取指定类型全部方法体调用的内部/外部类型集合，并返回解码降级计数（AbortedBodies：因 IL 损坏而中止解码的方法体数）。 内部/外部集合语义与 <see
    /// cref="ExtractMethodBodyCallTypesWithExternal"/> 完全一致，供调用方感知解码完整性。
    /// </summary>
    /// <param name="pe">程序集 PE 读取器（方法体位于 PE 数据段，需经 PEReader 读取）。</param>
    /// <param name="type">待扫描的类型定义。</param>
    /// <returns>内部与外部类型集合及中止解码的方法体计数。</returns>
    public static (IReadOnlyList<string> Internal, IReadOnlyList<string> External, int Aborted) ExtractMethodBodyCallTypesDetailed(
        PEReader pe, TypeDefinition type)
    {
        var scanner = new BodyScanner(pe);
        scanner.ScanType(type);
        return (scanner.Render(), scanner.RenderExternal(), scanner.AbortedBodies);
    }

    /// <summary>
    /// 反向扫描：遍历程序集全部类型（跳过编译器生成类型与自身），凡方法体调用含目标类型全名的来源类型全名，按元数据枚举序收集。
    /// </summary>
    /// <param name="pe">程序集 PE 读取器。</param>
    /// <param name="type">目标类型定义。</param>
    /// <param name="typeFullName">目标类型全名。</param>
    /// <returns>方法体调用了目标类型的类型全名列表。</returns>
    public static IReadOnlyList<string> FindCallers(PEReader pe, TypeDefinition type, string typeFullName)
    {
        var scanner = new BodyScanner(pe);
        var result = new List<string>();
        foreach (var handle in scanner.Reader.TypeDefinitions)
        {
            var candidate = scanner.Reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(scanner.Reader, candidate)) continue;
            var candidateName = MetadataNaming.FullName(scanner.Reader, candidate);
            if (candidateName == typeFullName) continue;
            scanner.Clear();
            scanner.ScanType(candidate);
            if (scanner.ContainsType(typeFullName)) result.Add(candidateName);
        }
        return result;
    }

    /// <summary>
    /// 方法级反向调用点：遍历程序集全部非编译器生成类型的方法体，凡调用指令指向指定方法 token 的来源方法， 以 <c>类型全名::成员签名</c> 行收集（泛型实例化
    /// MethodSpec 调用解包归约到目标方法；编译器生成类型的方法体不计）。 供 call_graph 的 token 参数做类型级反查的细化——直接回答「程序集内哪些方法体调用了这个具体方法」。
    /// </summary>
    /// <param name="pe">程序集 PE 读取器。</param>
    /// <param name="token">目标方法元数据 token（0x 开头的十六进制，如 0x06000005）。</param>
    /// <returns>调用点行列表（类型全名::成员签名），按全名排序去重；token 非法/非方法定义时为空列表。</returns>
    public static IReadOnlyList<string> FindMethodCallers(PEReader pe, string token)
    {
        if (!token.Trim().StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(token.Trim().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw))
            return Array.Empty<string>();
        var handle = MetadataTokens.EntityHandle(raw);
        if (handle.Kind != HandleKind.MethodDefinition) return Array.Empty<string>();
        var scanner = new BodyScanner(pe);
        foreach (var typeHandle in scanner.Reader.TypeDefinitions)
        {
            var type = scanner.Reader.GetTypeDefinition(typeHandle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(scanner.Reader, type)) continue;
            scanner.ScanType(type, (MethodDefinitionHandle)handle);
        }
        return scanner.RenderCallers();
    }

    /// <summary>
    /// 单类型方法体扫描器：复用 BodyScanner 以共享一次「全名→TypeDef 句柄」字典构建（反向扫描大量复用）。
    /// </summary>
    private sealed class BodyScanner
    {
        private readonly PEReader _pe;
        private readonly MetadataReader _reader;
        private readonly HashSet<TypeDefinitionHandle> _collected = new();
        private readonly HashSet<string> _external = new(StringComparer.Ordinal);
        private readonly HashSet<string> _callers = new(StringComparer.Ordinal);
        private readonly Provider _provider;
        private Dictionary<string, TypeDefinitionHandle>? _typeDefsByName;

        public BodyScanner(PEReader pe)
        {
            _pe = pe;
            _reader = pe.GetMetadataReader();
            _provider = new Provider(_collected, _external);
        }

        /// <summary>
        /// 解码中止计数：方法体 IL 解码遇损坏（IlScanHelper 解码异常）时累加，供调用方感知解码完整性。
        /// </summary>
        public int AbortedBodies { get; private set; }

        public MetadataReader Reader => _reader;

        /// <summary>
        /// 清空已收集集合、调用点集合与降级计数，供反向扫描在候选类型间复用实例。
        /// </summary>
        public void Clear()
        {
            _collected.Clear();
            _external.Clear();
            _callers.Clear();
            AbortedBodies = 0;
        }

        /// <summary>
        /// 扫描一个类型定义的全部方法体（含访问器方法体，属性 getter 内的调用同样是该类型的行为调用）。 callerTarget
        /// 非空时启用方法级反向定位：凡方法体调用指令指向该方法的来源方法记入 <see cref="_callers"/>。
        /// </summary>
        public void ScanType(TypeDefinition type, MethodDefinitionHandle? callerTarget = null)
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
                ScanBody(body, methodHandle, callerTarget);
            }
        }

        /// <summary>
        /// 按元数据枚举序输出已收集内部类型的全名。
        /// </summary>
        public List<string> Render()
        {
            var result = new List<string>();
            foreach (var handle in _reader.TypeDefinitions)
            {
                if (_collected.Contains(handle))
                {
                    result.Add(MetadataNaming.FullName(_reader, _reader.GetTypeDefinition(handle)));
                }
            }
            return result;
        }

        /// <summary>
        /// 按全名排序输出去重后的调用点集合（格式 类型全名::成员签名）。
        /// </summary>
        public List<string> RenderCallers()
        {
            var result = _callers.ToList();
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// 按全名排序输出去重后的外部类型集合（格式 全名 [程序集名]）。
        /// </summary>
        public List<string> RenderExternal()
        {
            var result = _external.ToList();
            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// 已收集集合是否包含指定全名的类型。
        /// </summary>
        public bool ContainsType(string fullName)
            => TryGetTypeDef(fullName, out var handle) && _collected.Contains(handle);

        /// <summary>
        /// 解码一个方法体的 IL 字节流（经 IlScanHelper 回调驱动）：方法调用 token 收集调用边，callerTarget 非空时同时做
        /// 调用点匹配；解码异常时中止并累计 AbortedBodies，保留已收集部分。
        /// </summary>
        private void ScanBody(MethodBodyBlock body, MethodDefinitionHandle sourceMethod, MethodDefinitionHandle? callerTarget)
        {
            var il = body.GetILReader();
            IlScanHelper.DecodeMethodBody(il, instr =>
            {
                switch (instr.Opcode)
                {
                    case ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Jmp
                         or ILOpCode.Ldftn or ILOpCode.Ldvirtftn:
                        CollectMethodToken(instr.RawToken);
                        if (callerTarget is not null && MatchesToken(instr.RawToken, callerTarget.Value)) RecordCaller(sourceMethod);
                        break;

                    case ILOpCode.Calli:
                        CollectSignatureToken(instr.RawToken);
                        break;
                }
            }, () => AbortedBodies++);
        }

        /// <summary>
        /// 收集方法 token 指向的声明类型：MethodDef 直取声明类型；MemberRef 沿 parent 解析；MethodSpec 解包方法并解码泛型实参。
        /// </summary>
        private void CollectMethodToken(int rawToken)
        {
            var handle = MetadataTokens.EntityHandle(rawToken);
            switch (handle.Kind)
            {
                case HandleKind.MethodDefinition:
                    CollectType(_reader.GetMethodDefinition((MethodDefinitionHandle)handle).GetDeclaringType());
                    break;

                case HandleKind.MemberReference:
                    CollectMemberParent(_reader.GetMemberReference((MemberReferenceHandle)handle).Parent);
                    break;

                case HandleKind.MethodSpecification:
                    var spec = _reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    CollectMethodToken(MetadataTokens.GetToken(spec.Method));
                    try
                    {
                        spec.DecodeSignature(_provider, EmptyContext);
                    }
                    catch (BadImageFormatException)
                    {
                        // 忽略损坏的泛型实参签名
                    }
                    break;
            }
        }

        /// <summary>
        /// 方法调用 token 是否指向目标方法：MethodDef 直接比较；MethodSpec 解包 spec.Method 再比较（覆盖泛型实例化调用 如
        /// GenericHelper.Echo&lt;int&gt; 的 MethodSpec 归约到 GenericHelper.Echo）。
        /// </summary>
        private bool MatchesToken(int rawToken, MethodDefinitionHandle target)
        {
            var handle = MetadataTokens.EntityHandle(rawToken);
            return handle.Kind switch
            {
                HandleKind.MethodDefinition => (MethodDefinitionHandle)handle == target,
                HandleKind.MethodSpecification => _reader.GetMethodSpecification((MethodSpecificationHandle)handle).Method == target,
                _ => false,
            };
        }

        /// <summary>
        /// 记录调用点来源方法：以 类型全名::成员签名 行加入去重集合（签名经 SignatureRenderer 渲染，与 signature 工具口径一致）。
        /// </summary>
        private void RecordCaller(MethodDefinitionHandle source)
        {
            var method = _reader.GetMethodDefinition(source);
            var declaringType = _reader.GetTypeDefinition(method.GetDeclaringType());
            var signature = SignatureRenderer.RenderMemberSignature(_reader, declaringType, method);
            _callers.Add($"{MetadataNaming.FullName(_reader, declaringType)}::{signature}");
        }

        /// <summary>
        /// 解析 MemberRef 的 parent：TypeDefinition 直收；TypeReference 沿 ResolutionScope
        /// 判定内部并映射回定义、外部收集归属； TypeSpecification 解码泛型实参收集。
        /// </summary>
        private void CollectMemberParent(EntityHandle parent)
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeDefinition:
                    CollectType((TypeDefinitionHandle)parent);
                    break;

                case HandleKind.TypeReference:
                    CollectTypeReference((TypeReferenceHandle)parent);
                    break;

                case HandleKind.TypeSpecification:
                    try
                    {
                        _reader.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(_provider, EmptyContext);
                    }
                    catch (BadImageFormatException)
                    {
                        // 忽略损坏的类型规范签名
                    }
                    break;
            }
        }

        /// <summary>
        /// calli 的 StandaloneSignature（函数指针签名）：解码参数/返回类型收集内部类型。
        /// </summary>
        private void CollectSignatureToken(int rawToken)
        {
            var handle = MetadataTokens.EntityHandle(rawToken);
            if (handle.Kind != HandleKind.StandaloneSignature) return;
            var sig = _reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
            if (sig.GetKind() != StandaloneSignatureKind.Method) return;
            try
            {
                sig.DecodeMethodSignature(_provider, EmptyContext);
            }
            catch (BadImageFormatException)
            {
                // 忽略损坏的函数指针签名
            }
        }

        /// <summary>
        /// 解析 MemberRef parent 的 TypeReference：内部类型映射回 TypeDef 句柄收集（编译器生成类型过滤）；跨程序集外部类型 用 全名 [程序集名]
        /// 加入外部集合（外部类型非编译器生成，不做过滤）。归属判定与全名渲染共用 MetadataNaming 的
        /// TypeReferenceScope/TypeReferenceFullName helper。
        /// </summary>
        private void CollectTypeReference(TypeReferenceHandle handle)
        {
            var (isInternal, assemblyName) = MetadataNaming.TypeReferenceScope(_reader, handle);
            var name = MetadataNaming.TypeReferenceFullName(_reader, handle);
            if (name is null) return; // 无法解析的类型引用：跳过
            if (isInternal)
            {
                if (TryGetTypeDef(name, out var typeDef))
                {
                    CollectType(typeDef);
                }
            }
            else
            {
                _external.Add(MetadataNaming.FormatExternal(name, assemblyName));
            }
        }

        private void CollectType(TypeDefinitionHandle handle)
        {
            // 编译器生成类型（闭包/状态机/私有实现细节）作为调用目标无业务语义，过滤避免噪声
            var type = _reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(_reader, type)) return;
            _collected.Add(handle);
        }

        /// <summary>
        /// 惰性构建「全名→TypeDef 句柄」字典（一次 O(n)，MemberRef 解析与 ContainsType 均查字典）。
        /// </summary>
        private bool TryGetTypeDef(string fullName, out TypeDefinitionHandle handle)
        {
            _typeDefsByName ??= BuildTypeDefsByName();
            return _typeDefsByName.TryGetValue(fullName, out handle);
        }

        private Dictionary<string, TypeDefinitionHandle> BuildTypeDefsByName()
        {
            var map = new Dictionary<string, TypeDefinitionHandle>(StringComparer.Ordinal);
            foreach (var handle in _reader.TypeDefinitions)
            {
                map[MetadataNaming.FullName(_reader, _reader.GetTypeDefinition(handle))] = handle;
            }
            return map;
        }
    }

    /// <summary>
    /// 泛型参数上下文：类型级与方法级泛型参数名数组，签名解码时按索引取名字（与 SignatureRenderer 一致）。
    /// </summary>
    private readonly record struct GenericContext(string[] TypeParameters, string[] MethodParameters);

    /// <summary>
    /// 签名解码器：只为触发类型解析回调、收集程序集内部 TypeDefinition 与跨程序集外部 TypeReference（格式 全名 [程序集名]）；
    /// 返回字符串只是占位，不参与任何展示（与 ReferenceExtractor.Provider 同构，用于 MethodSpec 泛型实参、TypeSpecification 父类型与
    /// calli 函数指针签名）。内部类型的泛型实参经 GetTypeFromDefinition 收集，外部泛型实参/泛型实例化的外部类型 经 GetTypeFromReference 收集——与内部集合语义对称。
    /// </summary>
    private sealed class Provider : ISignatureTypeProvider<string, GenericContext>
    {
        private readonly HashSet<TypeDefinitionHandle> _collected;
        private readonly HashSet<string>? _external;

        public Provider(HashSet<TypeDefinitionHandle> collected, HashSet<string>? external)
        {
            _collected = collected;
            _external = external;
        }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            _collected.Add(handle);
            return "";
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
            return "";
        }

        public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => "";

        public string GetGenericTypeParameter(GenericContext genericContext, int index) => $"T{index}";

        public string GetGenericMethodParameter(GenericContext genericContext, int index) => $"T{index}";

        public string GetArrayType(string elementType, ArrayShape shape) => "";

        public string GetSZArrayType(string elementType) => "";

        public string GetByReferenceType(string elementType) => "";

        public string GetPointerType(string elementType) => "";

        public string GetPinnedType(string elementType) => "";

        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => "";

        public string GetFunctionPointerType(MethodSignature<string> signature) => "";
    }
}