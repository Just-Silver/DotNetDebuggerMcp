using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;

using System.Runtime.Versioning;

namespace DotNetDebuggerMcp.Services.Ui;

/// <summary>
/// U1A verb → pattern 分派（spec §6）。决策与执行分离：<see cref="Choose"/>/<see cref="ChooseInput"/> 为纯函数
/// （只吃能力快照，供单测）；<see cref="Execute"/>/<see cref="ExecuteInput"/> 做真实 pattern 调用（e2e 覆盖）。
/// 全部不支持 → <see cref="UiException"/>（中文提示，spec §7）。
/// </summary>
[SupportedOSPlatform("windows7.0")]
internal static class UiPatternDispatcher
{
    /// <summary>ui_action 合法 verb 集合（windowstate 单独处理；无右键/双击——物理输入已移除）。</summary>
    internal static readonly string[] Verbs =
        ["invoke", "toggle", "select", "expand", "collapse", "focus", "scroll", "scrollintoview", "windowstate"];

    /// <summary>
    /// pattern 调用被 provider 拒绝时的补充引导（spec §9）：后台/不可见控件常导致语义动作失败，
    /// 提示 agent 用 ui_get what=offscreen 确认，并可用 windowstate=normal 还原最小化窗口（绝不静默提前台）。
    /// </summary>
    internal const string OffscreenHint =
        "；若为目标后台/不可见导致 provider 拒绝，可用 ui_get what=offscreen 确认，必要时先 ui_action verb=windowstate windowstate=normal 还原最小化窗口。";

    /// <summary>纯决策：按 verb 与能力快照选 pattern，全部不支持时抛 <see cref="UiException"/>（中文）。</summary>
    public static ChosenAction Choose(string verb, UiPatternCapabilities caps, string direction, int lines, string windowstate)
    {
        switch (verb)
        {
            case "invoke":
                if (caps.Invoke) return new ChosenAction(verb, UiActionKind.Invoke, "InvokePattern", direction, lines, windowstate);
                if (caps.Legacy) return new ChosenAction(verb, UiActionKind.LegacyDefaultAction, "LegacyIAccessiblePattern", direction, lines, windowstate);
                throw new UiException("该控件不支持无鼠标触发；请用其菜单/命令入口 invoke。");

            case "toggle":
                if (caps.Toggle) return new ChosenAction(verb, UiActionKind.Toggle, "TogglePattern", direction, lines, windowstate);
                if (caps.Invoke) return new ChosenAction(verb, UiActionKind.Invoke, "InvokePattern", direction, lines, windowstate);
                throw new UiException("该控件不支持无鼠标触发；请用其菜单/命令入口 invoke。");

            case "select":
                if (caps.SelectionItem) return new ChosenAction(verb, UiActionKind.SelectionItem, "SelectionItemPattern", direction, lines, windowstate);
                if (caps.Invoke) return new ChosenAction(verb, UiActionKind.Invoke, "InvokePattern", direction, lines, windowstate);
                if (caps.Legacy) return new ChosenAction(verb, UiActionKind.LegacySelect, "LegacyIAccessiblePattern", direction, lines, windowstate);
                throw new UiException("该控件不支持选中。");

            case "expand":
                if (caps.ExpandCollapse) return new ChosenAction(verb, UiActionKind.ExpandCollapse, "ExpandCollapsePattern", direction, lines, windowstate);
                throw new UiException("该控件不可展开。");

            case "collapse":
                if (caps.ExpandCollapse) return new ChosenAction(verb, UiActionKind.ExpandCollapse, "ExpandCollapsePattern", direction, lines, windowstate);
                throw new UiException("该控件不可折叠。");

            case "focus":
                // 窗口 Focus 会抢前台（spec §9）——有 WindowPattern 视为窗口，引导改走 windowstate。
                if (caps.Window) throw new UiException("目标是窗口，focus 会抢前台；请改用 ui_action verb=windowstate windowstate=normal。");
                return new ChosenAction(verb, UiActionKind.Focus, "Focus", direction, lines, windowstate);

            case "scroll":
            {
                var dir = (direction ?? "").Trim().ToLowerInvariant();
                if (dir is not ("up" or "down"))
                    throw new UiException($"direction 无效：{direction}（可选 up / down）。");
                if (!caps.Scroll)
                    throw new UiException("该控件不支持滚动；请改用 scrollintoview 或换目标。");
                return new ChosenAction(verb, UiActionKind.Scroll, "ScrollPattern", dir, lines, windowstate);
            }

            case "scrollintoview":
                if (caps.ScrollItem) return new ChosenAction(verb, UiActionKind.ScrollItem, "ScrollItemPattern", direction, lines, windowstate);
                throw new UiException("该控件不可滚入视野。");

            case "windowstate":
            {
                var state = (windowstate ?? "").Trim().ToLowerInvariant();
                if (state is not ("normal" or "maximized" or "minimized"))
                    throw new UiException($"windowstate 无效：{windowstate}（可选 normal / maximized / minimized）。");
                if (!caps.Window) throw new UiException("该窗口无 WindowPattern。");
                return new ChosenAction(verb, UiActionKind.Window, "WindowPattern", direction, lines, state);
            }

            default:
                throw new UiException($"verb 无效：{verb}（可选 {string.Join("/", Verbs)}；无右键/双击——物理输入已移除）。");
        }
    }

