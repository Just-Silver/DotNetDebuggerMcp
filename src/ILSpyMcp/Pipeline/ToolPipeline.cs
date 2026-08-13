using ILSpyMcp.Caching;
using ILSpyMcp.Configuration;
using ILSpyMcp.Decompiler;
using ILSpyMcp.Formatting;

using System.Collections.Concurrent;

namespace ILSpyMcp.Pipeline;

/// <summary>
/// 管道执行结果：反编译/格式化文本或错误提示。
/// </summary>
/// <param name="Text">格式化后的反编译文本或错误提示。</param>
public readonly record struct ToolPipelineResult(string Text);

/// <summary>
/// 反编译请求类型：决定进程内反编译入口。
/// </summary>
public enum DecompileKind
{
    /// <summary>反编译指定类型：Target 为类型全名（如 System.String）。</summary>
    Type,

    /// <summary>反编译指定成员：Target 为元数据 token（如 0x06000005）。</summary>
    Member,

    /// <summary>反编译整个程序集：Target 忽略。</summary>
    WholeModule,
}

/// <summary>
/// 一次进程内反编译请求：Kind 决定反编译入口，Target 为类型全名或成员 token。 同一成员不同子串查询得到的 token 相同
/// → 请求相同 → 缓存签名相同 → 共享缓存（原 decompile_member 语义保留）。
/// </summary>
public sealed record DecompileRequest(DecompileKind Kind, string Target);

/// <summary>
/// 一次反编译调用描述：程序集路径 + 反编译请求；缓存签名由 Kind+Target 统一派生（以 \u001F 拼接），
/// 杜绝调用方手写签名导致缓存 key 错配。DisplayName/MemberName/MemberToken 仅影响合并展示，不参与缓存签名。
/// </summary>
public sealed class ToolCommand
{
    /// <summary>
    /// 构造一次反编译调用：程序集路径 + 反编译请求（Kind 决定入口，Target 为类型全名或成员 token）。
    /// </summary>
    /// <param name="assembly">程序集路径（绝对路径）。</param>
    /// <param name="request">反编译请求描述。</param>
    public ToolCommand(string assembly, DecompileRequest request)
    {
        Assembly = assembly;
        Request = request;
        Signature = BuildSignature(request);
    }

    /// <summary>
    /// 程序集路径（缓存 key 的唯一数据源，与反编译目标分离）。
    /// </summary>
    public string Assembly { get; }

    /// <summary>
    /// 反编译请求描述（Kind + Target）。
    /// </summary>
    public DecompileRequest Request { get; }

    /// <summary>
    /// 缓存签名：由 Kind+Target 派生，参与缓存 key 计算；同一成员不同子串查询 token 相同 → 签名相同 → 共享缓存。
    /// </summary>
    public string Signature { get; }

    /// <summary>
    /// 可选展示名：ExecuteMergedAsync 合并输出时在每条命令结果前插入 `=== {DisplayName} ===` 分隔行（供 agent 分辨成员体归属）。
    /// 为空/null 时合并行为不变（不插分隔行）；仅影响合并展示，不参与缓存签名。
    /// </summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// 可选成员名：与 <see cref="MemberToken"/> 同时提供时，合并输出前插入 `#MEMBER {"name","token"}` JSON 分隔行
    /// （agent 免解析分隔线直接取 token）；仅影响合并展示，不参与缓存签名。优先级高于 <see cref="DisplayName"/>。
    /// </summary>
    public string? MemberName { get; set; }

    /// <summary>
    /// 可选成员 token（如 0x060004b2）：与 <see cref="MemberName"/> 同时提供时输出 JSON 分隔行；仅影响合并展示，不参与缓存签名。
    /// </summary>
    public string? MemberToken { get; set; }

    /// <summary>
    /// 由 Kind+Target 派生缓存签名：类型/成员/整模块前缀 + \u001F + 目标（整模块目标为空，仅前缀）。
    /// </summary>
    /// <param name="request">反编译请求。</param>
    /// <returns>缓存签名文本。</returns>
    private static string BuildSignature(DecompileRequest request)
    {
        var prefix = request.Kind switch
        {
            DecompileKind.Type => "type",
            DecompileKind.Member => "member",
            DecompileKind.WholeModule => "whole-module",
            _ => throw new ArgumentOutOfRangeException(nameof(request), $"未知反编译请求类型 {request.Kind}"),
        };
        return string.IsNullOrEmpty(request.Target) ? prefix : $"{prefix}\u001F{request.Target}";
    }
}

