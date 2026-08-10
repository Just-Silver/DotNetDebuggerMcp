using ILSpyMcp.Caching;
using ILSpyMcp.Configuration;
using ILSpyMcp.Pipeline;
using ILSpyMcp.Processes;
using ILSpyMcp.UpdateCheck;

namespace ILSpyMcp.Services;

/// <summary>
/// 进程级共享服务容器：缓存、执行管道、安装检测全会话单例，避免每个工具各自持有独立实例。 测试可经 <see cref="ConfigureForTest"/> 替换进程执行器与缓存。
/// </summary>
internal static class AppServices
{
    /// <summary>
    /// 共享子进程执行器（可替换：测试经 <see cref="ConfigureForTest"/> 注入 fake）。
    /// </summary>
    public static IProcessRunner Process = new ProcessRunner();

    /// <summary>
    /// 共享反编译结果缓存（LRU，上限 <see cref="AppConfig.MaxCacheBytes"/>）（可替换：测试经 <see
    /// cref="ConfigureForTest"/> 注入小缓存）。
    /// </summary>
    public static DecompileCache Cache = new();

    /// <summary>
    /// 共享执行管道（缓存 → 回源 → 分页），工具经此调用 ilspycmd。
    /// </summary>
    public static ToolPipeline Pipeline = new(Process, Cache);

    /// <summary>
    /// 共享 ilspycmd 安装检测（会话内缓存一次）。
    /// </summary>
    public static InstallChecker Installer = new(Process);

    /// <summary>
    /// 共享 NuGet 包版本查询（check_status 用它检查 ilspymcp 是否有新版本）。
    /// </summary>
    public static NuGetClient NuGet = new();

    /// <summary>
    /// 共享新版本检查与注入文本组装：check_status 与 MCP 握手（ServerInstructions 注入）共用同一磁盘缓存，
    /// 缓存文件跨进程共享（重启不丢、避免每次会话都联网复查）。
    /// </summary>
    public static UpdateChecker Updater = new();

    /// <summary>
    /// check_status 的环境自检报告：会话内只真实检查一次（安装/版本变化需重启 CLI 才生效），后续直接复用缓存文本。
    /// 单飞保证并发首次调用只执行一次完整检查。
    /// </summary>
    public static Lazy<Task<string>> StatusReport =
        new(EnvironmentChecker.BuildReportAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 测试注入：以指定进程执行器（与可选缓存）重建 Cache/Pipeline/Installer，使工具层可在不启动真实子进程的情况下测试。
    /// </summary>
    /// <param name="process">测试用进程执行器（fake）。</param>
    /// <param name="cache">测试用缓存；缺省为默认 <see cref="AppConfig.MaxCacheBytes"/> 上限的缓存。</param>
    internal static void ConfigureForTest(IProcessRunner process, DecompileCache? cache = null)
    {
        Process = process;
        Cache = cache ?? new DecompileCache();
        Pipeline = new ToolPipeline(Process, Cache);
        Installer = new InstallChecker(Process);
        NuGet = new NuGetClient();
        Updater = new UpdateChecker(Path.Combine(Path.GetTempPath(), "ilspymcp-tests", Guid.NewGuid().ToString("N")));
        StatusReport = new Lazy<Task<string>>(EnvironmentChecker.BuildReportAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// 恢复默认实现（测试后调用，避免污染其他用例）。
    /// </summary>
    internal static void ResetForTest()
    {
        Process = new ProcessRunner();
        Cache = new DecompileCache();
        Pipeline = new ToolPipeline(Process, Cache);
        Installer = new InstallChecker(Process);
        NuGet = new NuGetClient();
        Updater = new UpdateChecker();
        StatusReport = new Lazy<Task<string>>(EnvironmentChecker.BuildReportAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }
}
