namespace ILSpyMcp.Infrastructure;

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
    /// 检测到的 ilspycmd 版本（从 -v 输出解析，如 11.0.0.9335）；未检测过或解析失败为 null。
    /// </summary>
    private Version? _version;

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
    /// 检测到的 ilspycmd 版本；需先调用 <see cref="CheckInstalledAsync"/>，未安装或版本解析失败为 null。
    /// </summary>
    public Version? Version => _version;

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
    /// 实际执行一次安装检测：调用 ilspycmd -v，退出码为 0 视为已安装，并从输出解析版本号（格式 "ilspycmd: 11.0.0.9335"）。
    /// </summary>
    private async Task<bool> RunCheckAsync()
    {
        var result = await _process.RunAsync(ToolCommand.DefaultExecutable, new[] { "-v" }, Environment.CurrentDirectory, AppConfig.CheckTimeout);
        if (result.Code != 0) return false;
        _version = ParseVersion(result.Stdout);
        return true;
    }

    /// <summary>
    /// 从 ilspycmd -v 输出解析版本号：取 "ilspycmd:" 行冒号后的版本段；格式不符时返回 null（仍视为已安装，仅版本未知）。
    /// </summary>
    /// <param name="stdout">ilspycmd -v 的完整输出。</param>
    /// <returns>解析出的版本号；无法解析为 null。</returns>
    private static Version? ParseVersion(string stdout)
    {
        var line = stdout.Split('\n').FirstOrDefault(l => l.StartsWith("ilspycmd:", StringComparison.OrdinalIgnoreCase));
        if (line is null) return null;
        var value = line.Split(':')[1].Trim();
        return Version.TryParse(value, out var v) ? v : null;
    }
}