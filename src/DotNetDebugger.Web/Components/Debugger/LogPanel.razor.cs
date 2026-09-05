using DotNetDebugger.Web.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DotNetDebugger.Web.Components.Debugger;

/// <summary>内存日志面板（联调诊断）：环形缓冲最近条目，1s 自动刷新，支持一键复制。</summary>
public partial class LogPanel : IDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private IReadOnlyList<MemoryLogEntry> _logs = [];
    private long _count;

    protected override void OnInitialized()
    {
        // 零轮询铁律：订阅 MemoryLog 变化事件推送（写入/清空即刷），仅初始同步一次快照
        MemoryLog.Changed += OnLogChanged;
        Refresh();
    }

    private void OnLogChanged() => _ = InvokeAsync(Refresh);

    private void Refresh()
    {
        _logs = MemoryLog.Snapshot();
        _count = MemoryLog.Count;
        StateHasChanged();
    }

    private void Clear()
    {
        MemoryLog.Clear();
        Refresh();
    }

    /// <summary>一键复制全部日志文本到剪贴板（联调时贴给 agent/记录用）。</summary>
    private async Task CopyAll()
    {
        var text = string.Join("\n", MemoryLog.Snapshot()
            .Select(e => $"[{e.UtcTimestamp:HH:mm:ss.fff}] [{e.Source}] {e.Message}"));
        try { await Js.InvokeVoidAsync("navigator.clipboard.writeText", text); }
        catch { /* 剪贴板不可用（权限/非安全上下文）：静默 */ }
    }

    public void Dispose()
    {
        MemoryLog.Changed -= OnLogChanged;
    }
}
