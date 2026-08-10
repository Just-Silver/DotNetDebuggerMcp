using ILSpyMcp.Infrastructure;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace ILSpyMcp.Tools;

/// <summary>
/// 列出 .NET 程序集（dll/exe）中指定类别的实体类型到标准输出。
/// </summary>
[McpServerToolType]
public static class ListTypesTool
{
    /// <summary>
    /// 列出指定类别的实体类型，经共享管道缓存与 lines 分页。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="list">实体类型类别组合：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 "csi"（必填）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"（1-based 含两端，单次最多 500 行）。</param>
    /// <param name="timeoutSeconds">本次回源超时秒数（默认 30）。</param>
    /// <returns>带行号的类型列表或错误提示文本。</returns>
    [McpServerTool]
    [Description("列出 .NET 程序集（dll/exe）中指定类别的实体类型到标准输出。输出每行带行号标注，可直接引用具体行。结果默认只返回前 200 行，可用 lines 参数按行号范围拉取后续（结果缓存在内存）。")]
    public static async Task<string> ListTypes(
        [Description("要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("列出程序集中的实体类型：c=class, i=interface, s=struct, d=delegate, e=enum；可组合多个字母同时列出，例如 \"csi\"（必填）")] string list = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多 500 行），例如 \"200-400\"")] string lines = "",
        [Description("本次回源超时秒数，默认 30；大程序集可调大")] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds)
    {
        // 前置检查：ilspycmd 已安装且 assembly 参数有效；未通过直接返回提示
        if (await ToolPreflight.CheckAsync(assembly) is { } preflightError) return preflightError;
        // 参数校验：list 必填且只能由 c/i/s/d/e 组成
        if (!ArgumentValidators.ValidateList(list, out var argError)) return argError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        // 由参数结构统一派生命令行与缓存签名，杜绝命令/签名两处手写导致缓存 key 错配
        var command = new ToolCommand(ToolCommand.DefaultExecutable, Path.GetFullPath(assembly),
            new ToolParameter("-l", list));

        // 走共享执行管道：缓存命中 → 回源 → lines 分页（list 结果体量小，永不超限，仅取文本）
        return (await AppServices.Pipeline.ExecuteAsync(assembly, command, lines, TimeSpan.FromSeconds(timeoutSeconds))).Text;
    }
}