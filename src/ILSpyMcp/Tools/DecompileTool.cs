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
    /// <param name="lines">按行号范围读取反编译结果，格式 "start-end"（1-based 含两端，单次最多 500 行）；缺省返回前 200 行。</param>
    /// <param name="languageVersion">C# 语言版本，如 CSharp12_0、Latest；省略使用 ilspycmd 默认。</param>
    /// <param name="timeoutSeconds">本次反编译回源超时秒数（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的反编译结果或错误提示文本。</returns>
    [McpServerTool]
    [Description("反编译 .NET 程序集（dll/exe）中指定的单个类型到标准输出。输出每行带行号标注，可直接引用具体行。结果默认只返回前 200 行，超过时可用 lines 参数按行号范围拉取后续（结果缓存在内存）。全量/项目反编译请使用 decompile_to_dir 工具，按成员名搜索请使用 decompile_member 工具。")]
    public static async Task<string> Decompile(
        [Description("要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("要反编译的全限定类型名，例如 System.String（必填）")] string typeName = "",
        [Description("按行号范围读取反编译结果，格式 \"start-end\"（1-based 含两端，单次最多 500 行），例如 \"200-400\"；缺省返回前 200 行")] string lines = "",
        [Description("C# 语言版本，如 CSharp12_0、Latest；省略使用 ilspycmd 默认")] string languageVersion = "",
        [Description("本次反编译回源超时秒数，默认 30；大程序集可调大")] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 前置检查：ilspycmd 已安装且 assembly 参数有效；未通过直接返回提示
        if (await ToolPreflight.CheckAsync(assembly) is { } preflightError) return preflightError;
        // 参数校验：typeName 必填（成员级反编译由 decompile_member 承接）
        if (!ArgumentValidators.ValidateRequired(typeName, "请指定 typeName 参数；全量反编译请使用 decompile_to_dir 工具并指定 outputDir，按成员名搜索请使用 decompile_member。", out var argError)) return argError;
        // 参数校验：languageVersion 可选但需合法
        if (!ArgumentValidators.ValidateLanguageVersion(languageVersion, out var lvError)) return lvError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        // 由参数结构统一派生命令行与缓存签名，杜绝命令/签名两处手写导致缓存 key 错配
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;
        var command = new ToolCommand(ToolCommand.DefaultExecutable, assemblyFull,
            ToolParameter.Optional("-t", typeName),
            ToolParameter.Optional("-lv", languageVersion));

        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数，ilspycmd 命令行参数对 agent 无意义）
        var context = new FormatContext(assemblyFull, $"类型 {typeName}");

        // 走共享执行管道：缓存命中 → 回源 → lines 分页；stdout 超限时 ProcessRunner 直接返回错误提示
        return await ToolExecutor.RunPipelineAsync(command, lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, context);
    }
}
