using ILSpyMcp.Formatting;
using ILSpyMcp.Pipeline;
using ILSpyMcp.Processes;

namespace ILSpyMcp.Services;

/// <summary>
/// 工具执行共享辅助：统一「程序集路径安全解析」与「管道/子进程调用」样板，避免各工具重复手写并在细节上漂移。
/// </summary>
internal static class ToolExecutor
{
    /// <summary>
    /// 解析程序集绝对路径；路径非法时返回中文提示。
    /// </summary>
    /// <param name="assembly">程序集路径（相对或绝对）。</param>
    /// <param name="fullPath">解析出的绝对路径；失败时为空串。</param>
    /// <returns>路径非法时返回提示文本；成功为 null。</returns>
    public static string? ResolveAssembly(string assembly, out string fullPath)
    {
        try
        {
            fullPath = Path.GetFullPath(assembly);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            fullPath = "";
            return $"路径非法：{ex.Message}";
        }
    }

    /// <summary>
    /// 经共享执行管道反编译/列类型（缓存命中 → 回源 → 行号标注 + lines 分页 + 头部信息块）。
    /// </summary>
    public static async Task<string> RunPipelineAsync(ToolCommand command, string lines, TimeSpan timeout, CancellationToken cancellationToken, FormatContext context)
        => (await AppServices.Pipeline.ExecuteAsync(command, lines, timeout, cancellationToken, context)).Text;

    /// <summary>
    /// 经共享执行管道合并反编译（decompile_member 多匹配，各自缓存后合并、行号连续）。
    /// </summary>
    public static async Task<string> RunMergedAsync(IReadOnlyList<ToolCommand> commands, string lines, TimeSpan timeout, CancellationToken cancellationToken, FormatContext context)
        => (await AppServices.Pipeline.ExecuteMergedAsync(commands, lines, timeout, cancellationToken, context)).Text;

    /// <summary>
    /// 直接经子进程执行写盘（decompile_to_dir，不经缓存）；退出码非 0 由调用方处理 stderr 提示。
    /// </summary>
    public static Task<ProcessResult> RunProcessAsync(ToolCommand command, string cwd, TimeSpan timeout, CancellationToken cancellationToken)
        => AppServices.Process.RunAsync(command.Executable, command.Args, cwd, timeout, cancellationToken);
}