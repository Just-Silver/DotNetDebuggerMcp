using DotNetDebugger.Decompiler.Document;

namespace DotNetDebuggerMcp.Services;

/// <summary>
/// 停点上下文用的反编译文档缓存（P4）：按 模块路径+类型全名 缓存 DocumentService 产物，
/// 避免每次 debug_wait/debug_state 重复反编译同一类型。超容量整体清空（简单诚实；进程内缓存，
/// 键含模块路径，跨会话复用同 DLL 的文档亦正确）。线程安全（debug 工具可能并发调用）。
/// </summary>
internal sealed class DebugDocumentCache
{
    /// <summary>缓存条目上限（类型文档）；超出整体清空。常规调试会话触及的类型数远小于此。</summary>
    public const int Capacity = 32;

    private readonly object _gate = new();
    private readonly Dictionary<(string ModulePath, string TypeFullName), SourceDocument> _map = new();

    /// <summary>当前缓存条目数（诊断用）。</summary>
    public int Count { get { lock (_gate) return _map.Count; } }

    /// <summary>取文档：命中返回缓存；未命中反编译并缓存（失败文档不入缓存，下次重试——与 Web DocumentStore 同策略）。</summary>
    public SourceDocument GetOrLoad(string modulePath, string typeFullName)
    {
        var key = (modulePath, typeFullName);
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var cached)) return cached;
        }

        var doc = DocumentService.GetTypeDocument(modulePath, typeFullName);
        if (doc.IsSuccess)
        {
            lock (_gate)
            {
                if (_map.Count >= Capacity) _map.Clear();
                _map[key] = doc;
            }
        }
        return doc;
    }
}
