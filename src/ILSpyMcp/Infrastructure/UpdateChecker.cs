using System.Reflection;
using System.Text.Json;

namespace ILSpyMcp.Infrastructure;

/// <summary>
/// NuGet 新版本检查的磁盘缓存与注入文本组装：成功/失败结果落盘跨进程共享，重启不丢，避免每次会话都联网复查。
/// 网络查询经 <see cref="AppServices.NuGet"/>（运行时读取静态字段，测试可注入 fake）；一切 IO/网络异常静默降级，绝不影响核心功能。
/// </summary>
public sealed class UpdateChecker
{
    private readonly string _cacheDir;
    private readonly Func<DateTimeOffset> _now;

    /// <summary>
    /// 以默认缓存目录（LocalApplicationData/ilspymcp）与系统时钟构造。
    /// </summary>
    /// <param name="cacheDir">缓存目录；缺省为 LocalApplicationData/ilspymcp。</param>
    /// <param name="now">时间源（测试注入固定时钟）；缺省为 <see cref="DateTimeOffset.Now"/>。</param>
    public UpdateChecker(string? cacheDir = null, Func<DateTimeOffset>? now = null)
    {
        _cacheDir = cacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ilspymcp");
        _now = now ?? (() => DateTimeOffset.Now);
    }

    /// <summary>
    /// 磁盘缓存结构：最近一次尝试时间、最近一次成功时间与查到的最新版本（成功/失败均可空）。
    /// </summary>
    public sealed class UpdateCheckCache
    {
        /// <summary>最近一次检查尝试时间（成功或失败都会写入）。</summary>
        public DateTimeOffset LastAttemptAt { get; set; }

        /// <summary>最近一次成功联网查询时间（从未成功为空）。</summary>
        public DateTimeOffset? LastSuccessAt { get; set; }

        /// <summary>最近一次成功查询到的最新版本号（从未成功为空）。</summary>
        public string? Latest { get; set; }
    }

    /// <summary>
    /// 同步读磁盘缓存，返回有新版本时的注入提示文本；缓存缺失/损坏/已是新版一律返回 null（fail-silent，零网络）。
    /// </summary>
    public string? GetCachedInstructions()
    {
        try
        {
            var cache = ReadCache();
            if (cache is null || string.IsNullOrEmpty(cache.Latest)) return null;
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            if (current is null || !Version.TryParse(cache.Latest, out var latest)) return null;
            if (latest <= current) return null;
            return $"{AppConfig.NuGetPackageId}: 当前 {current.ToString(3)}，有新版本 {cache.Latest}。可执行 `dotnet tool update --global {AppConfig.NuGetPackageId}` 升级（升级属高风险操作，需用户确认执行）。";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 若缓存新鲜（成功 TTL 内或失败冷却期内）直接返回缓存结果不联网；否则联网查询最新稳定版并落盘，失败静默降级保留旧值。
    /// </summary>
    public async Task<string?> RefreshIfStaleAsync()
    {
        var cache = ReadCache();
        var now = _now();
        if (IsFresh(cache, now)) return cache?.Latest;

        try
        {
            var latest = await AppServices.NuGet.GetLatestStableVersionAsync(AppConfig.NuGetPackageId);
            if (latest is not null)
            {
                WriteCache(new UpdateCheckCache { LastAttemptAt = now, LastSuccessAt = now, Latest = latest });
                return latest;
            }
            WriteCache(new UpdateCheckCache { LastAttemptAt = now, LastSuccessAt = cache?.LastSuccessAt, Latest = cache?.Latest });
            return cache?.Latest;
        }
        catch
        {
            WriteCache(new UpdateCheckCache { LastAttemptAt = now, LastSuccessAt = cache?.LastSuccessAt, Latest = cache?.Latest });
            return cache?.Latest;
        }
    }

    private string CachePath => Path.Combine(_cacheDir, AppConfig.UpdateCheckCacheFileName);

    private bool IsFresh(UpdateCheckCache? cache, DateTimeOffset now)
    {
        if (cache is null) return false;
        if (cache.LastSuccessAt is { } s && now - s < AppConfig.UpdateCheckSuccessTtl) return true;
        return now - cache.LastAttemptAt < AppConfig.UpdateCheckFailureBackoff;
    }

    private UpdateCheckCache? ReadCache()
    {
        try
        {
            if (!File.Exists(CachePath)) return null;
            var json = File.ReadAllText(CachePath);
            return JsonSerializer.Deserialize<UpdateCheckCache>(json);
        }
        catch
        {
            return null;
        }
    }

    private void WriteCache(UpdateCheckCache cache)
    {
        try
        {
            Directory.CreateDirectory(_cacheDir);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(cache));
        }
        catch
        {
            // 只读目录/写盘失败静默吞掉，不影响功能
        }
    }
}
