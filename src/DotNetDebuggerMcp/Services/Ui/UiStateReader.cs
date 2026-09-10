using FlaUI.Core.AutomationElements;

using System.Runtime.Versioning;

namespace DotNetDebuggerMcp.Services.Ui;

/// <summary>
/// U1A ui_get 读取（spec §6 what 映射）。决策与执行分离：<see cref="Choose"/> 为纯函数（吃能力快照，
/// 供单测），<see cref="Execute"/> 读真实 pattern/属性。返回原始值（不脱敏——输出层负责）。
/// </summary>
[SupportedOSPlatform("windows7.0")]
internal static class UiStateReader
{
    /// <summary>ui_get 合法 what 集合。</summary>
    internal static readonly string[] Whats =
        ["value", "name", "toggle", "selected", "expandstate", "rangevalue", "enabled", "offscreen", "rect", "helptext"];

    /// <summary>纯决策：what → 读取品类；pattern 不支持时抛中文 <see cref="UiException"/>。</summary>
    public static ChosenRead Choose(string what, UiPatternCapabilities caps)
    {
        switch (what)
        {
            case "value":
                if (caps.Value) return new ChosenRead(what, UiStateKind.Value, "ValuePattern");
                if (caps.Legacy) return new ChosenRead(what, UiStateKind.LegacyValue, "LegacyIAccessiblePattern");
                throw new UiException("该控件不支持读取值（无 Value/LegacyIAccessible）。");
            case "name":
                return new ChosenRead(what, UiStateKind.Name, "Name");
            case "toggle":
                if (caps.Toggle) return new ChosenRead(what, UiStateKind.Toggle, "TogglePattern");
                throw new UiException("该控件无 TogglePattern（不是可勾选控件）。");
            case "selected":
                if (caps.SelectionItem) return new ChosenRead(what, UiStateKind.Selected, "SelectionItemPattern");
                throw new UiException("该控件无 SelectionItemPattern（不是可选项）。");
            case "expandstate":
                if (caps.ExpandCollapse) return new ChosenRead(what, UiStateKind.ExpandState, "ExpandCollapsePattern");
                throw new UiException("该控件无 ExpandCollapsePattern。");
            case "rangevalue":
                if (caps.RangeValue) return new ChosenRead(what, UiStateKind.RangeValue, "RangeValuePattern");
                throw new UiException("该控件无 RangeValuePattern。");
            case "enabled":
                return new ChosenRead(what, UiStateKind.Enabled, "IsEnabled");
            case "offscreen":
                return new ChosenRead(what, UiStateKind.Offscreen, "IsOffscreen");
            case "rect":
                return new ChosenRead(what, UiStateKind.Rect, "BoundingRectangle");
            case "helptext":
                return new ChosenRead(what, UiStateKind.HelpText, "HelpText");
            default:
                throw new UiException($"what 无效：{what}（可选 {string.Join("/", Whats)}）。");
        }
    }

    /// <summary>读取真实值；Value=展示值、Raw=原始值兜底、ControlName 供输出层脱敏。均不脱敏。</summary>
    public static UiStateResult Execute(AutomationElement element, ChosenRead choice)
    {
        var controlName = SafeRead(() => element.Name ?? "", "");
        if (controlName.Length == 0)
            controlName = SafeRead(() => element.Properties.AutomationId.ValueOrDefault ?? "", "");

        string value;
        string? raw;
        switch (choice.Kind)
        {
            case UiStateKind.Value:
            {
                var pattern = element.Patterns.Value.PatternOrDefault
                    ?? throw new UiException("该控件不支持读取值（无 Value/LegacyIAccessible）。");
                raw = SafeRead(() => pattern.Value.ValueOrDefault ?? "", "");
                value = raw;
                break;
            }
            case UiStateKind.LegacyValue:
            {
                var pattern = element.Patterns.LegacyIAccessible.PatternOrDefault
                    ?? throw new UiException("该控件不支持读取值（无 Value/LegacyIAccessible）。");
                raw = SafeRead(() => pattern.Value.ValueOrDefault ?? "", "");
                value = raw;
                break;
            }
            case UiStateKind.Name:
                raw = SafeRead(() => element.Name ?? "", "");
                value = raw;
                break;
            case UiStateKind.Toggle:
            {
                var pattern = element.Patterns.Toggle.PatternOrDefault
                    ?? throw new UiException("该控件无 TogglePattern（不是可勾选控件）。");
                raw = SafeRead(() => pattern.ToggleState.ValueOrDefault.ToString(), "Unknown");
                value = raw;
                break;
            }
            case UiStateKind.Selected:
            {
                var pattern = element.Patterns.SelectionItem.PatternOrDefault
                    ?? throw new UiException("该控件无 SelectionItemPattern（不是可选项）。");
                raw = SafeRead(() => pattern.IsSelected.ValueOrDefault.ToString(), "False");
                value = raw;
                break;
            }
            case UiStateKind.ExpandState:
            {
                var pattern = element.Patterns.ExpandCollapse.PatternOrDefault
                    ?? throw new UiException("该控件无 ExpandCollapsePattern。");
                raw = SafeRead(() => pattern.ExpandCollapseState.ValueOrDefault.ToString(), "None");
                value = raw;
                break;
            }
            case UiStateKind.RangeValue:
            {
                var pattern = element.Patterns.RangeValue.PatternOrDefault
                    ?? throw new UiException("该控件无 RangeValuePattern。");
                raw = SafeRead(() => pattern.Value.ValueOrDefault.ToString(System.Globalization.CultureInfo.InvariantCulture), "NaN");
                value = raw;
                break;
            }
            case UiStateKind.Enabled:
                raw = SafeRead(() => element.IsEnabled, false).ToString();
                value = raw;
                break;
            case UiStateKind.Offscreen:
                raw = SafeRead(() => element.IsOffscreen, true).ToString();
                value = raw;
                break;
            case UiStateKind.Rect:
            {
                var rect = SafeRead(() => element.BoundingRectangle, System.Drawing.Rectangle.Empty);
                raw = rect.IsEmpty ? "不可见/无几何" : $"{rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}";
                value = raw;
                break;
            }
            case UiStateKind.HelpText:
                raw = SafeRead(() => element.HelpText ?? "", "");
                value = raw;
                break;
            default:
                throw new UiException($"未实现的读取分派：{choice.Kind}。");
        }

        return new UiStateResult(value, raw, controlName);
    }

    private static T SafeRead<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }
}
