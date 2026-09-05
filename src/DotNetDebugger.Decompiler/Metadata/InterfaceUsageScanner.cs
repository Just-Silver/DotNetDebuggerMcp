using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebugger.Decompiler.Metadata;

/// <summary>
/// 纯元数据「接口调用点反查」：反扫程序集全部非编译器生成类型的方法体 IL 调用指令（call/callvirt/newobj/ldftn/ldvirtftn/jmp/calli）， 凡调用
/// token 目标声明类型为指定接口（内部接口 MethodDef 直比句柄；外部接口 MemberRef 沿 parent TypeRef 全名判定）时， 记录 来源类型::来源成员名 →
/// 接口成员名 调用点行，供 interface_usage 工具使用。 与 hierarchy 的实现者互补：本类回答「程序集内哪些方法体调用了接口成员」，hierarchy
/// 回答「谁实现了它」。 方法体读取经 PEReader.GetMethodBody，IL 解码经共享 IlScanHelper；解码异常安全中止并累计降级计数。
/// </summary>
public sealed class InterfaceUsageScanner
{
    private readonly PEReader _pe;
    private readonly MetadataReader _reader;
    private int _abortedBodies;

    /// <summary>
    /// 以已打开的 PE 读取器构建扫描器（复用其元数据读取器）。
    /// </summary>
    public InterfaceUsageScanner(PEReader pe)
    {
        _pe = pe;
        _reader = pe.GetMetadataReader();
    }

    /// <summary>
    /// 解码中止计数：方法体 IL 解码遇损坏（IlScanHelper 解码异常）时累加，供调用方感知解码完整性。
    /// </summary>
    public int AbortedBodies => _abortedBodies;

    /// <summary>
    /// 反扫程序集全部非编译器生成类型的方法体，收集调用指定接口成员的方法体调用点。 调用 token 目标解析：内部接口经 MethodDef 声明类型直比 iface 句柄；外部接口经
    /// MemberRef parent TypeRef 全名 等于 ifaceFullName 判定；MethodSpec（泛型实例化调用）解包归约到方法再判。调用点元素为
    /// <c>来源类型::来源成员名 → 接口成员名</c> 行，去重排序。
    /// </summary>
    /// <param name="iface">目标接口的定义句柄（程序集内）。</param>
    /// <param name="ifaceFullName">目标接口的规范全名（ <see cref="MetadataNaming.FullName"/> 输出）。</param>
    /// <returns>调用点行列表；程序集内无调用点时为空列表。</returns>
    public IReadOnlyList<string> FindCallSites(TypeDefinitionHandle iface, string ifaceFullName)
    {
        var callSites = new HashSet<string>(StringComparer.Ordinal);
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            var type = _reader.GetTypeDefinition(typeHandle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(_reader, type)) continue;
            ScanType(type, iface, ifaceFullName, callSites);
        }
        var result = callSites.ToList();
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// 扫描单个类型定义的全部方法体（含访问器方法体，属性 getter 内调用接口成员同样属于调用点）。
    /// </summary>
    private void ScanType(TypeDefinition type, TypeDefinitionHandle iface, string ifaceFullName, HashSet<string> callSites)
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
            ScanBody(body, method, iface, ifaceFullName, callSites);
        }
    }

    /// <summary>
    /// 解码一个方法体的 IL 字节流（经 IlScanHelper 回调驱动）：调用指令 token 命中接口成员时记入本方法的命中集合，
    /// 方法体扫描结束再统一渲染调用点行（免为无命中方法付出全名渲染成本）；解码异常时中止并累计 AbortedBodies，保留已收集部分。
    /// </summary>
    private void ScanBody(MethodBodyBlock body, MethodDefinition sourceMethod,
        TypeDefinitionHandle iface, string ifaceFullName, HashSet<string> callSites)
    {
        var ifaceMembers = new HashSet<string>(StringComparer.Ordinal);
        var il = body.GetILReader();
        IlScanHelper.DecodeMethodBody(il, instr =>
        {
            switch (instr.Opcode)
            {
                case ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj or ILOpCode.Jmp
                     or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Calli:
                    if (ResolveInterfaceMember(instr.RawToken, iface, ifaceFullName) is { } name)
                    {
                        ifaceMembers.Add(name);
                    }
                    break;
            }
        }, () => _abortedBodies++);
        if (ifaceMembers.Count == 0) return;

        var declaringType = _reader.GetTypeDefinition(sourceMethod.GetDeclaringType());
        var sourceType = MetadataNaming.FullName(_reader, declaringType);
        var sourceName = _reader.GetString(sourceMethod.Name);
        foreach (var member in ifaceMembers)
        {
            callSites.Add($"{sourceType}::{sourceName} → {member}");
        }
    }

    /// <summary>
    /// 调用 token 目标是否指向接口成员：MethodDef 声明类型直比 iface 句柄，返回接口成员元数据名； MemberRef parent 为接口（TypeRef 全名等于
    /// ifaceFullName 或 TypeDef 句柄直比）时返回成员名； MethodSpec（泛型实例化调用）解包归约到方法再判；其余返回 null。
    /// </summary>
    private string? ResolveInterfaceMember(int rawToken, TypeDefinitionHandle iface, string ifaceFullName)
    {
        var handle = MetadataTokens.EntityHandle(rawToken);
        switch (handle.Kind)
        {
            case HandleKind.MethodDefinition:
                var methodDef = _reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                return methodDef.GetDeclaringType() == iface ? _reader.GetString(methodDef.Name) : null;

            case HandleKind.MemberReference:
                var memberRef = _reader.GetMemberReference((MemberReferenceHandle)handle);
                return MemberRefParentIsIface(memberRef.Parent, iface, ifaceFullName) ? _reader.GetString(memberRef.Name) : null;

            case HandleKind.MethodSpecification:
                var spec = _reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                return ResolveInterfaceMember(MetadataTokens.GetToken(spec.Method), iface, ifaceFullName);

            default:
                return null;
        }
    }

    /// <summary>
    /// 判定 MemberRef 的 parent 是否为目标接口：TypeDef 直比 iface 句柄（内部接口经 MemberRef 访问）； TypeRef
    /// 沿全名比较（跨程序集外部接口，如 BCL/NuGet 中类型全名等于目标接口全名）。其余 parent 作用域非接口。
    /// </summary>
    private bool MemberRefParentIsIface(EntityHandle parent, TypeDefinitionHandle iface, string ifaceFullName)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeDefinition:
                return (TypeDefinitionHandle)parent == iface;

            case HandleKind.TypeReference:
                return MetadataNaming.TypeReferenceFullName(_reader, (TypeReferenceHandle)parent) == ifaceFullName;

            default:
                return false;
        }
    }
}