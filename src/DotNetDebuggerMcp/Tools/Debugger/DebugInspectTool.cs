using DotNetDebugger.Engine.Models;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 调试观察工具：进程停时读调用栈/线程列表/局部变量。查询立即返回；进程运行中读栈会得到提示。
/// </summary>
[McpServerToolType]
public static class DebugInspectTool
{
    /// <summary>
    /// 读取调用栈（进程需停在断点/异常/单步）。缺省读最近停点线程；threadId 指定时读该线程。
    /// 每帧输出 类型.方法 [token]，附位置（模块!token+ILoffset，仅当类型/方法名缺失时）。
    /// </summary>
    /// <param name="threadId">线程 id；缺省 0 = 用最近停点线程。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用栈文本或错误提示。</returns>
    [McpServerTool]
    [Description("读取调用栈（进程需停在断点/异常/单步）。每帧输出 类型.方法 [token]（解析失败降级为 模块!token+ILoffset）。缺省读最近停点线程；threadId 指定时读该线程。停在编译器生成的 async 状态机帧时可能读不到栈（返回空栈提示）——此时改用 debug_breakpoint_set typeName+line 断还原源码的 await 行。")]
    public static async Task<string> DebugStack(
        [Description("线程 id；缺省 0 = 用最近停点线程。")] int threadId = 0,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireStopped(out var active, out var error)) return error;

        var tid = threadId > 0 ? threadId : active.Buffer.StoppedThreadId;
        if (tid <= 0) return "无停点线程可读（先 debug_continue 运行至断点停下）。";

