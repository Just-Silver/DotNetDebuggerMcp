using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 一个匹配成员：方法名与其元数据 token（可直接用于进程内成员反编译）。
/// </summary>
/// <param name="Name">方法名（元数据原始名，含 get_/set_ 访问器与 .ctor）。</param>
/// <param name="Token">元数据 token，格式 0x06000005。</param>
public readonly record struct MemberMatch(string Name, string Token);

/// <summary>
/// 成员搜索聚合结果：类型是否命中、匹配成员列表、相近成员名。
/// </summary>
/// <param name="TypeFound">是否找到指定类型。</param>
/// <param name="Matches">按名字子串匹配到的成员（可能为空）。</param>
/// <param name="SimilarNames">无匹配时的相近成员名（仅 TypeFound 且 Matches 为空时非空，最多 5 个、按名字序）。</param>
public readonly record struct MemberSearchResult(bool TypeFound, IReadOnlyList<MemberMatch> Matches, IReadOnlyList<string> SimilarNames)
{
    /// <summary>
    /// 两元素解构（TypeFound/Matches）：与旧返回值形态兼容，既有调用点无需改动。
    /// </summary>
    public void Deconstruct(out bool typeFound, out IReadOnlyList<MemberMatch> matches)
    {
        typeFound = TypeFound;
        matches = Matches;
    }

    /// <summary>
    /// 三元素解构（含相近成员名）。
    /// </summary>
    public void Deconstruct(out bool typeFound, out IReadOnlyList<MemberMatch> matches, out IReadOnlyList<string> similarNames)
    {
        typeFound = TypeFound;
        matches = Matches;
        similarNames = SimilarNames;
    }
}

/// <summary>
/// 成员名搜索：纯元数据读取（PEReader + MetadataReader），不加载程序集、不反编译 IL。 经 <see cref="MetadataNaming.FindType"/>
/// 按输入定位 TypeDefinition（+ 与 . 嵌套分隔均可），枚举其全部方法并按名字子串匹配，返回可直用于进程内成员反编译的 token。
/// 默认排除属性/事件访问器方法，无匹配时给出相近成员名供调用方拼「未找到」提示。
/// </summary>
public static class MemberResolver
{
    /// <summary>
    /// 在指定类型的全部方法中按名字子串搜索（忽略大小写）。
    /// </summary>
    /// <param name="assemblyPath">程序集绝对路径。</param>
    /// <param name="typeName">全限定类型名（嵌套类型以 + 或 . 连接，如 Outer+Inner / Outer.Inner）。</param>
    /// <param name="memberName">成员名子串，忽略大小写。</param>
    /// <param name="includeAccessors">为 true 时不过滤属性/事件访问器方法（get_/set_/add_/remove_）。</param>
    /// <returns>TypeFound 为 false 表示未找到该类型；Matches 为匹配成员列表（可能为空）；SimilarNames 仅当 TypeFound 且 Matches 为空时非空。</returns>
    public static MemberSearchResult FindMembers(string assemblyPath, string typeName, string memberName, bool includeAccessors = false)
    {
        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        var typeHandle = MetadataNaming.FindType(reader, typeName);
        if (typeHandle is null) return new MemberSearchResult(false, Array.Empty<MemberMatch>(), Array.Empty<string>());

        var type = reader.GetTypeDefinition(typeHandle.Value);
        var matches = new List<MemberMatch>();
        var names = new List<string>();
        foreach (var methodHandle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            var name = reader.GetString(method.Name);
            if (!includeAccessors && IsAccessorName(name)) continue;
            names.Add(name);
            if (!name.Contains(memberName, StringComparison.OrdinalIgnoreCase)) continue;
            matches.Add(new MemberMatch(name, $"0x{MetadataTokens.GetToken(methodHandle):x8}"));
        }
        var similar = matches.Count == 0 ? SimilarNameMatcher.FindSimilar(names, memberName) : Array.Empty<string>();
        return new MemberSearchResult(true, matches, similar);
    }

    /// <summary>
    /// 无匹配时返回相近成员名：编辑距离 ≤ 2 或与查询名共享 ≥ 4 字符公共前缀，按名字序取前 5 个。
    /// 判定算法统一走 <see cref="SimilarNameMatcher"/>（与类型名相近判定共用，避免两处实现漂移）。
    /// </summary>
    private static IReadOnlyList<string> FindSimilarNames(List<string> names, string query)
        => SimilarNameMatcher.FindSimilar(names, query);

    /// <summary>
    /// 判断方法名是否为属性/事件访问器（get_X/set_X/add_/remove_）——与 SignatureRenderer.IsAccessorName 同逻辑。
    /// 显式接口实现的访问器名为 Ns.IFoo.get_Value（含 '.'），一并排除。
    /// </summary>
    private static bool IsAccessorName(string name)
        => name.StartsWith("get_", StringComparison.Ordinal)
        || name.StartsWith("set_", StringComparison.Ordinal)
        || name.StartsWith("add_", StringComparison.Ordinal)
        || name.StartsWith("remove_", StringComparison.Ordinal)
        || name.Contains(".get_", StringComparison.Ordinal)
        || name.Contains(".set_", StringComparison.Ordinal)
        || name.Contains(".add_", StringComparison.Ordinal)
        || name.Contains(".remove_", StringComparison.Ordinal);
}
