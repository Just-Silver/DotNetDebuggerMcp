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
                        _breakpoints.TrackModule(lm.Module);
                        _core.Log("info", $"LoadModule {SafeModuleName(lm.Module)}");
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

    /// <summary>处理异常。返回 true = 已停。</summary>
    private bool HandleException2(Exception2CorDebugManagedCallbackEventArgs e)
    {
        if (_exceptionFilter is null) return false; // 未设过滤器：放行

        // v1：设了过滤器即停在 first-chance（类型精确过滤列 v2）
        var top = _core.ReadTopFrame(e.Thread);
        var typeToken = TryGetExceptionType(e);
        _core.PublishExceptionHit(e.Thread.Id, typeToken ?? _exceptionFilter.TypeName ?? "<unknown>", null, top);
        _core.PublishState(DebugSessionState.Stopped, $"exception {_exceptionFilter.TypeName ?? typeToken ?? "<unknown>"}");
        return true;
    }

    private static string? TryGetExceptionType(Exception2CorDebugManagedCallbackEventArgs e)
    {
        try
        {
            var cls = e.Thread?.CurrentException?.ExactType?.Class;
            return cls is null ? null : $"0x{cls.Token.Value:x8}";
        }
        catch { return null; }
    }

    private static string SafeModuleName(CorDebugModule m)
    {
        try { return m.Name; } catch { return "<unknown>"; }
    }
}
