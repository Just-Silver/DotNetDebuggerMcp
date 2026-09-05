using ClrDebug;
using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Engine.Engine;

/// <summary>
/// CorDebugManagedCallback 事件 → 引擎状态机 + DebugEvent。
/// 规则：OnAnyEvent 兜底 Continue 全部未处理事件（否则进程卡死——spike 验证）；
/// 停点事件（登记的断点命中/步完成/设了过滤器的异常）置 <see cref="_holdContinue"/> 停住进程，
/// 发布 DebugEvent，等外部 ContinueAsync/StepAsync 恢复；ExitProcess 后不 Continue。
/// </summary>
public sealed class CallbackHandler
{
    private readonly DebugEngineCore _core;
    private readonly BreakpointManager _breakpoints;
    private volatile ExceptionBreakpointFilter? _exceptionFilter;

    // 停点挂起标志：置位时 OnAnyEvent 不 Continue（进程保持停止，等外部 continue 命令）。
    // 初始为 true：attach/launch 后进程停在初始同步点（可安全设断点），由外部首次 ContinueAsync 恢复。
    private volatile bool _holdContinue = true;

    public CallbackHandler(DebugEngineCore core, CorDebugManagedCallback cb, BreakpointManager breakpoints)
    {
        _core = core;
        _breakpoints = breakpoints;

        cb.OnCreateProcess += (_, e) =>
        {
            if (e.Process is not null) _core.SetProcess(e.Process);
            _core.Log("info", $"CreateProcess pid={e.Process?.Id}");
        };
        cb.OnExitProcess += (_, _) =>
        {
            // ExitProcess：进程即将终止，不 Continue（OnAnyEvent 对已退出事件无操作亦可）
            _core.PublishState(DebugSessionState.Exited, "process exited");
            _holdContinue = false;
        };
        cb.OnLoadModule += (_, e) =>
        {
            if (e.Module is not null)
            {
                _breakpoints.TrackModule(e.Module);
                _core.Log("info", $"LoadModule {SafeModuleName(e.Module)}");
            }
        };
        cb.OnBreakpoint += OnBreakpoint;
        cb.OnStepComplete += OnStepComplete;
        cb.OnException2 += OnException2;

        // 兜底 Continue：除「停点挂起」与「已退出」外全部继续（spike 验证的关键——未订阅事件不 Continue 会卡死进程）
        cb.OnAnyEvent += (_, e) =>
        {
            if (_holdContinue) return;
            try { e.Controller?.Continue(false); }
            catch { /* 进程已退出等 */ }
        };
    }

    /// <summary>设置 first-chance 异常过滤器（null=全部放行）。</summary>
    public void SetExceptionFilter(ExceptionBreakpointFilter? filter) => _exceptionFilter = filter;

    /// <summary>外部 Continue 命令清除停点挂起标志（由 DebugEngineCore 在命令泵线程调）。</summary>
    public void ReleaseHold() => _holdContinue = false;

    private void OnBreakpoint(object? sender, BreakpointCorDebugManagedCallbackEventArgs e)
    {
        if (e.Breakpoint is not CorDebugFunctionBreakpoint fbp)
            return; // 非函数断点：OnAnyEvent 兜底 Continue
        var matched = _breakpoints.Match(fbp);
        if (matched is null)
            return; // 非登记断点：OnAnyEvent 兜底 Continue

        // 登记的断点命中：发布事件并停（hold，OnAnyEvent 跳过 Continue）
        var top = _core.ReadTopFrame(e.Thread);
        _core.PublishBreakpointHit(matched.Id, e.Thread.Id, top);
        _core.PublishState(DebugSessionState.Stopped, $"breakpoint {matched.Id}");
        _holdContinue = true;
    }

    private void OnStepComplete(object? sender, StepCompleteCorDebugManagedCallbackEventArgs e)
    {
        var top = _core.ReadTopFrame(e.Thread);
        _core.PublishStepCompleted(e.Thread.Id, top, e.Reason.ToString());
        _core.PublishState(DebugSessionState.Stopped, $"step complete ({e.Reason})");
        _holdContinue = true; // 停，等外部继续/再单步
    }

    private void OnException2(object? sender, Exception2CorDebugManagedCallbackEventArgs e)
    {
        if (_exceptionFilter is null)
            return; // 未设异常断点：OnAnyEvent 兜底 Continue（放行）

        // v1：设了异常过滤器即停在 first-chance 异常（类型精确过滤需从元数据解析异常类型名，
        // 列为 v2 增强——ExceptionBreakpointFilter.TypeName 保留为后续精确过滤的占位契约）
        var top = _core.ReadTopFrame(e.Thread);
        var typeToken = TryGetExceptionType(e);
        _core.PublishExceptionHit(e.Thread.Id, typeToken ?? _exceptionFilter.TypeName ?? "<unknown>", null, top);
        _core.PublishState(DebugSessionState.Stopped, $"exception {_exceptionFilter.TypeName ?? typeToken ?? "<unknown>"}");
        _holdContinue = true; // 停在异常点
    }

    private static string? TryGetExceptionType(Exception2CorDebugManagedCallbackEventArgs e)
    {
        // v1 尽力而为：从异常对象 ExactType.Class 拿类型 token；拿不到返回 null（调用方用过滤器类型兜底）
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
