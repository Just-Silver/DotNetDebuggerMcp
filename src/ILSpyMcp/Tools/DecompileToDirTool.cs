using ILSpyMcp.Configuration;
using ILSpyMcp.Decompiler;
using ILSpyMcp.Services;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace ILSpyMcp.Tools;

/// <summary>
/// 将 .NET 程序集（dll/exe）反编译写入指定目录（全量或单个类型）。结果写入磁盘而非标准输出，不做输出量截断。
/// </summary>
[McpServerToolType]
public static class DecompileToDirTool
{
    /// <summary>
    /// 反编译程序集写入指定目录（全量或单个类型），不经过缓存。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="outputDir">输出目录；反编译结果写入该目录而非标准输出（必填）。</param>
    /// <param name="typeName">指定则仅反编译该全限定类型名；省略则反编译整个程序集。</param>
    /// <param name="nestedDirectories">指定则按命名空间嵌套目录输出；当前单文件输出模式下该参数不产生效果（默认 true）。</param>
    /// <param name="timeoutSeconds">本次反编译写盘超时秒数（默认 30，全量写盘大程序集可调大）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>写入结果提示或错误提示文本。</returns>
    [McpServerTool]
    [Description("将 .NET 程序集（dll/exe）反编译写入指定目录（全量或指定单个类型）。结果写入磁盘而非标准输出，不做输出量截断；写盘完成后可直接读取输出目录下的源码文件。typeName 指定则仅反编译该类型，省略则反编译整个程序集。")]
    public static async Task<string> DecompileToDir(
        [Description("要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("输出目录；反编译结果写入该目录而非标准输出（必填）")] string outputDir = "",
        [Description("指定则仅反编译该全限定类型名，例如 System.String；省略则反编译整个程序集")] string typeName = "",
        [Description("指定则按命名空间嵌套目录输出；当前单文件输出模式下该参数不产生效果（默认 true）")] bool nestedDirectories = true,
        [Description("本次反编译写盘超时秒数，默认 30；全量写盘大程序集可调大")] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（进程内反编译，无安装前置）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return assemblyError;
        // 参数校验：outputDir 必填且路径合法
        if (!ArgumentValidators.ValidateOutputDir(outputDir, out var argError)) return argError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        var cwd = Environment.CurrentDirectory;
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;
        var outputFull = Path.GetFullPath(outputDir, cwd);
        var timeoutHint = $"反编译写盘超时（超过 {timeoutSeconds} 秒），已放弃本次写盘；可调大 timeoutSeconds 后重试";

        // 进程内反编译写盘：单文件 {typeName 空 ? 程序集名 : typeName}.decompiled.cs；超时/取消返回提示文本，不抛异常
        return await InProcessDecompiler.RunWithTimeoutAsync(
            () => InProcessDecompiler.DecompileToDir(assemblyFull, outputFull, string.IsNullOrEmpty(typeName) ? null : typeName),
            TimeSpan.FromSeconds(timeoutSeconds),
            cancellationToken,
            timeoutHint);
    }
}
