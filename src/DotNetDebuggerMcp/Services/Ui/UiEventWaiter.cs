using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;

using System.Runtime.Versioning;

namespace DotNetDebuggerMcp.Services.Ui;

/// <summary>
/// U1A 事件订阅（spec §10）：对目标窗口订阅 StructureChanged（控件出现/消失）+ PropertyChanged（Name/Value，
/// 文本变化），任一事件触发即回调。**订阅/退订由 facade 在 gate 内调用**（与探测串行、无竞态）；回调只
/// <c>TrySetResult</c>，不重入 UIA 大操作。等待循环与探测由 facade 负责（每轮取放 gate），故本类不长时间持有 gate。
/// </summary>
[SupportedOSPlatform("windows7.0")]
internal sealed class UiEventWaiter
{
    /// <summary>
    /// 在给定窗口上注册结构/属性变化事件；任一事件触发即调用 <paramref name="onSignal"/>（应只做 TrySetResult）。
    /// 返回可释放句柄（Dispose 退订两个 handler，幂等；provider 不支持事件时仍返回有效句柄，由轮询兜底）。
    /// </summary>
    public IDisposable Subscribe(AutomationElement window, Action onSignal)
    {
        StructureChangedEventHandlerBase? structure = null;
        PropertyChangedEventHandlerBase? property = null;
        try
        {
            try
            {
                structure = window.RegisterStructureChangedEvent(TreeScope.Subtree, (_, _, _) => onSignal());
            }
            catch { /* provider 不支持事件：走轮询 */ }

            try
            {
                var ids = new[]
                {
                    window.Automation.PropertyLibrary.Element.Name,
                    window.Automation.PropertyLibrary.Value.Value,
                };
                property = window.RegisterPropertyChangedEvent(TreeScope.Subtree, (_, _, _) => onSignal(), ids);
            }
            catch { /* 同上 */ }
        }
        catch
        {
            try { property?.Dispose(); } catch { }
            try { structure?.Dispose(); } catch { }
            throw;
        }

        return new Subscription(structure, property);
    }

    private sealed class Subscription(StructureChangedEventHandlerBase? structure, PropertyChangedEventHandlerBase? property) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { property?.Dispose(); } catch { }
            try { structure?.Dispose(); } catch { }
        }
    }
}
