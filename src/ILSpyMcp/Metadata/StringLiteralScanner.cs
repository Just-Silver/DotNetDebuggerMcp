using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 纯元数据「字符串字面量反查」：扫描程序集全部（或指定）类型的方法体 IL 的 ldstr 指令，
/// 按用户字符串子串（忽略大小写）匹配，反查字符串字面量所在的成员，供 search_string 工具使用。
/// 方法体读取经 PEReader.GetMethodBody，IL 解码经共享 IlScanHelper；解码异常安全中止并累计降级计数。
/// </summary>
public sealed class StringLiteralScanner
{
    private readonly PEReader _pe;
    private readonly MetadataReader _reader;
    private int _abortedBodies;

    /// <summary>
    /// 以已打开的 PE 读取器构建扫描器（复用其元数据读取器）。
    /// </summary>
    public StringLiteralScanner(PEReader pe)
    {
        _pe = pe;
        _reader = pe.GetMetadataReader();
    }

    /// <summary>
    /// 元数据读取器。
    /// </summary>
    public MetadataReader Reader => _reader;

    /// <summary>
    /// 解码中止计数：方法体 IL 解码遇损坏（IlScanHelper 解码异常）时累加，供调用方感知解码完整性。
    /// </summary>
    public int AbortedBodies => _abortedBodies;

    /// <summary>
    /// 扫描类型方法体，反查字符串字面量含指定子串（忽略大小写）的成员。
    /// onlyType 有值时仅扫描该类型，否则扫描程序集全部非编译器生成类型。
    /// </summary>
    /// <param name="substring">待匹配的字符串字面量子串（忽略大小写）。</param>
    /// <param name="onlyType">限定的类型定义句柄；为 null 时扫描全部非编译器生成类型。</param>
    /// <returns>命中的字符串字面量条目列表（含来源类型全名/成员签名/token/原文），按元数据枚举序。</returns>
    public IReadOnlyList<StringHit> Scan(string substring, TypeDefinitionHandle? onlyType = null)
    {
        var results = new List<StringHit>();
        if (onlyType is { } handle)
        {
            ScanType(handle, substring, results);
            return results;
        }
        foreach (var typeHandle in _reader.TypeDefinitions)
        {
            ScanType(typeHandle, substring, results);
        }
        return results;
    }

    /// <summary>
    /// 扫描单个类型定义的全部方法体（含访问器方法体，属性 getter 内的字符串同样属该类型的字面量）。
    /// </summary>
    private void ScanType(TypeDefinitionHandle typeHandle, string substring, List<StringHit> results)
    {
        var type = _reader.GetTypeDefinition(typeHandle);
        if (CompilerGeneratedFilter.IsCompilerGenerated(_reader, type)) return;
        var fullName = MetadataNaming.FullName(_reader, type);
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
            ScanBody(body, methodHandle, method, fullName, substring, results);
        }
    }

    /// <summary>
    /// 解码一个方法体的 IL 字节流（经 IlScanHelper 回调驱动）：ldstr 指令解码用户字符串并匹配子串；
    /// 命中先收集原文，方法体扫描结束再统一渲染签名（免为无命中方法付出签名渲染成本）；
    /// 解码异常时中止并累计 AbortedBodies，保留已收集部分。
    /// </summary>
    private void ScanBody(MethodBodyBlock body, MethodDefinitionHandle methodHandle, MethodDefinition method,
        string fullName, string substring, List<StringHit> results)
    {
        var matches = new List<string>();
        var il = body.GetILReader();
        IlScanHelper.DecodeMethodBody(il, instr =>
        {
            if (instr.Opcode != ILOpCode.Ldstr) return;
            string? value;
            try
            {
                value = _reader.GetUserString(MetadataTokens.UserStringHandle(instr.RawToken));
            }
            catch (BadImageFormatException)
            {
                return; // 忽略损坏的用户字符串堆引用
            }
            if (value is not null && value.Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                matches.Add(value);
            }
        }, () => _abortedBodies++);
        if (matches.Count == 0) return;

        var declaringType = _reader.GetTypeDefinition(method.GetDeclaringType());
        var signature = SignatureRenderer.RenderMemberSignature(_reader, declaringType, method);
        var memberToken = $"0x{MetadataTokens.GetToken(methodHandle):x8}";
        foreach (var value in matches)
        {
            results.Add(new StringHit(fullName, signature, memberToken, value));
        }
    }
}

/// <summary>
/// 一条字符串字面量命中：来源类型全名、成员签名、成员 token（0x 十六进制）与匹配到的用户字符串原文。
/// </summary>
public readonly record struct StringHit(string TypeFullName, string MemberSignature, string MemberToken, string Value);
