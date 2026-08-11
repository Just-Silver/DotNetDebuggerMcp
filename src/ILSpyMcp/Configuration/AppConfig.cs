using System.Reflection;

namespace ILSpyMcp.Configuration;

/// <summary>
/// 全局静态参数集中配置：缓存上限、超时等可调参数统一在此维护，便于集中修改与调整。
/// </summary>
internal static class AppConfig
{
    /// <summary>
    /// 反编译结果内存缓存的总字节上限（LRU，超出后驱逐最久未访问的条目）；单条结果超过此值时不入缓存。
    /// </summary>
    public const long MaxCacheBytes = 64 * 1024 * 1024;

    /// <summary>
    /// 单次反编译生成文本的字符数上限：进程内反编译完成后检查，超过即返回「建议改用 decompile_to_dir」提示且该结果不入缓存，
    /// 防止巨型反编译输出（OOM）拖垮整个 MCP 进程。取值与缓存上限一致。
    /// </summary>
    public const long MaxOutputBytes = MaxCacheBytes;

    /// <summary>
    /// 全局操作默认超时秒数：所有 MCP 工具的 timeoutSeconds 参数默认值；工具可 per-call 覆盖。
    /// </summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// decompile_member 单次匹配成员数上限：超过此值时不再逐一反编译，仅返回成员签名清单（纯元数据秒回），避免为海量匹配启动过多子进程。
    /// </summary>
    public const int MaxMemberMatches = 20;

    /// <summary>
    /// 本工具发布的 NuGet 包 id，环境自检（CLI -c/握手注入）用它查询是否有新版本。
    /// </summary>
    public const string NuGetPackageId = "ilspymcp";

    /// <summary>
    /// NuGet flatcontainer 版本清单 API 前缀（拼上包 id 即得完整 URL）。
    /// </summary>
    public const string NuGetVersionListUrlPrefix = "https://api.nuget.org/v3-flatcontainer/";

    /// <summary>
    /// NuGet 新版本检查磁盘缓存文件名（位于 <see cref="ILSpyMcp.UpdateCheck.UpdateChecker"/> 的缓存目录下）。
    /// </summary>
    public const string UpdateCheckCacheFileName = "update-check.json";

    /// <summary>
    /// 全局操作默认超时（由 <see cref="DefaultTimeoutSeconds"/> 派生）。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <summary>
    /// NuGet 新版本检查的超时上限；超时/网络失败时静默跳过该检查项（不影响反编译功能）。
    /// </summary>
    public static readonly TimeSpan NuGetCheckTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// NuGet 新版本检查成功的缓存有效期：成功查到一次后，该时限内不再联网复查（跨进程共享、重启不丢）。
    /// </summary>
    public static readonly TimeSpan UpdateCheckSuccessTtl = TimeSpan.FromHours(24);

    /// <summary>
    /// NuGet 新版本检查失败的冷却期：失败后该时限内不再重试，避免断网/限流环境下每个会话都干等网络超时。
    /// </summary>
    public static readonly TimeSpan UpdateCheckFailureBackoff = TimeSpan.FromHours(1);

    /// <summary>
    /// 当前程序集版本（NuGet 包版本来源）。 反编译工具与环境自检/握手注入统一经此获取当前版本，避免各处重复读取 Assembly 元数据。
    /// </summary>
    public static Version? CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version;
}