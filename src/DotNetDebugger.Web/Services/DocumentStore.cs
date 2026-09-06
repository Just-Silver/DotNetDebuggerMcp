using DotNetDebugger.Decompiler.Document;

namespace DotNetDebugger.Web.Services;

/// <summary>
/// 反编译文档存储（Web 侧缓存）：按 程序集+类型 加载反编译文档（DocumentService），缓存复用
/// （反编译成本高，同一类型多次停点查看不应重反编译）。供代码视图展示与停点行映射查询。
/// 线程安全（Blazor 组件可能并发访问）。
/// </summary>
public sealed class DocumentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SourceDocument> _cache = new(StringComparer.OrdinalIgnoreCase);
    private ViewRecord? _lastView;

    /// <summary>最近一次成功加载的视图（刷新/重连后恢复代码视图与树定位用；随缓存同为进程级单例状态）。</summary>
    public sealed record ViewRecord(string AssemblyPath, string TypeFullName);

    /// <summary>最近成功加载的视图；一次都未成功加载过返回 null。</summary>
    public ViewRecord? LastView { get { lock (_gate) return _lastView; } }

    /// <summary>按 程序集+类型 取文档；未缓存则反编译并缓存。失败返回带 Error 的文档（不入缓存）。同步版。</summary>
    public SourceDocument GetOrLoad(string assemblyPath, string typeFullName)
    {
        var key = CacheKey(assemblyPath, typeFullName);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }

        var doc = DocumentService.GetTypeDocument(assemblyPath, typeFullName);
        if (doc.IsSuccess)
        {
            lock (_gate) { _cache[key] = doc; _lastView = new ViewRecord(assemblyPath, typeFullName); }
        }
        return doc;
    }

    /// <summary>异步版：反编译在后台线程执行（反编译成本高，避免阻塞 UI 线程——Blazor 组件加载指示依赖先刷新）。</summary>
    public Task<SourceDocument> GetOrLoadAsync(string assemblyPath, string typeFullName)
    {
        var key = CacheKey(assemblyPath, typeFullName);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached)) return Task.FromResult(cached);
        }

        return Task.Run(() =>
        {
            var doc = DocumentService.GetTypeDocument(assemblyPath, typeFullName);
            if (doc.IsSuccess)
            {
                lock (_gate) { _cache[key] = doc; _lastView = new ViewRecord(assemblyPath, typeFullName); }
            }
            return doc;
        });
    }

    /// <summary>停点 IL offset → 反编译文本行号（语句级映射，DocumentService）。无活动文档返回 null。</summary>
    public static int? GetStopLine(SourceDocument doc, int methodToken, int ilOffset)
        => DocumentService.GetLineForIlOffset(doc, methodToken, ilOffset);

    /// <summary>反向：文本行 → (方法 token, ilStart)，设断点用。无命中返回 null。</summary>
    public static (int MethodToken, int IlStart)? GetIlStartAtLine(SourceDocument doc, int line)
        => DocumentService.GetIlStartForLine(doc, line);

    /// <summary>光标行 → 所在方法 token（双向联动用）。实现已提升至 DocumentService（P3 行断点与 MCP 共用）。</summary>
    public static int? FindMethodTokenAtLine(SourceDocument doc, int line)
        => DocumentService.FindMethodTokenAtLine(doc, line);

    /// <summary>编辑器行 → 断点落点（glyph 点击设断点用）。实现已提升至 DocumentService；Exact 标志 Web 暂不消费。</summary>
    public static (int MethodToken, int IlOffset, bool Exact)? GetBreakpointTargetAtLine(SourceDocument doc, int line)
        => DocumentService.GetBreakpointTargetAtLine(doc, line);

    /// <summary>方法 token → 文档中首个映射行（树点成员叶子定位用）。实现已提升至 DocumentService。</summary>
    public static int? GetMethodFirstLine(SourceDocument doc, int methodToken)
        => DocumentService.GetMethodFirstLine(doc, methodToken);

    /// <summary>方法 token → 文档行区间 [首行, 末行]（选中成员高亮用）。实现已提升至 DocumentService。</summary>
    public static (int Start, int End)? GetMethodLineRange(SourceDocument doc, int methodToken)
        => DocumentService.GetMethodLineRange(doc, methodToken);

    /// <summary>清空缓存（换目标程序集/类型浏览时调用，避免缓存膨胀）。</summary>
    public void Clear()
    {
        lock (_gate) _cache.Clear();
    }

    public int Count { get { lock (_gate) return _cache.Count; } }

    private static string CacheKey(string assemblyPath, string typeFullName)
        => $"{assemblyPath}\u001F{typeFullName}";
}
