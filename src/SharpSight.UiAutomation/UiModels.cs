using FlaUI.Core.AutomationElements;

using System.Drawing;
using System.Runtime.Versioning;

namespace SharpSight.UiAutomation;

// ===== U1A UI 自动化模型（组件共享，集中定义） =====

/// <summary>
/// ui_find 命中控件清单行：Index 供 ui_action/ui_input/ui_get 复用；Patterns 为能力清单（逗号串，只列 IsSupported），
/// Semantic 为同名成员语义候选（可为 null）。
/// <para><paramref name="Rect"/> 是展示用字符串（旧输出保留）；<paramref name="RectPx"/> 是同一几何的
/// <b>结构化物理像素矩形</b>（<see cref="UiElementLocator.FindForCapture"/> 填，供元素级截图裁剪）；
/// <paramref name="TopLevelHwnd"/> 是该元素 <c>GetAncestor(GA_ROOT)</c> 的顶层窗口句柄（供 Capture 从窗口帧裁剪）。
/// 二者仅 <c>FindForCapture</c> 填充，ui_find（<see cref="UiElementLocator.Find"/>）恒为默认值。</para>
/// </summary>
/// <param name="Index">清单内序号（0 基）。</param>
/// <param name="Name">UIA Name。</param>
/// <param name="Type">UIA ControlType 名。</param>
/// <param name="AutoId">AutomationId。</param>
/// <param name="Rect">展示用几何字符串（「x,y WxH」或「不可见/无几何」）。</param>
/// <param name="Patterns">能力清单（逗号串）。</param>
/// <param name="Semantic">同名成员语义候选（可为 null）。</param>
/// <param name="RectPx">结构化物理像素矩形（<c>FindForCapture</c> 填；其余为 <see cref="Rectangle.Empty"/>）。</param>
/// <param name="TopLevelHwnd">元素自身 <c>GetAncestor(GA_ROOT)</c> 顶层窗口句柄（<c>FindForCapture</c> 填；其余为 0）。</param>
internal sealed record UiElementInfo(
    int Index,
    string Name,
    string Type,
    string AutoId,
    string Rect,
    string Patterns,
    string? Semantic,
    Rectangle RectPx = default,
    IntPtr TopLevelHwnd = default);

/// <summary>ui_action/ui_input 结果：Message 注明实际动作与命中的 pattern（「已 {verb}（{pattern}）」）。</summary>
internal sealed record UiActionResult(bool Ok, string Message);

/// <summary>
/// 元素采集中间结果（<c>ui_find</c> / <c>screenshot element</c> 共用）：<see cref="Elements"/> 为本次清单，
/// <see cref="FrameId"/> 为本次采集产出的代际号（<see cref="FrameRegistry.Next"/>，spec §7.4）。工具层把
/// <see cref="FrameId"/> 回显给 agent；消费侧（ui_action/ui_input/ui_get/screenshot element）带旧帧号 → 拒绝。
/// </summary>
internal sealed record UiFindResult(IReadOnlyList<UiElementInfo> Elements, int FrameId);

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
