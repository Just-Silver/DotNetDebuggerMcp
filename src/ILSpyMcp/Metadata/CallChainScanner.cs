using ICSharpCode.Decompiler.Disassembler;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 一条方法调用点：目标类型全名 + 成员名 + 内部/外部标记与反编译定位所需信息。
/// </summary>
/// <param name="IsExternal">是否跨程序集外部调用（false=程序集内部）。</param>
/// <param name="TypeFullName">目标类型全名（内部为类型定义全名；外部为类型引用全名，格式与 list_types 一致）。</param>
/// <param name="MemberName">成员元数据原始名（如 .ctor / Mid / get_Value）。</param>
/// <param name="Signature">内部调用为 <see cref="SignatureRenderer.RenderMemberSignature"/> 渲染的成员签名；外部调用为空串。</param>
/// <param name="MemberToken">内部调用为方法定义 token（0x06 开头，可直接用于进程内成员反编译）；外部调用为 null。</param>
/// <param name="AssemblyFullName">外部调用的归属程序集完整名（如 System.Console, Version=…）；内部调用为 null，未知归属也为 null。</param>
/// <param name="ParamCount">外部调用为 MemberRef 方法签名的参数个数；内部调用恒为 -1。</param>
public readonly record struct CallSite(
    bool IsExternal, string TypeFullName, string MemberName, string Signature,
    string? MemberToken, string? AssemblyFullName, int ParamCount);

/// <summary>
/// 方法级正向调用序列扫描器：纯元数据读取（PEReader + MetadataReader），不加载程序集、不反编译 IL。
/// 对单个方法体按 IL 序解码调用指令（call/callvirt/newobj/jmp/ldftn/ldvirtftn），提取方法调用点——
/// MethodDef 直判程序集内部；MemberRef 沿 ResolutionScope 判定内部/外部（内部映射回类型定义并定位方法）；
/// MethodSpec 解包归约到方法（泛型实例化）；calli 的 StandaloneSignature 函数指针跳过不展开。
/// IL 解码经共享 IlScanHelper（ICSharpCode.Decompiler.Disassembler.ILParser 权威跳表），解码异常安全中止并累计降级计数。
/// </summary>
public sealed class CallChainScanner
{
    private readonly PEReader _pe;
    private readonly MetadataReader _reader;
    private int _abortedBodies;
    private Dictionary<string, TypeDefinitionHandle>? _typeDefsByName;

    /// <summary>
    /// 以程序集 PE 读取器构造扫描器（方法体位于 PE 数据段，需经 PEReader 读取）。
    /// </summary>
    /// <param name="pe">程序集 PE 读取器。</param>
    public CallChainScanner(PEReader pe)
    {
        _pe = pe;
        _reader = pe.GetMetadataReader();
    }

    /// <summary>
    /// 解码中止计数：方法体 IL 解码遇损坏（IlScanHelper 解码异常）时累加，供调用方感知解码完整性。
    /// </summary>
    public int AbortedBodies => _abortedBodies;

    /// <summary>
    /// 扫描单个方法的调用序列：按 IL 序返回全部方法调用点（内部调用 MemberToken 为方法定义 token、
    /// 外部调用 AssemblyFullName/ParamCount 填充）；无方法体（abstract/pinvoke/internal call）时返回空。
    /// </summary>
    /// <param name="method">待扫描的方法定义句柄。</param>
    /// <returns>按 IL 序的调用点列表；无方法体或解码中止时可能为空（保留已收集部分）。</returns>
    public IReadOnlyList<CallSite> ScanMethod(MethodDefinitionHandle method)
    {
        var result = new List<CallSite>();
        var mdef = _reader.GetMethodDefinition(method);
        if (mdef.RelativeVirtualAddress == 0) return result; // abstract/pinvoke/internal call 无方法体
        MethodBodyBlock body;
        try
        {
            body = _pe.GetMethodBody(mdef.RelativeVirtualAddress);
        }
        catch (BadImageFormatException)
        {
            return result; // 单个损坏方法体不影响其余调用收集
        }
        if (body is null) return result;
        var il = body.GetILReader();
        IlScanHelper.DecodeMethodBody(il, instr =>
        {
            switch (instr.Opcode)
            {
                case ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Jmp
                     or ILOpCode.Ldftn or ILOpCode.Ldvirtftn:
                    CollectMethodToken(instr.RawToken, result);
                    break;
                // Calli：StandaloneSignature 函数指针，跳过不展开
            }
        }, () => _abortedBodies++);
        return result;
    }

