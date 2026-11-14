using System.Text;
using System.Text.Json;
using SharpSight.UiAutomation;
using SharpSight.ChildSession.Contracts;

namespace DotNetDebuggerMcp.Services.Gui;

internal static class UiOperationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal const string VerbOptions =
        "invoke（默认动作）/ toggle（可勾选控件）/ select（列表·树·页签项）/ expand / collapse / focus（仅控件，窗口会抢前台请用 windowstate）/ scroll（需给 direction=up/down，lines 行数）/ scrollintoview / windowstate（还原/最大化/最小化顶层窗口，需给 windowstate=normal/maximized/minimized）";

    internal static async Task<ChildReply> ExecuteAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        var request = new ChildRequest("", "", operation, arguments, DateTimeOffset.MaxValue);
        UiAutomationService.Instance.BeginUiCall();
        try
        {
            var r = arguments.Deserialize<UiOperationRequest>(Json) ?? throw new UiException("UIA参数不能为空。");
            if (operation == "ui_wait" && !arguments.TryGetProperty("timeoutSeconds", out _))
                r = r with { TimeoutSeconds = 30 };
            var text = operation switch
            {
                "ui_find" => await FindAsync(r, ct),
                "ui_action" => await ActionAsync(r, ct),
                "ui_input" => await InputAsync(r, ct),
                "ui_get" => await GetAsync(r, ct),
                "ui_wait" => await WaitAsync(r, ct),
                _ => throw new UiException("未知UIA操作。")
            };
            return ChildReply.Create(request, "completed", text, new { frameId = UiAutomationService.Instance.Frames.Current });
        }
        catch (OperationCanceledException) { return ChildReply.Create(request, "cancelled", "已取消。"); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", ex.Message); }
        finally { await UiAutomationService.Instance.EndUiCallAsync(CancellationToken.None); }
    }

    internal static async Task<string> FindAsync(UiOperationRequest r, CancellationToken ct)
    {
        RequireProcess(r);
        var find = await UiAutomationService.Instance.FindAsync(r.Process.Trim(), r.Title.Trim(), r.Text.Trim(),
            r.Type.Trim(), r.AutomationId.Trim(), r.Limit, r.TimeoutSeconds, ct, r.Scope, r.Depth);
        var service = UiAutomationService.Instance;
        var elements = find.Elements;
        var sb = new StringBuilder();
        sb.Append($"UI 控件清单 进程 {r.Process.Trim()}(pid={service.LastFindPid}) 窗口「{service.LastFindWindowTitle}」共 {elements.Count} 个控件{(service.LastFindTruncated ? "（已达上限，可能还有更多）" : "")}（帧: frameId={find.FrameId}）:");
        if (elements.Count == 0)
        {
            sb.Append("未找到符合条件的控件。可放宽条件（去掉 type/text）或用 ui_find process 只带进程名看全量。");
            return sb.ToString();
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
        if (service.LastFindTruncated)
            sb.AppendLine().Append($"已达上限 {elements.Count} 条——可加 text/type 条件缩小范围，或用 limit 放大。");
        sb.AppendLine().Append("操作：ui_action（语义动词 invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate）/ ui_input（写值）/ ui_get（读值）；动作后用 ui_wait 确认状态变化。");
        return sb.ToString();
    }

    internal static async Task<string> ActionAsync(UiOperationRequest r, CancellationToken ct)
    {
        UiAutomationService.Instance.Frames.Validate(r.FrameId);
        RequireProcess(r);
        if (string.IsNullOrWhiteSpace(r.Verb)) throw new UiException($"verb 不能为空（可选 {VerbOptions}）。");
        if (r.Verb.Trim().Equals("rightclick", StringComparison.OrdinalIgnoreCase) ||
            r.Verb.Trim().Equals("doubleclick", StringComparison.OrdinalIgnoreCase))
            throw new UiException("UIA 无右键/双击语义入口；请把操作改为等价菜单/命令的 verb=invoke（确需真实点击时用 verb=invoke/select + clickPoint=true）。");
        var result = await UiAutomationService.Instance.ActionAsync(r.Process.Trim(), r.Verb.Trim(), r.Index,
            r.Name.Trim(), r.Type.Trim(), r.Direction.Trim(), r.Lines, r.WindowState.Trim(),
            r.TimeoutSeconds, ct, r.ClickPoint);
        return result.Message;
    }

    internal static async Task<string> InputAsync(UiOperationRequest r, CancellationToken ct)
    {
        UiAutomationService.Instance.Frames.Validate(r.FrameId);
        RequireProcess(r);
        if (string.IsNullOrEmpty(r.Value)) throw new UiException("value 不能为空：ui_input 需给出要写入的值。");
        var result = await UiAutomationService.Instance.InputAsync(r.Process.Trim(), r.Value, r.Index,
            r.Name.Trim(), r.Type.Trim(), r.Commit, r.TimeoutSeconds, ct);
        return result.Message;
    }

    internal static async Task<string> GetAsync(UiOperationRequest r, CancellationToken ct)
    {
        UiAutomationService.Instance.Frames.Validate(r.FrameId);
        RequireProcess(r);
        if (string.IsNullOrWhiteSpace(r.What))
            throw new UiException("what 不能为空：value/name/toggle/selected/expandstate/rangevalue/enabled/offscreen/rect/helptext。");
        var state = await UiAutomationService.Instance.GetAsync(r.Process.Trim(), r.What.Trim(), r.Index,
            r.Name.Trim(), r.Type.Trim(), r.TimeoutSeconds, ct);
        var (safe, redacted) = SensitiveValueRedactor.Redact(state.ControlName, state.Value);
        return $"what={r.What.Trim()} → {safe}" + (redacted ? $"（{SensitiveValueRedactor.Notice}）" : "");
    }

    internal static async Task<string> WaitAsync(UiOperationRequest r, CancellationToken ct)
    {
        RequireProcess(r);
        if (string.IsNullOrWhiteSpace(r.Text) &&
            (string.IsNullOrWhiteSpace(r.TextChangedFrom) || string.IsNullOrWhiteSpace(r.TextChangedTo)))
            throw new UiException("ui_wait 需给出 text（等控件出现）或 textChangedFrom + textChangedTo 成对（等文本从 X 变 Y）。");
        var result = await UiAutomationService.Instance.WaitAsync(r.Process.Trim(), r.Text.Trim(), r.Type.Trim(),
            r.TextChangedFrom.Trim(), r.TextChangedTo.Trim(), r.TimeoutSeconds, ct);
        return $"{result.Outcome}：{result.Message}";
    }

    private static void RequireProcess(UiOperationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Process))
            throw new UiException("目标进程不能为空：给 pid 或进程名（如 UiSampleApp）。");
    }
}
