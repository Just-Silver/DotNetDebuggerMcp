using DotNetDebuggerMcp.Services;
using SharpSight.UiAutomation;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// U1A UI 自动化工具面：ui_find（控件清单 + patterns= 能力清单）/ ui_action（语义动词）/ ui_input / ui_get /
/// ui_wait（事件化）。语义动作走 UIA pattern；仅在必要处接管焦点：<c>ui_input</c> 默认失焦提交（让 LostFocus 绑定生效）、
/// <c>ui_action clickPoint=true</c> 先把窗口带到前台再真实点击。不要求活动 debug 会话——可先操作 UI 到某状态再
/// debug_attach；写操作（ui_action/ui_input）全程打 AgentActionLog。
/// </summary>
[SupportedOSPlatform("windows7.0")] // ui_* 依赖 FlaUI/UIA（仅 Windows）
[McpServerToolType]
public static class UiTools
{
    private const string UiVerbOptions =
        "invoke（默认动作）/ toggle（可勾选控件）/ select（列表·树·页签项）/ expand / collapse / focus（仅控件，窗口会抢前台请用 windowstate）/ scroll（需给 direction=up/down，lines 行数）/ scrollintoview / windowstate（还原/最大化/最小化顶层窗口，需给 windowstate=normal/maximized/minimized）";

    /// <summary>按进程/窗口条件查找 UI 控件清单（无视觉——返回文本清单与各控件支持的 pattern）。</summary>
    [McpServerTool]
    [Description("按进程/窗口条件查找 UI 控件清单（无视觉，返回文本）：process 传 pid 或进程名，可用 text/type/automationId 过滤；返回 index、Name、AutoId、patterns 与 Rect。**被禁用的控件（IsEnabled=false）在行尾标 [禁用]**——这类控件 UIA 会拒绝语义动作与写值，先让它变为可用再操作。大 UI 树用 scope（以上次 index 为根）与 depth 限层，避免超时。命中控件附同名成员反查候选（可 decompile_member 看实现）。执行动作走 ui_action，读值走 ui_get。注意观测扰动：UIA 注入会改变目标性能特征，性能排查优先 debug_* + 截图。")]
    public static async Task<string> UiFind(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示），如 UiSampleApp、1234。")] string process = "",
        [Description("窗口标题精确匹配；空 = 该进程首个顶层窗口。")] string title = "",
        [Description("控件文本/名称（Name，子串忽略大小写，也匹配 AutomationId）。")] string text = "",
        [Description("控件类型（UIA 类型名，如 Button/Text/Edit/List/ListItem/CheckBox/Window；TextBlock 等框架别名自动归一）。")] string type = "",
        [Description("AutomationId 精确匹配（可为空——WPF 常不设）。")] string automationId = "",
        [Description("返回条数上限，默认 50，范围 1-500。")] int limit = 50,
        [Description("限定子树根：以上一次 ui_find 的该 index 元素为根，只枚举其子控件（默认 -1=不限）。大表/长列表（如 999 行 DataGrid）建议先 scope 到容器再查，否则单次枚举可能超时。")] int scope = -1,
        [Description("最大层数（相对窗口/scope 根；默认 0=不限）。大 UI 树用 depth 限层可显著提速（如 depth=3 只看前几层容器）。")] int depth = 0,
        [Description("单次 UIA 调用的超时秒数，默认 5，范围 1-300；目标响应慢时可调大（这是护栏，不代表环境 UIA 不可用）。")] int timeoutSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} title={title} text={text} type={type} autoId={automationId} limit={limit} scope={scope} depth={depth} timeout={timeoutSeconds}";
        UiAutomationService.Instance.BeginUiCall();
        try
        {
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_find", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            var find = await UiAutomationService.Instance.FindAsync(process.Trim(), title.Trim(), text.Trim(), type.Trim(), automationId.Trim(), limit, timeoutSeconds, cancellationToken, scope, depth);
            var elements = find.Elements;

            var pid = UiAutomationService.Instance.LastFindPid;
            var winTitle = UiAutomationService.Instance.LastFindWindowTitle;
            var truncated = UiAutomationService.Instance.LastFindTruncated;
            var sb = new StringBuilder();
            sb.Append($"UI 控件清单 进程 {process.Trim()}(pid={pid}) 窗口「{winTitle}」共 {elements.Count} 个控件{(truncated ? "（已达上限，可能还有更多）" : "")}（帧: frameId={find.FrameId}）:");
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
                sb.Append($"[{el.Index}] {el.Type} Name={name} AutoId={autoId} patterns={patterns}{(el.Enabled ? "" : " [禁用]")} Rect=({el.Rect})");
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
        finally { await UiAutomationService.Instance.EndUiCallAsync(CancellationToken.None); } // P6：引用计数归零时释放 UIA 客户端
    }

