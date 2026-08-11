using ILSpyMcp.Caching;
using ILSpyMcp.Configuration;
using ILSpyMcp.Decompiler;
using ILSpyMcp.Pipeline;
using ILSpyMcp.Processes;
using ILSpyMcp.UpdateCheck;

namespace ILSpyMcp.Services;

/// <summary>
/// 进程级共享服务容器：缓存、执行管道、进程内反编译服务、NuGet 查询全会话单例，避免每个工具各自持有独立实例。 测试可经
/// <see cref="ConfigureForTest"/> 替换缓存。
/// </summary>
internal static class AppServices
{
    /// <summary>
    /// 共享反编译结果缓存（LRU，上限 <see cref="AppConfig.MaxCacheBytes"/>）（可替换：测试经 <see
    /// cref="ConfigureForTest"/> 注入小缓存）。
    /// </summary>
    public static DecompileCache Cache = new();

    /// <summary>
    /// 共享执行管道（缓存 → 进程内反编译回源 → 分页），反编译类工具经此调用。
    /// </summary>
    public static ToolPipeline Pipeline = new(Cache);

    /// <summary>
    /// 共享进程内反编译服务（InProcessDecompiler 方法均为静态，实例供统一入口与后续扩展）。
    /// </summary>
    public static InProcessDecompiler Decompiler = new();

    /// <summary>
    /// 共享 NuGet 包版本查询（环境自检用它检查 ilspymcp 是否有新版本）。
    /// </summary>
    public static NuGetClient NuGet = new();

    /// <summary>
    /// 共享新版本检查与注入文本组装：环境自检（CLI -c）与 MCP 握手（ServerInstructions 注入）共用同一磁盘缓存，
    /// 缓存文件跨进程共享（重启不丢、避免每次会话都联网复查）。网络查询复用 <see cref="NuGet"/> 共享实例。
    /// </summary>
    public static UpdateChecker Updater = new(queryLatest: id => NuGet.GetLatestStableVersionAsync(id));

    /// <summary>
    /// 环境自检报告：会话内只真实组装一次，后续直接复用缓存文本。 单飞保证并发首次调用只执行一次完整检查。依赖经参数传入 UpdateCheck 层，避免反向引用。
    /// </summary>
    public static Lazy<Task<string>> StatusReport =
        new(() => EnvironmentChecker.BuildReportAsync(Updater), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 待 Task 6 删除：ilspycmd 安装检测器——仅 <see cref="ILSpyMcp.Validation.ToolPreflight"/> 仍引用（本单元已移除全部工具对
    /// ToolPreflight 的调用），反编译已改进程内、不再需要安装检测；保留字段以维持 ToolPreflight 可编译，删除 ToolPreflight 时一并移除。
    /// </summary>
    public static InstallChecker Installer = new(new ProcessRunner());

    /// <summary>
    /// 测试注入：以指定缓存（缺省为默认 <see cref="AppConfig.MaxCacheBytes"/> 上限的缓存）重建 Cache/Pipeline， 使工具层可在可控
    /// 缓存状态下测试。
    /// </summary>
    /// <param name="cache">测试用缓存；缺省为默认上限的缓存。</param>
    internal static void ConfigureForTest(DecompileCache? cache = null)
    {
        Cache = cache ?? new DecompileCache();
        Pipeline = new ToolPipeline(Cache);
        NuGet = new NuGetClient();
        Updater = new UpdateChecker(Path.Combine(Path.GetTempPath(), "ilspymcp-tests", Guid.NewGuid().ToString("N")),
            queryLatest: id => NuGet.GetLatestStableVersionAsync(id));
        StatusReport = new Lazy<Task<string>>(() => EnvironmentChecker.BuildReportAsync(Updater), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// 恢复默认实现（测试后调用，避免污染其他用例）。
    /// </summary>
    internal static void ResetForTest()
    {
        Cache = new DecompileCache();
        Pipeline = new ToolPipeline(Cache);
        NuGet = new NuGetClient();
        Updater = new UpdateChecker(queryLatest: id => NuGet.GetLatestStableVersionAsync(id));
        StatusReport = new Lazy<Task<string>>(() => EnvironmentChecker.BuildReportAsync(Updater), LazyThreadSafetyMode.ExecutionAndPublication);
    }
}
