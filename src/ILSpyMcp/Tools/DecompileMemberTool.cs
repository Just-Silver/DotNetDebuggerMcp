using ILSpyMcp.Infrastructure;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace ILSpyMcp.Tools;

/// <summary>
/// 按成员名在指定类型内搜索并反编译匹配的成员：适合只知道方法名、给不出完整文档 ID 的场景。
/// 匹配的多个成员全部反编译并合并输出（行号连续），同样默认返回前 200 行、可用 lines 分页。
/// </summary>
[McpServerToolType]
public static class DecompileMemberTool
{
    /// <summary>
    /// 按成员名子串在指定类型内搜索并反编译匹配的成员，经共享管道缓存与 lines 分页。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">在指定类型内搜索成员，全限定类型名（必填）。</param>
    /// <param name="memberName">成员名子串，忽略大小写；匹配到的成员全部反编译（必填）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"（1-based 含两端，单次最多 500 行）；缺省返回前 200 行。</param>
    /// <param name="languageVersion">C# 语言版本，如 CSharp12_0、Latest；省略使用 ilspycmd 默认。</param>
    /// <param name="timeoutSeconds">本次反编译回源超时秒数（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>匹配成员反编译合并结果（带行号）或错误提示文本。</returns>
    [McpServerTool]
    [Description("按成员名在指定类型内搜索并反编译匹配的成员（适合只知道方法名、不知道完整文档 ID 的场景）。匹配到多个成员时全部反编译并合并输出，行号连续；结果默认只返回前 200 行，可用 lines 参数分页。")]
    public static async Task<string> DecompileMember(
        [Description("要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("在指定类型内搜索成员，全限定类型名，例如 System.Text.Json.JsonSerializer（必填）")] string typeName = "",
        [Description("成员名子串（忽略大小写），例如 SerializeAsync；匹配到的成员会全部反编译（必填）")] string memberName = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多 500 行），例如 \"200-400\"；缺省返回前 200 行")] string lines = "",
        [Description("C# 语言版本，如 CSharp12_0、Latest；省略使用 ilspycmd 默认")] string languageVersion = "",
        [Description("本次反编译回源超时秒数，默认 30；大程序集可调大")] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 前置检查：ilspycmd 已安装且 assembly 参数有效；未通过直接返回提示
        if (await ToolPreflight.CheckAsync(assembly) is { } preflightError) return preflightError;
        // 参数校验：typeName 与 memberName 均必填
        if (!ArgumentValidators.ValidateMemberNameSearch(typeName, memberName, out var argError)) return argError;
        // 参数校验：languageVersion 可选但需合法
        if (!ArgumentValidators.ValidateLanguageVersion(languageVersion, out var lvError)) return lvError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;

        // 纯元数据读取定位类型并枚举方法，按名字子串匹配；未命中类型/无匹配成员时直接返回提示
        var (typeFound, matches) = MemberResolver.FindMembers(assemblyFull, typeName, memberName);
        if (!typeFound) return $"未找到类型 {typeName}";
        if (matches.Count == 0) return $"类型 {typeName} 中未找到名称包含 \"{memberName}\" 的成员";

        // 每个匹配成员一条命令：token 全局唯一，ilspycmd 的 -t 与 -m 互斥，故仅传 -m <token>；
        // 各命令独立缓存 key，同一成员不同子串查询共享缓存
        var commands = matches
            .Select(m => new ToolCommand(ToolCommand.DefaultExecutable, assemblyFull,
                new ToolParameter("-m", m.Token),
                ToolParameter.Optional("-lv", languageVersion)))
            .ToArray();

        // 头部信息块：程序集绝对路径 + 目标描述（含匹配数）。不展示参数——对外工具没有 -m/token 概念，
        // 暴露内部 token 或 ilspycmd 参数会误导 agent（agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"类型 {typeName} 的成员 {memberName}（{matches.Count} 个匹配）");

        // 走共享执行管道：各成员缓存/回源后合并，统一行号与 lines 分页
        return await ToolExecutor.RunMergedAsync(commands, lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, context);
    }
}