    /// <summary>对 UI 控件执行语义动作（真实操作，有产线副作用）。</summary>
    [McpServerTool]
    [Description("对 UI 控件执行语义动作（真实操作，有产线副作用）。默认走 UIA pattern：不移动光标、不主动改前台（focus 对带 HWND 控件可能激活顶层窗口）。verb 按控件能力分派：invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate（复选框 toggle、列表/树/页签项 select、下拉/树节点 expand）。无右键/双击入口（会被明确拒绝）。定位用 index（仅紧邻的上次 ui_find 内有效）或 name/type（最稳）；windowstate 按 process 定位顶层窗口。legacy 控件结果标「可能不生效」，可显式 clickPoint=true（仅 invoke/select）走真实点击——**会先把目标窗口带到前台、再移动光标点击**，仅在语义动作确认无效时用。")]
    public static async Task<string> UiAction(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("语义动词（必填）：invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate。")] string verb = "",
        [Description("上次 ui_find 返回序号（&gt;=0 优先于 name/type 定位；默认 -1 用 name/type）。")] int index = -1,
        [Description("控件名/文本（Name/AutomationId 子串忽略大小写）。")] string name = "",
        [Description("控件类型（UIA 类型名）。")] string type = "",
        [Description("可交互性护栏（可省略；填写后用 ui_find 返回的帧号校验目标是否来自旧画面：0=不校验，非 0 且非当前帧会拒绝并提示重新 ui_find/screenshot）。")] int frameId = 0,
        [Description("滚动方向（verb=scroll 时必填）：up / down。")] string direction = "",
        [Description("滚动行数（verb=scroll 用，默认 0=3，范围 1-100）。")] int lines = 0,
        [Description("窗口状态（verb=windowstate 时必填）：normal / maximized / minimized。")] string windowstate = "",
        [Description("**真实点击**（默认 false；仅 verb=invoke/select）。true 时按控件 ClickablePoint 移动真实光标并注入左键点击——**会移动光标、注入输入、可能抢前台**；仅在目标无 Invoke/SelectionItem（如部分 WPF-UI 导航项只暴露 legacy）且语义动作被证实无效时开启。其它 verb 配 clickPoint=true 会报错。")] bool clickPoint = false,
        [Description("单次 UIA 调用的超时秒数，默认 5，范围 1-300；目标响应慢时可调大（这是护栏，不代表环境 UIA 不可用）。")] int timeoutSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} verb={verb} index={index} name={name} type={type} frameId={frameId} direction={direction} lines={lines} windowstate={windowstate} timeout={timeoutSeconds}";
        UiAutomationService.Instance.BeginUiCall();
        try
        {
            UiAutomationService.Instance.Frames.Validate(frameId);
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_action", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            if (string.IsNullOrWhiteSpace(verb))
                return Fail("ui_action", argsText, $"verb 不能为空（可选 {UiVerbOptions}）。");
            if (verb.Trim().Equals("rightclick", StringComparison.OrdinalIgnoreCase)
                || verb.Trim().Equals("doubleclick", StringComparison.OrdinalIgnoreCase))
                return Fail("ui_action", argsText, "UIA 无右键/双击语义入口；请把操作改为等价菜单/命令的 verb=invoke（确需真实点击时用 verb=invoke/select + clickPoint=true）。");
            var result = await UiAutomationService.Instance.ActionAsync(process.Trim(), verb.Trim(), index, name.Trim(), type.Trim(), direction.Trim(), lines, windowstate.Trim(), timeoutSeconds, cancellationToken, clickPoint);
            return Done("ui_action", argsText, result.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_action", argsText, "已取消。");
        }
        catch (StaleFrameException ex) { return Fail("ui_action", argsText, ex.Message); }
        catch (UiException ex) { return Fail("ui_action", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_action", argsText, $"UI 操作失败：{ex.Message}"); }
        finally { await UiAutomationService.Instance.EndUiCallAsync(CancellationToken.None); } // P6：引用计数归零时释放 UIA 客户端
    }

    /// <summary>对 UI 控件写入值（Value/RangeValue/LegacyIAccessible；真实操作，有产线副作用）。</summary>
    [McpServerTool]
    [Description("对 UI 控件写入值（真实操作，有产线副作用）。按写值能力分派：ValuePattern → RangeValuePattern → LegacyIAccessiblePattern（文本框写文本、滑块/数值框写数字），只读或无写值能力返回中文提示；空 value 返回提示。默认 commit=true：写完把焦点移出该控件（WPF 等失焦才提交的绑定否则读不到新值），会激活目标窗口；传 commit=false 不主动移焦（部分控件 SetValue 本身仍可能让窗口变前台）。定位用 index（上次 ui_find 序号，优先）或 name/type（歧义返回候选清单）。写入会打 AgentActionLog。")]
    public static async Task<string> UiInput(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("要写入的值（必填；空返回提示）。")] string value = "",
        [Description("上次 ui_find 返回序号（&gt;=0 优先于 name/type 定位；默认 -1 用 name/type）。")] int index = -1,
        [Description("控件名/文本（Name/AutomationId 子串忽略大小写）。")] string name = "",
        [Description("控件类型（UIA 类型名）。")] string type = "",
        [Description("写完是否失焦提交，默认 true：写完把焦点移出该控件（必要时先激活目标窗口），让 WPF 等「失焦才提交」的绑定真正收到新值；False = 只改控件文本、不主动移焦（连续填多个字段、或不想触发字段校验时用；部分控件 SetValue 本身仍可能让窗口变前台）。")] bool commit = true,
        [Description("可交互性护栏（可省略；填写后用 ui_find 返回的帧号校验目标是否来自旧画面：0=不校验，非 0 且非当前帧会拒绝并提示重新 ui_find/screenshot）。")] int frameId = 0,
        [Description("单次 UIA 调用的超时秒数，默认 5，范围 1-300；目标响应慢时可调大（这是护栏，不代表环境 UIA 不可用）。")] int timeoutSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} value={value} index={index} name={name} type={type} commit={commit} frameId={frameId} timeout={timeoutSeconds}";
        UiAutomationService.Instance.BeginUiCall();
        try
        {
            UiAutomationService.Instance.Frames.Validate(frameId);
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_input", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            if (string.IsNullOrEmpty(value))
                return Fail("ui_input", argsText, "value 不能为空：ui_input 需给出要写入的值。");
            var result = await UiAutomationService.Instance.InputAsync(process.Trim(), value, index, name.Trim(), type.Trim(), commit, timeoutSeconds, cancellationToken);
            return Done("ui_input", argsText, result.Message);
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_input", argsText, "已取消。");
        }
        catch (StaleFrameException ex) { return Fail("ui_input", argsText, ex.Message); }
        catch (UiException ex) { return Fail("ui_input", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_input", argsText, $"UI 写值失败：{ex.Message}"); }
        finally { await UiAutomationService.Instance.EndUiCallAsync(CancellationToken.None); } // P6：引用计数归零时释放 UIA 客户端
    }

    /// <summary>读取 UI 控件状态（只读，无副作用；展示值经 DB1 敏感脱敏）。</summary>
    [McpServerTool]
    [Description("读取 UI 控件状态（只读）。what 必填：value（优先 ValuePattern，退 LegacyIAccessible）/ name / toggle / selected / expandstate / rangevalue / enabled / offscreen / rect / helptext；无对应 pattern 返回中文提示。定位用 index（上次 ui_find 序号）或 name/type。值展示前按控件 Name/AutoId 脱敏。")]
    public static async Task<string> UiGet(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("要读取的状态（必填）：value/name/toggle/selected/expandstate/rangevalue/enabled/offscreen/rect/helptext。")] string what = "",
        [Description("上次 ui_find 返回序号（&gt;=0 优先于 name/type 定位；默认 -1 用 name/type）。")] int index = -1,
        [Description("控件名/文本（Name/AutomationId 子串忽略大小写）。")] string name = "",
        [Description("控件类型（UIA 类型名）。")] string type = "",
        [Description("可交互性护栏（可省略；填写后用 ui_find 返回的帧号校验目标是否来自旧画面：0=不校验，非 0 且非当前帧会拒绝并提示重新 ui_find/screenshot）。")] int frameId = 0,
        [Description("单次 UIA 调用的超时秒数，默认 5，范围 1-300；目标响应慢时可调大（这是护栏，不代表环境 UIA 不可用）。")] int timeoutSeconds = 5,
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} what={what} index={index} name={name} type={type} frameId={frameId} timeout={timeoutSeconds}";
        UiAutomationService.Instance.BeginUiCall();
        try
        {
            UiAutomationService.Instance.Frames.Validate(frameId);
            if (string.IsNullOrWhiteSpace(process))
                return Fail("ui_get", argsText, "目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
            if (string.IsNullOrWhiteSpace(what))
                return Fail("ui_get", argsText, "what 不能为空：value/name/toggle/selected/expandstate/rangevalue/enabled/offscreen/rect/helptext。");
            var state = await UiAutomationService.Instance.GetAsync(process.Trim(), what.Trim(), index, name.Trim(), type.Trim(), timeoutSeconds, cancellationToken);
            var (safe, redacted) = SensitiveValueRedactor.Redact(state.ControlName, state.Value);
            var text = $"what={what.Trim()} → {safe}";
            if (redacted) text += $"（{SensitiveValueRedactor.Notice}）";
            return Done("ui_get", argsText, text);
        }
        catch (OperationCanceledException)
        {
            return Fail("ui_get", argsText, "已取消。");
        }
        catch (StaleFrameException ex) { return Fail("ui_get", argsText, ex.Message); }
        catch (UiException ex) { return Fail("ui_get", argsText, ex.Message); }
        catch (Exception ex) { return Fail("ui_get", argsText, $"UI 读值失败：{ex.Message}"); }
        finally { await UiAutomationService.Instance.EndUiCallAsync(CancellationToken.None); } // P6：引用计数归零时释放 UIA 客户端
    }

    /// <summary>等待 UI 状态变化/控件出现（事件化 + 轮询兜底，只读；超时返回当前状态不报错）。</summary>
    [McpServerTool]
    [Description("等待 UI 状态变化/控件出现（只读：对目标窗口订阅结构/属性变化事件，命中即返回；无事件时每 200ms 轮询兜底，到 timeoutSeconds 止；超时返回当前状态提示不报错）。用法：text=期望出现的控件文本（等出现）；或 textChangedFrom + textChangedTo 成对（控件文本从 X 变 Y——动作后的状态确认，如 手动→自动）。type 可限定控件类型。timeoutSeconds 既是最长等待，也作单次 UIA 调用护栏。不移动光标、不注入输入。")]
    public static async Task<string> UiWait(
        [Description("目标进程：pid 或进程名子串（必填；空返回提示）。")] string process = "",
        [Description("期望出现的控件文本（Name 子串忽略大小写；与 textChangedFrom/To 二选一）。")] string text = "",
        [Description("限定控件类型（UIA 类型名，可空）。")] string type = "",
        [Description("状态切换起点文本（text 模式之外用成对 From/To 等文本变化）。")] string textChangedFrom = "",
        [Description("状态切换目标文本（成对使用）。")] string textChangedTo = "",
        [Description("最长等待秒数，默认 30，范围 1-300；同时作为单次 UIA 调用的超时护栏（目标响应慢时可调大）。")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
    {
        var argsText = $"process={process} text={text} type={type} from={textChangedFrom} to={textChangedTo} timeout={timeoutSeconds}";
        UiAutomationService.Instance.BeginUiCall();
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
        finally { await UiAutomationService.Instance.EndUiCallAsync(CancellationToken.None); } // P6：引用计数归零时释放 UIA 客户端
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
