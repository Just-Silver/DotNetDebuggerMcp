using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace DotNetDebugger.Decompiler.Metadata;

/// <summary>
/// 纯元数据「程序集概览」读取：目标框架、引用的程序集清单与入口点，供 assembly_info 工具输出。 与其它元数据组件一致只读
/// PEReader/MetadataReader，不加载程序集、不反编译 IL。
/// </summary>
public static class AssemblyInfoReader
{
    /// <summary>
    /// 读取 TargetFrameworkAttribute 自定义特性声明的目标框架（如 ".NETCoreApp,Version=v10.0"）； 特性缺失或解码失败时返回 null。
    /// 特性 blob 以 2 字节 prolog（0x0001）开头，随后是第一个构造参数 SerString： 压缩 uint32 字节长度 + UTF-8 字符（Roslyn 实际写入为
    /// UTF-8，而非规范的 UTF-16）。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <returns>目标框架标识；无特性/无法解析时返回 null。</returns>
    public static string? GetTargetFramework(MetadataReader reader)
    {
        try
        {
            var asm = reader.GetAssemblyDefinition();
            foreach (var attrHandle in asm.GetCustomAttributes())
            {
                var attr = reader.GetCustomAttribute(attrHandle);
                if (GetConstructorTypeName(reader, attr.Constructor) == "System.Runtime.Versioning.TargetFrameworkAttribute")
                {
                    return ReadStringBlob(reader, attr.Value);
                }
            }
            return null;
        }
        catch
        {
            return null; // 特性解析失败按缺省处理，不阻断概览输出
        }
    }

    /// <summary>
    /// 列出程序集引用的全部程序集（名 + 版本），按元数据 AssemblyRef 表枚举序。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <returns>(程序集名, 版本) 列表。</returns>
    public static IReadOnlyList<(string Name, Version Version)> GetReferences(MetadataReader reader)
        => reader.AssemblyReferences.Select(r =>
        {
            var ar = reader.GetAssemblyReference(r);
            return (reader.GetString(ar.Name), ar.Version);
        }).ToList();

    /// <summary>
    /// 读取程序集入口点：COR 头 EntryPoint token 非 0 时定位方法并返回 `类型全名::方法名`； 无入口点（如纯类库）或任何异常时返回 null。
    /// </summary>
    /// <param name="pe">PE 读取器（读 COR 头）。</param>
    /// <param name="reader">元数据读取器。</param>
    /// <returns>如 "MyApp.Program::Main"；无入口点/无法解析时返回 null。</returns>
    public static string? GetEntryPoint(PEReader pe, MetadataReader reader)
    {
        try
        {
            var token = pe.PEHeaders.CorHeader?.EntryPointTokenOrRelativeVirtualAddress ?? 0;
            if (token == 0) return null;
            var method = reader.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(token));
            var type = reader.GetTypeDefinition(method.GetDeclaringType());
            return $"{MetadataNaming.FullName(reader, type)}::{reader.GetString(method.Name)}";
        }
        catch
        {
            return null; // 非托管入口/损坏 token 等按无入口点处理
        }
    }

    /// <summary>
    /// 解析自定义特性构造函数的声明类型全名（MemberReference/MethodDefinition 两种形式）。
    /// </summary>
    private static string? GetConstructorTypeName(MetadataReader reader, EntityHandle constructor)
        => constructor.Kind switch
        {
            HandleKind.MemberReference => GetMemberReferenceTypeName(reader, reader.GetMemberReference((MemberReferenceHandle)constructor)),
            HandleKind.MethodDefinition => GetMethodDefinitionTypeName(reader, reader.GetMethodDefinition((MethodDefinitionHandle)constructor)),
            _ => null,
        };

    /// <summary>
    /// 解析 MemberReference 声明类型全名：Parent 为 TypeReference/TypeDefinition 时分别渲染；否则 null。
    /// </summary>
    private static string? GetMemberReferenceTypeName(MetadataReader reader, MemberReference memberRef)
        => memberRef.Parent.Kind switch
        {
            HandleKind.TypeReference => MetadataNaming.TypeReferenceFullName(reader, (TypeReferenceHandle)memberRef.Parent),
            HandleKind.TypeDefinition => MetadataNaming.FullName(reader, reader.GetTypeDefinition((TypeDefinitionHandle)memberRef.Parent)),
            _ => null,
        };

    /// <summary>
    /// 解析 MethodDefinition 声明类型全名。
    /// </summary>
    private static string? GetMethodDefinitionTypeName(MetadataReader reader, MethodDefinition method)
        => MetadataNaming.FullName(reader, reader.GetTypeDefinition(method.GetDeclaringType()));

    /// <summary>
    /// 解码自定义特性 blob 的首个 SerString 参数：跳过 2 字节 prolog（0x0001）→ 压缩 uint32 字节长度 → UTF-8 字符。
    /// </summary>
    private static string? ReadStringBlob(MetadataReader reader, BlobHandle blobHandle)
    {
        var blob = reader.GetBlobReader(blobHandle);
        if (blob.Length < 2) return null;
        blob.ReadUInt16(); // 跳过 2 字节 prolog（0x0001）
        var length = blob.ReadCompressedInteger();
        if (length < 0 || blob.RemainingBytes < length) return null;
        return Encoding.UTF8.GetString(blob.ReadBytes(length));
    }
}