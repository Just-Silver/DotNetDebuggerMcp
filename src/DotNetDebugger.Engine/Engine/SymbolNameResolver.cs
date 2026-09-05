using System.Collections.Concurrent;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebugger.Engine.Engine;

/// <summary>
/// 符号名解析：停点变量补名字（ICorDebug 只给槽位，名字在元数据/PDB 里）。
/// 参数名来自 DLL 元数据 Param 表（无需 PDB）；局部变量名来自模块旁的 portable PDB（LocalScopes，缺失则无名）。
/// 按 (模块路径, 方法 token) 缓存；解析失败静默返回空（变量仍以 slot 序号展示）。
/// </summary>
internal static class SymbolNameResolver
{
    private sealed record Names(string?[] ArgNames, string?[] LocalNames);

    private static readonly ConcurrentDictionary<(string ModulePath, int Token), Names?> _cache = new();

    /// <summary>解析指定方法的参数名（按序号 0 起）与局部变量名（按 slot）。任一侧缺失为空数组。</summary>
    public static (string?[] ArgNames, string?[] LocalNames) Resolve(string modulePath, int methodToken)
    {
        var key = (modulePath, methodToken);
        var names = _cache.GetOrAdd(key, static k => ResolveCore(k.ModulePath, k.Token));
        return names is null ? ([], []) : (names.ArgNames, names.LocalNames);
    }

    private static Names? ResolveCore(string modulePath, int methodToken)
    {
        string?[]? argNames = null;
        string?[]? localNames = null;
        try
        {
            argNames = ReadArgNames(modulePath, methodToken);
        }
        catch { /* 元数据读取失败：参数无名 */ }
        try
        {
            localNames = ReadLocalNames(modulePath, methodToken);
        }
        catch { /* PDB 缺失/读取失败：局部变量无名 */ }
        if ((argNames is null || argNames.Length == 0) && (localNames is null || localNames.Length == 0)) return null;
        return new Names(argNames ?? [], localNames ?? []);
    }

    /// <summary>参数名：DLL 元数据 Param 表（SequenceNumber 1 起 → 数组 0 起）。无需 PDB。</summary>
    private static string?[] ReadArgNames(string modulePath, int methodToken)
    {
        using var fs = File.OpenRead(modulePath);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();
        var handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        var md = mr.GetMethodDefinition(handle);
        var list = new List<(int Seq, string Name)>();
        foreach (var ph in md.GetParameters())
        {
            var p = mr.GetParameter(ph);
            if (p.SequenceNumber > 0 && !p.Name.IsNil)
                list.Add((p.SequenceNumber, mr.GetString(p.Name)));
        }
        if (list.Count == 0) return [];
        var max = list.Max(x => x.Seq);
        var names = new string?[max];
        foreach (var (seq, name) in list) names[seq - 1] = name;
        return names;
    }

    /// <summary>局部变量名：模块旁 portable PDB 的 LocalScopes（Index → 名字，外层作用域优先）。</summary>
    private static string?[] ReadLocalNames(string modulePath, int methodToken)
    {
        var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
        if (!File.Exists(pdbPath)) return [];

        using var pdbFs = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbFs);
        var mr = provider.GetMetadataReader();

        var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
        var byIndex = new Dictionary<int, string>();
        foreach (var scopeHandle in mr.GetLocalScopes(methodHandle))
        {
            var scope = mr.GetLocalScope(scopeHandle);
            foreach (var lvHandle in scope.GetLocalVariables())
            {
                var lv = mr.GetLocalVariable(lvHandle);
                // 外层作用域先遍历：同名槽位保留先见者
                byIndex.TryAdd(lv.Index, mr.GetString(lv.Name));
            }
        }
        if (byIndex.Count == 0) return [];
        var max = byIndex.Keys.Max();
        var names = new string?[max + 1];
        foreach (var (slot, name) in byIndex) names[slot] = name;
        return names;
    }

    /// <summary>
    /// 当前 IL offset 所在语句的 IL 区间 [start,end)（PDB 序列点；单步 StepRange 用）。
    /// start = 含 ilOffset（或其前最近）的非隐藏序列点偏移，end = 其后下一个序列点偏移（无则 IL 末尾）。
    /// 隐藏序列点（编译器生成，StartLine=HiddenLine）不参与边界。无 PDB/无序列点/ilOffset 早于首点返回 null。
    /// </summary>
    public static (int Start, int End)? GetStatementIlRange(string modulePath, int methodToken, int ilOffset, int ilSize)
    {
        var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
        if (!File.Exists(pdbPath)) return null;

        using var pdbFs = File.OpenRead(pdbPath);
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbFs);
        var mr = provider.GetMetadataReader();

        var methodHandle = MetadataTokens.MethodDefinitionHandle(methodToken);
        var sps = mr.GetMethodDebugInformation(methodHandle).GetSequencePoints()
            .Where(sp => sp.StartLine != SequencePoint.HiddenLine)
            .Select(sp => sp.Offset)
            .Distinct()
            .OrderBy(offset => offset)
            .ToList();
        if (sps.Count == 0 || ilOffset < sps[0]) return null;

        int? start = null;
        foreach (var offset in sps)
        {
            if (offset <= ilOffset) start = offset;
            else return (start!.Value, offset);
        }
        return (start!.Value, ilSize);
    }
}
