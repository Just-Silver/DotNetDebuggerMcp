using ILSpyMcp.Caching;
using ILSpyMcp.Configuration;
using ILSpyMcp.Formatting;
using ILSpyMcp.Processes;

using System.Collections.Concurrent;

namespace ILSpyMcp.Pipeline;

/// <summary>
/// 管道执行结果：反编译/格式化文本或错误提示。
/// </summary>
/// <param name="Text">格式化后的反编译文本或错误提示。</param>
public readonly record struct ToolPipelineResult(string Text);

/// <summary>
/// 一个 ilspycmd 参数：Flag 为开关名（如 "-t"），Value 为空表示无值开关（如 "-p"）。 工具通过本结构声明参数，命令行与缓存签名由 <see
/// cref="ToolCommand"/> 统一派生，杜绝两处手写导致缓存 key 错配。
/// </summary>
public sealed record ToolParameter(string Flag, string? Value)
{
    /// <summary>
    /// 可选带值参数：value 为空字符串或 null 时不启用（返回 null）。
    /// </summary>
    /// <param name="flag">开关名，如 "-t"。</param>
    /// <param name="value">参数值，如类型名；为空时不启用。</param>
    /// <returns>启用的参数；value 为空时为 null。</returns>
    public static ToolParameter? Optional(string flag, string? value)
        => string.IsNullOrEmpty(value) ? null : new ToolParameter(flag, value);

    /// <summary>
    /// 无值开关：enabled 为 true 时启用。
    /// </summary>
    /// <param name="flag">开关名，如 "-p"。</param>
    /// <param name="enabled">是否启用该开关。</param>
    /// <returns>启用的参数；enabled 为 false 时为 null。</returns>
    public static ToolParameter? Switch(string flag, bool enabled)
        => enabled ? new ToolParameter(flag, null) : null;
}

/// <summary>
/// 一次 ilspycmd 调用：由参数结构统一派生命令行与缓存签名，签名自动以 \u001F 拼接，无需调用方手动组装。
/// </summary>
public sealed class ToolCommand
{
    /// <summary>
    /// 默认 ilspycmd 可执行文件名。
    /// </summary>
    public const string DefaultExecutable = "ilspycmd";

    /// <summary>
    /// 构造一次调用：由可执行名、程序集路径与启用的参数生成纯参数列表与缓存签名。 调用方至少应提供一个启用的参数，否则签名为空（同程序集不同参数将共享缓存）。
    /// </summary>
    /// <param name="executable">可执行文件名，通常传 <see cref="DefaultExecutable"/>。</param>
    /// <param name="assembly">程序集路径（绝对路径）。</param>
    /// <param name="parameters">ilspycmd 参数；null 表示不启用。</param>
    public ToolCommand(string executable, string assembly, params ToolParameter?[] parameters)
    {
        Executable = executable;
        Assembly = assembly;
        var args = new List<string>();
        var sig = new List<string>();
        foreach (var p in parameters)
        {
            if (p is null) continue;
            args.Add(p.Flag);
            sig.Add(p.Flag);
            if (p.Value is not null)
            {
                args.Add(p.Value);
                sig.Add(p.Value);
            }
        }
        args.Add(assembly);
        Args = args;
        Signature = string.Join('\u001F', sig);
    }

    /// <summary>
    /// 程序集路径（缓存 key 与命令行共用同一份数据，杜绝「管道实参」与「命令内路径」双份错配）。
    /// </summary>
    public string Assembly { get; }

    /// <summary>
    /// 参数签名，参与缓存 key 计算；由启用的参数（开关名与值）自动派生，保证不同参数组合的 key 互不冲突。
    /// </summary>
    public string Signature { get; }

    /// <summary>
    /// 可执行文件名（与参数列表分离，见 <see cref="Args"/>）。
    /// </summary>
    public string Executable { get; }

    /// <summary>
    /// 传递给可执行文件的纯参数列表（含末尾程序集路径，不含可执行文件名）。
    /// </summary>
    public IReadOnlyList<string> Args { get; }
}

/// <summary>
/// 共享执行管道：缓存命中 → 回源反编译（同 key 并发单飞）→ lines 分页/截断格式化。 一切错误均返回提示文本，不抛异常。
/// </summary>
public sealed class ToolPipeline
{
    private readonly IProcessRunner _process;
    private readonly DecompileCache _cache;
    private readonly ConcurrentDictionary<CacheKey, Lazy<Task<List<string>>>> _inflight = new();

    /// <summary>
    /// 以共享执行管道方式构造：传入进程执行器与共享缓存。
    /// </summary>
    /// <param name="process">子进程执行器。</param>
    /// <param name="cache">共享反编译缓存。</param>
    public ToolPipeline(IProcessRunner process, DecompileCache cache)
    {
        _process = process;
        _cache = cache;
    }

