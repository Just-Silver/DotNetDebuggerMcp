using DotNetDebugger.Session;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 调试输出工具：查看被调试进程的控制台输出（stdout/stderr）。
/// 输出捕获仅对 debug_launch 启动的会话可用（attach 已运行进程无法重定向其输出）。
/// </summary>
[McpServerToolType]
public static class DebugOutputTool
{
    internal const string StreamPrefixOut = "[out] ";
    internal const string StreamPrefixErr = "[err] ";
    internal const string OutputSectionHeader = "目标输出（最近 {0} 行，旧→新）:";

    /// <summary>把目标输出尾部按统一格式追加到 result 之后（debug_wait 等复用）；无捕获或 0 行原样返回。</summary>
    internal static string AppendTargetOutput(DotNetDebugger.Session.ActiveDebugSession active, string result, int outputLines)
    {
        if (outputLines <= 0 || active.Output is null) return result;
        var tail = active.Output.Tail(Math.Clamp(outputLines, 1, ProcessOutputCapture.MaxLines));
        if (tail.Count == 0) return result;
        var sb = new StringBuilder(result);
        sb.AppendLine();
        sb.AppendLine(string.Format(OutputSectionHeader, tail.Count));
        foreach (var line in tail)
            sb.AppendLine((line.Stream == ProcessOutputStream.Stderr ? StreamPrefixErr : StreamPrefixOut) + line.Text);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 查看被调试进程的控制台输出（stdout/stderr）。进程运行中也可随时调用；
    /// 仅 debug_launch 启动的会话可捕获输出。
    /// </summary>
    /// <param name="lines">返回最近行数，默认 50，范围 1-500。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>目标输出文本或中文提示。</returns>
    [McpServerTool]
    [Description("查看被调试进程的控制台输出（stdout/stderr，旧→新）。进程运行中也可随时调用。仅 debug_launch 启动的会话可捕获输出（attach 已运行进程无法重定向其输出）。")]
    public static Task<string> DebugOutput(
        [Description("返回最近行数，默认 50，范围 1-500。")] int lines = 50,
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null)
            return Task.FromResult("当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。");
        if (active.Output is null)
            return Task.FromResult("当前会话为 attach 附加，未捕获目标输出（仅 debug_launch 启动的会话可捕获）。");

        var tail = active.Output.Tail(Math.Clamp(lines, 1, ProcessOutputCapture.MaxLines));
        if (tail.Count == 0)
            return Task.FromResult("目标暂无输出（缓冲保留最近 500 行）。");

        DebugSessionService.Manager.Actions.Log("debug_output", $"{lines}", "ok");
        var sb = new StringBuilder();
        sb.AppendLine(string.Format(OutputSectionHeader, tail.Count));
        foreach (var line in tail)
            sb.AppendLine((line.Stream == ProcessOutputStream.Stderr ? StreamPrefixErr : StreamPrefixOut) + line.Text);
        return Task.FromResult(sb.ToString().TrimEnd());
    }
}
