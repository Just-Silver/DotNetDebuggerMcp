using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;

using System.Runtime.Versioning;

namespace DotNetDebuggerMcp.Services.Ui;

/// <summary>
/// U1A 事件化等待（spec §10）：对目标窗口订阅 StructureChanged（控件出现/消失）+ PropertyChanged（Name/Value，
/// 文本变化），命中即唤醒（回调只判位 + TrySetResult，不重入 UIA 大操作）；无事件时按 200ms 轮询兜底。整体
/// 由 facade 在 gate 内调用（订阅/退订/探测均在 gate 内串行）。
/// </summary>
[SupportedOSPlatform("windows7.0")]
internal sealed class UiEventWaiter
{
    /// <summary>等待结果：Hit=命中、EventSeen=期间是否收到事件、Result=命中时 probe 返回的描述（超时=null）。</summary>
    public readonly record struct WaitOutcome(bool Hit, bool EventSeen, string? Result);

    /// <summary>
    /// 等到 <paramref name="probe"/> 返回非 null 或超时。<paramref name="probe"/> 返回命中描述文本，未命中返回 null。
    /// </summary>
    public async Task<WaitOutcome> WaitAsync(AutomationElement window, Func<string?> probe, int timeoutSeconds, CancellationToken ct)
    {
        // 先探测一次（已在树里/已满足条件时无需等事件）。
        var immediate = SafeProbe(probe);
        if (immediate is not null) return new WaitOutcome(true, false, immediate);

        var eventSeen = false;
        var signal = NewSignal();
        StructureChangedEventHandlerBase? structure = null;
        PropertyChangedEventHandlerBase? property = null;
        try
        {
            try
            {
                structure = window.RegisterStructureChangedEvent(TreeScope.Subtree,
                    (_, _, _) => signal.TrySetResult(true));
            }
            catch { /* provider 不支持事件：走轮询 */ }

            try
            {
                var ids = new[]
                {
                    window.Automation.PropertyLibrary.Element.Name,
                    window.Automation.PropertyLibrary.Value.Value,
                };
                property = window.RegisterPropertyChangedEvent(TreeScope.Subtree,
                    (_, _, _) => signal.TrySetResult(true), ids);
            }
            catch { /* 同上 */ }

            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 300));
            while (true)
            {
                var hit = SafeProbe(probe);
                if (hit is not null) return new WaitOutcome(true, eventSeen, hit);

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) return new WaitOutcome(false, eventSeen, null);

                var wait = (int)Math.Min(200, remaining.TotalMilliseconds);
                var wake = await Task.WhenAny(signal.Task, Task.Delay(wait, ct)).ConfigureAwait(false);
                if (wake == signal.Task)
                {
                    eventSeen = true;
                    signal = NewSignal(); // 重置：事件可能与本条件无关，继续等
                }
            }
        }
        finally
        {
            try { property?.Dispose(); } catch { }
            try { structure?.Dispose(); } catch { }
        }
    }

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static string? SafeProbe(Func<string?> probe)
    {
        try { return probe(); }
        catch { return null; } // 单次探测失败（UIA 瞬断/窗口重建）视作未命中，下轮再试
    }
}