    /// <summary>
    /// 收集调用 token 指向的调用点：MethodDef 直取声明类型（内部）；MemberRef 沿 parent 判定内外部；
    /// MethodSpec 解包 spec.Method 递归处理（泛型实例化归约到泛型方法定义）。
    /// </summary>
    private void CollectMethodToken(int rawToken, List<CallSite> result)
    {
        var handle = MetadataTokens.EntityHandle(rawToken);
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                CollectMethodDefinition((MethodDefinitionHandle)handle, result);
                break;
            case HandleKind.MemberReference:
                CollectMemberReference((MemberReferenceHandle)handle, result);
                break;
            case HandleKind.MethodSpecification:
                var spec = _reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                CollectMethodToken(MetadataTokens.GetToken(spec.Method), result);
                break;
        }
    }

    /// <summary>
    /// MethodDef 调用：内部调用点（声明类型编译器生成过滤；Signature/MemberToken 渲染）。
    /// </summary>
    private void CollectMethodDefinition(MethodDefinitionHandle handle, List<CallSite> result)
    {
        var method = _reader.GetMethodDefinition(handle);
        var declaringType = _reader.GetTypeDefinition(method.GetDeclaringType());
        if (CompilerGeneratedFilter.IsCompilerGenerated(_reader, declaringType)) return;
        result.Add(new CallSite(
            IsExternal: false,
            TypeFullName: MetadataNaming.FullName(_reader, declaringType),
            MemberName: _reader.GetString(method.Name),
            Signature: SignatureRenderer.RenderMemberSignature(_reader, declaringType, method),
            MemberToken: $"0x{MetadataTokens.GetToken(handle):x8}",
            AssemblyFullName: null,
            ParamCount: -1));
    }

    /// <summary>
    /// MemberRef 调用：parent 为 TypeDefinition 直收内部；TypeReference 经 <see cref="MetadataNaming.TypeReferenceScope"/>
    /// 判定内外部（内部映射回类型定义，外部填归属与参数个数）；TypeSpecification 解码取泛型定义再判内外部。
    /// </summary>
    private void CollectMemberReference(MemberReferenceHandle handle, List<CallSite> result)
    {
        var memberRef = _reader.GetMemberReference(handle);
        var name = _reader.GetString(memberRef.Name);
        switch (memberRef.Parent.Kind)
        {
            case HandleKind.TypeDefinition:
                CollectInternalMemberReference((TypeDefinitionHandle)memberRef.Parent, memberRef, name, result);
                break;
            case HandleKind.TypeReference:
                CollectTypeReferenceMember((TypeReferenceHandle)memberRef.Parent, memberRef, name, result);
                break;
            case HandleKind.TypeSpecification:
                var (def, refHandle) = ResolveTypeSpecDefinition((TypeSpecificationHandle)memberRef.Parent);
                if (def is not null)
                {
                    CollectInternalMemberReference(def.Value, memberRef, name, result);
                }
                else if (refHandle is not null)
                {
                    CollectTypeReferenceMember(refHandle.Value, memberRef, name, result);
                }
                break;
        }
    }

    /// <summary>
    /// MemberRef parent 为 TypeReference：内部映射回 TypeDef（编译器生成过滤）后定位方法；外部填归属与参数个数。
    /// </summary>
    private void CollectTypeReferenceMember(TypeReferenceHandle typeRef, MemberReference memberRef, string name, List<CallSite> result)
    {
        var fullName = MetadataNaming.TypeReferenceFullName(_reader, typeRef);
        if (fullName is null) return; // 无法解析的类型引用：跳过
        var (isInternal, assemblyName) = MetadataNaming.TypeReferenceScope(_reader, typeRef);
        if (isInternal)
        {
            if (TryGetTypeDef(fullName, out var typeDef)) CollectInternalMemberReference(typeDef, memberRef, name, result);
        }
        else
        {
            result.Add(BuildExternalCallSite(fullName, name, memberRef, assemblyName));
        }
    }

    /// <summary>
    /// MemberRef 指向程序集内部类型的调用点：定位到该类型内同名同参数个数的方法定义（MemberToken 需为方法定义 token
    /// 才能用于进程内成员反编译），编译器生成类型过滤；无法定位具体方法时跳过。
    /// </summary>
    private void CollectInternalMemberReference(TypeDefinitionHandle typeDef, MemberReference memberRef, string name, List<CallSite> result)
    {
        var type = _reader.GetTypeDefinition(typeDef);
        if (CompilerGeneratedFilter.IsCompilerGenerated(_reader, type)) return;
        var methodDef = FindMethodDef(typeDef, name, GetMemberRefParamCount(memberRef));
        if (methodDef is null) return; // 无法映射到具体方法（同名不匹配/签名无法解码）：跳过
        result.Add(new CallSite(
            IsExternal: false,
            TypeFullName: MetadataNaming.FullName(_reader, type),
            MemberName: name,
            Signature: SignatureRenderer.RenderMemberSignature(_reader, type, _reader.GetMethodDefinition(methodDef.Value)),
            MemberToken: $"0x{MetadataTokens.GetToken(methodDef.Value):x8}",
            AssemblyFullName: null,
            ParamCount: -1));
    }

    /// <summary>
    /// 在类型定义内按成员名 + 参数个数定位方法定义：参数个数匹配优先（区分重载）；无参数个数匹配时取首个同名（参数个数
    /// 无法解码时的兜底）；无同名方法返回 null。
    /// </summary>
    private MethodDefinitionHandle? FindMethodDef(TypeDefinitionHandle typeDef, string name, int paramCount)
    {
        var type = _reader.GetTypeDefinition(typeDef);
        MethodDefinitionHandle? first = null;
        foreach (var handle in type.GetMethods())
        {
            var method = _reader.GetMethodDefinition(handle);
            if (_reader.GetString(method.Name) != name) continue;
            if (first is null) first = handle;
            if (method.GetParameters().Count == paramCount) return handle;
        }
        return first;
    }

    /// <summary>
    /// 组装外部调用点：Signature 空、MemberToken 空、AssemblyFullName 为归属程序集完整名（未知归属为 null）、
    /// ParamCount 为 MemberRef 方法签名参数个数。
    /// </summary>
    private CallSite BuildExternalCallSite(string typeFullName, string memberName, MemberReference memberRef, string? assemblyName)
    {
        var fullName = assemblyName is not null && TryGetAssemblyReference(assemblyName, out var asmRef)
            ? new ICSharpCode.Decompiler.Metadata.AssemblyReference(_reader, asmRef).FullName
            : null;
        return new CallSite(
            IsExternal: true,
            TypeFullName: typeFullName,
            MemberName: memberName,
            Signature: "",
            MemberToken: null,
            AssemblyFullName: fullName,
            ParamCount: GetMemberRefParamCount(memberRef));
    }

    /// <summary>
    /// 取 MemberRef 方法签名的参数个数；签名无法解码（损坏/非方法签名）时返回 -1。
    /// </summary>
    private int GetMemberRefParamCount(MemberReference memberRef)
    {
        try
        {
            return memberRef.DecodeMethodSignature(CountingProvider.Instance, EmptyParams).ParameterTypes.Length;
        }
        catch (BadImageFormatException)
        {
            return -1;
        }
    }

    /// <summary>
    /// 解析 TypeSpecification 指向的泛型类型定义：解码签名捕获底层 TypeDefinition（内部）或 TypeReference（外部）。
    /// </summary>
    private (TypeDefinitionHandle? Definition, TypeReferenceHandle? Reference) ResolveTypeSpecDefinition(TypeSpecificationHandle handle)
    {
        try
        {
            var capture = new DefinitionCapture();
            _reader.GetTypeSpecification(handle).DecodeSignature(capture, EmptyParams);
            return (capture.Definition, capture.Reference);
        }
        catch (BadImageFormatException)
        {
            return (null, null); // 忽略损坏的类型规范签名
        }
    }

    /// <summary>
    /// 惰性构建「全名→TypeDef 句柄」字典（一次 O(n)，MemberRef 内部解析查字典）。
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

    /// <summary>
    /// 按程序集短名查找 AssemblyReference 句柄（用于构造外部程序集完整名）；未找到返回 false。
    /// </summary>
    private bool TryGetAssemblyReference(string name, out AssemblyReferenceHandle handle)
    {
        foreach (var h in _reader.AssemblyReferences)
        {
            if (_reader.GetString(_reader.GetAssemblyReference(h).Name) == name)
            {
                handle = h;
                return true;
            }
        }
        handle = default;
        return false;
    }

    private static readonly EmptyGenericParams EmptyParams = new();

    /// <summary>
    /// 泛型参数上下文：call_chain 只需参数个数与底层类型，不关心泛型参数名，统一空上下文。
    /// </summary>
    private readonly record struct EmptyGenericParams();

    /// <summary>
    /// 签名解码器（占位返回，不参与展示）：只为触发方法签名解码取参数个数。
    /// </summary>
    private sealed class CountingProvider : ISignatureTypeProvider<string, EmptyGenericParams>
    {
        public static readonly CountingProvider Instance = new();

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => "";
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => "";
        public string GetTypeFromSpecification(MetadataReader reader, EmptyGenericParams genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => "";
        public string GetGenericTypeParameter(EmptyGenericParams genericContext, int index) => "";
        public string GetGenericMethodParameter(EmptyGenericParams genericContext, int index) => "";
        public string GetArrayType(string elementType, ArrayShape shape) => "";
        public string GetSZArrayType(string elementType) => "";
        public string GetByReferenceType(string elementType) => "";
        public string GetPointerType(string elementType) => "";
        public string GetPinnedType(string elementType) => "";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => "";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "";
    }

    /// <summary>
    /// 签名解码器：捕获 TypeSpecification 泛型定义的底层句柄（TypeDefinition 直收、TypeReference 直收）。
    /// </summary>
    private sealed class DefinitionCapture : ISignatureTypeProvider<string, EmptyGenericParams>
    {
        public TypeDefinitionHandle? Definition { get; private set; }
        public TypeReferenceHandle? Reference { get; private set; }

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "";
        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            Definition ??= handle;
            return "";
        }
        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            Reference ??= handle;
            return "";
        }
        public string GetTypeFromSpecification(MetadataReader reader, EmptyGenericParams genericContext, TypeSpecificationHandle handle, byte rawTypeKind) => "";
        public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => "";
        public string GetGenericTypeParameter(EmptyGenericParams genericContext, int index) => "";
        public string GetGenericMethodParameter(EmptyGenericParams genericContext, int index) => "";
        public string GetArrayType(string elementType, ArrayShape shape) => "";
        public string GetSZArrayType(string elementType) => "";
        public string GetByReferenceType(string elementType) => "";
        public string GetPointerType(string elementType) => "";
        public string GetPinnedType(string elementType) => "";
        public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => "";
        public string GetFunctionPointerType(MethodSignature<string> signature) => "";
    }
}
