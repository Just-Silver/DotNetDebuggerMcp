using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// U1 UI 自动化工具面：ui_find（控件清单）/ ui_invoke(action=click/rightClick/doubleClick) / ui_wait / ui_scroll。
/// 不要求活动 debug 会话——可先操作 UI 到某状态再 debug_attach；副作用操作（ui_invoke/ui_scroll）全程打 AgentActionLog。
/// </summary>
[SupportedOSPlatform("windows7.0")] // ui_* 依赖 FlaUI/UIA（仅 Windows）
[McpServerToolType]
public static class UiTools
{
    private const string UiActionOptions = "可选 click（默认，Invoke 优先，不可用则物理左键）/ rightClick / doubleClick（后两者物理鼠标，可能触发系统级行为如系统上下文菜单）";

    /// <summary>按进程/窗口条件查找 UI 控件清单（无视觉——返回文本清单供选择）。</summary>
    [McpServerTool]
    [Description("按进程/窗口条件查找 UI 控件清单（无视觉——返回文本清单供选择，含 index/Name/Type/AutoId/Rect/可操作类型与同名成员语义候选；语义候选可用 decompile_member <类型> <成员> 看实现）。进程需已运行且是 .NET UI 应用（WPF/WinForms/…）；窗口树里没有的虚拟化项查不到。")]
    public static async Task<string> UiFind(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示），如 UiSampleApp、1234。")] string process = "",
        [Description("窗口标题精确匹配；空 = 该进程首个顶层窗口。")] string title = "",
        [Description("控件文本/名称（Name，子串忽略大小写，也匹配 AutomationId）。")] string text = "",
        [Description("控件类型（UIA 类型名，如 Button/Text/Edit/List/ListItem/CheckBox/Window；TextBlock 等框架别名自动归一）。")] string type = "",
        [Description("AutomationId 精确匹配（可为空——WPF 常不设）。")] string automationId = "",
        [Description("返回条数上限，默认 50，范围 1-500。")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} title={title} text={text} type={type} autoId={automationId} limit={limit}";
        try
        {
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_find", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            var elements = await UiAutomationService.Instance.FindAsync(process.Trim(), title.Trim(), text.Trim(), type.Trim(), automationId.Trim(), limit, cancellationToken);

            var pid = UiAutomationService.Instance.LastFindPid;
            var winTitle = UiAutomationService.Instance.LastFindWindowTitle;
            var sb = new StringBuilder();
            sb.Append($"UI 控件清单 进程 {process.Trim()}(pid={pid}) 窗口「{winTitle}」共 {elements.Count} 个控件:");
            if (elements.Count == 0)
            {
                sb.Append("未找到符合条件的控件。可放宽条件（去掉 type/text）或用 ui_find process 只带进程名看全量。");
                return Done("ui_find", argsText, sb.ToString());
            }
            foreach (var el in elements)
            {
                sb.AppendLine();
                var name = string.IsNullOrEmpty(el.Name) ? "" : el.Name;
                var autoId = string.IsNullOrEmpty(el.AutoId) ? "（无）" : el.AutoId;
                sb.Append($"[{el.Index}] {el.Type} Name={name} AutoId={autoId} {el.CanInvoke} Rect=({el.Rect})");
                if (!string.IsNullOrEmpty(el.Semantic))
                    sb.Append($" 语义候选: {el.Semantic}（可 decompile_member 看实现）");
            }
            if (elements.Count == limit)
                sb.AppendLine().Append($"已达上限 {limit} 条——可加 text/type 条件缩小范围，或用 limit 放大。");
            sb.AppendLine().Append("操作：ui_invoke <index|name> / ui_scroll <index|name>；点完用 ui_wait 确认状态变化。");
            return Done("ui_find", argsText, sb.ToString());
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_find", argsText, "已取消。");
        }
        catch (UiException ex) { return Fail("ui_find", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_find", argsText, $"UI 查找失败：{ex.Message}"); }
    }

    /// <summary>对 UI 控件执行点击（真实操作，有产线副作用）。</summary>
    [McpServerTool]
    [Description($"对 UI 控件执行点击（真实操作，有产线副作用——会影响目标应用状态与数据）。action：{UiActionOptions}。index=上次 ui_find 结果序号（优先）；或给 name/type 即时唯一定位（歧义会返回候选清单，请改用 index）。执行动作会写入 AgentActionLog 供复盘。")]
    public static async Task<string> UiInvoke(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("上次 ui_find 返回序号（&gt;=0 优先于 name/type 定位；默认 -1 用 name/type）。")] int index = -1,
        [Description("控件名/文本（Name/AutomationId 子串忽略大小写）。")] string name = "",
        [Description("控件类型（UIA 类型名）。")] string type = "",
        [Description("动作：click（默认，Invoke 优先）/ rightClick / doubleClick。后两者物理鼠标——可能触发系统级行为（如系统上下文菜单），注意副作用。")] string action = "click",
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} index={index} name={name} type={type} action={action}";
        try
        {
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_invoke", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            var result = await UiAutomationService.Instance.InvokeAsync(process.Trim(), index, name.Trim(), type.Trim(), action, cancellationToken);
            return Done("ui_invoke", argsText, result.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_invoke", argsText, "已取消。");
        }
        catch (UiException ex) { return Fail("ui_invoke", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_invoke", argsText, $"UI 操作失败：{ex.Message}"); }
    }

    /// <summary>滚动 UI 容器（长列表/文本定位；真实滚轮，有产线副作用）。</summary>
    [McpServerTool]
    [Description("滚动 UI 容器（长列表/文本定位；v1 物理滚轮，真实滚动，有产线副作用）。目标=容器元素（List/ListBox/DataGrid/TextBox 等，index 或 name/type 定位；都缺省=窗口中心）。direction=up/down，lines=行数（默认 3）。物理滚轮会把滚动交给指针下方控件——目标需可见、窗口尽量在前台；滚动不改变元素树，滚完请用 ui_find 复查目标控件。写入 AgentActionLog。")]
    public static async Task<string> UiScroll(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("上次 ui_find 返回序号（&gt;=0 优先于 name/type 定位）；-1 且无 name/type = 窗口中心。")] int index = -1,
        [Description("容器名/文本（Name/AutomationId 子串忽略大小写）。")] string name = "",
        [Description("容器类型（UIA 类型名，如 List/DataGrid）。")] string type = "",
        [Description("滚动方向：down（默认，内容上移/看更后面）/ up。")] string direction = "down",
        [Description("滚动行数，默认 3，范围 1-100。")] int lines = 3,
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} index={index} name={name} type={type} direction={direction} lines={lines}";
        try
        {
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_scroll", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            var result = await UiAutomationService.Instance.ScrollAsync(process.Trim(), index, name.Trim(), type.Trim(), direction, lines, cancellationToken);
            return Done("ui_scroll", argsText, result.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_scroll", argsText, "已取消。");
        }
        catch (UiException ex) { return Fail("ui_scroll", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_scroll", argsText, $"UI 滚动失败：{ex.Message}"); }
    }

    /// <summary>等待 UI 状态变化/控件出现（只读轮询，超时返回当前状态不报错）。</summary>
    [McpServerTool]
    [Description("等待 UI 状态变化/控件出现（只读轮询，每 200ms 一次，到 timeoutSeconds 止；超时返回当前状态提示不报错）。用法：text=期望出现的控件文本（等出现）；或 textChangedFrom + textChangedTo 成对（控件文本从 X 变 Y——点击后的状态确认，如 手动→自动）。type 可限定控件类型。")]
    public static async Task<string> UiWait(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("期望出现的控件文本（Name 子串忽略大小写；与 textChangedFrom/To 二选一）。")] string text = "",
        [Description("限定控件类型（UIA 类型名，可空）。")] string type = "",
        [Description("状态切换起点文本（text 模式之外用成对 From/To 等文本变化）。")] string textChangedFrom = "",
        [Description("状态切换目标文本（成对使用）。")] string textChangedTo = "",
        [Description("最长等待秒数，默认 30，范围 1-300。")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} text={text} type={type} from={textChangedFrom} to={textChangedTo} timeout={timeoutSeconds}";
        try
        {
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_wait", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            if (string.IsNullOrWhiteSpace(text) && (string.IsNullOrWhiteSpace(textChangedFrom) || string.IsNullOrWhiteSpace(textChangedTo)))
                return Fail("ui_wait", argsText, "ui_wait 需给出 text（等控件出现）或 textChangedFrom + textChangedTo 成对（等文本从 X 变 Y）。");
            var result = await UiAutomationService.Instance.WaitAsync(process.Trim(), text.Trim(), type.Trim(), textChangedFrom.Trim(), textChangedTo.Trim(), timeoutSeconds, cancellationToken);
            return Done("ui_wait", argsText, $"{result.Outcome}：{result.Message}");
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_wait", argsText, "已取消。");
        }
        catch (UiException ex) { return Fail("ui_wait", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_wait", argsText, $"UI 等待失败：{ex.Message}"); }
    }

    // ===== 轨迹与文案 =====

    private static string Done(string tool, string args, string result)
    {
        DebugSessionService.Manager.Actions.Log(tool, args, Summarize(result));
        return result;
    }

    private static string Fail(string tool, string args, string message)
    {
        DebugSessionService.Manager.Actions.Log(tool, args, "fail: " + Summarize(message));
        return message;
    }

    private static string Summarize(string text)
    {
        if (text.Length <= 160) return text;
        return text.Substring(0, 157) + "...";
    }
}