        try
        {
            var frames = await active.Session.GetStackFramesAsync(tid, cancellationToken);
            if (frames.Count == 0) return "调用栈为空（可能停在非托管/无 IL 帧处；async 状态机帧亦常见）。";
            var lines = frames.Select(f =>
            {
                var loc = f.Location;
                // 仅当类型名与方法名都解析出才组合真名（单侧成功会拼出 "Ns.Foo." / ".Bar" 残形）
                var name = f.TypeName is not null && f.MethodName is not null
                    ? $"{f.TypeName}.{f.MethodName}"
                    : null;
                // C④：类型名是编译器生成的 async 状态机（Ns.Outer+<Foo>d__N）时，在帧名上标注
                // 原 async 方法 Foo 并归一化方法名（状态机自身的 MoveNext 是编译器生成步骤，非业务方法）。
                if (name is not null && StateMachineFrameHelper.TryParseStateMachine(f.TypeName!) is { } sm)
                    name = $"{f.TypeName} (状态机 {sm.MethodName}).{f.MethodName}";
                var tokenSuffix = $"  [{loc.MethodTokenText}]"; // token 保留，供 debug_breakpoint_set 下断点
                var pos = $"{loc.ModuleName}!{loc.MethodTokenText}+0x{loc.IlOffset:x}";
                return $"  {f.FrameIndex}: {name ?? pos}{tokenSuffix}";
            }).ToList();
            DebugSessionService.Manager.Actions.Log("debug_stack", $"thread={tid}", $"{frames.Count} 帧");
            return $"调用栈（thread={tid}，{frames.Count} 帧）:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        }
        catch (Exception ex)
        {
            return $"读调用栈失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 列出被调试进程的托管线程。返回线程 id 列表。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>线程列表文本。</returns>
    [McpServerTool]
    [Description("列出被调试进程的托管线程（线程 id）。")]
    public static async Task<string> DebugThreads(CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。";

        try
        {
            var threads = await active.Session.GetThreadsAsync(cancellationToken);
            var lines = threads.Select(t => $"  thread {t.ThreadId}").ToList();
            DebugSessionService.Manager.Actions.Log("debug_threads", "", $"{threads.Count} 线程");
            return $"托管线程（{threads.Count}）:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        }
        catch (Exception ex)
        {
            return $"读线程列表失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 读取栈顶帧的局部变量与参数（进程需停）。names 白名单（逗号分隔，空=全量）按展示名精确忽略大小写过滤；
    /// 输出每个局部变量/参数的值（v1 标量），异常停点额外返回 $exception 节（当前异常对象：类型/Message/一级字段）。
    /// </summary>
    /// <param name="threadId">线程 id；缺省 0 = 用最近停点线程。</param>
    /// <param name="names">按名白名单（逗号分隔，空=全量）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>变量文本或错误提示。</returns>
    [McpServerTool]
    [Description("读取栈顶帧的局部变量与参数（进程需停）；异常停点额外返回 $exception 节（当前异常对象：类型/Message/一级字段）。")]
    public static async Task<string> DebugVariables(
        [Description("线程 id；缺省 0 = 用最近停点线程。")] int threadId = 0,
        [Description("按名白名单（逗号分隔，空=全量）。精确忽略大小写匹配 局部/参数名（无符号名用 slotN）与 $exception；最多 50 项；未知名会在返回中列出可用名。")] string names = "",
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireStopped(out var active, out var error)) return error;

        var tid = threadId > 0 ? threadId : active.Buffer.StoppedThreadId;
        if (tid <= 0) return "无停点线程可读（先 debug_continue 运行至断点停下）。";

        var requested = SplitNames(names);
        if (requested is { Count: > MaxNamesWhitelist })
            return $"names 项数 {requested.Count} 超上限 {MaxNamesWhitelist}——请缩小白名单或用空 names 取全量。";

        try
        {
            var vars = await active.Session.GetVariablesAsync(tid, cancellationToken);
            var (lines, hits, redacted) = BuildVariablesLines(vars, requested);
            var detail = requested is not null ? $"thread={tid}，names={requested.Count}" : $"thread={tid}";
            DebugSessionService.Manager.Actions.Log("debug_variables", detail, "ok");
            var header = $"局部变量/参数（thread={tid}";
            if (requested is not null) header += $"，白名单 {requested.Count} 项 → 命中 {hits} 项";
            if (redacted > 0) header += $"，{redacted} 个值{SensitiveValueRedactor.Notice}";
            header += "）";
            return $"{header}:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        }
        catch (Exception ex)
        {
            return $"读变量失败：{ex.Message}";
        }
    }

    /// <summary>DB2 names 白名单项数上限（防 agent 传整帧当白名单，逼其用空 names 取全量）。</summary>
    internal const int MaxNamesWhitelist = 50;

    /// <summary>
    /// 解析 names 白名单：空/空白=null（全量模式）；否则按逗号 split、trim、忽略空项、忽略大小写去重。
    /// </summary>
    internal static List<string>? SplitNames(string names)
    {
        if (string.IsNullOrWhiteSpace(names)) return null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var raw in names.Split(','))
        {
            var t = raw.Trim();
            if (t.Length > 0 && seen.Add(t)) result.Add(t);
        }
        return result;
    }

    /// <summary>按展示名匹配（DB2）：有符号名按名、无符号名按 slot{Slot}，精确忽略大小写。
    /// $exception 伪变量 Name="$exception" 即其展示名，按名命中无需特判。</summary>
    internal static bool MatchName(DotNetDebugger.Engine.Models.DebugVariable v, List<string> requested)
    {
        var display = v.Name ?? $"slot{v.Slot}";
        foreach (var r in requested)
            if (string.Equals(r, display, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// DB2 渲染层：requested null=现状全量渲染；否则按名白名单——每作用域独立匹配展示名（同名跨作用域都返回），
    /// 只渲染命中项（脱敏计数只覆盖可见命中，与 DB1「只计可见」语义一致）；未知名（不在任何作用域顶层展示名中）
    /// 追加零值反馈行（附当前帧可用名清单，不含值）。返回（渲染行、白名单命中数、脱敏值计数）。
    /// </summary>
    internal static (List<string> Lines, int Hits, int Redacted) BuildVariablesLines(
        IReadOnlyDictionary<string, IReadOnlyList<DotNetDebugger.Engine.Models.DebugVariable>> vars,
        List<string>? requested)
    {
        var lines = new List<string>();
        var hits = 0;
        var redacted = 0;

        if (requested is null)
        {
            // 全量模式：每节头 + 全量单次递归渲染（渲染同时产出脱敏计数）
            foreach (var (scope, list) in vars)
            {
                lines.Add($"[{scope}]");
                foreach (var v in list)
                {
                    lines.Add(RenderVariable(v, depth: 1, out var hit));
                    redacted += hit;
                }
            }
            return (lines, hits, redacted);
        }

        var available = new List<string>();                              // 各作用域顶层展示名（不含值）
        var availableSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (scope, list) in vars)
        {
            foreach (var v in list)
            {
                var display = v.Name ?? $"slot{v.Slot}";
                if (availableSeen.Add(display)) available.Add(display);
            }
            var wanted = list.Where(v => MatchName(v, requested)).ToList();
            if (wanted.Count == 0) continue;
            lines.Add($"[{scope}]");
            foreach (var v in wanted)
            {
                hits++;
                lines.Add(RenderVariable(v, depth: 1, out var hit));
                redacted += hit;
            }
        }

        var missing = requested.Where(r => !availableSeen.Contains(r)).ToList();
        if (missing.Count > 0)
        {
            var namesText = available.Count > 0 ? $"当前帧可用名：{string.Join("、", available)}——" : "当前帧无可用名——";
            lines.Add($"未找到：{string.Join("、", missing)}（{namesText}白名单需精确匹配，忽略大小写）。");
        }
        return (lines, hits, redacted);
    }

    /// <summary>递归渲染变量（对象/数组 children 缩进展示；引擎已按一级展开 + 截断）。debug_evaluate/debug_object 复用。
    /// DB1：每层按变量自身名 + 值内容形态脱敏（父对象名不敏感不整体脱敏，children 逐字段行各自判定）。</summary>
    internal static string RenderVariable(DotNetDebugger.Engine.Models.DebugVariable v, int depth)
        => RenderVariable(v, depth, out _);

    /// <summary>单次递归渲染并回传子树脱敏命中数（计数与占位符恒一致；debug_variables 顶部计数用）。</summary>
    private static string RenderVariable(DotNetDebugger.Engine.Models.DebugVariable v, int depth, out int redacted)
    {
        var indent = new string(' ', depth * 2);
        var (valueText, hit) = SensitiveValueRedactor.Redact(v.Name, v.Value.Display);
        redacted = hit ? 1 : 0;
        var line = $"{indent}{v.Name ?? $"slot{v.Slot}"} = {valueText}";
        if (v.Value.Children is not { } children) return line;
        foreach (var c in children)
        {
            line += Environment.NewLine + RenderVariable(c, depth + 1, out var childHit);
            redacted += childHit;
        }
        return line;
    }

    /// <summary>
    /// 前置校验：存在活动调试会话且进程处于 Stopped（断点/异常/单步停点）。
    /// 返回 false 时 <paramref name="active"/> 为 null、<paramref name="error"/> 带中文提示（调用方直接返回该提示）。
    /// debug_evaluate 等停点读取类工具共用。
    /// </summary>
    internal static bool TryRequireStopped(
        [NotNullWhen(true)] out DotNetDebugger.Session.ActiveDebugSession? active,
        [NotNullWhen(false)] out string? error)
    {
        active = DebugSessionService.Manager.Active;
        if (active is null)
        {
            error = "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";
            return false;
        }
        if (active.Buffer.CurrentState != DebugSessionState.Stopped)
        {
            error = "进程未停在断点/异常（当前非 Stopped 状态）。先 debug_continue 运行至断点停下，再读栈/变量。";
            return false;
        }
        error = null;
        return true;
    }
}
