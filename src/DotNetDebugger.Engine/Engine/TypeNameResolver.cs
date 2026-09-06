using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebugger.Engine.Engine;

/// <summary>
/// 运行时类型全名解析：按 CorDebugClass 的 (模块路径, TypeDef/TypeRef token) 从模块元数据解出「命名空间.类型名」。
/// TypeDef/TypeRef 行自身携带 namespace+name，嵌套类型走 enclosing 链（'.' 连接，与反编译侧全名风格一致），
/// 无需跨程序集解析。按 (模块路径, token) 缓存（同 DLL 同 token 恒定，可跨会话复用）；
/// 解析失败返回 null（调用方降级为 token 展示）。全部调用在引擎命令泵 MTA 线程，锁仅防御。
/// </summary>
public static class TypeNameResolver
{
    private static readonly object Gate = new();
    private static readonly Dictionary<(string ModulePath, int Token), string?> Cache = new();

    public static string? Resolve(string modulePath, int classToken)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue((modulePath, classToken), out var cached)) return cached;
        }

        string? result = null;
        try
        {
            using var fs = File.OpenRead(modulePath);
            using var pe = new PEReader(fs);
            result = ResolveCore(pe.GetMetadataReader(), classToken);
        }
        catch
        {
            // 模块文件不可读等：降级 null，调用方回退 token 展示
        }

        lock (Gate) Cache[(modulePath, classToken)] = result;
        return result;
    }

    private static string? ResolveCore(MetadataReader reader, int token)
    {
        switch (token & 0xFF000000)
        {
            case 0x02000000: // TypeDef
            {
                var td = reader.GetTypeDefinition(MetadataTokens.TypeDefinitionHandle(token));
                var enclosing = td.IsNested
                    ? ResolveCore(reader, MetadataTokens.GetToken(td.GetDeclaringType()))
                    : null;
                return BuildName(reader, td.Namespace, reader.GetString(td.Name), enclosing);
            }
            case 0x01000000: // TypeRef
            {
                var tr = reader.GetTypeReference(MetadataTokens.TypeReferenceHandle(token));
                var enclosing = tr.ResolutionScope.Kind == HandleKind.TypeReference
                    ? ResolveCore(reader, MetadataTokens.GetToken((TypeReferenceHandle)tr.ResolutionScope))
                    : null;
                return BuildName(reader, tr.Namespace, reader.GetString(tr.Name), enclosing);
            }
            default:
                return null; // TypeSpec 等不做展开（调用方降级）
        }
    }

    private static string BuildName(MetadataReader reader, StringHandle namespaceHandle, string name, string? enclosing)
    {
        var ns = namespaceHandle.IsNil ? "" : reader.GetString(namespaceHandle);
        if (string.IsNullOrEmpty(enclosing)) return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
        // 嵌套类型：namespace 挂在最外层（enclosing 已含），嵌套行自身 namespace 为空
        return string.IsNullOrEmpty(ns) ? $"{enclosing}.{name}" : $"{ns}.{enclosing}.{name}";
    }
}
