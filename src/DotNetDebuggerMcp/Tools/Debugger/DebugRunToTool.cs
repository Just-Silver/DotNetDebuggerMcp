using DotNetDebugger.Engine.Models;
using DotNetDebugger.Session;
using DotNetDebugger.Session.Models;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 运行到目标工具（I1，VS「运行到光标处 / Run to Cursor」的 agent 对等）：在目标位置设**一次性临时断点**，
/// 进程继续运行直至命中即停并**自动移除**该临时断点。纯宿主编排（不新增 Engine 一次性断点概念）：
/// 复用 DebugBreakpointTool 的定位解析（typeName+memberName / typeName+line）→ SetBreakpointAsync 记 id →
/// 若进程被调试器持有则 ContinueAsync → WaitForStopAsync(timeout) → 命中（stop.BreakpointId==id）→ finally 一律 RemoveBreakpointAsync 兜底。
/// </summary>
[McpServerToolType]
public static class DebugRunToTool
{
    /// <summary>
    /// 运行到目标位置（一次运行即命中即停，目标可以是 typeName+line 反编译视图行或 typeName+memberName 方法入口）。
    /// 命中后临时断点自动移除；未在 timeoutSeconds 内命中返回提示且临时断点同样自动清理（可改普通断点+debug_continue 场景）。
    /// 语义边界：run_to 只对「目标会被进程自然执行到」有效——若当前停在的代码路径不会自行走向目标
    /// （如停在前置断点、目标在另一分支），run_to 会等到超时；此类「停住不自己走」场景应设普通断点再 debug_continue。
    /// </summary>
    /// <param name="typeName">类型全名（与 decompile 输出同格式）；与 line 组合按反编译视图行定位，或与 memberName 组合按成员级定位（二选一必填）。</param>
    /// <param name="line">行号（1-based，decompile 输出行号）；与 typeName 组合时提供（默认 0=未提供）。</param>
    /// <param name="memberName">成员名（方法，子串忽略大小写，默认空=不启用）；与 typeName 组合按成员级定位（命中唯一方法即设断点，line 忽略）。</param>
    /// <param name="moduleName">模块名（如 DebugTarget.dll）；行/成员定位省缺时在已加载模块中解析（默认空）。</param>
    /// <param name="timeoutSeconds">等待命中秒数上限，默认 30；超时返回提示。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示或错误提示。</returns>
    [McpServerTool]
    [Description("运行到目标位置后停下（对标 VS 运行到光标处 / Run to Cursor）：在目标处设一次性临时断点并继续运行，命中即停、临时断点自动移除——用 debug_stack/debug_variables 观察现场。目标定位同 debug_breakpoint_set 的 typeName+line（line 为 decompile 输出行号）或 typeName+memberName（方法名子串，命中唯一方法）；moduleName 可省。超时/命中其它断点/进程退出时返回对应提示且临时断点一并自动清理。注意边界：run_to 只对「目标会被进程自然执行到」有效——当前执行路径若不会走向目标（停住不自己走的分支/等待），run_to 将等到超时，此类场景请改用 debug_breakpoint_set 设普通断点 + debug_continue。")]
    public static async Task<string> DebugRunTo(
        [Description("类型全名（与 decompile 输出同格式，默认空）；与 line 组合按反编译视图行定位，或与 memberName 组合按成员级定位——typeName+line / typeName+memberName 二选一必填。")] string typeName = "",
        [Description("行号（1-based，decompile 输出行号，默认 0=未提供）；与 typeName 组合按反编译视图行定位。")] int line = 0,
        [Description("成员名（方法，子串忽略大小写，默认空=不启用）；与 typeName 组合按成员级定位——命中唯一方法即设临时断点（line 忽略）。")] string memberName = "",
        [Description("模块名（如 DebugTarget.dll，默认空）；行/成员定位省缺时在已加载模块中解析。")] string moduleName = "",
        [Description("等待命中秒数上限，默认 30。")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";
        if (timeoutSeconds < 1) return "timeoutSeconds 须 ≥ 1（等待命中秒数上限，默认 30）。";

        // 1. 定位参数校验（run_to 目标定位为行/成员两种；校验在设断点前给清晰提示）
        if (string.IsNullOrWhiteSpace(typeName))
            return "请提供 typeName：run_to 目标定位为 typeName+line（decompile 输出行号）或 typeName+memberName（方法名子串），二选一必填。";
        if (string.IsNullOrWhiteSpace(memberName) && line <= 0)
            return "请提供 line（typeName+line 定位，decompile 输出行号）或 memberName（typeName+memberName 定位），二选一必填。";

        // 2. 设一次性临时断点（与 debug_breakpoint_set 完全同款定位/设置逻辑，取回断点 id）
        var setText = await DebugBreakpointTool.SetResolvedAsync(moduleName, "", 0, typeName, memberName, "", line, 1, "stop", null, cancellationToken);
        var targetId = DebugBreakpointTool.ParseBreakpointIdText(setText);
        if (targetId is null)
            return setText.Contains("匹配", StringComparison.Ordinal) && setText.Contains("方法成员", StringComparison.Ordinal)
                ? $"run-to 目标未唯一：{setText}"
                : $"未能在 {typeName} 定位到 run-to 目标：{setText}";

        // 3. 编排：总是尝试 continue 放行跑向目标（引擎对「已在运行」的 continue 是安全空操作；缓冲可能滞后）。
        //    等待命中用 deadline 循环跳过**陈旧停点**：continue 前同一停点实例（ContinueAsync 返回时事件缓冲
        //    未必已消费 Running 事件，单次 WaitForStopAsync 会立刻返回旧 Stopped 快照），以及残留的 StepCompleted
        //    （前序 step 的遗留事件）——否则会误判「停在 STEP_NORMAL，尚未到目标」。
        //    退出按 CurrentState 判定（进程退出时 WaitForStopAsync 返回最近停点快照，可能是 continue 前那个）。
        StopContext? stop = null;
        string message;
        try
        {
            var preStop = active.Buffer.LastStop; // continue 前快照（引用比较识别陈旧返回）
            // 进程被调试器持有（Stopped 停点 / launch 冻结在 Main 前 Attaching）→ continue 放行跑向目标。
            // 事件缓冲可能滞后于引擎（如刚 step 完缓冲仍显示 Running 而引擎已 Stopped）——**总是尝试 continue**
            // 以免漏放行而卡住（引擎对「已在运行」的 continue 是安全空操作：TryIsRunning 直接返回）。
            if (active.Buffer.CurrentState != DebugSessionState.Exited)
            {
                await active.Session.ContinueAsync(cancellationToken);
            }

            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (true)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break; // 到期 = 超时（stop 保持 null 或最后陈旧快照）
                stop = await active.Buffer.WaitForStopAsync(remaining, cancellationToken);
                // 陈旧停点（continue 前同一实例 / 残留单步完成事件）→ 丢弃继续等真正的新停点；
                // 进程 Exited 时 LastStop 也可能仍是 preStop——交给下方状态判定。
                if (stop is not null && IsStaleStop(stop, preStop, active.Buffer.CurrentState))
                {
                    stop = null;
                    continue;
                }
                break;
            }

            // 分类（判定顺序：退出 → 超时 → 命中目标 → 其它断点 → 异常/单步）
            var state = active.Buffer.CurrentState;
            if (state == DebugSessionState.Exited)
            {
                message = $"进程在到达目标前已退出。临时断点 {targetId.Value} 已自动清理。";
            }
            else if (stop is null)
            {
                message = $"等待 {timeoutSeconds} 秒未命中（当前 {DebugSessionTool.StateText(state)}）。run-to 只对「目标会被进程自然执行到」有效；" +
                          "可再 debug_run_to 继续等，或改用 debug_breakpoint_set 设普通断点 + debug_continue。";
            }
            else if (stop.BreakpointId == targetId)
            {
                message = $"已运行到目标。临时断点 {targetId.Value} 命中并已自动移除。用 debug_stack/debug_variables 观察。";
            }
            else if (stop.Kind == DebugEventKind.BreakpointHit && stop.BreakpointId is not null)
            {
                message = $"已停下但先命中了其它断点 id={stop.BreakpointId}，非本次 run-to 目标。临时断点 {targetId.Value} 已自动清理；" +
                          "可 debug_continue 继续跑向目标，或 debug_breakpoint_remove 清理已命中的其它断点后再试。";
            }
            else if (stop.Kind == DebugEventKind.ExceptionHit)
            {
                message = $"进程停在异常（{stop.Reason}），未到 run-to 目标。临时断点 {targetId.Value} 已自动清理。" +
                          $"用 debug_state 看异常现场（附 $exception）；处理后可 debug_continue 继续跑向目标。";
            }
            else
            {
                // StepCompleted 等：未到目标但进程已停——诚实告知 + 提示可继续
                message = $"进程停在 {stop.Reason ?? stop.Kind.ToString()}，尚未到 run-to 目标。临时断点 {targetId.Value} 已自动清理；可 debug_continue 继续运行。";
            }
        }
        catch (Exception ex)
        {
            // 编排异常（如 continue/wait 本身失败）：临时断点留待 finally 清理后返回
            message = $"run-to 执行失败：{ex.Message}";
        }
        finally
        {
            // 兜底清理临时断点（命中/未命中/超时/退出/异常都移除；Remove 幂等，断点已不在也安全——防残留副作用）。
            // 必须用 CancellationToken.None 而非请求取消令牌：客户端取消时清理不能一并被取消（否则临时断点残留），
            // 会话关闭/进程退出等由 TryRemoveSafeAsync 的吞异常兜住。
            if (targetId is not null)
                await TryRemoveSafeAsync(active, targetId.Value, CancellationToken.None);
        }
        DebugSessionService.Manager.Actions.Log("debug_run_to", $"{typeName} member={memberName} line={line}", message);
        return message;
    }

    /// <summary>安全移除临时断点：不抛异常（已移除/会话关闭等场景吞掉——finally 兜底清理不允许再抛覆盖主结果）。</summary>
    private static async Task TryRemoveSafeAsync(ActiveDebugSession active, int breakpointId, CancellationToken ct)
    {
        try
        {
            await active.Session.RemoveBreakpointAsync(breakpointId, ct);
        }
        catch (Exception)
        {
            // 清理失败不影响主结果——引擎侧断点可能已随进程退出/会话关闭释放
        }
    }

    /// <summary>run_to 等待期间的陈旧停点判定：需丢弃并继续等待——①continue 前同一停点实例（事件缓冲滞后返回旧快照）；
    /// ②残留的 StepCompleted（run_to 自身不单步，等待期间出现的单步停点必是前序 step 的遗留）。进程已退出不算陈旧（交退出判定）。</summary>
    internal static bool IsStaleStop(StopContext stop, StopContext? preStop, DebugSessionState state)
        => state != DebugSessionState.Exited
           && (ReferenceEquals(stop, preStop) || stop.Kind == DebugEventKind.StepCompleted);
}
