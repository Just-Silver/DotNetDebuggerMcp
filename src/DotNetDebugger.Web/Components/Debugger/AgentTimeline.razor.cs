using DotNetDebugger.Session;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using DotNetDebugger.Web.Services;

namespace DotNetDebugger.Web.Components.Debugger;

/// <summary>agent 轨迹时间线：AgentActionLog 事件推送（零轮询），新动作到达自动滚到最新。</summary>
public partial class AgentTimeline
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private IReadOnlyList<AgentAction> _actions = [];
    private long _count;
    private string _scrollId = "agentTimeline_" + Guid.NewGuid().ToString("N");

    protected override void OnInitialized()
    {
        // 零轮询铁律：订阅轨迹变化事件推送，仅初始同步一次快照（页面晚开不丢历史）
        try
        {
            WebHostBootstrap.Manager.Actions.Changed += OnActionsChanged;
        }
        catch { /* 非 --web 场景：组件不可达，防御 */ }
        Refresh();
    }

    private void OnActionsChanged() => _ = InvokeAsync(OnActionsChangedAsync);

    private async Task OnActionsChangedAsync()
    {
        Refresh();
        // 新动作到达：滚到时间线底部（最新在后，符合轨迹阅读顺序）
        try
        {
            await Js.InvokeVoidAsync("eval",
                $"var el = document.getElementById('{_scrollId}'); if (el) el.scrollTop = el.scrollHeight;");
        }
        catch (Exception ex) when (ex is JSDisconnectedException or ObjectDisposedException) { }
    }

    private void Refresh()
    {
        try
        {
            _actions = WebHostBootstrap.Manager.Actions.Snapshot();
            _count = _actions.Count;
        }
        catch { /* 非 --web：保持空 */ }
        StateHasChanged();
    }

    public void Dispose()
    {
        try { WebHostBootstrap.Manager.Actions.Changed -= OnActionsChanged; } catch { }
    }
}
