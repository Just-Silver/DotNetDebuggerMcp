using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 一个匹配成员：方法名与其元数据 token（可直接用于 ilspycmd -m）。
/// </summary>
/// <param name="Name">方法名（元数据原始名，含 get_/set_ 访问器与 .ctor）。</param>
/// <param name="Token">元数据 token，格式 0x06000005。</param>
public readonly record struct MemberMatch(string Name, string Token);

/// <summary>
/// 成员名搜索：纯元数据读取（PEReader + MetadataReader），不加载程序集、不反编译 IL。
/// 按全限定类型名定位 TypeDefinition，枚举其全部方法并按名字子串匹配，返回可直用于 ilspycmd -m 的 token。
/// </summary>
public static class MemberResolver
{
    /// <summary>
    /// 在指定类型的全部方法中按名字子串搜索（忽略大小写）。
    /// </summary>
    /// <param name="assemblyPath">程序集绝对路径。</param>
    /// <param name="typeName">全限定类型名（嵌套类型以 . 连接，如 Outer.Inner）。</param>
    /// <param name="memberName">成员名子串，忽略大小写。</param>
    /// <returns>TypeFound 为 false 表示未找到该类型；Matches 为匹配成员列表（可能为空）。</returns>
    public static (bool TypeFound, IReadOnlyList<MemberMatch> Matches) FindMembers(string assemblyPath, string typeName, string memberName)
    {
        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (GetFullName(reader, type) != typeName) continue;

            var matches = new List<MemberMatch>();
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                var name = reader.GetString(method.Name);
                if (!name.Contains(memberName, StringComparison.OrdinalIgnoreCase)) continue;
                matches.Add(new MemberMatch(name, $"0x{MetadataTokens.GetToken(methodHandle):x8}"));
            }
            return (true, matches);
        }
        return (false, Array.Empty<MemberMatch>());
    }

    /// <summary>
    /// 拼 TypeDefinition 的全限定名：嵌套类型沿 declaring type 链拼接，命名空间继承自最外层。
    /// </summary>
    private static string GetFullName(MetadataReader reader, TypeDefinition type)
    {
        var name = reader.GetString(type.Name);
        if (type.IsNested)
        {
            var declaring = reader.GetTypeDefinition(type.GetDeclaringType());
            return $"{GetFullName(reader, declaring)}.{name}";
        }
        var ns = reader.GetString(type.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }
}
