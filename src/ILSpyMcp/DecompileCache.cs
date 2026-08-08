namespace ILSpyMcp;

/// <summary>
/// 缓存键：程序集绝对路径 + 文件指纹 + 参数签名。dll 更新后指纹变化，旧键自然失配。
/// </summary>
/// <param name="AssemblyPath">程序集绝对路径。</param>
/// <param name="Fingerprint">文件指纹（最后修改时间 + 大小）。</param>
/// <param name="Signature">参数签名（typeName/member/list/languageVersion 组合）。</param>
public readonly record struct CacheKey(string AssemblyPath, string Fingerprint, string Signature);

/// <summary>
/// 反编译结果内存缓存（线程安全 LRU，总上限可配置，默认 <see cref="AppConfig.MaxCacheBytes"/>）。key = 程序集绝对路径 + 文件指纹（mtime+size）+ 参数签名， 不同参数组合各自独立缓存；程序集更新后指纹变化，同路径同签名的旧条目自动清理。
/// </summary>
public sealed class DecompileCache
{
    private readonly long _maxBytes;
    private readonly Dictionary<CacheKey, CacheEntry> _map = new();
    private readonly LinkedList<CacheKey> _lru = new();
    private readonly Lock _lock = new();
    private long _totalBytes;

    /// <param name="maxBytes">缓存总字节上限，超出后按 LRU 驱逐；测试可传小值。</param>
    public DecompileCache(long maxBytes = AppConfig.MaxCacheBytes) => _maxBytes = maxBytes;

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
    /// 读取缓存条目；未命中返回 null，命中即刷新 LRU 访问时间。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <returns>命中的行列表；未命中为 null。</returns>
    public List<string>? Get(CacheKey key)
    {
        lock (_lock)
        {
            if (!_map.TryGetValue(key, out var entry)) return null;
            _lru.Remove(entry.Node!);
            _lru.AddFirst(entry.Node!); // 命中即移到队首，供 LRU 使用
            return entry.Lines;
        }
    }

    /// <summary>
    /// 写入缓存条目；同 key 覆盖，程序集更新旧条目清理，超限按 LRU 驱逐。
    /// </summary>
    /// <param name="key">缓存键。</param>
    /// <param name="lines">反编译结果行列表。</param>
    public void Put(CacheKey key, List<string> lines)
    {
        lock (_lock)
        {
            if (_map.TryGetValue(key, out var entry))
            {
                _totalBytes -= entry.TotalBytes;
                _lru.Remove(entry.Node!);
                entry.Lines = lines;
                entry.TotalBytes = OutputFormatter.CountBytes(lines);
                _totalBytes += entry.TotalBytes;
                _lru.AddFirst(entry.Node!);
            }
            else
            {
                var newEntry = new CacheEntry { Lines = lines, TotalBytes = OutputFormatter.CountBytes(lines) };
                newEntry.Node = _lru.AddFirst(key);
                _map[key] = newEntry;
                _totalBytes += newEntry.TotalBytes;
            }

            // 程序集更新（指纹变化）后，主动删除同路径、同签名、但指纹不同的旧条目（dll 更新留下的孤儿）
            RemoveStaleSameAssembly(key);
            // 总量超限时从队尾逐个驱逐最久未访问条目，直到不超限或只剩当前条目
            EvictIfOver();
        }
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

    private sealed class CacheEntry
    {
        public required List<string> Lines;
        public long TotalBytes;
        public LinkedListNode<CacheKey>? Node;
    }
}