using ILSpyMcp.Configuration;

using System.Text.Json;

namespace ILSpyMcp.UpdateCheck;

/// <summary>
/// NuGet 新版本检查的磁盘缓存与报告段组装：成功/失败结果落盘跨进程共享，重启不丢，避免每次会话都联网复查。 环境自检报告经 <see cref="GetCachedNuGetLine"/>
/// 同步读缓存（零网络，无结果留白），网络刷新由握手后台 <see cref="RefreshIfStaleAsync"/> 承担。网络查询经构造函数注入的查询委托（测试注入 fake，生产由
/// AppServices 传入共享 NuGetClient）； 一切 IO/网络异常静默降级，绝不影响核心功能。
/// </summary>
public sealed class UpdateChecker
{
    private readonly string _cacheDir;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, Task<string?>> _queryLatest;

    /// <summary>
    /// 以默认缓存目录（LocalApplicationData/ilspymcp）、系统时钟与 NuGet 查询委托构造。
    /// </summary>
    /// <param name="cacheDir">缓存目录；缺省为 LocalApplicationData/ilspymcp。</param>
    /// <param name="now">时间源（测试注入固定时钟）；缺省为 <see cref="DateTimeOffset.Now"/>。</param>
    /// <param name="queryLatest">按包 id 查询最新稳定版的委托；缺省为新建 <see cref="NuGetClient"/> 查询。</param>
    public UpdateChecker(string? cacheDir = null, Func<DateTimeOffset>? now = null, Func<string, Task<string?>>? queryLatest = null)
    {
        _cacheDir = cacheDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ilspymcp");
        _now = now ?? (() => DateTimeOffset.Now);
        _queryLatest = queryLatest ?? (id => new NuGetClient().GetLatestStableVersionAsync(id));
    }

    private string CachePath => Path.Combine(_cacheDir, AppConfig.UpdateCheckCacheFileName);

    /// <summary>
    /// 判断 NuGet 最新版本是否高于当前程序集版本。 环境自检报告与握手注入共用此比较，避免版本比较规则两处独立实现后漂移。
    /// </summary>
    /// <param name="latestVersion">NuGet 查询到的最新版本号；为 null 或无法解析时视为无法比较。</param>
    /// <param name="currentVersion">当前程序集版本；为 null 时视为无法比较。</param>
    /// <returns>最新版本解析成功且高于当前版本时返回 true。</returns>
    public static bool IsNewerThanCurrent(string? latestVersion, Version? currentVersion)
        => currentVersion is not null
           && latestVersion is not null
           && Version.TryParse(latestVersion, out var latest)
           && latest > currentVersion;

    /// <summary>
    /// 判断最新版本是否高于当前程序集版本（已解析版本重载，供缓存读取路径复用解析结果，避免重复 TryParse）。
    /// </summary>
    /// <param name="latestVersion">NuGet 查询到的最新版本（已解析）。</param>
    /// <param name="currentVersion">当前程序集版本；为 null 时视为无法比较。</param>
    /// <returns>最新版本高于当前版本时返回 true。</returns>
    public static bool IsNewerThanCurrent(Version latestVersion, Version? currentVersion)
        => currentVersion is not null && latestVersion > currentVersion;

    /// <summary>
    /// 同步读磁盘缓存（零网络），返回 NuGet 更新状态：是否有新版本与报告行。 无有效检查记录（无缓存/损坏/版本无法解析）返回 null，
    /// 报告该段留白——由握手后台刷新补位供下次会话，绝不阻塞握手。
    /// </summary>
    public NuGetUpdateStatus? GetCachedNuGetStatus()
    {
        try
        {
            var current = AppConfig.CurrentVersion;
            var currentText = current?.ToString(3) ?? "未知";
            var cache = ReadCache();
            if (cache is null || string.IsNullOrEmpty(cache.Latest) || !Version.TryParse(cache.Latest, out var latest)) return null;
            var hasNew = IsNewerThanCurrent(latest, current);
            var line = hasNew
                ? $"{AppConfig.NuGetPackageId}: 当前 {currentText}，NuGet 最新 {cache.Latest}。可执行 `dotnet tool update --global {AppConfig.NuGetPackageId}` 升级。"
                : $"{AppConfig.NuGetPackageId}: 当前 {currentText}，已是最新版本。";
            return new NuGetUpdateStatus(hasNew, line);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 同步读磁盘缓存（零网络），返回环境自检报告用的 NuGet 段整行（CLI -c 输出）：有新版本给升级建议、已是最新明确告知。 无有效检查记录返回 null。
    /// </summary>
    public string? GetCachedNuGetLine() => GetCachedNuGetStatus()?.Line;

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
            var latest = await _queryLatest(AppConfig.NuGetPackageId);
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

    /// <summary>
    /// NuGet 更新状态：是否有新版本与对应的报告行（握手注入据此区分指令式提示与背景信息）。
    /// </summary>
    /// <param name="HasNewVersion">NuGet 最新稳定版高于当前程序集版本。</param>
    /// <param name="Line">中文报告行：有新版本给升级建议、已是最新明确告知。</param>
    public sealed record NuGetUpdateStatus(bool HasNewVersion, string Line);

    /// <summary>
    /// 磁盘缓存结构：最近一次尝试时间、最近一次成功时间与查到的最新版本（成功/失败均可空）。
    /// </summary>
    public sealed class UpdateCheckCache
    {
        /// <summary>
        /// 最近一次检查尝试时间（成功或失败都会写入）。
        /// </summary>
        public DateTimeOffset LastAttemptAt { get; set; }

        /// <summary>
        /// 最近一次成功联网查询时间（从未成功为空）。
        /// </summary>
        public DateTimeOffset? LastSuccessAt { get; set; }

        /// <summary>
        /// 最近一次成功查询到的最新版本号（从未成功为空）。
        /// </summary>
        public string? Latest { get; set; }
    }
}