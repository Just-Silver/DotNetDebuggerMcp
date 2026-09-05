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

    /// <summary>按 程序集+类型 取文档；未缓存则反编译并缓存。失败返回带 Error 的文档（不入缓存）。</summary>
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
            lock (_gate) _cache[key] = doc;
        }
        return doc;
    }

    /// <summary>停点 IL offset → 反编译文本行号（语句级映射，DocumentService）。无活动文档返回 null。</summary>
    public static int? GetStopLine(SourceDocument doc, int methodToken, int ilOffset)
        => DocumentService.GetLineForIlOffset(doc, methodToken, ilOffset);

    /// <summary>反向：文本行 → (方法 token, ilStart)，设断点用。无命中返回 null。</summary>
    public static (int MethodToken, int IlStart)? GetIlStartAtLine(SourceDocument doc, int line)
        => DocumentService.GetIlStartForLine(doc, line);

    /// <summary>清空缓存（换目标程序集/类型浏览时调用，避免缓存膨胀）。</summary>
    public void Clear()
    {
        lock (_gate) _cache.Clear();
    }

    public int Count { get { lock (_gate) return _cache.Count; } }

    private static string CacheKey(string assemblyPath, string typeFullName)
        => $"{assemblyPath}\u001F{typeFullName}";
}
