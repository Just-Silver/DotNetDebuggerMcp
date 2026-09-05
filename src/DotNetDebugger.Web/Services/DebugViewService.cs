using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session;
using DotNetDebugger.Session.Models;

namespace DotNetDebugger.Web.Services;

/// <summary>
/// Web 调试会话服务：封装经 WebHostBootstrap.Manager（宿主注入的共享 DebugSessionManager，与 MCP 工具同会话）
/// 的调试命令与状态查询。供 Debugger 页面组件调用——控制命令返回中文结果，状态读 SessionEventBuffer 快照。
/// </summary>
public sealed class DebugViewService
{
    /// <summary>当前活动会话（无则 null）。</summary>
    public ActiveDebugSession? Active => WebHostBootstrap.Manager.Active;

    /// <summary>会话状态快照（无活动会话返回 null）。</summary>
    public DebugSessionInfo? SessionInfo => WebHostBootstrap.Manager.GetInfo();

    /// <summary>启动目标并附加（页面人工调试入口；目标需有启动延迟供 attach）。</summary>
    public async Task<string> LaunchAndAttachAsync(string commandLine, CancellationToken ct = default)
    {
        try
        {
            var active = await WebHostBootstrap.Manager.LaunchAndAttachAsync(commandLine, ct);
            return $"已启动并附加：{commandLine}（状态 {active.Buffer.CurrentState}）";
        }
        catch (Exception ex)
        {
            return $"启动调试失败：{ex.Message}";
        }
    }

    /// <summary>断开会话（目标进程继续独立运行）。</summary>
    public async Task<string> DisconnectAsync(CancellationToken ct = default)
    {
        try
        {
            await WebHostBootstrap.Manager.CloseAsync(ct);
            return "已断开调试会话。";
        }
        catch (Exception ex)
        {
            return $"断开失败：{ex.Message}";
        }
    }

    /// <summary>设置断点（模块+方法 token+IL offset）。</summary>
    public async Task<string> SetBreakpointAsync(string moduleName, int methodToken, int ilOffset, CancellationToken ct = default)
    {
        var active = Active;
        if (active is null) return "无活动调试会话，先启动/附加目标。";
        try
        {
            var bp = await active.Session.SetBreakpointAsync(moduleName, methodToken, ilOffset, ct);
            return $"断点已设: id={bp.Id} 位置={bp}";
        }
        catch (Exception ex)
        {
            return $"设置断点失败：{ex.Message}";
        }
    }

    /// <summary>移除断点（glyph 点击切换用）。</summary>
    public async Task<string> RemoveBreakpointAsync(int id, CancellationToken ct = default)
    {
        var active = Active;
        if (active is null) return "无活动调试会话。";
        try
        {
            return await active.Session.RemoveBreakpointAsync(id, ct)
                ? $"断点 {id} 已移除。"
                : $"断点 {id} 不存在。";
        }
        catch (Exception ex)
        {
            return $"移除断点失败：{ex.Message}";
        }
    }

    /// <summary>当前会话断点快照（无活动会话返回空；红点渲染数据源）。常态经 BreakpointsChanged 事件推送，此为兜底查询。</summary>
    public async Task<IReadOnlyList<BreakpointSnapshot>> GetBreakpointsAsync(CancellationToken ct = default)
    {
        var active = Active;
        if (active is null) return [];
        try
        {
            var list = await active.Session.GetBreakpointsAsync(ct);
            return list.Select(b => new BreakpointSnapshot(b.Id, b.ModuleName, b.MethodToken, b.IlOffset)).ToList();
        }
        catch { return []; }
    }

    /// <summary>模块短名（或全路径）→ 模块全路径（停点无条件跟随用；无活动会话/未登记返回 null）。</summary>
    public async Task<string?> GetModulePathAsync(string moduleName, CancellationToken ct = default)
    {
        var active = Active;
        if (active is null) return null;
        try { return await active.Session.GetModulePathAsync(moduleName, ct); }
        catch { return null; }
    }

    /// <summary>继续执行（进程运行至下个断点/退出）。</summary>
    public async Task<string> ContinueAsync(CancellationToken ct = default)
    {
        var active = Active;
        if (active is null) return "无活动调试会话。";
        try { await active.Session.ContinueAsync(ct); return "已继续执行。"; }
        catch (Exception ex) { return $"继续执行失败：{ex.Message}"; }
    }

    /// <summary>单步（into/over/out）。</summary>
    public async Task<string> StepAsync(string stepType, CancellationToken ct = default)
    {
        var active = Active;
        if (active is null) return "无活动调试会话。";
        if (active.Buffer.CurrentState != DebugSessionState.Stopped)
            return "进程未停在断点（当前非 Stopped）。先继续运行至停点。";
        try
        {
            switch (stepType.Trim().ToLowerInvariant())
            {
                case "into": await active.Session.StepIntoAsync(ct); break;
                case "out": await active.Session.StepOutAsync(ct); break;
                default: await active.Session.StepOverAsync(ct); break;
            }
            return $"已单步 {stepType}。";
        }
        catch (Exception ex) { return $"单步失败：{ex.Message}"; }
    }

    /// <summary>停点线程的调用栈（进程停时；未停返回空）。</summary>
    public async Task<IReadOnlyList<DebugStackFrame>> GetStackAsync(CancellationToken ct = default)
    {
        var active = Active;
        if (active is null || active.Buffer.CurrentState != DebugSessionState.Stopped) return [];
        var tid = active.Buffer.StoppedThreadId;
        if (tid <= 0) return [];
        try { return await active.Session.GetStackFramesAsync(tid, ct); }
        catch { return []; }
    }

    /// <summary>停点线程的局部变量/参数（进程停时）。</summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>> GetVariablesAsync(CancellationToken ct = default)
    {
        var active = Active;
        if (active is null || active.Buffer.CurrentState != DebugSessionState.Stopped) return new Dictionary<string, IReadOnlyList<DebugVariable>>();
        var tid = active.Buffer.StoppedThreadId;
        if (tid <= 0) return new Dictionary<string, IReadOnlyList<DebugVariable>>();
        try { return await active.Session.GetVariablesAsync(tid, ct); }
        catch { return new Dictionary<string, IReadOnlyList<DebugVariable>>(); }
    }

    /// <summary>托管线程列表（任意状态可读）。</summary>
    public async Task<IReadOnlyList<DebugThreadInfo>> GetThreadsAsync(CancellationToken ct = default)
    {
        var active = Active;
        if (active is null) return [];
        try { return await active.Session.GetThreadsAsync(ct); }
        catch { return []; }
    }
}
