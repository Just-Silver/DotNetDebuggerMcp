using ILSpyMcp.Configuration;
using ILSpyMcp.Formatting;
using ILSpyMcp.Pipeline;
using ILSpyMcp.Services;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace ILSpyMcp.Tools;

/// <summary>
/// 反编译 .NET 程序集（dll/exe）中指定的单个类型到标准输出。
/// </summary>
[McpServerToolType]
public static class DecompileTool
{
    /// <summary>
    /// 反编译指定类型到标准输出，经共享管道缓存与 lines 分页。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">要反编译的全限定类型名，例如 System.String（必填）。</param>
    /// <param name="lines">按行号范围读取反编译结果，格式 "start-end"（1-based 含两端，单次最多约 32 KB）；缺省返回前约 8 KB。</param>
    /// <param name="timeoutSeconds">本次反编译回源超时秒数（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的反编译结果或错误提示文本。</returns>
    [McpServerTool]
    [Description("反编译指定类型的源码到标准输出（类型级，含全部成员）。输出每行带行号，可直接引用具体行。单成员反编译用 decompile_member；需要完整源码写盘用 decompile_to_dir。未找到类型时返回相近类型名提示。" + ToolParameterText.FooterPagination)]
    public static async Task<string> Decompile(
        [Description(ToolParameterText.AssemblyParam)] string assembly = "",
        [Description("要反编译的类型全名（必填），格式与 list_types 输出一致（命名空间.类型，嵌套类型用 + 或 . 分隔，泛型带 arity 如 GenericBox`1）")] string typeName = "",
        [Description(ToolParameterText.LinesParam)] string lines = "",
        [Description(ToolParameterText.TimeoutParam)] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（进程内反编译，无安装前置）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return assemblyError;
        // 参数校验：typeName 必填（成员级反编译由 decompile_member 承接）
        if (!ArgumentValidators.ValidateRequired(typeName, "请指定 typeName 参数；全量反编译请使用 decompile_to_dir 工具并指定 outputDir，按成员名搜索请使用 decompile_member。", out var argError)) return argError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        // 由程序集路径 + 反编译请求统一派生缓存签名，杜绝签名两处手写导致缓存 key 错配
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;
        var command = new ToolCommand(assemblyFull, new DecompileRequest(DecompileKind.Type, typeName));

        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"类型 {typeName}");

        // 走共享执行管道：缓存命中 → 进程内反编译回源 → lines 分页；超时/取消返回提示文本
        return await ToolExecutor.RunPipelineAsync(command, lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, context);
    }
}