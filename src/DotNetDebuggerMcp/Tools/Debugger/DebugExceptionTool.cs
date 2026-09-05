using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// first-chance 异常断点工具：设异常断点后进程在抛异常时停下（v1：设了即停全部 first-chance）。
/// </summary>
[McpServerToolType]
public static class DebugExceptionTool
{
    /// <summary>
    /// 设置 first-chance 异常断点：进程后续抛异常时停下（v1：typeName 空 = 全部异常停下；
    /// 类型精确过滤 v2）。设好后 debug_continue，异常抛出时进程停，用 debug_state/debug_stack 观察。
    /// </summary>
    /// <param name="typeName">异常类型名过滤（v2 生效）；缺省空 = 全部异常停下。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示。</returns>
    [McpServerTool]
    [Description("设置 first-chance 异常断点：进程后续抛异常时停下（typeName 空 = 全部异常；类型精确过滤 v2）。设好后 debug_continue，异常抛出时进程停。")]
    public static async Task<string> DebugExceptions(
        [Description("异常类型名过滤（v2 生效）；缺省空 = 全部异常停下。")] string typeName = "",
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";

        try
        {
            if (string.IsNullOrWhiteSpace(typeName))
                await active.Session.SetExceptionBreakpointAsync(null, cancellationToken);
            else
                await active.Session.SetExceptionBreakpointAsync(typeName.Trim(), cancellationToken);
            DebugSessionService.Manager.Actions.Log("debug_exceptions", typeName, "ok");
            return string.IsNullOrWhiteSpace(typeName)
                ? "已设异常断点：全部 first-chance 异常将停下进程。"
                : $"已设异常断点：类型 {typeName.Trim()} 相关异常将停下进程（v1 为全部异常，精确过滤 v2）。";
        }
        catch (Exception ex)
        {
            return $"设异常断点失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 清除异常断点（异常不再停下进程）。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示。</returns>
    [McpServerTool]
    [Description("清除异常断点（异常不再停下进程）。")]
    public static async Task<string> DebugExceptionsClear(CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。";

        try
        {
            await active.Session.ClearExceptionBreakpointsAsync(cancellationToken);
            DebugSessionService.Manager.Actions.Log("debug_exceptions_clear", "", "ok");
            return "已清除异常断点。";
        }
        catch (Exception ex)
        {
            return $"清除异常断点失败：{ex.Message}";
        }
    }
}
