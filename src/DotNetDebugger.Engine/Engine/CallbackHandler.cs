using ClrDebug;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;

namespace DotNetDebugger.Engine.Engine;

/// <summary>
/// CorDebugManagedCallback → 事件入队（回调线程只入队，绝不在回调线程调 Continue——与命令并发会导致
/// SUPERFLOUS_CONTINUE/卡死）；事件处理（停点决策 + Continue）由 DebugEngineCore 命令泵线程经
/// <see cref="HandleEvent"/> 统一执行（对齐 sharpdbg：单线程事件循环）。
/// </summary>
public sealed class CallbackHandler
{
    private readonly DebugEngineCore _core;
    private readonly BreakpointManager _breakpoints;
    private volatile ExceptionBreakpointFilter? _exceptionFilter;

    // attach/launch 初始停：置位时除 ExitProcess 外一切事件不 Continue（进程停在初始同步点供配置）。
    // 外部首次 ContinueAsync 清此标志。
    private volatile bool _initialHold = true;

    public CallbackHandler(DebugEngineCore core, CorDebugManagedCallback cb, BreakpointManager breakpoints)
    {
        _core = core;
        _breakpoints = breakpoints;

        // 回调线程只入队；处理在命令泵线程（HandleEvent）
        cb.OnAnyEvent += (_, e) => core.EventChannelWriter.TryWrite(e);
    }

    /// <summary>设置 first-chance 异常过滤器（null=全部放行）。</summary>
    public void SetExceptionFilter(ExceptionBreakpointFilter? filter) => _exceptionFilter = filter;

    /// <summary>外部首次 Continue 清除初始停（由 DebugEngineCore.ContinueAsync 调用）。</summary>
    public void ReleaseInitialHold() => _initialHold = false;

    /// <summary>
    /// 命令泵线程统一处理一个回调事件：决定停或 Continue。
    /// 停点事件（登记断点命中/步完成/配了过滤器的异常）→ 发布 DebugEvent、不 Continue（进程停）；
    /// 其余（模块加载/线程/未配过滤器的异常/未登记断点/退出）→ Continue（或按初始停/退出规则）。
    /// </summary>
    public void HandleEvent(CorDebugManagedCallbackEventArgs e)
    {
        try
        {
            switch (e)
            {
                case CreateProcessCorDebugManagedCallbackEventArgs cp:
                    if (cp.Process is not null) _core.SetProcess(cp.Process);
                    _core.Log("info", $"CreateProcess pid={cp.Process?.Id}");
                    break;
                case ExitProcessCorDebugManagedCallbackEventArgs:
                    _core.PublishState(DebugSessionState.Exited, "process exited");
                    _initialHold = false;
                    return; // 进程已退出：不 Continue
                case LoadModuleCorDebugManagedCallbackEventArgs lm:
                    if (lm.Module is not null)
                    {
                        var rebound = _breakpoints.TrackModule(lm.Module); // pending 断点自动重绑
                        _core.Log("info", $"LoadModule {SafeModuleName(lm.Module)}"
                            + (rebound > 0 ? $"（重绑断点 {rebound} 个）" : ""));
                        if (rebound > 0) _core.PublishBreakpointsChanged();
                    }
                    break;
                case BreakpointCorDebugManagedCallbackEventArgs bp:
                    if (HandleBreakpoint(bp)) return; // 已停
                    break;
                case StepCompleteCorDebugManagedCallbackEventArgs sc:
                    HandleStepComplete(sc);
                    return; // 已停
                case Exception2CorDebugManagedCallbackEventArgs ex2:
                    if (HandleException2(ex2)) return; // 已停
                    break;
            }

            // 默认/其余事件：Continue（停点事件未停时也落到此处继续）
            if (_initialHold) return; // attach 初始停：等外部首次 ContinueAsync
            try { e.Controller?.Continue(false); }
            catch (Exception cex) { _core.Log("warn", $"事件 {e.GetType().Name} Continue 失败: {cex.Message}"); }
        }
        catch (Exception ex)
        {
            _core.Log("error", $"处理事件 {e.GetType().Name} 异常: {ex.Message}");
            // 避免进程卡死：尝试 Continue
            try { e.Controller?.Continue(false); } catch { }
        }
    }

    /// <summary>处理断点命中。返回 true = 已停（不 Continue）。</summary>
    private bool HandleBreakpoint(BreakpointCorDebugManagedCallbackEventArgs e)
    {
        if (e.Breakpoint is not CorDebugFunctionBreakpoint fbp) return false; // 非函数断点：继续
        var matched = _breakpoints.Match(fbp);
        if (matched is null) return false; // 非登记断点：继续

        var top = _core.ReadTopFrame(e.Thread);
        _core.PublishBreakpointHit(matched.Id, e.Thread.Id, top);
        _core.PublishState(DebugSessionState.Stopped, $"breakpoint {matched.Id}");
        return true; // 停在断点
    }

    /// <summary>处理步完成。始终停。</summary>
    private void HandleStepComplete(StepCompleteCorDebugManagedCallbackEventArgs e)
    {
        var top = _core.ReadTopFrame(e.Thread);
        _core.PublishStepCompleted(e.Thread.Id, top, e.Reason.ToString());
        _core.PublishState(DebugSessionState.Stopped, $"step complete ({e.Reason})");
    }

    /// <summary>
    /// 处理异常回调。返回 true = 已停。
    /// 仅在 FIRST_CHANCE 阶段决策停/跳（USER_FIRST_CHANCE/CATCH_HANDLER_FOUND 是同一异常传播的后续阶段，
    /// 重复停/重复计数只会干扰调试）；过滤器 null = 全部放行（含 UNHANDLED，保持 v1 行为）。
    /// 匹配语义：异常类型全名与过滤名相等或以「.过滤名」结尾（忽略大小写）——不匹配则发 skipped 事件后放行
    /// （Session 计数，供 debug_wait/debug_state 给不命中反馈；对齐 sharpdbg「不停即 Continue」模式）。
    /// </summary>
    private bool HandleException2(Exception2CorDebugManagedCallbackEventArgs e)
    {
        if (e.EventType != CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE) return false;
        if (_exceptionFilter is null) return false; // 未设过滤器：放行

        var info = e.Thread.CurrentException is { } excValue ? _core.ReadCurrentExceptionInfo(excValue) : null;
        var typeName = info?.TypeName ?? "<unknown>";
        if (!_exceptionFilter.Matches(typeName))
        {
            _core.PublishExceptionSkipped(e.Thread.Id, typeName, info?.Message);
            return false; // 不停：落到默认 Continue
        }

        var top = _core.ReadTopFrame(e.Thread);
        _core.PublishExceptionHit(e.Thread.Id, typeName, info?.Message, top);
        _core.PublishState(DebugSessionState.Stopped, $"exception {typeName}");
        return true;
    }

    private static string SafeModuleName(CorDebugModule m)
    {
        try { return m.Name; } catch { return "<unknown>"; }
    }
}
