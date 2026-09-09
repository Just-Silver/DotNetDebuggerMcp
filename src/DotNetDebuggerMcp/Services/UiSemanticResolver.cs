using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Services;

/// <summary>
/// U1 UI 语义反查：对被控进程主模块（apphost 的同名 dll——现代 .NET 布局元数据在 dll 而非原生 exe）做纯元数据
/// （PEReader，不加载程序集、不反编译 IL）成员名倒排索引，按「子串忽略大小写」反查与 UI 元素 Name/AutomationId 同名的
/// 成员候选（字段/方法/属性/事件），供 ui_find 命中元素附「语义候选: 类型全名.成员」标注。索引按 assemblyPath
/// 进程内缓存（同一程序集只读盘一次）。
/// </summary>
internal static class UiSemanticResolver
{
    private sealed record TypeIndex(IReadOnlyList<(string TypeName, string Member)> Entries);

    /// <summary>按程序集路径缓存的成员名索引（key 忽略大小写；值为 null 表示模块不可读，避免反复读坏文件）。</summary>
    private static readonly ConcurrentDictionary<string, TypeIndex?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 反查指定程序集中与 elementName 子串忽略大小写匹配的成员候选（格式「类型全名.成员」，嵌套类型用 + 连接）。
    /// </summary>
    /// <param name="assemblyPath">程序集绝对路径（apphost 场景传同名 dll）。</param>
    /// <param name="elementName">控件 Name/AutomationId 文本。</param>
    /// <returns>候选列表；路径为空/文件不可读/无匹配 → null。</returns>
    public static IReadOnlyList<string>? Lookup(string assemblyPath, string elementName)
    {
        if (string.IsNullOrWhiteSpace(elementName)) return null;
        if (string.IsNullOrWhiteSpace(assemblyPath) || !File.Exists(assemblyPath)) return null;

        var index = Cache.GetOrAdd(assemblyPath, BuildIndex);
        if (index is null) return null;

        List<string>? matches = null;
        foreach (var (typeName, member) in index.Entries)
        {
            if (!member.Contains(elementName, StringComparison.OrdinalIgnoreCase)) continue;
            (matches ??= new List<string>(4)).Add(typeName + "." + member);
        }
        return matches;
    }

    private static TypeIndex? BuildIndex(string assemblyPath)
    {
        try
        {
            using var fs = File.OpenRead(assemblyPath);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var entries = new List<(string, string)>(512);
            foreach (var typeHandle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(typeHandle);
                // 编译器生成类型（<...> 闭包/状态机/顶层语句）跳过——不对 UI 业务语义产生候选
                if (reader.GetString(type.Name).Contains('<')) continue;
                var typeName = FullName(reader, type);
                EnumerateMembers(reader, type, typeName, entries);
            }
            return entries.Count == 0 ? null : new TypeIndex(entries);
        }
        catch
        {
            return null; // 模块不可读（权限/占用/非托管程序集）——调用方降级为无语义标注
        }
    }

    /// <summary>枚举类型的可反查成员（字段→方法→属性→事件；跳过编译器生成与访问器噪音）。</summary>
    private static void EnumerateMembers(MetadataReader reader, TypeDefinition type, string typeName, List<(string, string)> entries)
    {
        var eventNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var eventHandle in type.GetEvents())
            eventNames.Add(reader.GetString(reader.GetEventDefinition(eventHandle).Name));

        foreach (var handle in type.GetFields())
        {
            var name = reader.GetString(reader.GetFieldDefinition(handle).Name);
            if (name.Contains('<')) continue;                    // 自动属性 backing field
            if (eventNames.Contains(name)) continue;             // 字段式事件 backing field（事件名已代表）
            entries.Add((typeName, name));
        }
        foreach (var handle in type.GetMethods())
        {
            var name = reader.GetString(reader.GetMethodDefinition(handle).Name);
            if (name.Contains('<')) continue;                    // 编译器生成方法（MoveNext/匿名方法入口等）
            if (IsAccessor(name)) continue;                      // 属性/事件访问器（get_/set_/add_/remove_）
            entries.Add((typeName, name));
        }
        foreach (var handle in type.GetProperties())
        {
            var name = reader.GetString(reader.GetPropertyDefinition(handle).Name);
            if (name == "Item") continue;                        // 索引器元数据名统一为 Item，非业务语义
            entries.Add((typeName, name));
        }
        foreach (var handle in type.GetEvents())
            entries.Add((typeName, reader.GetString(reader.GetEventDefinition(handle).Name)));
    }

    private static bool IsAccessor(string name)
        => name.StartsWith("get_", StringComparison.Ordinal)
        || name.StartsWith("set_", StringComparison.Ordinal)
        || name.StartsWith("add_", StringComparison.Ordinal)
        || name.StartsWith("remove_", StringComparison.Ordinal)
        || name.Contains(".get_", StringComparison.Ordinal)
        || name.Contains(".set_", StringComparison.Ordinal)
        || name.Contains(".add_", StringComparison.Ordinal)
        || name.Contains(".remove_", StringComparison.Ordinal)
        || name is ".ctor" or ".cctor";

    /// <summary>类型全名：命名空间.外层+嵌套（对齐仓库 MetadataNaming 用 + 表示嵌套）。</summary>
    private static string FullName(MetadataReader reader, TypeDefinition type)
    {
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            var outer = reader.GetTypeDefinition(declaring);
            return FullName(reader, outer) + "+" + reader.GetString(type.Name);
        }
        var ns = reader.GetString(type.Namespace);
        var name = reader.GetString(type.Name);
        return ns.Length == 0 ? name : ns + "." + name;
    }
}
