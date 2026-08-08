namespace ILSpyMcp;

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
    /// 子进程 stdout 读取的累计字节上限；超过即终止进程并返回提示，防止单类型反编译巨型输出（OOM）拖垮整个 MCP 进程。
    /// 取值与缓存上限一致：超过此值的结果不缓存也不返回给 agent，由 agent 改用 decompile_to_dir 反编译到本地目录。
    /// </summary>
    public const long MaxOutputBytes = MaxCacheBytes;

    /// <summary>
    /// 全局操作默认超时秒数：所有 MCP 工具的 timeoutSeconds 参数默认值；工具可 per-call 覆盖。
    /// </summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// 全局操作默认超时（由 <see cref="DefaultTimeoutSeconds"/> 派生）。
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    /// <summary>
    /// ilspycmd 安装检测子进程的超时上限（快速失败，避免拖慢首次工具调用）。
    /// </summary>
    public static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
}
