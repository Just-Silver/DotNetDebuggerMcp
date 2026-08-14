using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ICSharpCode.Decompiler.Disassembler;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 纯元数据「字段读写点反查」：扫描程序集全部非编译器生成类型的方法体 IL 的字段访问指令
/// （ldfld/ldsfld 读取、stfld/stsfld 写入、ldflda/ldsflda 取地址），按目标字段（FieldDefinitionHandle）收集访问点来源
/// （类型全名::成员签名），供 field_access 工具使用。
/// 方法体读取经 PEReader.GetMethodBody，IL 解码经共享 IlScanHelper；解码异常安全中止并累计降级计数。
/// </summary>
public sealed class FieldAccessScanner
{
    private readonly PEReader _pe;
    private readonly MetadataReader _reader;
    private int _abortedBodies;

    /// <summary>
    /// 以已打开的 PE 读取器构建扫描器（复用其元数据读取器）。
    /// </summary>
    public FieldAccessScanner(PEReader pe)
    {
        _pe = pe;
        _reader = pe.GetMetadataReader();
    }

    /// <summary>
    /// 解码中止计数：方法体 IL 解码遇损坏（IlScanHelper 解码异常）时累加，供调用方感知解码完整性。
    /// </summary>
    public int AbortedBodies => _abortedBodies;

    /// <summary>
    /// 反向扫描程序集全部非编译器生成类型的方法体，收集访问目标字段的成员。
    /// 读取（ldfld/ldsfld）/写入（stfld/stsfld）/取地址（ldflda/ldsflda）分别成段，
    /// 元素为 类型全名::成员签名 行，去重排序。
    /// </summary>
    /// <param name="target">目标字段的定义句柄。</param>
    /// <returns>读取/写入/取地址三段的访问点行列表。</returns>
    public FieldAccessResult Scan(FieldDefinitionHandle target)
    {
        var targetField = _reader.GetFieldDefinition(target);
        var targetDeclaringType = targetField.GetDeclaringType();
        var targetDeclaringTypeName = MetadataNaming.FullName(_reader, _reader.GetTypeDefinition(targetDeclaringType));
        var targetFieldName = _reader.GetString(targetField.Name);

        var reads = new HashSet<string>(StringComparer.Ordinal);
        var writes = new HashSet<string>(StringComparer.Ordinal);
        var addresses = new HashSet<string>(StringComparer.Ordinal);

        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var type = _reader.GetTypeDefinition(typeHandle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(_reader, type)) continue;
            ScanType(type, target, targetDeclaringType, targetDeclaringTypeName, targetFieldName, reads, writes, addresses);
        }

