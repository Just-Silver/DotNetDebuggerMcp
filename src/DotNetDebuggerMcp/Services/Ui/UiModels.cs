using FlaUI.Core.AutomationElements;

using System.Runtime.Versioning;

namespace DotNetDebuggerMcp.Services.Ui;

// ===== U1A UI 自动化模型（组件共享，集中定义） =====

/// <summary>ui_find 命中控件清单行：Index 供 ui_action/ui_input/ui_get 复用；Patterns 为能力清单（逗号串，只列 IsSupported），Semantic 为同名成员语义候选（可为 null）。</summary>
internal sealed record UiElementInfo(
    int Index,
    string Name,
    string Type,
    string AutoId,
    string Rect,
    string Patterns,
    string? Semantic);

/// <summary>ui_action/ui_input 结果：Message 注明实际动作与命中的 pattern（「已 {verb}（{pattern}）」）。</summary>
internal sealed record UiActionResult(bool Ok, string Message);

/// <summary>
/// ui_get 结果：Value=展示值、Raw=原始值兜底、ControlName=目标控件 Name（空则 AutomationId，供输出层
/// 经 DB1 <c>SensitiveValueRedactor.Redact</c> 脱敏）。三值均**未脱敏**——脱敏由工具/verify 输出层做。
/// </summary>
internal sealed record UiStateResult(string Value, string? Raw = null, string ControlName = "");

/// <summary>ui_wait 结果：Outcome ∈ 出现/已变化/超时/失败。</summary>
internal sealed record UiWaitResult(string Outcome, string Message);

/// <summary>UIA 失败/目标状态不满足的中文提示（工具层 catch 转返回文本，不抛给 MCP）。</summary>
internal sealed class UiException : Exception
{
    public UiException(string message) : base(message) { }
}

/// <summary>控件 UIA pattern 能力快照（IsSupported 布尔集合）；纯数据，供 dispatcher/stateReader 的纯 Choose 决策与单测。</summary>
[SupportedOSPlatform("windows7.0")]
internal readonly record struct UiPatternCapabilities(
    bool Invoke,
    bool Toggle,
    bool SelectionItem,
    bool ExpandCollapse,
    bool Value,
    bool RangeValue,
    bool Scroll,
    bool ScrollItem,
    bool Window,
    bool Legacy)
{
    /// <summary>按 ui_find 顺序列出已支持 pattern（逗号分隔，空=无）。</summary>
    public string Describe()
    {
        var parts = new List<string>(10);
        if (Invoke) parts.Add("invoke");
        if (Toggle) parts.Add("toggle");
        if (SelectionItem) parts.Add("selectionitem");
        if (ExpandCollapse) parts.Add("expandcollapse");
        if (Value) parts.Add("value");
        if (RangeValue) parts.Add("rangevalue");
        if (Scroll) parts.Add("scroll");
        if (ScrollItem) parts.Add("scrollitem");
        if (Window) parts.Add("window");
        if (Legacy) parts.Add("legacy");
        return string.Join(",", parts);
    }

    /// <summary>从 live 元素探测能力快照（每项 IsSupported 独立容错——provider 偶发不支持某 pattern 不应中断整次遍历）。</summary>
    public static UiPatternCapabilities Probe(AutomationElement element) => new(
        Invoke: Safe(() => element.Patterns.Invoke.IsSupported),
        Toggle: Safe(() => element.Patterns.Toggle.IsSupported),
        SelectionItem: Safe(() => element.Patterns.SelectionItem.IsSupported),
        ExpandCollapse: Safe(() => element.Patterns.ExpandCollapse.IsSupported),
        Value: Safe(() => element.Patterns.Value.IsSupported),
        RangeValue: Safe(() => element.Patterns.RangeValue.IsSupported),
        Scroll: Safe(() => element.Patterns.Scroll.IsSupported),
        ScrollItem: Safe(() => element.Patterns.ScrollItem.IsSupported),
        Window: Safe(() => element.Patterns.Window.IsSupported),
        Legacy: Safe(() => element.Patterns.LegacyIAccessible.IsSupported));

    private static bool Safe(Func<bool> f)
    {
        try { return f(); }
        catch { return false; }
    }
}

/// <summary>ui_action 分派到的 UIA pattern 动作品类（Execute 据此调用具体 API）。</summary>
internal enum UiActionKind
{
    Invoke,
    Toggle,
    SelectionItem,
    ExpandCollapse,
    Scroll,
    ScrollItem,
    Window,
    LegacyDefaultAction,
    LegacySelect,
    Focus,
}

/// <summary>ui_action 的纯决策结果（Choose 产出，Execute 消费）；PatternName 为展示用 pattern 名。</summary>
internal sealed record ChosenAction(
    string Verb,
    UiActionKind Kind,
    string PatternName,
    string Direction,
    int Lines,
    string WindowState);

/// <summary>ui_input 分派到的写值 pattern 品类。</summary>
internal enum UiInputKind
{
    Value,
    RangeValue,
    LegacySetValue,
}

/// <summary>ui_input 的纯决策结果。</summary>
internal sealed record ChosenInput(UiInputKind Kind, string PatternName);

/// <summary>ui_get 读取的 pattern 属性品类。</summary>
internal enum UiStateKind
{
    Value,
    LegacyValue,
    Name,
    Toggle,
    Selected,
    ExpandState,
    RangeValue,
    Enabled,
    Offscreen,
    Rect,
    HelpText,
}

/// <summary>ui_get 的纯决策结果（Choose 产出，Execute 消费）。</summary>
internal sealed record ChosenRead(string What, UiStateKind Kind, string PatternName);
