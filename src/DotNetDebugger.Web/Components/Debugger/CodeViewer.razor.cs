using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace DotNetDebugger.Web.Components.Debugger;

/// <summary>Monaco 编辑器封装（自研最小互操作桥见 CodeViewer.razor.js；容器高度由父级 flex 链确定）。</summary>
public partial class CodeViewer : IAsyncDisposable
{
    [Inject] private IJSRuntime Js { get; set; } = default!;

    private string _editorId = "codeviewer_" + Guid.NewGuid().ToString("N");
    private DotNetObjectReference<CodeViewer>? _self;
    private string[] _currentDecorationIds = [];   // Monaco 装饰 id 是 string（deltaDecorations 返回 string[]）

    [Parameter] public string? Language { get; set; } = "csharp";

    /// <summary>编辑器光标行变化回调（行号，1 起）。父组件接此做 树↔编辑器 双向联动。</summary>
    [Parameter] public EventCallback<int> CursorLineChanged { get; set; }

    /// <summary>glyph 区（断点红点槽）点击回调（行号，1 起）。父组件接此设/删断点。</summary>
    [Parameter] public EventCallback<int> GlyphClicked { get; set; }

    /// <summary>电路断开（浏览器刷新/关页）或组件已释放时 JS 互操作必然失败——按官方指引静默吞掉，
    /// 避免轮询/事件回调路径刷 JSDisconnectedException 日志。其它异常照常上抛。</summary>
    private static bool IsCircuitGone(Exception ex) =>
        ex is JSDisconnectedException or ObjectDisposedException;

    /// <summary>设编辑器文本（换文档；内部清装饰）。</summary>
    public async Task SetValueAsync(string text)
    {
        try { await Js.InvokeVoidAsync("dotnetDebuggerMonaco.setValue", _editorId, text); }
        catch (Exception ex) when (IsCircuitGone(ex)) { }
    }

    /// <summary>更新断点行 + 当前执行行 + 选中成员行区间（全量重推装饰）。</summary>
    public async Task SetDecorationsAsync(int[] breakpointLines, int? currentLine, (int Start, int End)? memberRange = null)
    {
        try
        {
            _currentDecorationIds = await Js.InvokeAsync<string[]>(
                "dotnetDebuggerMonaco.deltaDecorations", _editorId, _currentDecorationIds,
                breakpointLines, currentLine ?? 0, memberRange?.Start ?? 0, memberRange?.End ?? 0) ?? [];
        }
        catch (Exception ex) when (IsCircuitGone(ex)) { }
    }

    /// <summary>滚动定位到行。</summary>
    public async Task RevealLineAsync(int line)
    {
        try { await Js.InvokeVoidAsync("dotnetDebuggerMonaco.revealLineInCenter", _editorId, line); }
        catch (Exception ex) when (IsCircuitGone(ex)) { }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                await Js.InvokeVoidAsync("dotnetDebuggerMonaco.create", _editorId, Language);
                // 光标行回调：编辑器可能仍在异步重试创建，桥会暂存引用、创建完成时挂钩
                _self ??= DotNetObjectReference.Create(this);
                await Js.InvokeVoidAsync("dotnetDebuggerMonaco.setCursorCallback", _editorId, _self);
            }
            catch (Exception ex) when (IsCircuitGone(ex)) { }
        }
    }

    /// <summary>JS 桥回推光标行（CodeViewer.razor.js onDidChangeCursorPosition）。</summary>
    [JSInvokable]
    public async Task OnCursorLine(int line) => await CursorLineChanged.InvokeAsync(line);

    /// <summary>JS 桥回推 glyph 区点击（CodeViewer.razor.js onMouseDown GUTTER_GLYPH_MARGIN）。</summary>
    [JSInvokable]
    public async Task OnGlyphClick(int line) => await GlyphClicked.InvokeAsync(line);

    public async ValueTask DisposeAsync()
    {
        try { await Js.InvokeVoidAsync("dotnetDebuggerMonaco.dispose", _editorId); } catch { }
        _self?.Dispose();
    }
}
