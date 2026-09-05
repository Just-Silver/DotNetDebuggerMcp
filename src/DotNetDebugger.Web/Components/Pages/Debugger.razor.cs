using System.Reflection.Metadata;
using DotNetDebugger.Decompiler.Document;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session;
using DotNetDebugger.Session.Models;
using DotNetDebugger.Web.Components.Debugger;
using DotNetDebugger.Web.Services;

namespace DotNetDebugger.Web.Components.Pages;

/// <summary>动态调试工作台（/debugger）：控制条 + 会话状态 + 左类型树/右代码视图 + 调试面板 + AgentView 订阅。</summary>
public partial class Debugger
{
    private CodeViewer? _viewer;
    private TypeTree? _tree;
    private readonly DebugViewService _debug = new();
    private SourceDocument? _doc;
    private IReadOnlyList<BreakpointSnapshot> _breakpoints = [];   // 会话断点快照（红点渲染数据源，断点事件推送）
    private SessionEventBuffer? _subscribedBuffer;                  // 当前订阅快照推送的事件缓冲（随活动会话切换）
    private string _lastBpSignature = "";                       // 断点集合签名（变化才重推装饰）
    private string _targetPath = "";
    private string? _state;          // 会话状态文本
    private string? _lastStopText;   // 最近停点
    private string? _ctrlMessage;    // 控制操作结果
    private bool _loading;
    private IReadOnlyList<DebugStackFrame> _stack = [];
    private IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>> _variables =
        new Dictionary<string, IReadOnlyList<DebugVariable>>();
    private IReadOnlyList<DebugThreadInfo> _threads = [];
    private long _lastAgentRevision = -1;   // 已处理的 agent 上下文版本
    private int _lastCursorToken;           // 光标联动已选中的方法 token（变化才动树，防扰动）
    private int _selectedMemberToken;       // 当前选中成员（树点叶子/光标联动/停点），编辑器行区间高亮用
    private long _lastStopKey;              // 最近停点身份（UtcTimestamp.Ticks；单步后状态不变，靠它感知新停点）
    /// <summary>演示目标 DebugTarget.exe（从仓库根上溯定位 tests/TestData）。</summary>
    private static string DemoAssembly
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DotNetDebuggerMcp.slnx"))) break;
                dir = dir.Parent;
            }
            var path = dir is null ? "" : Path.Combine(dir.FullName, "tests", "TestData", "DebugTarget.exe");
            return File.Exists(path) ? path : "";
        }
    }

    protected override void OnInitialized()
    {
        // 事件推送（替代轮询）：会话快照（状态/停点）与断点集合变化均经事件推到电路；
        // 栈/变量/线程仅在停点变化时读一次（对齐 dnSpyEx：停点期间值不变）。非 --web 场景防御性 try
        try
        {
            WebHostBootstrap.Manager.ActiveSessionChanged += OnActiveSessionChanged;
            SubscribeBuffer(WebHostBootstrap.Manager.Active);
        }
        catch { /* 未注入 Manager（非 --web 场景）：跳过 */ }

        // 订阅 agent 视图上下文：agent 反编译/浏览类型时自动联动（树加载程序集 + 右侧显示代码）
        try
        {
            WebHostBootstrap.AgentView.Changed += OnAgentViewChanged;
        }
        catch { /* 未注入 AgentView（非 --web 场景）：跳过订阅 */ }
    }

    /// <summary>活动会话切换（agent 经 MCP 启动/断开/替换均触发）：重订阅事件推送并同步快照。</summary>
    private void OnActiveSessionChanged(ActiveDebugSession? active)
    {
        _ = InvokeAsync(() =>
        {
            _lastStopKey = 0;   // 会话切换：停点基线复位，新会话首个停点必触发跟随
            SubscribeBuffer(active);
        });
    }

    /// <summary>订阅当前活动会话的事件缓冲（快照 + 断点）；订阅后立即同步一次当前快照（页面晚开不丢状态）。</summary>
    private void SubscribeBuffer(ActiveDebugSession? active)
    {
        var buffer = active?.Buffer;
        if (ReferenceEquals(_subscribedBuffer, buffer)) return;
        if (_subscribedBuffer is not null)
        {
            _subscribedBuffer.SnapshotChanged -= OnSnapshotChanged;
            _subscribedBuffer.BreakpointsChanged -= OnBreakpointsChanged;
        }
        _subscribedBuffer = buffer;
        if (_subscribedBuffer is not null)
        {
            _subscribedBuffer.SnapshotChanged += OnSnapshotChanged;
            _subscribedBuffer.BreakpointsChanged += OnBreakpointsChanged;
        }
        _ = InvokeAsync(RefreshState);
    }

    /// <summary>会话快照推送（状态/停点变化；Buffer 事件消费线程触发 → 转电路线程刷新）。</summary>
    private void OnSnapshotChanged(DebugSessionState state, StopContext? stop) => _ = InvokeAsync(RefreshState);

    /// <summary>断点集合推送（引擎命令泵设/删/清后发出；MCP agent 与 Web 同源）。</summary>
    private void OnBreakpointsChanged(IReadOnlyList<BreakpointSnapshot> breakpoints)
    {
        _ = InvokeAsync(async () =>
        {
            _breakpoints = breakpoints;
            var signature = string.Join(",", _breakpoints.Select(b => b.Id));
            if (signature == _lastBpSignature) return;
            _lastBpSignature = signature;
            await ApplyDecorationsAsync();
        });
    }

    /// <summary>TypeTree 首次渲染就绪回调：此时 _tree 已 ready。补同步 agent 上下文（页面晚开错过早期动作）。
    /// 页面冷启动为空树——跳转完全由 agent 驱动（真实场景：无 agent 动作不预加载任何类型）。</summary>
    private async Task OnTreeReady()
    {
        try
        {
            // 恢复源优先级：agent 上下文快照 > 最近查看记录（进程级 DocumentStore.LastView，跨电路存活）
            var snap = WebHostBootstrap.AgentView.Snapshot();
            string? asm = null, type = null;
            if (snap.Revision > _lastAgentRevision)
            {
                _lastAgentRevision = snap.Revision;
                if (snap.AssemblyPath is not null && snap.TypeFullName is not null)
                {
                    asm = snap.AssemblyPath;
                    type = snap.TypeFullName;
                }
            }
            if (asm is null && Store.LastView is { } view)
            {
                // 刷新/重连恢复：无 agent 上下文（如刷新前是人工浏览/演示流）时按最近查看还原（缓存命中秒回）
                asm = view.AssemblyPath;
                type = view.TypeFullName;
            }
            if (asm is not null && type is not null)
            {
                await _tree!.SelectTypeAsync(asm, type);
                await ShowTypeAsync(asm, type);
            }
        }
        catch (Exception ex) { MemoryLog.Write("AgentView", $"OnTreeReady 异常: {ex.Message}"); }
    }

    private void OnAgentViewChanged(DotNetDebugger.Web.Services.AgentViewSnapshot snap)
    {
        // 宿主工具线程触发 → 转到 UI 线程处理，避免跨线程渲染
        _ = InvokeAsync(async () =>
        {
            if (snap.AssemblyPath is null || snap.TypeFullName is null) return;
            if (snap.Revision <= _lastAgentRevision) return;
            _lastAgentRevision = snap.Revision;
            try
            {
                MemoryLog.Write("AgentView", $"rev={snap.Revision} → {Path.GetFileName(snap.AssemblyPath)}::{snap.TypeFullName} (_tree={( _tree is null ? "null" : "ok")}, _viewer={(_viewer is null ? "null" : "ok")})");
                bool treeOk = false;
                if (_tree is not null)
                    treeOk = await _tree.SelectTypeAsync(snap.AssemblyPath, snap.TypeFullName);
                MemoryLog.Write("AgentView", treeOk ? "树定位成功" : "树定位失败/跳过");
                await ShowTypeAsync(snap.AssemblyPath, snap.TypeFullName);
                _ctrlMessage = $"[agent] {Path.GetFileName(snap.AssemblyPath)}::{snap.TypeFullName}";
                await InvokeAsync(StateHasChanged);
            }
            catch (Exception ex) { MemoryLog.Write("AgentView", $"异常: {ex}"); }
        });
    }

    private async Task RefreshState()
    {
        var info = _debug.SessionInfo;
        var newState = info?.State switch
        {
            DebugSessionState.Stopped => "已停止",
            DebugSessionState.Running => "运行中",
            DebugSessionState.Exited => "已退出",
            DebugSessionState.Detached => "已断开",
            _ => null,
        };
        _lastStopText = info?.LastStop is { } stopCtx ? $"{stopCtx.Kind} @ {stopCtx.TopFrame} (thread {stopCtx.ThreadId})" : null;
        // 停点变化检测：单步/步入后状态保持「已停止」，状态跃迁检测会丢步进的新停点——
        // 改按停点身份（LastStop 时间戳）判断：状态为停且停点身份变化即触发跟随渲染
        var stopKey = info?.LastStop is { } s ? s.UtcTimestamp.Ticks : 0;
        var stopChanged = newState == "已停止" && stopKey != _lastStopKey;
        _lastStopKey = stopKey;
        _state = newState;
        // 面板数据：停时读栈/变量/线程（Engine 同步停态才可读）；非停清空
        if (newState == "已停止")
        {
            try
            {
                _stack = await _debug.GetStackAsync();
                _variables = await _debug.GetVariablesAsync();
                _threads = await _debug.GetThreadsAsync();
            }
            catch { /* 面板读取失败不阻断状态刷新 */ }
        }
        else
        {
            _stack = [];
            _variables = new Dictionary<string, IReadOnlyList<DebugVariable>>();
        }
        // 断点快照：事件推送常态下由 OnBreakpointsChanged 维护；此处兜底同步一次（页面晚开/订阅建立）
        await RefreshBreakpointsAsync();
        StateHasChanged();
        if (stopChanged)
        {
            await ApplyDecorationsAsync();   // 新停点：高亮 + 滚动定位（含单步/步入/异常）
            await SelectStopTypeAsync();     // 树跟随停点类型
            StateHasChanged();
        }
    }

    /// <summary>拉取当前断点快照；签名变化才重推装饰。</summary>
    private async Task RefreshBreakpointsAsync()
    {
        try { _breakpoints = await _debug.GetBreakpointsAsync(); }
        catch { return; }
        var signature = string.Join(",", _breakpoints.Select(b => b.Id));
        if (signature == _lastBpSignature) return;
        _lastBpSignature = signature;
        await ApplyDecorationsAsync();
    }

    /// <summary>停点跟随（无条件）：命中模块 == 当前文档 → 树内定位；否则经 Engine 模块路径查询反查磁盘文件、
    /// 由 token 解析类型，整页切到停点类型/方法（树 + 代码视图 + 装饰一并跟随，不再要求 agent 恰好在看命中模块）。</summary>
    private async Task SelectStopTypeAsync()
    {
        if (_tree is null) return;
        var info = _debug.SessionInfo;
        if (info?.State != DebugSessionState.Stopped || info.LastStop?.TopFrame is not { } frame) return;

        string assemblyPath, typeFullName;
        if (_doc is { IsSuccess: true } && frame.ModuleName.Equals(Path.GetFileName(_doc.AssemblyPath), StringComparison.OrdinalIgnoreCase))
        {
            assemblyPath = _doc.AssemblyPath;
            typeFullName = _doc.TypeFullName;
        }
        else
        {
            var modulePath = await _debug.GetModulePathAsync(frame.ModuleName);
            if (modulePath is null || !File.Exists(modulePath))
            {
                MemoryLog.Write("StopFollow", $"停点模块 {frame.ModuleName} 反查路径失败（模块未登记或文件不在磁盘）");
                return;
            }
            var type = FindTypeByToken(modulePath, frame.MethodToken);
            if (type is null)
            {
                MemoryLog.Write("StopFollow", $"停点 token 0x{frame.MethodToken:x8} 在 {Path.GetFileName(modulePath)} 中未定位到类型");
                return;
            }
            assemblyPath = modulePath;
            typeFullName = type;
            MemoryLog.Write("StopFollow", $"无条件跟随 → {Path.GetFileName(modulePath)}::{type} (token 0x{frame.MethodToken:x8})");
        }
        await _tree.SelectTypeAsync(assemblyPath, typeFullName, frame.MethodToken);
        await ShowTypeAsync(assemblyPath, typeFullName);
        _selectedMemberToken = frame.MethodToken;   // 停点方法整段高亮
        await ApplyDecorationsAsync();
    }

    private async Task LaunchTarget()
    {
        if (string.IsNullOrWhiteSpace(_targetPath)) _targetPath = DemoAssembly + " 3 8";
        _ctrlMessage = await _debug.LaunchAndAttachAsync(_targetPath.Trim());
        await RefreshState();
    }

    /// <summary>演示闭环：启动 DebugTarget + Work 断点 + continue（Work token 从目标 dll 元数据解析）。</summary>
    private async Task DemoBreakpoint()
    {
        if (string.IsNullOrWhiteSpace(_targetPath)) _targetPath = DemoAssembly + " 3 8";
        var parts = _targetPath.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!File.Exists(parts[0]))
        {
            _ctrlMessage = $"目标不存在：{parts[0]}";
            return;
        }
        _ctrlMessage = await _debug.LaunchAndAttachAsync(_targetPath.Trim());
        // 目标 dll（与 exe 同目录，托管代码所在）；自动反编译 Program 到编辑器——停点命中后可高亮 Work 内行
        var dll = Path.ChangeExtension(parts[0], ".dll");
        if (File.Exists(dll))
        {
            await ShowTypeAsync(dll, "DebugTarget.Program");
        }
        // 解析 Work token 并设断点、继续
        var token = FindMethodToken(dll, "Work");
        if (token <= 0) { _ctrlMessage += "；未找到 Work 方法 token"; return; }
        var module = Path.GetFileName(dll);
        _ctrlMessage += "；" + await _debug.SetBreakpointAsync(module, token, 0);
        _ctrlMessage += "；" + await _debug.ContinueAsync();
        await RefreshState();
        await ApplyDecorationsAsync();   // 进程停时才有效；未停则等待轮询跃迁触发
    }

    private async Task ContinueRun() { _ctrlMessage = await _debug.ContinueAsync(); await RefreshState(); await ApplyDecorationsAsync(); }
    private async Task StepOver() { _ctrlMessage = await _debug.StepAsync("over"); await RefreshState(); await ApplyDecorationsAsync(); }
    private async Task StepInto() { _ctrlMessage = await _debug.StepAsync("into"); await RefreshState(); await ApplyDecorationsAsync(); }

    private async Task Disconnect() { _ctrlMessage = await _debug.DisconnectAsync(); _state = null; _lastStopText = null; }

    /// <summary>反编译并显示指定类型到代码视图（树点击 / agent 上下文 / 演示闭环共用入口）。
    /// 统一入口：先确保程序集已进左侧树（LoadAssembly 幂等），再反编译显示。</summary>
    private async Task ShowTypeAsync(string assemblyPath, string typeFullName)
    {
        if (_viewer is null) return;
        // 程序集进树（幂等；非程序集/不存在返回 false，不影响反编译尝试）
        if (_tree is not null) _tree.LoadAssembly(assemblyPath);
        _loading = true;
        _lastCursorToken = 0;        // 换文档：token 空间不同，光标联动基线复位
        _selectedMemberToken = 0;    // 换文档：旧 token 在新文档无意义（跨程序集 token 空间重叠，防误高亮）
        await InvokeAsync(StateHasChanged);
        try
        {
            _doc = await Store.GetOrLoadAsync(assemblyPath.Trim(), typeFullName.Trim());
            if (_doc.IsSuccess)
            {
                await _viewer.SetValueAsync(_doc.Text);
                await ApplyDecorationsAsync();
            }
        }
        finally { _loading = false; }
    }

    /// <summary>树加载了某程序集根：同步到树（当前仅触发 UI 刷新；后续 agent 上下文联动可在此高亮）。</summary>
    private Task OnAssemblyLoaded(string assemblyPath)
    {
        return Task.CompletedTask;
    }

    /// <summary>双向联动（树→编辑器）：点成员叶子切到所属类型文档、滚动定位到成员首行，
    /// 并以行区间背景高亮整个成员。</summary>
    private async Task ShowMemberAsync(string assemblyPath, string typeFullName, int methodToken)
    {
        await ShowTypeAsync(assemblyPath, typeFullName);
        if (methodToken <= 0 || _doc is not { IsSuccess: true } || _doc.Error is not null) return;
        _selectedMemberToken = methodToken;
        await ApplyDecorationsAsync();
        if (DocumentStore.GetMethodFirstLine(_doc, methodToken) is { } line)
        {
            await _viewer!.RevealLineAsync(line);
        }
    }

    /// <summary>双向联动（编辑器→树）：光标所在行定位方法叶子。行不在任何方法区间时取其后最近的方法；
    /// token 未变化则跳过（幂等，防光标扫过扰动）。setValue 后的首个程序性事件由桥侧抑制。</summary>
    private async Task OnCursorLineChanged(int line)
    {
        if (_tree is null || _doc is not { IsSuccess: true } || _doc.Error is not null) return;
        if (DocumentStore.FindMethodTokenAtLine(_doc, line) is not { } token || token <= 0) return;
        if (token == _lastCursorToken) return;
        _lastCursorToken = token;
        _selectedMemberToken = token;
        await ApplyDecorationsAsync();   // 选中成员行区间高亮跟随光标
        await _tree.SelectTypeAsync(_doc.AssemblyPath, _doc.TypeFullName, token);
    }

    /// <summary>glyph 区（断点红点槽）点击切换断点：该行已是断点则移除，否则在语句落点设置。
    /// 红点由断点事件推送自动刷新，无需手动重推。</summary>
    private async Task ToggleBreakpointAtLineAsync(int line)
    {
        if (_debug.Active is null)
        {
            _ctrlMessage = "无活动调试会话，先启动/附加目标再设断点。";
            await InvokeAsync(StateHasChanged);
            return;
        }
        if (_doc is not { IsSuccess: true } || _doc.Error is not null) return;
        if (DocumentStore.GetBreakpointTargetAtLine(_doc, line) is not { } target) return;
        var module = Path.GetFileName(_doc.AssemblyPath);
        var existing = _breakpoints.FirstOrDefault(b =>
            b.ModuleName.Equals(module, StringComparison.OrdinalIgnoreCase)
            && b.MethodToken == target.MethodToken && b.IlOffset == target.IlOffset);
        _ctrlMessage = existing is not null
            ? await _debug.RemoveBreakpointAsync(existing.Id)
            : await _debug.SetBreakpointAsync(module, target.MethodToken, target.IlOffset);
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>统一装饰：断点红点（模块匹配当前文档的断点经 IL→行映射）+ 停点当前行高亮，全量重推；
    /// 停点行存在时滚动定位。文档换页/断点增删/停点跃迁共用。</summary>
    private async Task ApplyDecorationsAsync()
    {
        if (_viewer is null || _doc is null || !_doc.IsSuccess) return;
        var assemblyName = Path.GetFileName(_doc.AssemblyPath);
        var info = _debug.SessionInfo;
        int? currentLine = null;
        if (info?.State == DebugSessionState.Stopped && info.LastStop?.TopFrame is { } frame
            && frame.ModuleName.Equals(assemblyName, StringComparison.OrdinalIgnoreCase))
        {
            currentLine = DocumentStore.GetStopLine(_doc, frame.MethodToken, frame.IlOffset);
        }
        var breakpointLines = new List<int>();
        foreach (var bp in _breakpoints)
        {
            if (!bp.ModuleName.Equals(assemblyName, StringComparison.OrdinalIgnoreCase)) continue;
            if (DocumentStore.GetStopLine(_doc, bp.MethodToken, bp.IlOffset) is { } line) breakpointLines.Add(line);
        }
        await _viewer.SetDecorationsAsync([.. breakpointLines], currentLine,
            DocumentStore.GetMethodLineRange(_doc, _selectedMemberToken));
        if (currentLine is not null) await _viewer.RevealLineAsync(currentLine.Value);
    }

    private static int FindMethodToken(string dll, string methodName)
    {
        if (!File.Exists(dll)) return 0;
        using var fs = File.OpenRead(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                var md = mr.GetMethodDefinition(mh);
                if (mr.GetString(md.Name) == methodName)
                    return System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }

    /// <summary>按方法 token 反查类型全名（停点无条件跟随用；读模块元数据，无命中返回 null）。</summary>
    private static string? FindTypeByToken(string dllPath, int methodToken)
    {
        if (!File.Exists(dllPath)) return null;
        using var fs = File.OpenRead(dllPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                if (System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh) == methodToken)
                    return FullTypeName(mr, th);
            }
        }
        return null;
    }

    /// <summary>TypeDefinition 全名（命名空间 + 嵌套链，嵌套用 + 连接）。</summary>
    private static string FullTypeName(System.Reflection.Metadata.MetadataReader mr, TypeDefinitionHandle th)
    {
        var td = mr.GetTypeDefinition(th);
        var names = new List<string> { mr.GetString(td.Name) };
        var decl = td.GetDeclaringType();
        while (!decl.IsNil)
        {
            var parent = mr.GetTypeDefinition(decl);
            names.Add(mr.GetString(parent.Name));
            decl = parent.GetDeclaringType();
        }
        names.Reverse();
        var ns = mr.GetString(td.Namespace);
        return (ns.Length > 0 ? ns + "." : "") + string.Join("+", names);
    }

    public void Dispose()
    {
        try
        {
            WebHostBootstrap.Manager.ActiveSessionChanged -= OnActiveSessionChanged;
            if (_subscribedBuffer is not null)
            {
                _subscribedBuffer.SnapshotChanged -= OnSnapshotChanged;
                _subscribedBuffer.BreakpointsChanged -= OnBreakpointsChanged;
            }
        }
        catch { /* 非 --web 场景 */ }
        try { WebHostBootstrap.AgentView.Changed -= OnAgentViewChanged; } catch { }
    }
}
