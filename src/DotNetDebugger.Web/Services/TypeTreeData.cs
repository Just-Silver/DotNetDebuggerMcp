using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotNetDebugger.Web.Services;

/// <summary>
/// 左侧类型树的数据源：给定程序集，枚举实体类型并按「命名空间 → 类型全名」分组。
/// 纯元数据读取（秒回，不反编译），一次枚举缓存；树组件展开命名空间时取对应组即可（UI 懒加载）。
/// 命名空间含全局（空名，显示为 "(全局)"）；嵌套类型归入最外层声明类型的命名空间。
/// </summary>
public sealed class TypeTreeData
{
    private readonly object _gate = new();
    private readonly Dictionary<string /*dll 绝对路径*/, AssemblyTree> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一个程序集的类型树数据：命名空间 → 该命名空间下类型全名列表。</summary>
    public sealed record AssemblyTree(IReadOnlyList<string> Namespaces, IReadOnlyDictionary<string, IReadOnlyList<string>> TypesByNamespace);

    /// <summary>取程序集的命名空间列表（首次枚举并缓存）。失败返回 null（文件不存在/非托管程序集）。</summary>
    public IReadOnlyList<string>? GetNamespaces(string assemblyPath)
    {
        var tree = Load(assemblyPath);
        return tree?.Namespaces;
    }

    /// <summary>取某命名空间下的类型全名列表（程序集已枚举）。未知命名空间返回空列表。</summary>
    public IReadOnlyList<string> GetTypes(string assemblyPath, string ns)
    {
        var tree = Load(assemblyPath);
        if (tree is null) return [];
        return tree.TypesByNamespace.TryGetValue(ns, out var types) ? types : [];
    }

    private AssemblyTree? Load(string assemblyPath)
    {
        var full = Path.GetFullPath(assemblyPath);
        lock (_gate)
        {
            if (_cache.TryGetValue(full, out var cached)) return cached;
        }
        AssemblyTree? tree;
        try
        {
            tree = Enumerate(full);
        }
        catch
        {
            return null; // 文件不存在 / BadImage 等：非程序集，返回 null 由调用方提示
        }
        if (tree is not null)
        {
            lock (_gate) _cache[full] = tree;
        }
        return tree;
    }

    /// <summary>枚举实体类型并按命名空间分组。跳过编译器生成类型；嵌套类型取最外层命名空间。</summary>
    private static AssemblyTree Enumerate(string assemblyPath)
    {
        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        var byNs = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeDefinitions)
        {
            var td = reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, td)) continue;
            var ns = GetNamespace(reader, td);
            var full = MetadataNaming.FullName(reader, td);
            if (!byNs.TryGetValue(ns, out var list)) byNs[ns] = list = [];
            list.Add(full);
        }
        return new AssemblyTree([.. byNs.Keys],
            byNs.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal));
    }

    /// <summary>类型所属有效命名空间：嵌套沿 DeclaringType 上溯取最外层 Namespace（空则 "(全局)"）。</summary>
    private static string GetNamespace(MetadataReader reader, TypeDefinition type)
    {
        var current = type;
        while (!current.GetDeclaringType().IsNil)
        {
            current = reader.GetTypeDefinition(current.GetDeclaringType());
        }
        var ns = reader.GetString(current.Namespace);
        return string.IsNullOrEmpty(ns) ? "(全局)" : ns;
    }
}
