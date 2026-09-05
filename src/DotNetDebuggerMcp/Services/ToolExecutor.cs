using DotNetDebugger.Decompiler.Decompiler;
using DotNetDebuggerMcp.Formatting;
using DotNetDebuggerMcp.Pipeline;
using DotNetDebuggerMcp.Validation;

using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Services;

/// <summary>
/// 工具执行共享辅助：统一「程序集路径安全解析」与「执行管道调用」样板，避免各工具重复手写并在细节上漂移。
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
    /// 写盘工具共享执行辅助：统一「参数校验 + 路径解析 + 超时提示 + RunWithTimeoutAsync」样板，供 decompile_to_dir /
    /// decompile_to_project 复用，避免各工具重复手写同一段校验与超时包装并在细节上漂移。 依次校验
    /// assembly/outputDir/timeoutSeconds，失败返回对应中文提示；随后解析程序集绝对路径与输出目录 绝对路径（相对当前工作目录），转调 <see
    /// cref="InProcessDecompiler.RunWithTimeoutAsync"/> 执行 work。
    /// </summary>
    /// <param name="assembly">程序集路径（必填，相对或绝对）。</param>
    /// <param name="outputDir">输出目录（必填，相对或绝对；目录不存在允许，写盘时自动创建）。</param>
    /// <param name="timeoutSeconds">写盘超时秒数，必须为正整数。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="work">实际写盘委托：入参为解析后的程序集绝对路径、输出目录绝对路径与取消令牌。</param>
    /// <returns>写盘结果提示或错误提示文本。</returns>
    public static async Task<string> RunToDisk(string assembly, string outputDir, int timeoutSeconds,
        CancellationToken cancellationToken, Func<string, string, CancellationToken, string> work)
    {
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return assemblyError;
        if (!ArgumentValidators.ValidateOutputDir(outputDir, out var argError)) return argError;
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;
        if (ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;
        var outputFull = Path.GetFullPath(outputDir, Environment.CurrentDirectory);
        var timeoutHint = $"反编译写盘超时（超过 {timeoutSeconds} 秒），已放弃本次写盘；可调大 timeoutSeconds 后重试";
        return await InProcessDecompiler.RunWithTimeoutAsync(
            ct => work(assemblyFull, outputFull, ct),
            TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, timeoutHint);
    }

    /// <summary>
    /// 经共享执行管道反编译/列类型（缓存命中 → 回源 → 行号标注 + lines 分页 + 头部信息块）。
    /// 反编译调用同时写入「agent 当前查看上下文」（AgentViewService），供 Web 监视器联动左侧树/右侧代码。
    /// </summary>
    public static async Task<string> RunPipelineAsync(ToolCommand command, string lines, TimeSpan timeout, CancellationToken cancellationToken, FormatContext context)
    {
        // agent 视图联动：反编译了什么类型/成员 → 写入共享上下文（Web 订阅侧据此展开树/切代码）。
        // Member 时 Target 为成员 token，经 MemberType 带所属类型全名（decompile_member 提供）；无则只记成员 token。
        var typeName = command.Request.Kind switch
        {
            DecompileKind.Type => command.Request.Target,
            DecompileKind.Member => command.MemberType,
            _ => null,
        };
        AgentViewService.Context.Update(command.Assembly, typeName, command.MemberName ?? command.MemberToken);
        return (await AppServices.Pipeline.ExecuteAsync(command, lines, timeout, cancellationToken, context)).Text;
    }

    /// <summary>
    /// 经共享执行管道合并反编译（decompile_member 多匹配，各自缓存后合并、行号连续）。
    /// </summary>
    public static async Task<string> RunMergedAsync(IReadOnlyList<ToolCommand> commands, string lines, TimeSpan timeout, CancellationToken cancellationToken, FormatContext context)
        => (await AppServices.Pipeline.ExecuteMergedAsync(commands, lines, timeout, cancellationToken, context)).Text;

    /// <summary>
    /// 元数据工具经共享缓存的执行辅助：与反编译工具共用同一个全局缓存（ <see cref="AppServices.Cache"/>）。 缓存命中直接格式化返回（头部标注「缓存:
    /// 命中」）；未命中则调用 produce 生成纯行列表后写缓存。 produce 抛 <see
    /// cref="InvalidOperationException"/>（未找到类型/空结果提示）或 IO 类异常时返回提示文本、不入缓存 （与反编译路径「错误提示不入缓存」同规则，同
    /// key 可重试）。元数据秒回，不做并发单飞。
    /// </summary>
    /// <param name="assemblyFull">程序集绝对路径。</param>
    /// <param name="signature">缓存签名（工具前缀 + 参数，经 \u001F 拼接，与反编译签名互不冲突）。</param>
    /// <param name="lines">lines 参数原文；空字符串时按默认预算返回。</param>
    /// <param name="context">头部信息块上下文。</param>
    /// <param name="produce">生成纯行列表的委托；错误/未找到以 <see cref="InvalidOperationException"/> 抛提示文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="degradedProvider">
    /// 降级解析计数委托：新鲜扫描（缓存未命中）成功后在结果入缓存前调用，取本次扫描降级解析的方法体计数 （&gt;0 时经 context.Degraded 注入头部提示）；缓存命中分支不调用，仅新鲜扫描显示。
    /// </param>
    /// <returns>格式化后的元数据结果或错误提示文本。</returns>
    public static string RunMetadata(string assemblyFull, string signature, string lines, FormatContext context,
        Func<CancellationToken, List<string>> produce, CancellationToken cancellationToken, Func<int>? degradedProvider = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = AppServices.Cache.BuildKey(assemblyFull, signature);
        var cached = AppServices.Cache.Get(key);
        if (cached is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return OutputFormatter.Format(cached, lines, context with { IsCached = true });
        }

        List<string> result;
        try
        {
            result = produce(cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return $"无法读取程序集元数据：{ex.Message}";
        }
        AppServices.Cache.Put(key, result);
        if (degradedProvider is not null)
        {
            var degraded = degradedProvider();
            if (degraded > 0) context = context with { Degraded = degraded };
        }
        return OutputFormatter.Format(result, lines, context);
    }

    /// <summary>
    /// 元数据工具经共享缓存且自开 PE 的执行辅助：在 <see cref="RunMetadata"/> 之上打开程序集文件与 <see cref="PEReader"/>，produce
    /// 直接拿 <see cref="MetadataReader"/> 读元数据，缓存与异常处理复用上层。
    /// </summary>
    /// <param name="assemblyFull">程序集绝对路径。</param>
    /// <param name="signature">缓存签名（工具前缀 + 参数，经 \u001F 拼接，与反编译签名互不冲突）。</param>
    /// <param name="lines">lines 参数原文；空字符串时按默认预算返回。</param>
    /// <param name="context">头部信息块上下文。</param>
    /// <param name="produce">
    /// 生成纯行列表的委托，入参为已打开的 <see cref="PEReader"/> 与其 <see cref="MetadataReader"/>；错误/未找到以 <see
    /// cref="InvalidOperationException"/> 抛提示文本。
    /// </param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="degradedProvider">降级解析计数委托，原样透传给 <see cref="RunMetadata"/>（缓存命中分支不调用，仅新鲜扫描显示）。</param>
    /// <returns>格式化后的元数据结果或错误提示文本。</returns>
    public static string RunMetadataPe(string assemblyFull, string signature, string lines, FormatContext context,
        Func<PEReader, MetadataReader, List<string>> produce, CancellationToken cancellationToken, Func<int>? degradedProvider = null)
        => RunMetadata(assemblyFull, signature, lines, context, _ =>
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            return produce(pe, pe.GetMetadataReader());
        }, cancellationToken, degradedProvider);
}