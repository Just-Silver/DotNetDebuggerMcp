using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Services.Ui;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// U1A UI 自动化工具面：ui_find（控件清单 + patterns= 能力清单）/ ui_action（语义动词）/ ui_input / ui_get /
/// ui_wait（事件化）。**全 UIA 语义 pattern，无任何物理输入**（不移动光标/不注入输入/不抢前台）。不要求活动
/// debug 会话——可先操作 UI 到某状态再 debug_attach；写操作（ui_action/ui_input）全程打 AgentActionLog。
/// </summary>
[SupportedOSPlatform("windows7.0")] // ui_* 依赖 FlaUI/UIA（仅 Windows）
[McpServerToolType]
public static class UiTools
{
    private const string UiVerbOptions =
        "invoke（默认动作）/ toggle（可勾选控件）/ select（列表·树·页签项）/ expand / collapse / focus（仅控件，窗口会抢前台请用 windowstate）/ scroll（需给 direction=up/down，lines 行数）/ scrollintoview / windowstate（还原/最大化/最小化顶层窗口，需给 windowstate=normal/maximized/minimized）";

    /// <summary>按进程/窗口条件查找 UI 控件清单（无视觉——返回文本清单与各控件支持的 pattern）。</summary>
    [McpServerTool]
    [Description("按进程/窗口条件查找 UI 控件清单（无视觉——返回文本清单供选择，含 index/Name/Type/AutoId/patterns 能力清单/Rect 与同名成员语义候选；语义候选可用 decompile_member <类型> <成员> 看实现）。全部走 UIA 语义，不移动光标、不注入输入、不抢前台。进程需已运行且是 .NET UI 应用（WPF/WinForms/…）；窗口树里没有的虚拟化项查不到。")]
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
            var truncated = UiAutomationService.Instance.LastFindTruncated;
            var sb = new StringBuilder();
            sb.Append($"UI 控件清单 进程 {process.Trim()}(pid={pid}) 窗口「{winTitle}」共 {elements.Count} 个控件{(truncated ? "（已达上限，可能还有更多）" : "")}:");
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
                var patterns = string.IsNullOrEmpty(el.Patterns) ? "（无）" : el.Patterns;
                sb.Append($"[{el.Index}] {el.Type} Name={name} AutoId={autoId} patterns={patterns} Rect=({el.Rect})");
                if (!string.IsNullOrEmpty(el.Semantic))
                    sb.Append($" 语义候选: {el.Semantic}（可 decompile_member 看实现）");
            }
            if (truncated)
                sb.AppendLine().Append($"已达上限 {elements.Count} 条——可加 text/type 条件缩小范围，或用 limit 放大。");
            sb.AppendLine().Append("操作：ui_action（语义动词 invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate）/ ui_input（写值）/ ui_get（读值）；动作后用 ui_wait 确认状态变化。");
            return Done("ui_find", argsText, sb.ToString());
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_find", argsText, "已取消。");
        }
        catch (UiException ex) { return Fail("ui_find", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_find", argsText, $"UI 查找失败：{ex.Message}"); }
    }

    /// <summary>对 UI 控件执行语义动作（真实操作，有产线副作用）。</summary>
    [McpServerTool]
    [Description("对 UI 控件执行语义动作（真实操作，有产线副作用——会影响目标应用状态与数据）。全 UIA 语义 pattern：不移动光标、不注入输入、不抢前台（仅窗口最小化时经 WindowPattern 还原）。verb 必填，可选 invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate——按控件能力分派而非固定「点一下」：复选框用 toggle、列表/树/页签项用 select、下拉/树节点用 expand。**无右键/双击**：UIA 无该类入口、物理输入已移除，传 rightclick/doubleclick 会返回明确中文拒绝并引导改用等价菜单/命令的 verb=invoke。定位用 index（上次 ui_find 序号，优先）或 name/type（歧义返回候选清单）；verb=windowstate 忽略 index/name/type，按 process 定位顶层窗口。执行动作会写入 AgentActionLog 供复盘。")]
    public static async Task<string> UiAction(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("语义动词（必填）：invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate。")] string verb = "",
        [Description("上次 ui_find 返回序号（&gt;=0 优先于 name/type 定位；默认 -1 用 name/type）。")] int index = -1,
        [Description("控件名/文本（Name/AutomationId 子串忽略大小写）。")] string name = "",
        [Description("控件类型（UIA 类型名）。")] string type = "",
        [Description("滚动方向（verb=scroll 时必填）：up / down。")] string direction = "",
        [Description("滚动行数（verb=scroll 用，默认 0=3，范围 1-100）。")] int lines = 0,
        [Description("窗口状态（verb=windowstate 时必填）：normal / maximized / minimized。")] string windowstate = "",
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} verb={verb} index={index} name={name} type={type} direction={direction} lines={lines} windowstate={windowstate}";
        try
        {
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_action", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            if (string.IsNullOrWhiteSpace(verb))
                return Fail("ui_action", argsText, $"verb 不能为空（可选 {UiVerbOptions}）。");
            if (verb.Trim().Equals("rightclick", StringComparison.OrdinalIgnoreCase)
                || verb.Trim().Equals("doubleclick", StringComparison.OrdinalIgnoreCase))
                return Fail("ui_action", argsText, "UIA 无该语义入口，物理输入已移除；请改用等价菜单/命令的 ui_action verb=invoke。");
            var result = await UiAutomationService.Instance.ActionAsync(process.Trim(), verb.Trim(), index, name.Trim(), type.Trim(), direction.Trim(), lines, windowstate.Trim(), cancellationToken);
            return Done("ui_action", argsText, result.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_action", argsText, "已取消。");
        }
        catch (UiException ex) { return Fail("ui_action", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_action", argsText, $"UI 操作失败：{ex.Message}"); }
    }

    /// <summary>对 UI 控件写入值（Value/RangeValue/LegacyIAccessible；真实操作，有产线副作用）。</summary>
    [McpServerTool]
    [Description("对 UI 控件写入值（真实操作，有产线副作用）。全 UIA 语义：按 ValuePattern → RangeValuePattern → LegacyIAccessiblePattern 分派（文本框写文本、滑块/数值框写数字），只读或无写值能力返回中文提示；空 value 返回提示。不移动光标、不注入输入、不抢前台。定位用 index（上次 ui_find 序号，优先）或 name/type（歧义返回候选清单）。写入会打 AgentActionLog。")]
    public static async Task<string> UiInput(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("要写入的值（必填；空返回提示）。")] string value = "",
        [Description("上次 ui_find 返回序号（&gt;=0 优先于 name/type 定位；默认 -1 用 name/type）。")] int index = -1,
        [Description("控件名/文本（Name/AutomationId 子串忽略大小写）。")] string name = "",
        [Description("控件类型（UIA 类型名）。")] string type = "",
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} value={value} index={index} name={name} type={type}";
        try
        {
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_input", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            if (string.IsNullOrEmpty(value))
                return Fail("ui_input", argsText, "value 不能为空：ui_input 需给出要写入的值。");
            var result = await UiAutomationService.Instance.InputAsync(process.Trim(), value, index, name.Trim(), type.Trim(), cancellationToken);
            return Done("ui_input", argsText, result.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_input", argsText, "已取消。");
        }
        catch (UiException ex) { return Fail("ui_input", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_input", argsText, $"UI 写值失败：{ex.Message}"); }
    }

    /// <summary>读取 UI 控件状态（只读，无副作用；展示值经 DB1 敏感脱敏）。</summary>
    [McpServerTool]
    [Description("读取 UI 控件状态（只读、无副作用）。what 必填，可选 value（优先 ValuePattern，无则 LegacyIAccessible）/ name / toggle / selected / expandstate / rangevalue / enabled / offscreen / rect / helptext（默认 \"\"，必填；无对应 pattern 返回中文提示）。定位用 index（上次 ui_find 序号，优先）或 name/type。读出的值展示前按控件 Name/AutoId 经敏感脱敏（命中凭据规则替换为占位符）。")]
    public static async Task<string> UiGet(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("要读取的状态（必填）：value/name/toggle/selected/expandstate/rangevalue/enabled/offscreen/rect/helptext。")] string what = "",
        [Description("上次 ui_find 返回序号（&gt;=0 优先于 name/type 定位；默认 -1 用 name/type）。")] int index = -1,
        [Description("控件名/文本（Name/AutomationId 子串忽略大小写）。")] string name = "",
        [Description("控件类型（UIA 类型名）。")] string type = "",
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} what={what} index={index} name={name} type={type}";
        try
        {
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_get", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            if (string.IsNullOrWhiteSpace(what))
                return Fail("ui_get", argsText, "what 不能为空：value/name/toggle/selected/expandstate/rangevalue/enabled/offscreen/rect/helptext。");
            var state = await UiAutomationService.Instance.GetAsync(process.Trim(), what.Trim(), index, name.Trim(), type.Trim(), cancellationToken);
            var (safe, redacted) = SensitiveValueRedactor.Redact(state.ControlName, state.Value);
            var text = $"what={what.Trim()} → {safe}";
            if (redacted) text += $"（{SensitiveValueRedactor.Notice}）";
            return Done("ui_get", argsText, text);
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_get", argsText, "已取消。");
        }
        catch (UiException ex) { return Fail("ui_get", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_get", argsText, $"UI 读值失败：{ex.Message}"); }
    }

    /// <summary>等待 UI 状态变化/控件出现（事件化 + 轮询兜底，只读；超时返回当前状态不报错）。</summary>
    [McpServerTool]
    [Description("等待 UI 状态变化/控件出现（只读：对目标窗口订阅结构/属性变化事件，命中即返回；无事件时每 200ms 轮询兜底，到 timeoutSeconds 止；超时返回当前状态提示不报错）。用法：text=期望出现的控件文本（等出现）；或 textChangedFrom + textChangedTo 成对（控件文本从 X 变 Y——动作后的状态确认，如 手动→自动）。type 可限定控件类型。不移动光标、不注入输入。")]
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
