using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Formatting;

namespace DotNetDebuggerMcp.Caching;

/// <summary>
/// 缓存键：程序集绝对路径 + 文件指纹 + 参数签名。dll 更新后指纹变化，旧键自然失配。
/// </summary>
/// <param name="AssemblyPath">程序集绝对路径。</param>
/// <param name="Fingerprint">文件指纹（最后修改时间 + 大小）。</param>
/// <param name="Signature">参数签名（typeName/member/list/languageVersion 组合）。</param>
public readonly record struct CacheKey(string AssemblyPath, string Fingerprint, string Signature);

/// <summary>
/// 单条缓存条目的状态快照：占用字节数与行数（供 cache_stats 工具展示每条占用，定位缓存大头）。
/// </summary>
/// <param name="AssemblyPath">程序集绝对路径。</param>
/// <param name="Signature">参数签名。</param>
/// <param name="Bytes">该条目占用字节数。</param>
/// <param name="LineCount">该条目的行数。</param>
/// <param name="Hits">该条目被命中的累计次数。</param>
public sealed record CacheEntryInfo(string AssemblyPath, string Signature, long Bytes, int LineCount, long Hits);

/// <summary>
/// 缓存整体状态快照：当前占用/上限（供评估缓存大小设置）、条目数、累计命中/未命中（供命中率计算）与逐条目明细。
/// </summary>
/// <param name="EntryCount">当前条目数。</param>
/// <param name="TotalBytes">当前占用总字节数。</param>
/// <param name="MaxBytes">缓存总字节上限。</param>
/// <param name="HitCount">累计命中次数。</param>
/// <param name="MissCount">累计未命中次数。</param>
/// <param name="Entries">逐条目明细（快照时点）。</param>
public sealed record CacheStats(int EntryCount, long TotalBytes, long MaxBytes, long HitCount, long MissCount, IReadOnlyList<CacheEntryInfo> Entries);

/// <summary>
/// 反编译结果内存缓存（线程安全 LRU，总上限可配置，默认 <see cref="AppConfig.MaxCacheBytes"/>；固定 30 分钟滑动过期 + 5
/// 分钟定时清理，空闲即回收，MCP 常驻场景下不长期占用）。key = 程序集绝对路径 + 文件指纹（mtime+size）+ 参数签名， 不同参数组合各自独立缓存；程序集更新后指纹变化，同路径同签名的旧条目自动清理。
/// </summary>
public sealed class DecompileCache : IDisposable
{
    private readonly long _maxBytes;
    private readonly TimeSpan _slidingTtl;
    private readonly Func<DateTimeOffset> _now;
    private readonly Timer? _timer;
    private readonly Dictionary<CacheKey, CacheEntry> _map = new();
    private readonly LinkedList<CacheKey> _lru = new();
    private readonly Lock _lock = new();
    private long _totalBytes;
    private long _hitCount;
    private long _missCount;
    private bool _disposed;

    /// <param name="maxBytes">缓存总字节上限，超出后按 LRU 驱逐；测试可传小值。</param>
    /// <param name="slidingTtl">
    /// 滑动过期时长；为 null 时取 <see cref="AppConfig.CacheEntrySlidingTtl"/>（固定 30 分钟）。测试可传小值。
    /// </param>
    /// <param name="now">时间源；为 null 时取 <see cref="DateTimeOffset.UtcNow"/>。测试可注入固定时钟。</param>
    /// <param name="cleanupInterval">
    /// 定时清理间隔；为 null 时取 <see cref="AppConfig.CacheCleanupInterval"/>（固定 5 分钟）。测试可传小值。
    /// </param>
    public DecompileCache(
        long maxBytes = AppConfig.MaxCacheBytes,
        TimeSpan? slidingTtl = null,
        Func<DateTimeOffset>? now = null,
        TimeSpan? cleanupInterval = null)
    {
        _maxBytes = maxBytes;
        _slidingTtl = slidingTtl ?? AppConfig.CacheEntrySlidingTtl;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        var interval = cleanupInterval ?? AppConfig.CacheCleanupInterval;
        _timer = new Timer(OnTimer, null, interval, interval);
    }

    /// <summary>
    /// 构造缓存 key。相对路径以当前工作目录为基准解析为绝对路径； Windows 下统一小写，避免 C:\a.dll 与 c:\a.dll 生成双缓存条目。
    /// </summary>
    /// <param name="assembly">程序集路径（相对或绝对）。</param>
    /// <param name="signature">参数签名。</param>
    /// <returns>包含绝对路径、指纹与签名的缓存键。</returns>
    public CacheKey BuildKey(string assembly, string signature)
    {
        var absPath = Path.GetFullPath(assembly);
        // Windows 文件系统不区分大小写：归一化避免同文件因大小写差异产生两条缓存
        absPath = absPath.ToLowerInvariant();
        return new CacheKey(absPath, FileFingerprint(absPath), signature);
    }