        return new FieldAccessResult(Sort(reads), Sort(writes), Sort(addresses));
    }

    /// <summary>
    /// 按元数据枚举序排序去重集合。
    /// </summary>
    private static List<string> Sort(HashSet<string> set)
    {
        var result = set.ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// 扫描单个类型定义的全部方法体（含访问器方法体，属性 setter 内的写入同样属该类型的字段访问）。
    /// </summary>
    private void ScanType(TypeDefinition type, FieldDefinitionHandle target, TypeDefinitionHandle targetDeclaringType,
        string targetDeclaringTypeName, string targetFieldName,
        HashSet<string> reads, HashSet<string> writes, HashSet<string> addresses)
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
            ScanBody(body, method, target, targetDeclaringType, targetDeclaringTypeName, targetFieldName, reads, writes, addresses);
        }
    }

    /// <summary>
    /// 解码一个方法体的 IL 字节流（经 IlScanHelper 回调驱动）：字段访问指令按 opcode 分类后解析 token 判定是否命中目标字段，
    /// 命中先记分类掩码，方法体扫描结束再统一渲染来源签名（免为无命中方法付出签名渲染成本）；
    /// 解码异常时中止并累计 AbortedBodies，保留已收集部分。
    /// </summary>
    private void ScanBody(MethodBodyBlock body, MethodDefinition method,
        FieldDefinitionHandle target, TypeDefinitionHandle targetDeclaringType,
        string targetDeclaringTypeName, string targetFieldName,
        HashSet<string> reads, HashSet<string> writes, HashSet<string> addresses)
    {
        var mask = 0; // bit0=读取 bit1=写入 bit2=取地址
        var il = body.GetILReader();
        IlScanHelper.DecodeMethodBody(il, instr =>
        {
            var bit = instr.Opcode switch
            {
                ILOpCode.Ldfld or ILOpCode.Ldsfld => 1,
                ILOpCode.Stfld or ILOpCode.Stsfld => 2,
                ILOpCode.Ldflda or ILOpCode.Ldsflda => 4,
                _ => 0,
            };
            if (bit == 0) return;
            if (!MatchesField(instr.RawToken, target, targetDeclaringType, targetDeclaringTypeName, targetFieldName)) return;
            mask |= bit;
        }, () => _abortedBodies++);
        if (mask == 0) return;

        var declaringType = _reader.GetTypeDefinition(method.GetDeclaringType());
        var signature = SignatureRenderer.RenderMemberSignature(_reader, declaringType, method);
        var source = $"{MetadataNaming.FullName(_reader, declaringType)}::{signature}";
        if ((mask & 1) != 0) reads.Add(source);
        if ((mask & 2) != 0) writes.Add(source);
        if ((mask & 4) != 0) addresses.Add(source);
    }

    /// <summary>
    /// 字段 token 是否指向目标字段：FieldDef 直比 token == target；MemberRef 沿 parent（TypeDef/TypeRef/TypeSpec）解析——
    /// 内部字段且 declaring type == 目标声明类型 且 MemberRef.Name == 目标字段名 时命中。
    /// </summary>
    private bool MatchesField(int rawToken, FieldDefinitionHandle target, TypeDefinitionHandle targetDeclaringType,
        string targetDeclaringTypeName, string targetFieldName)
    {
        var handle = MetadataTokens.EntityHandle(rawToken);
        if (handle.Kind == HandleKind.FieldDefinition)
        {
            return (FieldDefinitionHandle)handle == target;
        }
        if (handle.Kind != HandleKind.MemberReference)
        {
            return false;
        }
        var memberRef = _reader.GetMemberReference((MemberReferenceHandle)handle);
        if (!string.Equals(_reader.GetString(memberRef.Name), targetFieldName, StringComparison.Ordinal)) return false;
        return MemberRefParentIsTargetType(memberRef.Parent, targetDeclaringType, targetDeclaringTypeName);
    }

    /// <summary>
    /// 判定 MemberRef 的 parent 是否解析为目标字段的声明类型：TypeDef 直比；TypeRef 经归属判定为内部且全名一致；
    /// TypeSpecification 解码收集底层类型定义句柄比对（覆盖泛型实例化字段如 GenericBox&lt;int&gt;.Data）。
    /// </summary>
    private bool MemberRefParentIsTargetType(EntityHandle parent, TypeDefinitionHandle targetDeclaringType, string targetDeclaringTypeName)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeDefinition:
                return (TypeDefinitionHandle)parent == targetDeclaringType;
            case HandleKind.TypeReference:
                var trHandle = (TypeReferenceHandle)parent;
                var (isInternal, _) = MetadataNaming.TypeReferenceScope(_reader, trHandle);
                if (!isInternal) return false;
                var fullName = MetadataNaming.TypeReferenceFullName(_reader, trHandle);
                return fullName == targetDeclaringTypeName;
            case HandleKind.TypeSpecification:
                var collected = new HashSet<TypeDefinitionHandle>();
                try
                {
                    _reader.GetTypeSpecification((TypeSpecificationHandle)parent).DecodeSignature(new TypeDefCollector(_reader, collected), null);
                }
                catch (BadImageFormatException)
                {
                    return false; // 忽略损坏的类型规范签名
                }
                return collected.Contains(targetDeclaringType);
            default:
                return false;
            // 其余 parent 作用域（如方法定义等非法字段父）非字段引用
        }
    }

    /// <summary>
    /// 签名解码器：仅为收集 TypeSpecification 解码过程中出现的内部 TypeDefinition 句柄（覆盖泛型实例化字段的底层声明类型）。
    /// 返回布尔只作占位，不参与任何判定。
    /// </summary>
    private sealed class TypeDefCollector : ISignatureTypeProvider<bool, object?>
    {
        private readonly MetadataReader _reader;
        private readonly HashSet<TypeDefinitionHandle> _collected;

        public TypeDefCollector(MetadataReader reader, HashSet<TypeDefinitionHandle> collected)
        {
            _reader = reader;
            _collected = collected;
        }

        public bool GetPrimitiveType(PrimitiveTypeCode typeCode) => false;

        public bool GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
        {
            _collected.Add(handle);
            return true;
        }

        public bool GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) => false;

        public bool GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
            => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public bool GetGenericInstantiation(bool genericType, System.Collections.Immutable.ImmutableArray<bool> typeArguments) => false;

        public bool GetGenericTypeParameter(object? genericContext, int index) => false;

        public bool GetGenericMethodParameter(object? genericContext, int index) => false;

        public bool GetArrayType(bool elementType, ArrayShape shape) => false;

        public bool GetSZArrayType(bool elementType) => false;

        public bool GetByReferenceType(bool elementType) => false;

        public bool GetPointerType(bool elementType) => false;

        public bool GetPinnedType(bool elementType) => false;

        public bool GetModifiedType(bool modifier, bool unmodifiedType, bool isRequired) => false;

        public bool GetFunctionPointerType(MethodSignature<bool> signature) => false;
    }
}

/// <summary>
/// 字段访问点扫描结果：读取/写入/取地址三段的来源成员行（类型全名::成员签名，去重排序）。
/// </summary>
/// <param name="Reads">读取该字段的成员（ldfld/ldsfld）。</param>
/// <param name="Writes">写入该字段的成员（stfld/stsfld）。</param>
/// <param name="Addresses">取地址的成员（ldflda/ldsflda）。</param>
public readonly record struct FieldAccessResult(IReadOnlyList<string> Reads, IReadOnlyList<string> Writes, IReadOnlyList<string> Addresses);
