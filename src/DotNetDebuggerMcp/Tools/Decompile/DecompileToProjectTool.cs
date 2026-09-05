using DotNetDebuggerMcp.Configuration;
using DotNetDebugger.Decompiler.Decompiler;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Decompile;

/// <summary>
/// 将 .NET 程序集（dll/exe）以可编译项目形式反编译写入指定目录。结果写入磁盘而非标准输出，不做输出量截断。
/// </summary>
[McpServerToolType]
public static class DecompileToProjectTool
{
    /// <summary>
    /// 以可编译项目形式反编译整个程序集写入指定目录（每个类型一个源码文件），不经过缓存。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="outputDir">输出目录；反编译结果写入该目录而非标准输出（必填）。</param>
    /// <param name="nestedDirectories">输出到目录时按命名空间使用嵌套目录（默认 true）。</param>
    /// <param name="timeoutSeconds">本次反编译写盘超时秒数（默认 30，全量写盘大程序集可调大）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>写入结果提示或错误提示文本。</returns>
    [McpServerTool]
    [Description("以可编译项目形式反编译整个程序集到指定目录（含项目文件，每个类型一个源码文件，结果写盘不受截断）。nestedDirectories 默认 true（按命名空间嵌套目录输出）。只取个别类型的源码文件请用 decompile_to_dir 的 typeName 参数。")]
    public static async Task<string> DecompileToProject(
        [Description(ToolParameterText.AssemblyParam)] string assembly = "",
        [Description("输出目录，反编译结果写入该目录而非标准输出（必填）")] string outputDir = "",
        [Description("是否按命名空间嵌套目录输出（默认 true）")] bool nestedDirectories = true,
        [Description(ToolParameterText.DiskTimeoutParam)] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        return await ToolExecutor.RunToDisk(assembly, outputDir, timeoutSeconds, cancellationToken,
            (assemblyFull, outputFull, ct) => InProcessDecompiler.DecompileToProject(
                assemblyFull, outputFull, nestedDirectories, ct));
    }
}