    /// <summary>纯决策：ui_input 写值候选（ValuePattern → RangeValuePattern → LegacyIAccessiblePattern，只列已支持）。</summary>
    public static IReadOnlyList<ChosenInput> ChooseInputCandidates(UiPatternCapabilities caps)
    {
        var candidates = new List<ChosenInput>(3);
        if (caps.Value) candidates.Add(new ChosenInput(UiInputKind.Value, "ValuePattern"));
        if (caps.RangeValue) candidates.Add(new ChosenInput(UiInputKind.RangeValue, "RangeValuePattern"));
        if (caps.Legacy) candidates.Add(new ChosenInput(UiInputKind.LegacySetValue, "LegacyIAccessiblePattern"));
        if (candidates.Count == 0)
            throw new UiException("该控件不支持写值（无 Value/RangeValue/LegacyIAccessible）。");
        return candidates;
    }

    /// <summary>首选写值方式（候选首项）；执行侧会读回确认，未生效则依次退候。</summary>
    public static ChosenInput ChooseInput(UiPatternCapabilities caps) => ChooseInputCandidates(caps)[0];

    /// <summary>真实执行已决策动作（跨进程 UIA 调用；异常转中文 <see cref="UiException"/>）。</summary>
    public static void Execute(AutomationElement element, ChosenAction action)
    {
        switch (action.Kind)
        {
            case UiActionKind.Invoke:
            {
                var pattern = element.Patterns.Invoke.PatternOrDefault
                    ?? throw new UiException("该控件不支持无鼠标触发；请用其菜单/命令入口 invoke。");
                Invoke(pattern.Invoke, "Invoke");
                return;
            }
            case UiActionKind.Toggle:
            {
                var pattern = element.Patterns.Toggle.PatternOrDefault
                    ?? throw new UiException("该控件不支持无鼠标触发；请用其菜单/命令入口 invoke。");
                Invoke(pattern.Toggle, "Toggle");
                return;
            }
            case UiActionKind.SelectionItem:
            {
                var pattern = element.Patterns.SelectionItem.PatternOrDefault
                    ?? throw new UiException("该控件不支持选中。");
                Invoke(pattern.Select, "Select");
                return;
            }
            case UiActionKind.ExpandCollapse:
            {
                var pattern = element.Patterns.ExpandCollapse.PatternOrDefault
                    ?? throw new UiException(action.Verb == "collapse" ? "该控件不可折叠。" : "该控件不可展开。");
                Invoke(() => { if (action.Verb == "collapse") pattern.Collapse(); else pattern.Expand(); },
                    action.Verb == "collapse" ? "Collapse" : "Expand");
                return;
            }
            case UiActionKind.Scroll:
                ExecuteScroll(element, action);
                return;
            case UiActionKind.ScrollItem:
            {
                var pattern = element.Patterns.ScrollItem.PatternOrDefault
                    ?? throw new UiException("该控件不可滚入视野。");
                Invoke(pattern.ScrollIntoView, "ScrollIntoView");
                return;
            }
            case UiActionKind.Window:
            {
                var pattern = element.Patterns.Window.PatternOrDefault
                    ?? throw new UiException("该窗口无 WindowPattern。");
                var state = action.WindowState switch
                {
                    "maximized" => WindowVisualState.Maximized,
                    "minimized" => WindowVisualState.Minimized,
                    _ => WindowVisualState.Normal,
                };
                Invoke(() => pattern.SetWindowVisualState(state), "SetWindowVisualState");
                return;
            }
            case UiActionKind.LegacyDefaultAction:
            {
                var pattern = element.Patterns.LegacyIAccessible.PatternOrDefault
                    ?? throw new UiException("该控件不支持无鼠标触发；请用其菜单/命令入口 invoke。");
                Invoke(pattern.DoDefaultAction, "DoDefaultAction");
                return;
            }
            case UiActionKind.LegacySelect:
            {
                var pattern = element.Patterns.LegacyIAccessible.PatternOrDefault
                    ?? throw new UiException("该控件不支持选中。");
                Invoke(() => pattern.Select(0), "LegacySelect");
                return;
            }
            case UiActionKind.Focus:
                // 非窗口控件；Focus() 对带 HWND 控件可能激活顶层窗口（spec §9 已述，非物理输入）。
                Invoke(element.Focus, "Focus");
                return;
            default:
                throw new UiException($"未实现的动作分派：{action.Kind}。");
        }
    }

