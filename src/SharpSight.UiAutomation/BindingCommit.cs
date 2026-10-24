using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

using System.Drawing;
using System.Runtime.Versioning;

namespace SharpSight.UiAutomation;

/// <summary>
/// ui_input 的「提交」步骤：把 UIA 写入推过应用的 LostFocus 提交点。
/// <para>WPF 的 <c>TextBox.Text</c> 默认 <c>UpdateSourceTrigger=LostFocus</c>，只调 ValuePattern 改的是控件文本，
/// ViewModel 要等失去焦点才更新——写完立刻点「搜索/保存」时应用读到的仍是旧值（表现为写入"成功"但业务无反应）。</para>
/// <para>做法：先激活目标窗口，让目标控件拿到键盘焦点，再按一次 Tab 把焦点移走（键盘注入不可用时点窗口空白处兜底）。
/// 这一步会激活目标窗口（本工具按「共享桌面、允许打扰」运行，功能优先）。</para>
/// </summary>
[SupportedOSPlatform("windows7.0")]
internal static class BindingCommit
{
    /// <summary>写入结果里的提示后缀（供 ui_input 回显，说明已做过失焦提交）。</summary>
    internal const string Hint = "，已失焦提交";

    /// <summary>报告里说明用了哪种方式（"Tab" / "点击空白" / ""=未成功）。</summary>
    internal static string TryCommit(AutomationElement element, AutomationElement window)
    {
        try
        {
            // 键盘焦点只在活动窗口内成立：前台锁下直接 SetForegroundWindow 会被拒，BringToForeground 内部会借用前台线程兜底。
            UiPatternDispatcher.BringToForeground(window);

            // 让目标控件拿到焦点，再按一次 Tab 把焦点移走 —— 控件触发 LostKeyboardFocus，绑定此时才写回 ViewModel。
            element.Focus();
            Thread.Sleep(60);
            try
            {
                Keyboard.Press(VirtualKeyShort.TAB);
                Thread.Sleep(120);
                return "Tab";
            }
            catch (Exception)
            {
                // 键盘注入不可用：退化为点击窗口空白处（同样能让焦点离开输入控件）。
                if (FindBlankPoint(window) is not { } point) return "";
                Mouse.Click(point);
                Thread.Sleep(150);
                return "点击空白";
            }
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>在窗口矩形内找一个不落在任何子控件上的点（16px 网格扫描）；整窗都是控件时返回 null。</summary>
    private static Point? FindBlankPoint(AutomationElement window)
    {
        var bounds = window.BoundingRectangle;
        if (bounds.Width <= 16 || bounds.Height <= 16) return null;

        var obstacles = window.FindAllDescendants()
            .Select(e => SafeRead(() => e.BoundingRectangle, Rectangle.Empty))
            .Where(r => r.Width > 0 && r.Height > 0)
            .ToList();

        for (var y = bounds.Top + 8; y < bounds.Bottom - 8; y += 16)
        {
            for (var x = bounds.Left + 8; x < bounds.Right - 8; x += 16)
            {
                var point = new Point(x, y);
                if (!obstacles.Any(r => r.Contains(point))) return point;
            }
        }
        return null;
    }

    private static T? SafeRead<T>(Func<T> read, T? fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }
}
