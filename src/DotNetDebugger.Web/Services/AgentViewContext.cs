namespace DotNetDebugger.Web.Services;

/// <summary>
/// agent 当前查看/操作上下文的不可变快照（供 Web 监视器展示与联动）。
/// </summary>
/// <param name="AssemblyPath">agent 当前反编译/浏览的程序集绝对路径；无则 null。</param>
/// <param name="TypeFullName">agent 当前查看的类型全名（如 ILSpyMcp.Samples.BigClass）；无则 null。</param>
/// <param name="MemberName">agent 当前查看的成员名（decompile_member 时）；无则 null。</param>
/// <param name="Revision">写入序号，每次 Update 自增；订阅者据此检测变化。</param>
public sealed record AgentViewSnapshot(
    string? AssemblyPath, string? TypeFullName, string? MemberName, long Revision);

/// <summary>
/// 宿主 → Web 的「agent 正在看什么」共享可观察状态（P4 监视器核心链路）。
///
/// 宿主在 MCP 工具执行时调用 <see cref="Update"/> 写入 agent 当前反编译/调试上下文；
/// Web 组件订阅 <see cref="Changed"/>，上下文变化时自动联动左侧树展开、右侧代码切换。
/// 线程安全（宿主工具并发调用与 Blazor 渲染线程都可能访问）。
/// </summary>
public sealed class AgentViewContext
{
    private readonly object _gate = new();
    private string? _assembly;
    private string? _type;
    private string? _member;
    private long _revision;

    /// <summary>上下文变化事件（宿主线程 Update 后触发；订阅者应自行 InvokeAsync 到 UI 线程）。</summary>
    public event Action<AgentViewSnapshot>? Changed;

    /// <summary>当前上下文快照（无写入时为初始空快照，Revision=0）。</summary>
    public AgentViewSnapshot Snapshot()
    {
        lock (_gate) return new AgentViewSnapshot(_assembly, _type, _member, _revision);
    }

    /// <summary>
    /// 更新 agent 当前上下文。仅当 assembly/type/member 任一变化时才推进 Revision 并触发 Changed，
    /// 避免同一类型重复反编译（缓存命中）反复扰动 Web。
    /// </summary>
    public void Update(string? assembly, string? type, string? member = null)
    {
        AgentViewSnapshot snap;
        bool changed;
        lock (_gate)
        {
            changed = !string.Equals(_assembly, assembly, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_type, type, StringComparison.Ordinal)
                || !string.Equals(_member, member, StringComparison.Ordinal);
            if (!changed) return;
            _assembly = assembly;
            _type = type;
            _member = member;
            _revision++;
            snap = new AgentViewSnapshot(_assembly, _type, _member, _revision);
        }
        Changed?.Invoke(snap);
    }

    /// <summary>清空上下文（断开/新会话等场景）。Revision 推进触发 Changed。</summary>
    public void Clear()
    {
        AgentViewSnapshot snap;
        lock (_gate)
        {
            if (_assembly is null && _type is null && _member is null) return;
            _assembly = null;
            _type = null;
            _member = null;
            _revision++;
            snap = new AgentViewSnapshot(null, null, null, _revision);
        }
        Changed?.Invoke(snap);
    }
}
