namespace ILSpyMcp;

/// <summary>
/// 检测 ilspycmd 是否已安装；结果会话内缓存一次，避免每次调用都拉起子进程。
/// </summary>
public sealed class InstallChecker
{
    /// <summary>
    /// 未安装 ilspycmd 时返回给用户的安装提示文本。
    /// </summary>
    public const string InstallHint =
        "未检测到 ilspycmd 已安装。请告知用户：运行 `dotnet tool install --global ilspycmd` 安装后重试（安装属于高风险操作，需用户手动确认执行）。";

    private readonly IProcessRunner _process;

    /// <summary>
    /// 检测任务的单飞缓存：ExecutionAndPublication 保证并发首次调用只执行一次检测。
    /// </summary>
    private readonly Lazy<Task<bool>> _check;

    private bool? _installed;

    /// <summary>
    /// 以注入的进程执行器构造；检测结果缓存于实例内。
    /// </summary>
    /// <param name="process">子进程执行器。</param>
    public InstallChecker(IProcessRunner process)
    {
        _process = process;
        _check = new Lazy<Task<bool>>(RunCheckAsync, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// 本次会话内已确定的检测结果；未检测过为 null。
    /// </summary>
    public bool? IsInstalled => _installed;

    /// <summary>
    /// 检测 ilspycmd 是否已安装；单飞检测一次，结果缓存后直接复用。
    /// </summary>
    /// <returns>ilspycmd 是否已安装。</returns>
    public async Task<bool> CheckInstalledAsync()
    {
        var installed = await _check.Value;
        _installed = installed;
        return installed;
    }

    /// <summary>
    /// 实际执行一次安装检测：调用 ilspycmd -v，退出码为 0 视为已安装。
    /// </summary>
    private async Task<bool> RunCheckAsync()
    {
        var result = await _process.RunAsync(ToolCommand.DefaultExecutable, new[] { "-v" }, Environment.CurrentDirectory, AppConfig.CheckTimeout);
        return result.Code == 0;
    }
}