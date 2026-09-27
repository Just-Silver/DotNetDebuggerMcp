using System.Reflection;

namespace DotNetDebuggerMcp.Configuration;

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
    /// 全局操作默认超时秒数：所有 MCP 工具的 timeoutSeconds 参数默认值；工具可 per-call 覆盖。
    /// </summary>
    public const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// decompile_member 单次匹配成员数上限：超过此值时不再逐一反编译，仅返回成员签名清单（元数据秒回），避免为海量匹配做无谓反编译。
    /// </summary>
    public const int MaxMemberMatches = 20;

    /// <summary>
    /// debug_wait/debug_state 停点上下文的默认行数预算：方法完整行数 ≤ 预算显示整个函数，超出则按当前语句截取预算行
    /// （0=关闭）。工具参数默认值与实现引用此常量；[Description] 文案（ToolParameterText.ContextLinesParam）中
    /// 「默认 100」为编译期常量限制下的文本副本，改预算两处同步。
    /// </summary>
    public const int DefaultStopContextBudgetLines = 100;

    /// <summary>
    /// screenshot 输出单边最大像素（长边上限）：超过则双轴等比缩到限内（spec §6.2）。
    /// 1568 对齐官方 vision 长边上限（超限会被服务端二次 resize 导致坐标漂移）。
    /// 经参数传入捕获库（`SharpSight.Capture`），缩放实现唯一在库侧（spec §4.2）。
    /// </summary>
    public const int ScreenshotMaxDimension = 1568;

    /// <summary>
    /// screenshot 内联返回阈值默认值（base64 后字节数，chrome-devtools 先例 2MB）：达到即改落盘返回绝对路径。
    /// 实际判定读 <see cref="InlineImageBase64Bytes"/>（测试可注入，见 <see cref="ConfigureForTest"/>）。
    /// </summary>
    public const long DefaultInlineImageBase64Bytes = 2 * 1024 * 1024;

    /// <summary>
    /// screenshot 内联返回阈值（默认 <see cref="DefaultInlineImageBase64Bytes"/>；测试经
    /// <see cref="ConfigureForTest"/> 注入，使「超限自动落盘」分支可测）。
    /// </summary>
    internal static long InlineImageBase64Bytes = DefaultInlineImageBase64Bytes;

    /// <summary>
    /// screenshot 落盘根目录默认值：**临时目录**（%TEMP%\DotNetDebuggerMcp\screenshots）。
    /// 截图是临时产物——绝不落进程工作目录（那会污染调用方的项目目录），也不占用本地应用数据目录。
    /// 落盘文件不自动清理（YAGNI，README 注明位置）。
    /// </summary>
    private static readonly string DefaultScreenshotsDir = Path.Combine(
        Path.GetTempPath(), NuGetPackageId, "screenshots");

    /// <summary>测试注入的落盘根目录覆盖（见 <see cref="ConfigureForTest"/>）。</summary>
    internal static string? ScreenshotsDirOverride;

    /// <summary>
    /// screenshot 落盘根目录：超限自动落盘、以及**相对 <c>filePath</c>** 均以此为基准（绝对 <c>filePath</c> 按原样）。
    /// </summary>
    public static string ScreenshotsDir => ScreenshotsDirOverride ?? DefaultScreenshotsDir;

    /// <summary>
    /// 测试注入（对齐 <c>AppServices.ConfigureForTest</c> 范式）：阈值/落盘目录是运行时不可控点，
    /// 由此可替换。参数传 null = 该项恢复默认值。用完须 <see cref="ResetForTest"/>。
    /// </summary>
    internal static void ConfigureForTest(long? inlineImageBase64Bytes = null, string? screenshotsDirOverride = null)
    {
        InlineImageBase64Bytes = inlineImageBase64Bytes ?? DefaultInlineImageBase64Bytes;
        ScreenshotsDirOverride = screenshotsDirOverride;
    }

    /// <summary>恢复 <see cref="ConfigureForTest"/> 造成的替换。</summary>
    internal static void ResetForTest()
    {
        InlineImageBase64Bytes = DefaultInlineImageBase64Bytes;
        ScreenshotsDirOverride = null;
    }

    /// <summary>
    /// 本工具发布的 NuGet 包 id，环境自检（CLI -c/握手注入）用它查询是否有新版本。
    /// </summary>
    public const string NuGetPackageId = "DotNetDebuggerMcp";

    /// <summary>
    /// NuGet flatcontainer 版本清单 API 前缀（拼上包 id 即得完整 URL）。
    /// </summary>
    public const string NuGetVersionListUrlPrefix = "https://api.nuget.org/v3-flatcontainer/";

    /// <summary>
    /// NuGet 新版本检查磁盘缓存文件名（位于 <see cref="DotNetDebuggerMcp.UpdateCheck.UpdateChecker"/> 的缓存目录下）。
    /// </summary>
    public const string UpdateCheckCacheFileName = "update-check.json";

    /// <summary>
    /// 缓存条目滑动过期时长：自最后一次 Get/Put 起超过该时长未访问即过期（固定 30 分钟，不可关闭）。
    /// </summary>
    public static readonly TimeSpan CacheEntrySlidingTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// 缓存定时清理间隔：后台 Timer 每隔该时长扫描并清理过期条目（固定 5 分钟）。
    /// </summary>
    public static readonly TimeSpan CacheCleanupInterval = TimeSpan.FromMinutes(5);

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