    /// <summary>真实执行 ui_input 写入（含只读校验与数字解析）。</summary>
    public static void ExecuteInput(AutomationElement element, ChosenInput choice, string value)
    {
        switch (choice.Kind)
        {
            case UiInputKind.Value:
            {
                var pattern = element.Patterns.Value.PatternOrDefault
                    ?? throw new UiException("该控件不支持写值（无 Value/RangeValue/LegacyIAccessible）。");
                if (SafeRead(() => pattern.IsReadOnly.ValueOrDefault, false))
                    throw new UiException("该控件为只读，无法写值。");
                Invoke(() => pattern.SetValue(value), "SetValue");
                return;
            }
            case UiInputKind.RangeValue:
            {
                var pattern = element.Patterns.RangeValue.PatternOrDefault
                    ?? throw new UiException("该控件不支持写值（无 Value/RangeValue/LegacyIAccessible）。");
                if (SafeRead(() => pattern.IsReadOnly.ValueOrDefault, false))
                    throw new UiException("该控件为只读，无法写值。");
                if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var number))
                    throw new UiException($"值「{value}」不是有效数字（RangeValuePattern 需要数字）。");
                Invoke(() => pattern.SetValue(number), "SetValue");
                return;
            }
            case UiInputKind.LegacySetValue:
            {
                var pattern = element.Patterns.LegacyIAccessible.PatternOrDefault
                    ?? throw new UiException("该控件不支持写值（无 Value/RangeValue/LegacyIAccessible）。");
                Invoke(() => pattern.SetValue(value), "SetValue");
                return;
            }
            default:
                throw new UiException($"未实现的写值分派：{choice.Kind}。");
        }
    }

    /// <summary>滚动：主路径按方向调 Scroll（>5 行用 Large）；provider 拒绝时回退 SetScrollPercent 步进。</summary>
    private static void ExecuteScroll(AutomationElement element, ChosenAction action)
    {
        var pattern = element.Patterns.Scroll.PatternOrDefault
            ?? throw new UiException("该控件不支持滚动；请改用 scrollintoview 或换目标。");
        var lines = Math.Clamp(action.Lines <= 0 ? 3 : action.Lines, 1, 100);
        var small = action.Direction == "up" ? ScrollAmount.SmallDecrement : ScrollAmount.SmallIncrement;
        var large = action.Direction == "up" ? ScrollAmount.LargeDecrement : ScrollAmount.LargeIncrement;

        try
        {
            var unit = lines > 5 ? large : small;
            var count = lines > 5 ? Math.Max(1, lines / 5) : lines;
            for (var i = 0; i < count; i++)
                pattern.Scroll(ScrollAmount.NoAmount, unit);
            return;
        }
        catch (Exception)
        {
            // 回退：按方向步进百分比（每行约 10%，上限 100）。
        }

        try
        {
            var current = SafeRead(() => pattern.VerticalScrollPercent.ValueOrDefault, -1.0);
            if (current < 0) current = 0;
            var delta = Math.Min(100.0, lines * 10.0);
            var target = action.Direction == "up" ? Math.Max(0, current - delta) : Math.Min(100, current + delta);
            pattern.SetScrollPercent(ScrollPatternConstants.NoScroll, target);
        }
        catch (Exception ex)
        {
            throw new UiException($"滚动调用失败：{ex.Message}{OffscreenHint}");
        }
    }

    /// <summary>执行并包装 pattern 调用异常为中文 <see cref="UiException"/>（附 provider 后台/不可见排查引导）。</summary>
    private static void Invoke(Action action, string op)
    {
        try { action(); }
        catch (Exception ex) { throw new UiException($"{op} 调用失败：{ex.Message}{OffscreenHint}"); }
    }

    private static T SafeRead<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }
}
