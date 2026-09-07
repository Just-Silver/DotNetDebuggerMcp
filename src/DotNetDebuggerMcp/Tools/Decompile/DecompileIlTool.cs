using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Formatting;
using DotNetDebuggerMcp.Pipeline;
using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Decompile;

/// <summary>
/// 反汇编指定方法的方法体为 IL 文本（按方法元数据 token，如 0x06000005）。
/// IL 是 async 状态机等编译器生成类型反编译为 C# 失败/难读场景的兜底——逐指令还原方法体真相，
/// 供 agent 定位真实控制流后回到 debug 断点闭环（token 直接可用于 debug_breakpoint_set）。
/// </summary>
[McpServerToolType]
public static class DecompileIlTool
{
    /// <summary>
    /// 按方法 token 反汇编方法体为 IL 文本，经共享管道缓存与 lines 分页。
    /// </summary>
    /// <param name="assembly">要反汇编的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="token">方法定义元数据 token，0x 开头的十六进制，如 0x06000005（必填）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"（1-based 含两端，单次最多约 32 KB）；缺省返回前约 8 KB。</param>
    /// <param name="timeoutSeconds">本次反汇编超时秒数（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的 IL 文本或错误提示文本。</returns>
    [McpServerTool]
    [Description("按方法元数据 token（0x06 开头，取 signature 行尾或 #MEMBER 分隔行的 token）反汇编方法体为 IL 文本。IL 逐指令还原方法体真相，是 async 状态机等编译器生成类型反编译为 C# 失败/难读时的兜底（反编译见 decompile_member，IL 偏移/调用目标可直接用于调试断点分析）。仅接受方法 token：字段/属性/事件 token 返回提示。输出行号体系独立于 C# 反编译视图（不与 decompile 行号对齐）。" + ToolParameterText.FooterPagination)]
    public static async Task<string> DecompileIl(
        [Description(ToolParameterText.AssemblyParam)] string assembly = "",
        [Description("方法定义元数据 token，0x 开头的十六进制，如 0x06000005（必填；取 signature 行尾或 #MEMBER 分隔行的 token）")] string token = "",
        [Description(ToolParameterText.LinesParam)] string lines = "",
        [Description(ToolParameterText.TimeoutParam)] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（进程内反汇编，无安装前置）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return assemblyError;
        // 参数校验：token 必填且为 0x 开头十六进制（Kind/越界校验由反汇编引擎兜底）
        if (!ArgumentValidators.ValidateToken(token, out var tokenError)) return tokenError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;

        var command = new ToolCommand(assemblyFull, new DecompileRequest(DecompileKind.Il, token));

        // 头部信息块：程序集绝对路径 + 目标描述（不展示参数——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"方法 {token} 的 IL 反汇编");

        // 走共享执行管道：缓存命中 → 进程内反汇编回源 → lines 分页；超时/取消返回提示文本
        return await ToolExecutor.RunPipelineAsync(command, lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, context);
    }
}
