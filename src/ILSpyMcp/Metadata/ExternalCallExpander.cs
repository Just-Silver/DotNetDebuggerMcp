using ICSharpCode.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using AssemblyNameRef = ICSharpCode.Decompiler.Metadata.AssemblyNameReference;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 跨程序集调用链展开：将 call_chain 的外部调用点解析到磁盘程序集（主 dll 同目录 / CWD / searchDirs / deps.json NuGet 缓存 /
/// 共享框架 / GAC，解析优先级由 UniversalAssemblyResolver 内置），在该程序集定位被调方法并扫描其方法体子序列，
/// 子序列内的跨程序集调用递归展开（行随深度缩进）。纯元数据读取，不加载程序集、不反编译 IL；解析失败/找不到/防环
/// 均返回空列表（调用方标注终止）。解码中止计数跨展开过程累计。
/// </summary>
public sealed class ExternalCallExpander : IDisposable
{
    private readonly string _mainAssemblyPath;
    private readonly FileStream _mainFs;
    private readonly PEReader _mainPe;
    private readonly MetadataReader _mainReader;
    private int _abortedBodies;

    /// <summary>
    /// 以主 dll 绝对路径构造展开器（内部自开 PEReader：resolver 需 mainAssemblyFileName 文件路径，PEReader 拿不到文件名；
    /// 目标框架经 <see cref="DotNetCorePathFinderExtensions.DetectTargetFrameworkId(MetadataReader, string)"/> 探测）。
    /// </summary>
    /// <param name="mainAssemblyPath">主 dll 绝对路径（被分析程序集，其同目录为解析搜索目录之一）。</param>
    public ExternalCallExpander(string mainAssemblyPath)
    {
        _mainAssemblyPath = mainAssemblyPath;
        _mainFs = File.OpenRead(mainAssemblyPath);
        _mainPe = new PEReader(_mainFs);
        _mainReader = _mainPe.GetMetadataReader();
    }

    /// <summary>
    /// 累计解码中止的方法体计数（展开跨多个外部程序集累加），供调用方感知解码完整性。
    /// </summary>
    public int AbortedBodies => _abortedBodies;

    /// <summary>
    /// 展开单个外部调用点：经 UniversalAssemblyResolver 定位归属程序集（主 dll 同目录由 resolver 构造自带，
    /// searchDirs 逐目录追加，另恒含 CWD），按类型全名 + 成员名 + 参数个数定位方法并扫描其方法体序列，
    /// 返回展开行（首行 <c>  {程序集}::{类型}::{成员} 调用:</c> + 子序列行）。
    /// 子序列内的跨程序集调用递归展开；解析失败/找不到/已访问（防环）返回空列表。
    /// </summary>
    /// <param name="external">外部调用点（AssemblyFullName 为归属程序集完整名，TypeFullName/MemberName/ParamCount 用于定位）。</param>
    /// <param name="searchDirs">追加搜索目录（主 dll 同目录与 CWD 恒在，此处传其余目录）。</param>
    /// <returns>展开行列表（随深度缩进）；解析失败/找不到/已访问为空。</returns>
    public IReadOnlyList<string> Expand(CallSite external, IReadOnlyList<string> searchDirs)
    {
        var visited = new HashSet<(string Path, int Token)>();
        try
        {
            return ExpandCore(external, CreateResolver(searchDirs), visited, depth: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
             or ArgumentException or FormatException or OverflowException)
        {
            return Array.Empty<string>(); // resolver 构造/框架探测异常按未展开处理，不抛异常
        }
    }

    private IReadOnlyList<string> ExpandCore(CallSite external, UniversalAssemblyResolver resolver,
        HashSet<(string Path, int Token)> visited, int depth)
    {
        if (string.IsNullOrEmpty(external.AssemblyFullName)) return Array.Empty<string>();
        try
        {
            string? path = resolver.FindAssemblyFile(AssemblyNameRef.Parse(external.AssemblyFullName));
            if (path is null) return Array.Empty<string>(); // 解析失败返回空（调用方标注终止）

            using var fs = File.OpenRead(path);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var typeDef = MetadataNaming.FindType(reader, external.TypeFullName);
            if (typeDef is null) return Array.Empty<string>();
            var methodDef = FindMethod(reader, typeDef.Value, external.MemberName, external.ParamCount);
            if (methodDef is null) return Array.Empty<string>();
            if (!visited.Add((Path.GetFullPath(path), MetadataTokens.GetToken(methodDef.Value)))) return Array.Empty<string>(); // 防环

            var scanner = new CallChainScanner(pe);
            var subSites = scanner.ScanMethod(methodDef.Value);
            _abortedBodies += scanner.AbortedBodies;

            var headerIndent = new string(' ', depth * 2 + 2);
            var subIndent = new string(' ', depth * 2 + 4);
            var lines = new List<string>
            {
                $"{headerIndent}{ShortAssemblyName(external.AssemblyFullName)}::{external.TypeFullName}::{external.MemberName} 调用:"
            };
            foreach (var sub in subSites)
            {
                if (sub.IsExternal)
                {
                    lines.Add($"{subIndent}{sub.TypeFullName}::{sub.MemberName} [{ShortAssemblyName(sub.AssemblyFullName)}]");
                    lines.AddRange(ExpandCore(sub, resolver, visited, depth + 1));
                }
                else
                {
                    lines.Add($"{subIndent}{sub.TypeFullName}::{sub.MemberName}()");
                }
            }
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException
             or ArgumentException or FormatException or OverflowException)
        {
            return Array.Empty<string>(); // 解析/读取/解码异常一律按未展开处理，不抛异常
        }
    }

    private UniversalAssemblyResolver CreateResolver(IReadOnlyList<string> searchDirs)
    {
        var resolver = new UniversalAssemblyResolver(_mainAssemblyPath, throwOnError: false,
            targetFramework: _mainReader.DetectTargetFrameworkId());
        resolver.AddSearchDirectory(Environment.CurrentDirectory);
        foreach (var dir in searchDirs)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            resolver.AddSearchDirectory(dir);
        }
        return resolver;
    }

    /// <summary>
    /// 在类型定义内按成员名 + 参数个数定位方法定义：参数个数匹配优先（区分重载）；无参数个数匹配时取首个同名
    /// （参数个数为 -1 时的兜底）；无同名方法返回 null。
    /// </summary>
    private MethodDefinitionHandle? FindMethod(MetadataReader reader, TypeDefinitionHandle typeDef, string name, int paramCount)
    {
        var type = reader.GetTypeDefinition(typeDef);
        MethodDefinitionHandle? first = null;
        foreach (var handle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(handle);
            if (reader.GetString(method.Name) != name) continue;
            if (first is null) first = handle;
            if (paramCount >= 0 && method.GetParameters().Count == paramCount) return handle;
        }
        return first;
    }

    /// <summary>
    /// 程序集完整名的短名（首段）；归属未知时返回 "&lt;外部&gt;"。
    /// </summary>
    private static string ShortAssemblyName(string? fullName)
    {
        if (string.IsNullOrEmpty(fullName)) return "<外部>";
        var comma = fullName.IndexOf(',');
        return comma >= 0 ? fullName[..comma].Trim() : fullName;
    }

    /// <summary>
    /// 释放主 dll 的 PEReader 与文件流。
    /// </summary>
    public void Dispose()
    {
        _mainPe.Dispose();
        _mainFs.Dispose();
    }
}
