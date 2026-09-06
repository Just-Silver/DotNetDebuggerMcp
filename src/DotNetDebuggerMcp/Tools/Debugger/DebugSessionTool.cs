using DotNetDebugger.Session;
using DotNetDebugger.Session.Models;
using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 调试会话管理工具：启动/附加/断开 .NET 进程进行动态调试，查询会话状态。
/// 控制工具异步返回（带默认超时），停点信息经 debug_state/debug_stack 查询。
/// </summary>
[McpServerToolType]
public static class DebugSessionTool
{
    /// <summary>
    /// 启动并附加一个 .NET 进程进行调试（异步返回，不等停点）。返回会话 id 与初始状态；
    /// 进程命中断点/异常停下后经 debug_state/debug_stack/debug_variables 查询。
    /// 目标需有启动延迟（attach 窗口），如 DebugTarget 可传 delay 参数。
    /// </summary>
    /// <param name="commandLine">目标可执行文件路径（可含参数，空格分隔），相对当前工作目录（必填）。</param>
    /// <param name="timeoutSeconds">本次启动等待秒数上限，默认 30。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>中文结果提示（会话 id 与状态）或错误提示。</returns>
    [McpServerTool]
    [Description("启动并附加一个 .NET 进程进行调试（异步返回，不等停点）。返回会话 id 与初始状态；命中断点后用 debug_state/debug_stack/debug_variables 查询。目标需有启动延迟（attach 窗口）。")]
    public static async Task<string> DebugLaunch(
        [Description("目标可执行文件路径（可含参数，如 DebugTarget.exe 3 8），相对当前工作目录（必填）。")] string commandLine = "",
        [Description("本次启动等待秒数上限，默认 30。")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return "请指定目标可执行文件路径（commandLine 必填）。";
        var exePath = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        if (!File.Exists(exePath) && !File.Exists(Path.GetFullPath(exePath)))
            return $"目标文件不存在：{exePath}（相对当前工作目录解析）。";

        try
        {
            var active = await DebugSessionService.Manager.LaunchAndAttachAsync(commandLine, cancellationToken);
            DebugSessionService.Manager.Actions.Log("debug_launch", commandLine, "ok");
            return $"已启动并附加调试会话。目标：{commandLine}。当前状态：{StateText(active.Buffer.CurrentState)}。" +
                   "用 debug_state 查询状态；用 debug_breakpoint_set 下断点后 debug_continue 运行。";
        }
        catch (Exception ex)
        {
            return $"启动调试失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 附加到已运行的 .NET 进程进行调试。返回会话 id 与初始状态；后续经 debug_* 工具控制。
    /// </summary>
    /// <param name="processId">目标进程 id（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示或错误提示。</returns>
    [McpServerTool]
    [Description("附加到已运行的 .NET 进程进行调试。返回会话 id 与初始状态；用 debug_breakpoint_set 下断点后 debug_continue 运行。")]
    public static async Task<string> DebugAttach(
        [Description("目标进程 id（必填）。")] int processId = 0,
        CancellationToken cancellationToken = default)
    {
        if (processId <= 0)
            return "请指定目标进程 id（processId 必填，正整数）。";

        try
        {
            var active = await DebugSessionService.Manager.AttachAsync(processId, cancellationToken);
            DebugSessionService.Manager.Actions.Log("debug_attach", $"pid={processId}", "ok");
            return $"已附加调试会话。目标 pid={processId}。当前状态：{StateText(active.Buffer.CurrentState)}。" +
                   "用 debug_state 查询状态；用 debug_breakpoint_set 下断点后 debug_continue 运行。";
        }
        catch (Exception ex)
        {
            return $"附加调试失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 断开当前调试会话（目标进程继续独立运行）。无活动会话返回提示。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示。</returns>
    [McpServerTool]
    [Description("断开当前调试会话（目标进程继续独立运行）。")]
    public static async Task<string> DebugDisconnect(CancellationToken cancellationToken = default)
    {
        if (DebugSessionService.Manager.Active is null)
            return "当前无活动调试会话。";
        await DebugSessionService.Manager.CloseAsync(cancellationToken);
        DebugSessionService.Manager.Actions.Log("debug_disconnect", "", "ok");
        return "已断开调试会话（目标进程继续独立运行）。";
    }

    /// <summary>
    /// 查询当前调试会话状态（有无活动会话、会话状态、最近停点）。立即返回，不等停。
    /// 停点时默认附反编译视图上下文（行号=decompile 输出行号；contextLines=0 关闭）。
    /// </summary>
    /// <param name="contextLines">停点上下文行数预算，默认见 AppConfig（100），0=不附。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>会话摘要文本。</returns>
    [McpServerTool]
    [Description("查询当前调试会话状态：有无活动会话、会话状态（Running/Stopped/Exited…）、最近停点现场（断点/异常/单步）。立即返回，不等停；进程停在断点时先调此工具确认 Stopped 再 debug_stack/debug_variables。停点时默认附反编译视图上下文（当前语句周边代码，行号=decompile 输出行号，contextLines 可调/0 关闭）。")]
    public static async Task<string> DebugState(
        [Description(ToolParameterText.ContextLinesParam)] int contextLines = AppConfig.DefaultStopContextBudgetLines,
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null)
            return "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";

        var buffer = active.Buffer;
        var info = DebugSessionService.Manager.GetInfo();
        var lines = new List<string>
        {
            $"会话状态: {StateText(buffer.CurrentState)}",
            $"最近停点: {StopText(buffer.LastStop)}",
        };
        var context = buffer.CurrentState == DotNetDebugger.Engine.Models.DebugSessionState.Stopped
            ? await StopContextRenderer.RenderAsync(active, contextLines)
            : null;
        if (context is not null) lines.Add(context);
        var traces = TracesText(buffer);
        if (traces is not null) lines.Add(traces);
        var skipped = SkippedExceptionsText(buffer);
        if (skipped is not null) lines.Add(skipped);
        DebugSessionService.Manager.Actions.Log("debug_state", "", string.Join("; ", lines));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>取走并格式化「期间被过滤器跳过的异常」反馈（无则 null）。消费式：每次调用清零。</summary>
    internal static string? SkippedExceptionsText(SessionEventBuffer buffer)
    {
        var (count, lastType) = buffer.ConsumeSkippedExceptions();
        return count > 0 ? $"期间跳过 {count} 个不在过滤范围的异常（如 {lastType}）。" : null;
    }

    /// <summary>取走并格式化 trace 轨迹（P5；无则 null）。消费式：读走即清，防重复吐给 agent。</summary>
    internal static string? TracesText(SessionEventBuffer buffer)
    {
        var traces = buffer.ConsumeTraces(out var dropped);
        if (traces.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        sb.Append($"trace 轨迹（{traces.Count} 条，旧→新{(dropped > 0 ? $"；因环形上限已丢弃最早 {dropped} 条" : "")}）:");
        var index = 0;
        foreach (var t in traces)
        {
            index++;
            sb.Append($"{Environment.NewLine}  [{index}] {t.UtcTimestamp.LocalDateTime:HH:mm:ss.fff} id={t.BreakpointId} top={t.TopFrame?.ToString() ?? "?"}");
            foreach (var v in t.Variables)
                sb.Append($"{Environment.NewLine}      [{v.Scope}] {v.Name ?? $"slot{v.Slot}"} = {v.Display}");
        }
        return sb.ToString();
    }

    internal static string StateText(DotNetDebugger.Engine.Models.DebugSessionState state) => state switch
    {
        DotNetDebugger.Engine.Models.DebugSessionState.Running => "运行中 (Running)",
        DotNetDebugger.Engine.Models.DebugSessionState.Stopped => "已停止 (Stopped，停在断点/异常/单步)",
        DotNetDebugger.Engine.Models.DebugSessionState.Exited => "已退出 (Exited)",
        DotNetDebugger.Engine.Models.DebugSessionState.Detached => "已断开 (Detached)",
        _ => state.ToString(),
    };

    internal static string StopText(StopContext? stop)
    {
        if (stop is null) return "（无）";
        var text = $"{stop.Kind} thread={stop.ThreadId} top={stop.TopFrame} reason={stop.Reason}";
        if (!string.IsNullOrEmpty(stop.Message)) text += $" message=\"{stop.Message}\"";
        return text;
    }
}
