using System.Reflection.Metadata;
using DotNetDebugger.Decompiler.Document;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
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
    private IReadOnlyList<DebugBreakpoint> _breakpoints = [];   // 会话断点快照（红点渲染数据源）
    private string _lastBpSignature = "";                       // 断点集合签名（变化才重推装饰，避免 500ms 轮询空转）
    private string _targetPath = "";
    private string? _state;          // 会话状态文本
    private string? _lastStopText;   // 最近停点
    private string? _ctrlMessage;    // 控制操作结果
    private bool _loading;
    private IReadOnlyList<DebugStackFrame> _stack = [];
    private IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>> _variables =
        new Dictionary<string, IReadOnlyList<DebugVariable>>();
    private IReadOnlyList<DebugThreadInfo> _threads = [];
    private CancellationTokenSource? _pollCts;
    private System.Timers.Timer? _pollTimer;
    private long _lastAgentRevision = -1;   // 已处理的 agent 上下文版本
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
        // 轮询会话状态（500ms）——SessionEventBuffer 快照刷新
        _pollCts = new CancellationTokenSource();
        _pollTimer = new System.Timers.Timer(500) { AutoReset = true };
        _pollTimer.Elapsed += async (_, _) => await PollStateAsync();
        _pollTimer.Start();

        // 订阅 agent 视图上下文：agent 反编译/浏览类型时自动联动（树加载程序集 + 右侧显示代码）
        // 宿主 --web 启动已 Configure 注入 AgentView；非 --web 页面不可达，防御性 try
        try
        {
            WebHostBootstrap.AgentView.Changed += OnAgentViewChanged;
        }
        catch { /* 未注入 AgentView（非 --web 场景）：跳过订阅 */ }
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

    private async Task PollStateAsync()
    {
        try { await InvokeAsync(RefreshState); } catch { }
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
        _lastStopText = info?.LastStop is { } stop ? $"{stop.Kind} @ {stop.TopFrame} (thread {stop.ThreadId})" : null;
        // 停点跃迁检测：状态变为 Stopped 时触发代码视图高亮（断点命中自动定位语句行）
        var transitionedToStopped = newState == "已停止" && _state != "已停止";
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
        // 断点快照轮询：agent 随时可能经 MCP 设/清断点，签名变化才重推装饰（避免 500ms 轮询空转 JS interop）
        try { _breakpoints = await _debug.GetBreakpointsAsync(); }
        catch { /* 会话刚断开等瞬时失败：沿用旧快照 */ }
        var bpSignature = string.Join(",", _breakpoints.Select(b => b.Id));
        var bpChanged = bpSignature != _lastBpSignature;
        StateHasChanged();
        if (bpChanged)
        {
            _lastBpSignature = bpSignature;
            await ApplyDecorationsAsync();
        }
        if (transitionedToStopped)
        {
            await ApplyDecorationsAsync();
            await SelectStopTypeAsync();   // 树跟随停点类型
            StateHasChanged();
        }
    }

    /// <summary>停点命中时左侧树跟随到命中方法叶子（当前文档即命中类型，方法按停点 token 精确匹配）。</summary>
    private async Task SelectStopTypeAsync()
    {
        if (_tree is null || _doc is not { IsSuccess: true } || _doc.Error is not null) return;
        var info = _debug.SessionInfo;
        if (info?.State != DebugSessionState.Stopped || info.LastStop?.TopFrame is not { } frame) return;
        // 当前文档程序集短名 == 停点模块名才跟随（agent 正在看命中模块的代码才展开树）
        if (!frame.ModuleName.Equals(Path.GetFileName(_doc.AssemblyPath), StringComparison.OrdinalIgnoreCase)) return;
        await _tree.SelectTypeAsync(_doc.AssemblyPath, _doc.TypeFullName, frame.MethodToken);
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
        await _viewer.SetDecorationsAsync([.. breakpointLines], currentLine);
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

    public void Dispose()
    {
        _pollCts?.Cancel();
        _pollTimer?.Dispose();
        try { WebHostBootstrap.AgentView.Changed -= OnAgentViewChanged; } catch { }
    }
}