    /// <summary>
    /// 读取缓存条目；未命中返回 null，命中即刷新 LRU 访问时间与滑动过期时间。过期视为未命中。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <returns>命中的行列表；未命中或已过期为 null。</returns>
    public List<string>? Get(CacheKey key)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var entry))
            {
                _missCount++;
                return null;
            }
            var now = _now();
            if (IsExpired(entry, now))
            {
                RemoveEntry(key);
                _missCount++;
                return null;
            }
            _hitCount++;
            entry.Hits++;
            entry.LastAccess = now;
            _lru.Remove(entry.Node!);
            _lru.AddFirst(entry.Node!); // 命中即移到队首，供 LRU 使用
            return entry.Lines;
        }
    }

    /// <summary>
    /// 返回缓存当前状态快照：总占用/上限、条目数、累计命中/未命中与逐条目明细（供 cache_stats 工具评估缓存大小设置）。
    /// </summary>
    /// <returns>快照时点的缓存状态。</returns>
    public CacheStats GetStats()
    {
        lock (_lock)
        {
            var entries = new List<CacheEntryInfo>(_map.Count);
            foreach (var (key, entry) in _map)
            {
                entries.Add(new CacheEntryInfo(key.AssemblyPath, key.Signature, entry.TotalBytes, entry.Lines.Count, entry.Hits));
            }
            return new CacheStats(_map.Count, _totalBytes, _maxBytes, _hitCount, _missCount, entries);
        }
    }

    /// <summary>
    /// 写入缓存条目；同 key 覆盖，程序集更新旧条目清理，过期条目清理，超限按 LRU 驱逐。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <param name="lines">反编译结果行列表。</param>
    public void Put(CacheKey key, List<string> lines)
    {
        lock (_lock)
        {
            var now = _now();
            if (_map.TryGetValue(key, out var entry))
            {
                _totalBytes -= entry.TotalBytes;
                _lru.Remove(entry.Node!);
                entry.Lines = lines;
                entry.TotalBytes = OutputFormatter.CountBytes(lines);
                entry.LastAccess = now;
                _totalBytes += entry.TotalBytes;
                _lru.AddFirst(entry.Node!);
            }
            else
            {
                var newEntry = new CacheEntry { Lines = lines, TotalBytes = OutputFormatter.CountBytes(lines), LastAccess = now };
                newEntry.Node = _lru.AddFirst(key);
                _map[key] = newEntry;
                _totalBytes += newEntry.TotalBytes;
            }

            // 程序集更新（指纹变化）后，主动删除同路径、同签名、但指纹不同的旧条目（dll 更新留下的孤儿）
            RemoveStaleSameAssembly(key);
            // 顺带清理所有已过期的冷数据，再做容量 LRU
            TrimExpired(now);
            // 总量超限时从队尾逐个驱逐最久未访问条目，直到不超限或只剩当前条目
            EvictIfOver();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer?.Dispose();
    }

    /// <summary>
    /// 文件指纹 = 最后修改时间 + 文件大小，用于判断程序集是否已更新。 dll 重新编译后 mtime/size 变化，指纹随之变化，旧缓存自动失效、重新反编译。
    /// </summary>
    /// <param name="absPath">程序集绝对路径。</param>
    /// <returns>指纹字符串；文件不存在/不可读时为空串。</returns>
    private static string FileFingerprint(string absPath)
    {
        try
        {
            var fi = new FileInfo(absPath);
            if (!fi.Exists) return "";
            return $"{fi.LastWriteTimeUtc.Ticks}:{fi.Length}";
        }
        catch
        {
            return ""; // 文件不存在/不可读时指纹为空，仅凭路径与参数区分
        }
    }

    /// <summary>
    /// 清理同路径、同签名但指纹不同的旧条目（程序集已更新）。
    /// </summary>
    /// <param name="key">刚写入的新缓存键，其同路径同签名的旧指纹条目将被移除。</param>
    private void RemoveStaleSameAssembly(CacheKey key)
    {
        foreach (var k in _map.Keys.ToList())
        {
            if (k != key && k.AssemblyPath == key.AssemblyPath && k.Signature == key.Signature)
            {
                RemoveEntry(k);
            }
        }
    }

    /// <summary>
    /// 移除指定缓存条目（字典、字节计数、LRU 链表一并更新）。
    /// </summary>
    /// <param name="key">待移除的缓存键。</param>
    private void RemoveEntry(CacheKey key)
    {
        if (_map.Remove(key, out var entry))
        {
            _totalBytes -= entry.TotalBytes;
            _lru.Remove(entry.Node!);
        }
    }

    /// <summary>
    /// 总量超限时从队尾（最久未访问）逐个驱逐，直到不超限或只剩当前条目。
    /// </summary>
    private void EvictIfOver()
    {
        while (_totalBytes > _maxBytes && _map.Count > 1)
        {
            RemoveEntry(_lru.Last!.Value);
        }
    }

    private bool IsExpired(CacheEntry entry, DateTimeOffset now) => now - entry.LastAccess > _slidingTtl;

    private void TrimExpired(DateTimeOffset now)
    {
        foreach (var k in _map.Keys.ToList())
        {
            if (_map.TryGetValue(k, out var e) && IsExpired(e, now))
            {
                RemoveEntry(k);
            }
        }
    }

    private void OnTimer(object? _)
    {
        lock (_lock)
        {
            if (_disposed) return;
            TrimExpired(_now());
        }
    }

    private sealed class CacheEntry
    {
        public List<string> Lines = null!;
        public long TotalBytes;
        public long Hits;
        public DateTimeOffset LastAccess;
        public LinkedListNode<CacheKey>? Node;
    }
}