using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 纯元数据「方法体调用图」：扫描类型全部方法体 IL 的调用指令（call/callvirt/newobj/ldftn/ldvirtftn/jmp/calli），
/// 提取程序集内部被调用的类型（跨程序集类型不计、编译器生成类型不计），供 call_graph 工具使用。
/// 与 ReferenceExtractor 的签名级引用互补：本类基于方法体执行流而非成员签名。
/// IL 解码采用 ECMA-335 操作数表：只精确读取 metadata token 操作数，其余指令按表跳过；
/// 同程序集成员调用编译器通常发 MethodDef/FieldDef 直接 token，MemberRef 兜底沿 ResolutionScope 回溯判定内部。
/// </summary>
public static class CallGraphExtractor
{
    /// <summary>
    /// 提取指定类型全部方法体调用的程序集内部类型全名（去重、按元数据枚举序）。
    /// </summary>
    /// <param name="pe">程序集 PE 读取器（方法体位于 PE 数据段，需经 PEReader 读取）。</param>
    /// <param name="type">待扫描的类型定义。</param>
    /// <returns>内部类型全名列表；无内部调用时为空列表。</returns>
    public static IReadOnlyList<string> ExtractMethodBodyCallTypes(PEReader pe, TypeDefinition type)
    {
        var scanner = new BodyScanner(pe);
        scanner.ScanType(type);
        return scanner.Render();
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
    /// 单类型方法体扫描器：复用 BodyScanner 以共享一次「全名→TypeDef 句柄」字典构建（反向扫描大量复用）。
    /// </summary>
    private sealed class BodyScanner
    {
        private readonly PEReader _pe;
        private readonly MetadataReader _reader;
        private readonly HashSet<TypeDefinitionHandle> _collected = new();
        private readonly Provider _provider;
        private Dictionary<string, TypeDefinitionHandle>? _typeDefsByName;

        public BodyScanner(PEReader pe)
        {
            _pe = pe;
            _reader = pe.GetMetadataReader();
            _provider = new Provider(_collected);
        }

        public MetadataReader Reader => _reader;

        /// <summary>
        /// 清空已收集集合，供反向扫描在候选类型间复用实例。
        /// </summary>
        public void Clear() => _collected.Clear();

        /// <summary>
        /// 扫描一个类型定义的全部方法体（含访问器方法体，属性 getter 内的调用同样是该类型的行为调用）。
        /// </summary>
        public void ScanType(TypeDefinition type)
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
                ScanBody(body);
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
        /// 已收集集合是否包含指定全名的类型。
        /// </summary>
        public bool ContainsType(string fullName)
            => TryGetTypeDef(fullName, out var handle) && _collected.Contains(handle);

        /// <summary>
        /// 解码一个方法体的 IL 字节流：按 opcode 操作数表精确读取 token 与跳过定长操作数。
        /// 遇到非法操作数表（越界/超大 switch）时安全中止，保留已收集部分。
        /// </summary>
        private void ScanBody(MethodBodyBlock body)
        {
            var il = body.GetILReader();
            try
            {
                while (il.RemainingBytes > 0)
                {
                    var opcode = il.ReadByte();
                    int kind;
                    if (opcode == 0xFE) // 双字节前缀
                    {
                        if (il.RemainingBytes == 0) return;
                        var op2 = il.ReadByte();
                        kind = op2 < TwoByteOperands.Length ? TwoByteOperands[op2] : OperandKind.None;
                    }
                    else
                    {
                        kind = OneByteOperands[opcode];
                    }

                    switch (kind)
                    {
                        case OperandKind.MethodToken:
                            CollectMethodToken(il.ReadInt32());
                            break;
                        case OperandKind.SignatureToken:
                            CollectSignatureToken(il.ReadInt32());
                            break;
                        case OperandKind.Token:
                            il.ReadInt32(); // 字段/类型/字符串/token 引用：非方法调用边，跳过
                            break;
                        case OperandKind.Byte:
                            il.ReadByte();
                            break;
                        case OperandKind.TwoBytes:
                            il.ReadUInt16();
                            break;
                        case OperandKind.FourBytes:
                            il.ReadInt32();
                            break;
                        case OperandKind.EightBytes:
                            il.ReadInt64();
                            break;
                        case OperandKind.Switch:
                            var count = il.ReadInt32();
                            if (count < 0 || count > il.RemainingBytes / 4) return; // 非法跳转表，中止本方法体
                            for (var i = 0; i < count; i++) il.ReadInt32();
                            break;
                        default:
                            break; // 无操作数或保留 opcode
                    }
                }
            }
            catch (BadImageFormatException)
            {
                // 操作数表与 IL 不匹配或 IL 损坏：中止本方法体，保留已收集部分
            }
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
        /// 解析 MemberRef 的 parent：TypeDefinition 直收；TypeReference 沿 ResolutionScope 判定内部并映射回定义；
        /// TypeSpecification 解码泛型实参收集。
        /// </summary>
        private void CollectMemberParent(EntityHandle parent)
        {
            switch (parent.Kind)
            {
                case HandleKind.TypeDefinition:
                    CollectType((TypeDefinitionHandle)parent);
                    break;
                case HandleKind.TypeReference:
                    ResolveInternalTypeReference((TypeReferenceHandle)parent);
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
        /// 把 TypeReference 判定为内部后映射回 TypeDef 句柄收集；TypeRef 名格式与 <see cref="MetadataNaming.FullName"/> 对齐
        /// （命名空间.名，嵌套 + 分隔，泛型带 arity）。
        /// </summary>
        private void ResolveInternalTypeReference(TypeReferenceHandle handle)
        {
            var tr = _reader.GetTypeReference(handle);
            if (!IsInternalScope(_reader, tr.ResolutionScope)) return;
            var name = TypeReferenceFullName(_reader, handle);
            if (name is not null && TryGetTypeDef(name, out var typeDef))
            {
                CollectType(typeDef);
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
        /// 沿 TypeReference 的 ResolutionScope 链回溯判定是否本程序集（ModuleDefinition = 本模块即内部）。
        /// </summary>
        private static bool IsInternalScope(MetadataReader reader, EntityHandle scope)
        {
            while (true)
            {
                switch (scope.Kind)
                {
                    case HandleKind.ModuleDefinition:
                        return true;
                    case HandleKind.AssemblyReference:
                    case HandleKind.ModuleReference:
                        return false;
                    case HandleKind.TypeReference: // 嵌套类型：沿外层继续上溯
                        scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
                        continue;
                    default:
                        return false;
                }
            }
        }

        /// <summary>
        /// 渲染 TypeReference 全名（命名空间.名，嵌套沿 ResolutionScope 递归用 + 连接）。
        /// </summary>
        private static string? TypeReferenceFullName(MetadataReader reader, TypeReferenceHandle handle)
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
    /// 操作数种类：决定扫描时如何读取/跳过操作数；MethodToken/SignatureToken 是需要提取的调用边。
    /// 用常量类而非 enum：项目内嵌 private enum 会被 TypeLister 归类为实体枚举类型（System.Enum 基类），
    /// 破坏「主程序集无 enum」的既有测试事实。
    /// </summary>
    private static class OperandKind
    {
        public const int None = 0;
        public const int Byte = 1;
        public const int TwoBytes = 2;
        public const int FourBytes = 3;
        public const int EightBytes = 4;
        public const int Token = 5;
        public const int MethodToken = 6;
        public const int SignatureToken = 7;
        public const int Switch = 8;
    }

    /// <summary>
    /// 单字节 opcode 操作数表（0x00-0xFF），数据对照 ECMA-335 III.C.4。
    /// </summary>
    private static readonly int[] OneByteOperands = BuildOneByteOperands();

    private static int[] BuildOneByteOperands()
    {
        var t = new int[256];
        for (var i = 0x0E; i <= 0x13; i++) t[i] = OperandKind.Byte;       // ldarg.s..stloc.s
        t[0x1F] = OperandKind.Byte;                                       // ldc.i4.s
        t[0x20] = OperandKind.FourBytes;                                  // ldc.i4
        t[0x21] = OperandKind.EightBytes;                                 // ldc.i8
        t[0x22] = OperandKind.FourBytes;                                  // ldc.r4
        t[0x23] = OperandKind.EightBytes;                                 // ldc.r8
        t[0x27] = OperandKind.MethodToken;                                // jmp
        t[0x28] = OperandKind.MethodToken;                                // call
        t[0x29] = OperandKind.SignatureToken;                             // calli
        for (var i = 0x2B; i <= 0x37; i++) t[i] = OperandKind.Byte;       // br.s..blt.un.s
        for (var i = 0x38; i <= 0x44; i++) t[i] = OperandKind.FourBytes;  // br..blt.un
        t[0x45] = OperandKind.Switch;                                     // switch
        t[0x6F] = OperandKind.MethodToken;                                // callvirt
        t[0x70] = OperandKind.Token;                                      // cpobj
        t[0x71] = OperandKind.Token;                                      // ldobj
        t[0x72] = OperandKind.Token;                                      // ldstr
        t[0x73] = OperandKind.MethodToken;                                // newobj
        t[0x74] = OperandKind.Token;                                      // castclass
        t[0x75] = OperandKind.Token;                                      // isinst
        t[0x79] = OperandKind.Token;                                      // unbox
        for (var i = 0x7B; i <= 0x81; i++) t[i] = OperandKind.Token;      // ldfld..stobj
        t[0x8C] = OperandKind.Token;                                      // box
        t[0x8D] = OperandKind.Token;                                      // newarr
        t[0x8F] = OperandKind.Token;                                      // ldelema
        t[0xA3] = OperandKind.Token;                                      // ldelem.any
        t[0xA4] = OperandKind.Token;                                      // stelem.any
        t[0xA5] = OperandKind.Token;                                      // unbox.any
        t[0xC2] = OperandKind.Token;                                      // refanyval
        t[0xC6] = OperandKind.Token;                                      // mkrefany
        t[0xD0] = OperandKind.Token;                                      // ldtoken
        t[0xDD] = OperandKind.FourBytes;                                  // leave
        t[0xDE] = OperandKind.Byte;                                       // leave.s
        return t;
    }

    /// <summary>
    /// 双字节前缀 0xFE 操作数表（0xFE00-0xFE1E），数据对照 ECMA-335 III.C.4。
    /// </summary>
    private static readonly int[] TwoByteOperands = BuildTwoByteOperands();

    private static int[] BuildTwoByteOperands()
    {
        var t = new int[0x1F];
        t[0x06] = OperandKind.MethodToken; // ldftn
        t[0x07] = OperandKind.MethodToken; // ldvirtftn
        t[0x09] = OperandKind.TwoBytes;    // ldarg
        t[0x0A] = OperandKind.TwoBytes;    // ldarga
        t[0x0B] = OperandKind.TwoBytes;    // starg
        t[0x0C] = OperandKind.TwoBytes;    // ldloc
        t[0x0D] = OperandKind.TwoBytes;    // ldloca
        t[0x0E] = OperandKind.TwoBytes;    // stloc
        t[0x12] = OperandKind.Byte;        // unaligned.
        t[0x15] = OperandKind.Token;       // initobj
        t[0x16] = OperandKind.Token;       // constrained.
        t[0x19] = OperandKind.Byte;        // no.
        t[0x1C] = OperandKind.Token;       // sizeof
        return t;
    }

    private static readonly GenericContext EmptyContext = new(Array.Empty<string>(), Array.Empty<string>());

    /// <summary>
    /// 泛型参数上下文：类型级与方法级泛型参数名数组，签名解码时按索引取名字（与 SignatureRenderer 一致）。
    /// </summary>
    private readonly record struct GenericContext(string[] TypeParameters, string[] MethodParameters);

    /// <summary>
    /// 签名解码器：只为触发类型解析回调、收集程序集内部 TypeDefinition；返回字符串只是占位，不参与任何展示
    /// （与 ReferenceExtractor.Provider 同构，用于 MethodSpec 泛型实参与 calli 函数指针签名）。
    /// </summary>
    private sealed class Provider : ISignatureTypeProvider<string, GenericContext>
    {
        private readonly HashSet<TypeDefinitionHandle> _collected;

        public Provider(HashSet<TypeDefinitionHandle> collected) => _collected = collected;

        public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "";

        public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            _collected.Add(handle);
            return "";
        }

        public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => "";

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
