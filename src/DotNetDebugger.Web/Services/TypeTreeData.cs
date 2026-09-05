using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebugger.Web.Services;

/// <summary>树成员种类（dnSpyEx 类型节点子级分类）。</summary>
public enum TypeMemberKind
{
    /// <summary>方法（含构造 .ctor/.cctor；属性/事件访问器已剔除并入对应节点）。</summary>
    Method,
    /// <summary>属性（get_/set_ 访问器折叠为一个节点）。</summary>
    Property,
    /// <summary>事件（add_/remove_ 访问器折叠为一个节点；字段式事件不再单列字段）。</summary>
    Event,
    /// <summary>字段（自动属性/字段式事件 backing field 已剔除）。</summary>
    Field,
    /// <summary>嵌套类型。</summary>
    NestedType,
}

/// <summary>类型下的一个成员（树叶子节点数据）。Name 为元数据原始名（方法含 .ctor，属性/事件为原名）；Token 为成员 token（方法可直接匹配停点 TopFrame.MethodToken）。</summary>
public sealed record TypeMember(string Name, TypeMemberKind Kind, int Token, string DisplayName);

/// <summary>
/// 左侧类型树的数据源：给定程序集，枚举实体类型与类型成员（方法/属性/事件/字段），按「命名空间 → 类型全名」与「类型全名 → 成员」索引。
/// 纯元数据读取（秒回，不反编译），一次枚举缓存；树组件展开命名空间/类型时取对应组即可（UI 懒加载）。
/// 命名空间含全局（空名，显示为 "(全局)"）；嵌套类型归入最外层声明类型的命名空间。
/// </summary>
public sealed class TypeTreeData
{
    private readonly object _gate = new();
    private readonly Dictionary<string /*dll 绝对路径*/, AssemblyTree> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一个程序集的类型树数据：命名空间 → 该命名空间下类型全名列表；类型全名 → 成员列表。</summary>
    public sealed record AssemblyTree(
        IReadOnlyList<string> Namespaces,
        IReadOnlyDictionary<string, IReadOnlyList<string>> TypesByNamespace,
        IReadOnlyDictionary<string, IReadOnlyList<TypeMember>> MembersByType);

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

    /// <summary>取类型下的成员列表（dnSpyEx 顺序：方法→属性→事件→字段）。未知类型返回空列表。</summary>
    public IReadOnlyList<TypeMember> GetMembers(string assemblyPath, string typeFullName)
    {
        var tree = Load(assemblyPath);
        if (tree is null) return [];
        return tree.MembersByType.TryGetValue(typeFullName, out var members) ? members : [];
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

    /// <summary>枚举实体类型（命名空间分组）与各类型成员（dnSpyEx 顺序）。跳过编译器生成类型；嵌套类型归最外层命名空间。</summary>
    private static AssemblyTree Enumerate(string assemblyPath)
    {
        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        var byNs = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        var membersByType = new Dictionary<string, List<TypeMember>>(StringComparer.Ordinal);
        foreach (var handle in reader.TypeDefinitions)
        {
            var td = reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, td)) continue;
            var ns = GetNamespace(reader, td);
            var full = MetadataNaming.FullName(reader, td);
            if (!byNs.TryGetValue(ns, out var list)) byNs[ns] = list = [];
            list.Add(full);
            membersByType[full] = EnumerateMembers(reader, td);
        }
        return new AssemblyTree([.. byNs.Keys],
            byNs.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value, StringComparer.Ordinal),
            membersByType.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<TypeMember>)kv.Value, StringComparer.Ordinal));
    }

    /// <summary>枚举类型成员，dnSpyEx 顺序：方法→属性→事件→字段（属性/事件访问器并入属主节点，不单列方法）。</summary>
    private static List<TypeMember> EnumerateMembers(MetadataReader reader, TypeDefinition type)
    {
        var result = new List<TypeMember>();
        var accessorNames = new HashSet<string>(StringComparer.Ordinal); // get_X/set_X/add_X/remove_X

        // 方法：跳过访问器（并入属性/事件节点）与编译器生成（名称含 < 的如 MoveNext 状态机入口已随类型过滤，方法级再兜底）
        var methodTokens = new Dictionary<int, string>(/* token → name */);
        foreach (var handle in type.GetMethods())
        {
            var md = reader.GetMethodDefinition(handle);
            var name = reader.GetString(md.Name);
            if (IsAccessorName(name)) { accessorNames.Add(name); continue; }
            if (name.Contains('<')) continue; // 编译器生成方法兜底
            var token = MetadataTokens.GetToken(handle);
            result.Add(new TypeMember(name, TypeMemberKind.Method, token, MethodDisplay(name)));
        }

        // 属性
        foreach (var handle in type.GetProperties())
        {
            var pd = reader.GetPropertyDefinition(handle);
            var name = reader.GetString(pd.Name);
            var token = MetadataTokens.GetToken(handle);
            result.Add(new TypeMember(name, TypeMemberKind.Property, token, name));
        }

        // 事件
        var eventNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in type.GetEvents())
        {
            var ed = reader.GetEventDefinition(handle);
            var name = reader.GetString(ed.Name);
            eventNames.Add(name);
            var token = MetadataTokens.GetToken(handle);
            result.Add(new TypeMember(name, TypeMemberKind.Event, token, name));
        }

        // 字段：跳过自动属性 backing field（名含 &lt;）与字段式事件同名 backing field
        foreach (var handle in type.GetFields())
        {
            var name = reader.GetString(reader.GetFieldDefinition(handle).Name);
            if (name.Contains('<')) continue;
            if (eventNames.Contains(name)) continue;
            var token = MetadataTokens.GetToken(handle);
            result.Add(new TypeMember(name, TypeMemberKind.Field, token, name));
        }
        return result;
    }

    /// <summary>方法显示名：.ctor→.ctor 保留，否则原名。dnSpyEx 不重命名，此处保持元数据名。</summary>
    private static string MethodDisplay(string name) => name;

    /// <summary>判断方法名是否为属性/事件访问器（get_X/set_X/add_/remove_，含显式接口实现 Ns.I.get_X）。</summary>
    private static bool IsAccessorName(string name)
        => name.StartsWith("get_", StringComparison.Ordinal)
        || name.StartsWith("set_", StringComparison.Ordinal)
        || name.StartsWith("add_", StringComparison.Ordinal)
        || name.StartsWith("remove_", StringComparison.Ordinal)
        || name.Contains(".get_", StringComparison.Ordinal)
        || name.Contains(".set_", StringComparison.Ordinal)
        || name.Contains(".add_", StringComparison.Ordinal)
        || name.Contains(".remove_", StringComparison.Ordinal);

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
