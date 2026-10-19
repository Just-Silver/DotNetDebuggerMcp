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
/// 供 agent 定位真实控制流后回到 debug 断点闭环（token 直接可用于 debug_breakpoint(action=set)）。
/// </summary>
[McpServerToolType]
public static class DecompileIlTool
{
    /// <summary>
    /// 按方法 token 反汇编方法体为 IL 文本，经共享管道缓存与 lines 分页。
    /// </summary>
    /// <param name="assembly">要反汇编的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="token">方法定义元数据 token，0x 开头的十六进制，如 0x06000005（与 memberName 二选一）。</param>
    /// <param name="memberName">成员名子串（忽略大小写）；未提供 token 时按成员名定位唯一方法。</param>
    /// <param name="typeName">限定在指定类型内按成员名定位（默认空=跨程序集）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"（1-based 含两端，单次最多约 32 KB）；缺省返回前约 8 KB。</param>
    /// <param name="timeoutSeconds">本次反汇编超时秒数（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的 IL 文本或错误提示文本。</returns>
    [McpServerTool]
    [Description("反汇编方法体为 IL 文本（async 状态机等编译器生成类型反编译为 C# 失败/难读时的兜底）。定位二选一：token（0x06 开头，取 signature 行尾或 #MEMBER 分隔行）或 memberName（方法名子串，可配 typeName 限定，多匹配会列候选并提示改用 token）。IL 逐指令还原方法体真相，IL 偏移/调用目标可直接用于调试断点分析。仅接受方法：字段/属性/事件 token 返回提示。输出行号体系独立于 C# 反编译视图（不与 decompile 行号对齐）。")]
    public static async Task<string> DecompileIl(
        [Description(ToolParameterText.AssemblyParam)] string assembly = "",
        [Description("方法定义元数据 token，0x 开头的十六进制，如 0x06000005（取 signature 行尾或 #MEMBER 分隔行的 token；与 memberName 二选一）")] string token = "",
        [Description("成员名子串（忽略大小写；未提供 token 时按成员名定位唯一方法，与 decompile_member 一致）")] string memberName = "",
        [Description("限定在指定类型内按成员名定位，类型全名（格式与 list_types 输出一致；默认空=跨程序集）")] string typeName = "",
        [Description(ToolParameterText.LinesParam)] string lines = "",
        [Description(ToolParameterText.TimeoutParam)] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（进程内反汇编，无安装前置）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return assemblyError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;

        // token 未提供时按成员名定位唯一方法（与 decompile_member 同款解析，只收 0x06 方法）
        if (string.IsNullOrWhiteSpace(token))
        {
            var (resolvedToken, resolveError) = ResolveTokenByMemberName(assemblyFull, typeName, memberName);
            if (resolveError is not null) return resolveError;
            token = resolvedToken;
        }

        // 参数校验：token 为 0x 开头十六进制（Kind/越界校验由反汇编引擎兜底）
        if (!ArgumentValidators.ValidateToken(token, out var tokenError)) return tokenError;

        var command = new ToolCommand(assemblyFull, new DecompileRequest(DecompileKind.Il, token));

        // 头部信息块：程序集绝对路径 + 目标描述（不展示参数——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"方法 {token} 的 IL 反汇编");

        // 走共享执行管道：缓存命中 → 进程内反汇编回源 → lines 分页；超时/取消返回提示文本
        return await ToolExecutor.RunPipelineAsync(command, lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, context);
    }

    /// <summary>
    /// 按成员名子串定位唯一方法 token：复用 decompile_member 的类型/成员解析，只接受 0x06 方法；
    /// 多匹配时列候选并提示改用 token（不替 agent 猜一个）。
    /// </summary>
    private static (string Token, string? Error) ResolveTokenByMemberName(string assemblyFull, string typeName, string memberName)
    {
        if (string.IsNullOrWhiteSpace(memberName))
            return ("", "请指定 token 或 memberName：token 取 signature 行尾的 0x06 token；memberName 按方法名子串定位（可配 typeName 限定）。");

        var (matches, similarNames, effectiveTypeName, locateError) =
            DecompileMemberTool.LocateMembers(assemblyFull, typeName, "", memberName);
        if (locateError is not null) return ("", locateError);

        var methods = matches
            .Where(m => m.Token.StartsWith("0x06", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (methods.Count == 0)
        {
            var scope = effectiveTypeName is null ? "程序集中" : $"类型 {effectiveTypeName} 中";
            var message = $"{scope}未找到名称包含 \"{memberName}\" 的方法";
            if (similarNames.Count > 0) message += $"。相近成员：{string.Join("、", similarNames)}";
            return ("", message);
        }
        if (methods.Count > 1)
        {
            var candidates = methods.Select(m => $"{m.TypeName}::{m.Name}  {m.Token}");
            return ("", $"成员名 \"{memberName}\" 匹配 {methods.Count} 个方法，请改用 token 精确定位：\n" +
                        string.Join("\n", candidates));
        }

        return (methods[0].Token, null);
    }
}