/// <summary>
/// 共享执行管道：缓存命中 → 进程内反编译回源（同 key 并发单飞）→ lines 分页/截断格式化。 一切错误均返回提示文本，不抛异常。
/// </summary>
public sealed class ToolPipeline
{
    private readonly DecompileCache _cache;
    private readonly Func<ToolCommand, CancellationToken, string> _decompile;
    private readonly ConcurrentDictionary<CacheKey, Lazy<Task<List<string>>>> _inflight = new();

    /// <summary>
    /// 以共享缓存与反编译探针构造执行管道。
    /// </summary>
    /// <param name="cache">共享反编译缓存。</param>
    /// <param name="decompile">反编译探针：接收调用描述与取消令牌，返回反编译文本或错误提示；缺省经 <see cref="InProcessDecompiler"/> 静态入口按 Kind 分发。</param>
    public ToolPipeline(DecompileCache cache, Func<ToolCommand, CancellationToken, string>? decompile = null)
    {
        _cache = cache;
        _decompile = decompile ?? DecompileStatic;
    }

    /// <summary>
    /// 执行一次反编译调用：缓存命中直接返回；未命中则并发单飞回源（Lazy 保证同 key 只启动一次进程内反编译）后写缓存。 指定 lines
    /// 时按行号切片，否则返回前约 8 KB；一切错误返回提示文本，不抛异常。
    /// </summary>
    /// <param name="command">调用描述（程序集路径 + 反编译请求）。</param>
    /// <param name="lines">lines 分页参数，格式 "start-end"；空字符串返回前约 8 KB。</param>
    /// <param name="timeout">本次回源超时；为 null 时用全局默认超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="context">头部信息块上下文；提供时结果前置程序集/目标说明。</param>
    /// <returns>管道执行结果：格式化后的反编译文本或错误提示。</returns>
    public async Task<ToolPipelineResult> ExecuteAsync(ToolCommand command, string lines, TimeSpan? timeout = null, CancellationToken cancellationToken = default, FormatContext? context = null)
    {
        List<string> source;
        bool fromCache;
        try
        {
            (source, fromCache) = await GetSourceLinesAsync(command, timeout ?? AppConfig.DefaultTimeout, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ToolPipelineResult($"反编译失败：{ex.Message}");
        }
        var fmtContext = fromCache && context is not null ? context with { IsCached = true } : context;
        return new ToolPipelineResult(OutputFormatter.Format(source, lines, fmtContext));
    }

    /// <summary>
    /// 合并执行多条命令（decompile_member 多匹配场景）：各自走缓存/回源拿全量纯净行，按命令顺序合并为一个大行列表， 再统一做行号标注与
    /// lines 分页/截断（总行数/当前输出均基于合并结果）。任一命令失败即返回错误提示，不抛异常。
    /// </summary>
    /// <param name="commands">多条调用描述（每个匹配成员一条，各自独立缓存）。</param>
    /// <param name="lines">lines 分页参数，格式 "start-end"；空字符串返回前约 8 KB。</param>
    /// <param name="timeout">本次回源超时；为 null 时用全局默认超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="context">头部信息块上下文；提供时结果前置程序集/目标说明。</param>
    /// <returns>管道执行结果：合并后格式化文本或错误提示。</returns>
    public async Task<ToolPipelineResult> ExecuteMergedAsync(IReadOnlyList<ToolCommand> commands, string lines, TimeSpan? timeout = null, CancellationToken cancellationToken = default, FormatContext? context = null)
    {
        var merged = new List<string>();
        var allCached = commands.Count > 0;
        foreach (var command in commands)
        {
            List<string> source;
            bool fromCache;
            try
            {
                (source, fromCache) = await GetSourceLinesAsync(command, timeout ?? AppConfig.DefaultTimeout, cancellationToken);
            }
            catch (Exception ex)
            {
                return new ToolPipelineResult($"反编译失败：{ex.Message}");
            }
            allCached &= fromCache;
            if (!string.IsNullOrEmpty(command.MemberName) && !string.IsNullOrEmpty(command.MemberToken))
                merged.Add($"#MEMBER {OutputFormatter.MemberJson(command.MemberName, command.MemberToken)}");
            else if (!string.IsNullOrEmpty(command.DisplayName)) merged.Add($"=== {command.DisplayName} ===");
            merged.AddRange(source);
        }
        var fmtContext = allCached && context is not null ? context with { IsCached = true } : context;
        return new ToolPipelineResult(OutputFormatter.Format(merged, lines, fmtContext));
    }

    /// <summary>
    /// 取指定调用的全量纯净行列表：缓存命中直接返回；未命中则并发单飞回源后写缓存。错误向上抛出，由调用方转为提示文本。
    /// </summary>
    /// <param name="command">调用描述（程序集路径 + 反编译请求）。</param>
    /// <param name="timeout">本次回源超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反编译结果纯净行列表（不含头部与行号，供渲染期统一格式化）与是否缓存命中的标志。</returns>
    private async Task<(List<string> Lines, bool FromCache)> GetSourceLinesAsync(ToolCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        CacheKey key;
        try
        {
            key = _cache.BuildKey(command.Assembly, command.Signature);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }
        var cached = _cache.Get(key);
        if (cached is not null) return (cached, true);

        // 并发单飞：同 key 只启动一次进程内反编译，其余等待者复用同一 Lazy 承载的 Task
        var lazy = _inflight.GetOrAdd(key,
            _ => new Lazy<Task<List<string>>>(() => RunSourceAsync(command, timeout, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            cached = await lazy.Value;
            // 能走到这里说明回源未抛异常（超时/取消/错误提示等已在 RunSourceAsync 转为异常），结果必为正常反编译文本，直接写缓存
            _cache.Put(key, cached);
            return (cached, false);
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<CacheKey, Lazy<Task<List<string>>>>(key, lazy));
        }
    }

    /// <summary>
    /// 回源反编译：经 <see cref="InProcessDecompiler.RunWithTimeoutAsync"/> 在后台线程执行反编译探针并拆分行列表；
    /// 超时/取消提示与探针返回的错误提示（未找到类型/超限/非法 token 等）均识别后抛异常（走「错误转提示」路径且不入缓存，同 key 后续调用仍可重试）。
    /// </summary>
    /// <param name="command">调用描述（程序集路径 + 反编译请求）。</param>
    /// <param name="timeout">本次回源超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反编译结果行列表。</returns>
    private async Task<List<string>> RunSourceAsync(ToolCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var timeoutHint = $"反编译超时（超过 {timeout.TotalSeconds:0.#} 秒），已放弃本次反编译；可调大 timeoutSeconds 或改用 decompile_to_dir";
        // 探针返回反编译文本或错误提示（未找到类型/超限/非法 token 等）；超时/取消语义由 RunWithTimeoutAsync 处理，不在此处展开。
        // 取消令牌随调用链透传给探针：RunWithTimeoutAsync 将同一令牌注入 work，探针再转发给引擎实现协作式中断
        var text = await InProcessDecompiler.RunWithTimeoutAsync(ct => _decompile(command, ct), timeout, cancellationToken, timeoutHint);
        // 超时/取消时 RunWithTimeoutAsync 原样返回 timeoutHint：识别并抛异常走错误提示路径，避免把超时提示误当反编译结果写入缓存
        if (text == timeoutHint) throw new InvalidOperationException(timeoutHint);
        // 探针返回的错误提示（未找到类型/输出超限/非法或越界 token 等）同样不入缓存：抛异常由调用方转为提示文本，同 key 后续调用可重试
        if (InProcessDecompiler.IsErrorResult(text)) throw new InvalidOperationException(text);
        return OutputFormatter.SplitLines(text);
    }

    /// <summary>
    /// 默认反编译探针：按 <see cref="DecompileKind"/> 分发到 <see cref="InProcessDecompiler"/> 静态入口（类型/成员/整模块），
    /// 并将取消令牌透传给引擎实现协作式中断。
    /// 生产路径恒走本实现；测试可注入替代探针（计数回源次数/制造失败）验证并发单飞与错误分支。
    /// </summary>
    /// <param name="command">调用描述（程序集路径 + 反编译请求）。</param>
    /// <param name="cancellationToken">取消令牌，透传给反编译引擎。</param>
    /// <returns>反编译文本或错误提示。</returns>
    private static string DecompileStatic(ToolCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        return request.Kind switch
        {
            DecompileKind.Type => InProcessDecompiler.DecompileType(command.Assembly, request.Target, cancellationToken),
            DecompileKind.Member => InProcessDecompiler.DecompileMember(command.Assembly, request.Target, cancellationToken),
            DecompileKind.WholeModule => InProcessDecompiler.DecompileWholeModule(command.Assembly, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(request), $"未知反编译请求类型 {request.Kind}"),
        };
    }
}
