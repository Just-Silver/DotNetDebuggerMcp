using DotNetDebugger.Engine.Models;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 调试执行控制工具：继续运行、单步。控制工具异步返回（进程继续执行，停点后经 debug_state 查询），带默认超时。
/// </summary>
[McpServerToolType]
public static class DebugControlTool
{
    /// <summary>
    /// 继续执行被调试进程（异步返回，不等停）。进程运行至下个断点/异常停下后，
    /// 用 debug_state 确认 Stopped，再 debug_stack/debug_variables 观察。
    /// </summary>
    /// <param name="timeoutSeconds">继续操作的等待上限（若进程已在运行则立即返回），默认 30。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示或错误提示。</returns>
    [McpServerTool]
    [Description("继续执行被调试进程（异步返回，不等停）。进程会在下个断点/异常/退出时停下；用 debug_state 确认 Stopped 后 debug_stack/debug_variables 观察。")]
    public static async Task<string> DebugContinue(
        [Description("继续操作等待秒数上限，默认 30。")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";

        try
        {
            await active.Session.ContinueAsync(cancellationToken);
            DebugSessionService.Manager.Actions.Log("debug_continue", "", "ok");
            return "已继续执行。进程将运行至下个断点/异常/退出；用 debug_state 查询是否停下。";
        }
        catch (Exception ex)
        {
            return $"继续执行失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 单步执行（进程需已停在断点/异常）。stepType：into=进入被调方法 / over=不进入 / out=步出当前方法。
    /// 单步完成后进程停下，用 debug_state/debug_stack 观察新位置。
    /// </summary>
    /// <param name="stepType">单步类型：into/over/out，默认 over。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示或错误提示。</returns>
    [McpServerTool]
    [Description("单步执行（进程需已停在断点/异常）。stepType：into=进入被调方法 / over=不进入 / out=步出当前方法。单步完成后进程停下，用 debug_state/debug_stack 观察新位置。")]
    public static async Task<string> DebugStep(
        [Description("单步类型：into=进入被调方法 / over=不进入 / out=步出当前方法，默认 over。")] string stepType = "over",
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";
        if (active.Buffer.CurrentState != DebugSessionState.Stopped)
            return "进程未停在断点/异常（当前非 Stopped 状态）。先 debug_continue 运行至断点停下，再单步。";

        try
        {
            switch (stepType.Trim().ToLowerInvariant())
            {
                case "into": await active.Session.StepIntoAsync(cancellationToken); break;
                case "out": await active.Session.StepOutAsync(cancellationToken); break;
                default: await active.Session.StepOverAsync(cancellationToken); break;
            }
            DebugSessionService.Manager.Actions.Log("debug_step", stepType, "ok");
            return $"已执行 step {stepType}。单步完成进程停下；用 debug_state/debug_stack 观察新位置。";
        }
        catch (Exception ex)
        {
            return $"单步失败：{ex.Message}";
        }
    }
}
