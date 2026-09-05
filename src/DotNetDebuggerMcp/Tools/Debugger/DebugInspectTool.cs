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
    /// 每帧输出 模块!token+ILoffset 位置。
    /// </summary>
    /// <param name="threadId">线程 id；缺省 0 = 用最近停点线程。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>调用栈文本或错误提示。</returns>
    [McpServerTool]
    [Description("读取调用栈（进程需停在断点/异常/单步）。每帧输出 模块!token+ILoffset。缺省读最近停点线程；threadId 指定时读该线程。")]
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
            if (frames.Count == 0) return "调用栈为空（可能停在非托管/无 IL 帧处）。";
            var lines = frames.Select(f => $"  {f.FrameIndex}: {f.Location}").ToList();
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
    /// 读取栈顶帧的局部变量与参数（进程需停）。输出每个局部变量/参数的值（v1 标量）。
    /// </summary>
    /// <param name="threadId">线程 id；缺省 0 = 用最近停点线程。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>变量文本或错误提示。</returns>
    [McpServerTool]
    [Description("读取栈顶帧的局部变量与参数（进程需停）。v1 覆盖标量值；对象/数组显示摘要。")]
    public static async Task<string> DebugVariables(
        [Description("线程 id；缺省 0 = 用最近停点线程。")] int threadId = 0,
        CancellationToken cancellationToken = default)
    {
        if (!TryRequireStopped(out var active, out var error)) return error;

        var tid = threadId > 0 ? threadId : active.Buffer.StoppedThreadId;
        if (tid <= 0) return "无停点线程可读（先 debug_continue 运行至断点停下）。";

        try
        {
            var vars = await active.Session.GetVariablesAsync(tid, cancellationToken);
            var lines = new List<string>();
            foreach (var (scope, list) in vars)
            {
                lines.Add($"[{scope}]");
                foreach (var v in list)
                    lines.Add(RenderVariable(v, depth: 1));
            }
            DebugSessionService.Manager.Actions.Log("debug_variables", $"thread={tid}", "ok");
            return $"局部变量/参数（thread={tid}）:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
        }
        catch (Exception ex)
        {
            return $"读变量失败：{ex.Message}";
        }
    }

    /// <summary>递归渲染变量（对象/数组 children 缩进展示；引擎已按一级展开 + 截断）。</summary>
    private static string RenderVariable(DotNetDebugger.Engine.Models.DebugVariable v, int depth)
    {
        var indent = new string(' ', depth * 2);
        var line = $"{indent}{v.Name ?? $"slot{v.Slot}"} = {v.Value.Display}";
        if (v.Value.Children is not { } children) return line;
        foreach (var c in children)
            line += Environment.NewLine + RenderVariable(c, depth + 1);
        return line;
    }

    /// <summary>
    /// 前置校验：存在活动调试会话且进程处于 Stopped（断点/异常/单步停点）。
    /// 返回 false 时 <paramref name="active"/> 为 null、<paramref name="error"/> 带中文提示（调用方直接返回该提示）。
    /// </summary>
    private static bool TryRequireStopped(
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
