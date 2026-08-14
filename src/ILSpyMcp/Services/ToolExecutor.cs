using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using ILSpyMcp.Formatting;
using ILSpyMcp.Pipeline;

namespace ILSpyMcp.Services;

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
    /// 元数据工具经共享缓存的执行辅助：与反编译工具共用同一个全局缓存（<see cref="AppServices.Cache"/>）。
    /// 缓存命中直接格式化返回（头部标注「缓存: 命中」）；未命中则调用 produce 生成纯行列表后写缓存。
    /// produce 抛 <see cref="InvalidOperationException"/>（未找到类型/空结果提示）或 IO 类异常时返回提示文本、不入缓存
    /// （与反编译路径「错误提示不入缓存」同规则，同 key 可重试）。元数据秒回，不做并发单飞。
    /// </summary>
    /// <param name="assemblyFull">程序集绝对路径。</param>
    /// <param name="signature">缓存签名（工具前缀 + 参数，经 \u001F 拼接，与反编译签名互不冲突）。</param>
    /// <param name="lines">lines 参数原文；空字符串时按默认预算返回。</param>
    /// <param name="context">头部信息块上下文。</param>
    /// <param name="produce">生成纯行列表的委托；错误/未找到以 <see cref="InvalidOperationException"/> 抛提示文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>格式化后的元数据结果或错误提示文本。</returns>
    public static string RunMetadata(string assemblyFull, string signature, string lines, FormatContext context,
        Func<CancellationToken, List<string>> produce, CancellationToken cancellationToken)
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
        return OutputFormatter.Format(result, lines, context);
    }

    /// <summary>
    /// 元数据工具经共享缓存且自开 PE 的执行辅助：在 <see cref="RunMetadata"/> 之上打开程序集文件与
    /// <see cref="PEReader"/>，produce 直接拿 <see cref="MetadataReader"/> 读元数据，缓存与异常处理复用上层。
    /// </summary>
    /// <param name="assemblyFull">程序集绝对路径。</param>
    /// <param name="signature">缓存签名（工具前缀 + 参数，经 \u001F 拼接，与反编译签名互不冲突）。</param>
    /// <param name="lines">lines 参数原文；空字符串时按默认预算返回。</param>
    /// <param name="context">头部信息块上下文。</param>
    /// <param name="produce">生成纯行列表的委托，入参为已打开的 <see cref="PEReader"/> 与其 <see cref="MetadataReader"/>；错误/未找到以 <see cref="InvalidOperationException"/> 抛提示文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>格式化后的元数据结果或错误提示文本。</returns>
    public static string RunMetadataPe(string assemblyFull, string signature, string lines, FormatContext context,
        Func<PEReader, MetadataReader, List<string>> produce, CancellationToken cancellationToken)
        => RunMetadata(assemblyFull, signature, lines, context, _ =>
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            return produce(pe, pe.GetMetadataReader());
        }, cancellationToken);
}