    /// <summary>
    /// 执行一次 ilspycmd 调用：缓存命中直接返回；未命中则并发单飞回源（Lazy 保证同 key 只启动一个子进程）后写缓存。 指定 lines 时按行号切片，否则返回前 200 行；一切错误返回提示文本，不抛异常。
    /// </summary>
    /// <param name="command">调用描述（程序集路径 + 参数签名 + 命令行参数）。</param>
    /// <param name="lines">lines 分页参数，格式 "start-end"；空字符串返回前 200 行。</param>
    /// <param name="timeout">本次回源超时；为 null 时用全局默认超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="context">头部信息块上下文；提供时结果前置程序集/目标说明。</param>
    /// <returns>管道执行结果：格式化后的反编译文本或错误提示。</returns>
    public async Task<ToolPipelineResult> ExecuteAsync(ToolCommand command, string lines, TimeSpan? timeout = null, CancellationToken cancellationToken = default, FormatContext? context = null)
    {
        List<string> source;
        try
        {
            source = await GetSourceLinesAsync(command, timeout ?? AppConfig.DefaultTimeout, cancellationToken);
        }
        catch (Exception ex)
        {
            return new ToolPipelineResult($"反编译失败：{ex.Message}");
        }
        return new ToolPipelineResult(OutputFormatter.Format(source, lines, context));
    }

    /// <summary>
    /// 合并执行多条命令（decompile_member 多匹配场景）：各自走缓存/回源拿全量纯净行，按命令顺序合并为一个大行列表，
    /// 再统一做行号标注与 lines 分页/截断（总行数/当前输出均基于合并结果）。任一命令失败即返回错误提示，不抛异常。
    /// </summary>
    /// <param name="commands">多条调用描述（每个匹配成员一条，各自独立缓存）。</param>
    /// <param name="lines">lines 分页参数，格式 "start-end"；空字符串返回前 200 行。</param>
    /// <param name="timeout">本次回源超时；为 null 时用全局默认超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="context">头部信息块上下文；提供时结果前置程序集/目标说明。</param>
    /// <returns>管道执行结果：合并后格式化文本或错误提示。</returns>
    public async Task<ToolPipelineResult> ExecuteMergedAsync(IReadOnlyList<ToolCommand> commands, string lines, TimeSpan? timeout = null, CancellationToken cancellationToken = default, FormatContext? context = null)
    {
        var merged = new List<string>();
        foreach (var command in commands)
        {
            List<string> source;
            try
            {
                source = await GetSourceLinesAsync(command, timeout ?? AppConfig.DefaultTimeout, cancellationToken);
            }
            catch (Exception ex)
            {
                return new ToolPipelineResult($"反编译失败：{ex.Message}");
            }
            merged.AddRange(source);
        }
        return new ToolPipelineResult(OutputFormatter.Format(merged, lines, context));
    }

    /// <summary>
    /// 取指定调用的全量纯净行列表：缓存命中直接返回；未命中则并发单飞回源后写缓存。错误向上抛出，由调用方转为提示文本。
    /// </summary>
    /// <param name="command">调用描述（程序集路径 + 参数签名 + 命令行参数）。</param>
    /// <param name="timeout">本次回源超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反编译结果纯净行列表（不含头部与行号，供渲染期统一格式化）。</returns>
    private async Task<List<string>> GetSourceLinesAsync(ToolCommand command, TimeSpan timeout, CancellationToken cancellationToken)
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
        if (cached is not null) return cached;

        // 并发单飞：同 key 只启动一个子进程，其余等待者复用同一 Lazy 承载的 Task
        var lazy = _inflight.GetOrAdd(key,
            _ => new Lazy<Task<List<string>>>(() => RunSourceAsync(command, timeout, cancellationToken), LazyThreadSafetyMode.ExecutionAndPublication));
        try
        {
            cached = await lazy.Value;
            // 超限（超过 AppConfig.MaxOutputBytes）时 ProcessRunner 返回 Code=-1，RunSourceAsync 抛异常，
            // 由调用方 catch 拦截返回提示；能走到这里说明 await 未抛异常，结果必未超限，直接写入
            _cache.Put(key, cached);
            return cached;
        }
        finally
        {
            _inflight.TryRemove(new KeyValuePair<CacheKey, Lazy<Task<List<string>>>>(key, lazy));
        }
    }

    /// <summary>
    /// 回源反编译：调用子进程并拆分结果；退出码非 0 时抛异常由调用方转为提示文本。
    /// </summary>
    /// <param name="command">调用描述（程序集路径 + 参数签名 + 命令行参数）。</param>
    /// <param name="timeout">本次回源超时。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>反编译结果行列表。</returns>
    private async Task<List<string>> RunSourceAsync(ToolCommand command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await _process.RunAsync(command.Executable, command.Args, Environment.CurrentDirectory, timeout, cancellationToken);
        if (result.Code != 0)
        {
            throw new InvalidOperationException($"ilspycmd 退出码: {result.Code}\n{result.Stderr}");
        }
        return OutputFormatter.SplitLines(result.Stdout);
    }
}