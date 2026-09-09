using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using System.Text;

namespace DotNetDebuggerMcp.Services;

// ===== U1 UI 自动化模型（UiAutomationService 同文件，供 ui_* 工具消费） =====

/// <summary>ui_find 命中控件清单行：Index 供 ui_invoke/ui_scroll 复用；Semantic 为同名成员语义候选（可为 null）。</summary>
internal sealed record UiElementInfo(
    int Index,
    string Name,
    string Type,
    string AutoId,
    string Rect,
    string CanInvoke,
    string? Semantic);

/// <summary>ui_invoke/ui_scroll 结果：Message 注明实际动作（Invoke/物理左键/物理右键/双击/滚轮方向·行数）。</summary>
internal sealed record UiActionResult(bool Ok, string Message);

/// <summary>ui_wait 结果：Outcome ∈ 出现/已变化/超时/失败。</summary>
internal sealed record UiWaitResult(string Outcome, string Message);

/// <summary>UIA 失败/目标状态不满足的中文提示（工具层 catch 转返回文本，不抛给 MCP）。</summary>
internal sealed class UiException : Exception
{
    public UiException(string message) : base(message) { }
}

/// <summary>
/// U1 UI 自动化服务：FlaUI（UIA3）封装。单自动化实例 + 全操作串行锁（SemaphoreSlim(1,1)——MCP server
/// 工具可能并发调用，FlaUI 自动化实例不并发访问）；所有跨进程 UIA 调用包 5s 超时护栏（ConnectionTimeout/
/// TransactionTimeout 5s + 外层 WhenAny 兜底）；Find 结果缓存（ui_invoke/ui_scroll index 复用）；点击分派
/// （click=Invoke 优先 + 物理左键兜底；rightClick/doubleClick 无 UIA pattern 一律物理鼠标）；v1 滚动 = 物理滚轮
/// （Mouse.Scroll，正=上滚/负=下滚）。副作用由工具层打 AgentActionLog。
/// </summary>
[SupportedOSPlatform("windows7.0")] // FlaUI 仅 Windows；宿主保持 net10.0（非 platform TFM），本服务禁止在非 Windows 使用
internal sealed class UiAutomationService
{
    internal static UiAutomationService Instance { get; } = new();

    private static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UIA3Automation? _automation;

    // Find 结果缓存：最近一次 ui_find 的元素清单（含 pid 与顶层窗口），ui_invoke/ui_scroll 按 index 复用
    private sealed record FindEntry(int Pid, UiElementInfo Info, AutomationElement Element, AutomationElement Window);
    private readonly List<FindEntry> _lastFind = new();
    private int _lastFindPid = -1;
    private string _lastFindWindowTitle = "";

    /// <summary>最近一次 ui_find 的目标 pid（供工具头部展示）。</summary>
    public int LastFindPid => _lastFindPid;

    /// <summary>最近一次 ui_find 命中的窗口标题（供工具头部展示）。</summary>
    public string LastFindWindowTitle => _lastFindWindowTitle;

    private UiAutomationService()
    {
    }

    // ===== 对外 API（Find/Invoke/Scroll/Wait；gate 内串行 + 5s 兜底） =====

    /// <summary>按进程/窗口条件查找控件清单（返回前 limit 条并更新 index 缓存）。</summary>
    public Task<IReadOnlyList<UiElementInfo>> FindAsync(string process, string title, string text, string type, string automationId, int limit, CancellationToken ct)
        => RunGateAsync(() => FindCore(process, title, text, type, automationId, limit), ct);

    /// <summary>对目标控件执行动作（index=最近 find 结果序号；否则按 name/type 即时唯一命中）。</summary>
    public Task<UiActionResult> InvokeAsync(string process, int index, string name, string type, string action, CancellationToken ct)
        => RunGateAsync(() => InvokeCore(process, index, name, type, action), ct);

    /// <summary>在目标容器上滚动（v1 物理滚轮；index&lt;0 时按 name/type 定位，均缺省=窗口中心）。</summary>
    public Task<UiActionResult> ScrollAsync(string process, int index, string name, string type, string direction, int lines, CancellationToken ct)
        => RunGateAsync(() => ScrollCore(process, index, name, type, direction, lines), ct);

    /// <summary>轮询等待控件文本出现（text 模式）或控件文本从 X 变 Y（textChangedFrom/To 模式），到 timeoutSeconds 止。</summary>
    public async Task<UiWaitResult> WaitAsync(string process, string text, string type, string textChangedFrom, string textChangedTo, int timeoutSeconds, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(timeoutSeconds, 1, 300));
        while (true)
        {
            var result = await RunGateAsync(() => PollWaitOnce(process, text, type, textChangedFrom, textChangedTo), ct);
            if (result is not null) return result;
            if (DateTime.UtcNow >= deadline)
            {
                string? still = null;
                try { still = await RunGateAsync(() => PollCurrentTextCore(process, text, type, textChangedFrom, textChangedTo), ct); }
                catch { /* 终态查询失败视作当前不可见 */ }
                return new UiWaitResult("超时", still is null
                    ? $"等待 {timeoutSeconds}s 未命中。"
                    : $"等待 {timeoutSeconds}s 未命中（当前仍: {still}）。");
            }
            await Task.Delay(200, ct);
        }
    }

    // ===== Find =====

    private IReadOnlyList<UiElementInfo> FindCore(string process, string title, string text, string type, string automationId, int limit)
    {
        var (pid, _) = ResolveProcess(process);
        var automation = GetAutomation();
        var window = FindWindow(automation, pid, title)
            ?? throw new UiException($"进程 {process}（pid={pid}）没有 UIA 可见顶层窗口。确认目标 UI 应用已启动且主窗口已创建；若窗口开在其它桌面/以管理员运行，本进程需同权限才能访问。");

        var typeFilter = ResolveControlType(type); // null=不限
        var cap = Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);

        // 一次性枚举窗口全部后代，条件工厂只做「全部」以避开 ByName 精确匹配——文本匹配走 .NET 子串忽略大小写
        AutomationElement[] all;
        try { all = window.FindAllDescendants(); }
        catch (Exception ex) { throw new UiException($"读取窗口控件树失败：{ex.Message}"); }

        var entries = new List<FindEntry>(Math.Min(cap, 64));
        var semanticModule = TryGetMetadataModule(pid);
        var semanticByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); // 元素名 → 语义候选（去重反查）

        for (var i = 0; i < all.Length && entries.Count < cap; i++)
        {
            var el = all[i];
            var elType = SafeRead(() => el.ControlType.ToString(), "");
            if (typeFilter is not null && !string.Equals(elType, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;

            var name = SafeRead(() => el.Name ?? "", "");
            var autoId = SafeRead(() => el.Properties.AutomationId.ValueOrDefault ?? "", "");
            if (!string.IsNullOrEmpty(automationId) && !string.Equals(autoId, automationId, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(text)
                && !name.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !autoId.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;

            var canInvoke = SafeRead(() => el.Patterns.Invoke.IsSupported == true, false);
            var rect = SafeRead(() => el.BoundingRectangle, Rectangle.Empty);
            var rectText = rect.IsEmpty ? "不可见/无几何" : $"{rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}";

            // 语义反查（务实成员反查）：Name/AutomationId 去重后反查元数据同名成员候选
            string? semantic = null;
            if (semanticModule is not null)
            {
                foreach (var query in new[] { name, autoId })
                {
                    if (string.IsNullOrWhiteSpace(query) || query.Length < 2) continue;
                    if (!semanticByName.TryGetValue(query, out var hit))
                    {
                        hit = BuildSemanticText(semanticModule, query);
                        semanticByName[query] = hit;
                    }
                    if (!string.IsNullOrEmpty(hit)) { semantic = hit; break; }
                }
            }

            var info = new UiElementInfo(entries.Count, name, elType, autoId, rectText,
                canInvoke ? "Invoke✓" : "", semantic);
            entries.Add(new FindEntry(pid, info, el, window));
        }

        _lastFind.Clear();
        _lastFind.AddRange(entries);
        _lastFindPid = pid;
        _lastFindWindowTitle = SafeRead(() => window.Name ?? "", "");

        return entries.Select(e => e.Info).ToList();
    }

    /// <summary>反查命中候选文本（前 3 条 + …）。</summary>
    private static string? BuildSemanticText(string modulePath, string elementName)
    {
        var candidates = UiSemanticResolver.Lookup(modulePath, elementName);
        if (candidates is null || candidates.Count == 0) return null;
        var shown = candidates.Take(3);
        return candidates.Count > 3 ? string.Join("；", shown) + "；…" : string.Join("；", shown);
    }

    // ===== Invoke（action 分派） =====

    private UiActionResult InvokeCore(string process, int index, string name, string type, string action)
    {
        var (pid, _) = ResolveProcess(process);
        var actionNorm = action.Trim().ToLowerInvariant();
        if (actionNorm is not ("click" or "rightClick" or "doubleClick"))
            throw new UiException($"action 无效：{action}（可选 click / rightClick / doubleClick）。");

        var (element, window, label) = ResolveTarget(pid, index, name, type, invoke: true);

        string done;
        if (actionNorm == "click" && SafeRead(() => element.Patterns.Invoke.IsSupported == true, false))
        {
            var pattern = SafeRead(() => element.Patterns.Invoke.PatternOrDefault, null);
            if (pattern is not null)
            {
                try { pattern.Invoke(); }
                catch (Exception ex) { throw new UiException($"Invoke 调用失败：{ex.Message}"); }
                WaitAfterAction();
                done = $"已 Invoke（语义点击，{label}）";
                return new UiActionResult(true, done);
            }
        }

        done = PerformPhysicalMouse(window, element, label, actionNorm);
        return new UiActionResult(true, done);
    }

    /// <summary>物理鼠标动作（右键/双击无 UIA pattern 一律走这里；左键在 Invoke 不可用时兜底）。</summary>
    private static string PerformPhysicalMouse(AutomationElement window, AutomationElement element, string label, string action)
    {
        ActivateWindow(window);
        if (!TryGetTargetPoint(element, out var point))
            throw new UiException($"目标控件无可用点击点（clickable point 与 BoundingRectangle 均不可得）——自绘/离屏控件请改用 ui_find 找可见控件。");

        switch (action)
        {
            case "click":
                try { Mouse.LeftClick(point); return $"已物理左键点击 {label}（Invoke 不可用，坐标兜底）"; }
                catch (Exception ex) { throw new UiException($"物理左键点击失败：{ex.Message}"); }
            case "rightClick":
                try { Mouse.RightClick(point); return $"已物理右键点击 {label}（可能弹系统菜单——注意副作用）"; }
                catch (Exception ex) { throw new UiException($"物理右键点击失败：{ex.Message}"); }
            default:
                try { Mouse.DoubleClick(point); return $"已物理双击 {label}"; }
                catch (Exception ex) { throw new UiException($"物理双击失败：{ex.Message}"); }
        }
    }

    private UiActionResult ScrollCore(string process, int index, string name, string type, string direction, int lines)
    {
        var (pid, _) = ResolveProcess(process);
        var dirNorm = direction.Trim().ToLowerInvariant();
        if (dirNorm is not ("up" or "down"))
            throw new UiException($"direction 无效：{direction}（可选 up / down）。");
        var n = Math.Clamp(lines <= 0 ? 3 : lines, 1, 100);

        AutomationElement window;
        AutomationElement? target;
        string targetLabel;
        if (index >= 0)
        {
            var entry = GetCachedEntry(pid, index);
            window = entry.Window;
            target = entry.Element;
            targetLabel = $"控件 {DisplayLabel(entry.Info)}";
        }
        else if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(type))
        {
            var located = ResolveTarget(pid, -1, name, type, invoke: false);
            window = located.Window;
            target = located.Element;
            targetLabel = $"控件 {located.Label}";
        }
        else
        {
            // 缺省：窗口首个可滚区/窗口中心
            window = FindWindow(GetAutomation(), pid, "") ?? throw new UiException($"进程 {process}（pid={pid}）没有 UIA 可见顶层窗口。");
            target = null;
            targetLabel = "窗口";
        }

        ActivateWindow(window);
        // 物理滚轮需要目标窗口在前台且指针落在可滚区域上方：先尽量把目标控件聚焦（失败静默，坐标兜底）
        if (target is not null)
        {
            try { target.Focus(); } catch { /* 非聚焦型控件忽略 */ }
        }
        var scrollPoint = CenterPoint(target, window);
        Mouse.Position = scrollPoint;

        var signed = dirNorm == "down" ? -n : n; // FlaUI MouseTests 实证：Scroll(+n) 上滚、Scroll(-n) 下滚（MOUSEEVENTF_WHEEL 正负约定）
        try { Mouse.Scroll(signed); }
        catch (Exception ex) { throw new UiException($"物理滚轮失败：{ex.Message}"); }
        WaitAfterAction();

        var where = target is null
            ? $"窗口工作区中心（{FormatPoint(scrollPoint)}）"
            : $"控件 {targetLabel} 中心";
        var actText = dirNorm == "down" ? "向下" : "向上";
        return new UiActionResult(true, $"已{actText}滚动 {n} 行 @ {where}（物理滚轮）。滚动不改变元素树——请用 ui_find 复查目标控件。");
    }

    /// <summary>目标解析：index 优先（pid 不匹配清缓存提示）；否则 name/type 即时唯一定位（歧义返回候选清单）。</summary>
    private (AutomationElement Element, AutomationElement Window, string Label) ResolveTarget(int pid, int index, string name, string type, bool invoke)
    {
        if (index >= 0)
        {
            var entry = GetCachedEntry(pid, index);
            return (entry.Element, entry.Window, DisplayLabel(entry.Info));
        }

        var typeFilter = ResolveControlType(type);
        var window = FindWindow(GetAutomation(), pid, "")
            ?? throw new UiException($"进程 pid={pid} 没有 UIA 可见顶层窗口。");
        var all = window.FindAllDescendants();
        var typeMatches = new List<AutomationElement>();
        foreach (var el in all)
        {
            if (typeFilter is not null)
            {
                var elType = SafeRead(() => el.ControlType.ToString(), "");
                if (!string.Equals(elType, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            }
            typeMatches.Add(el);
        }

        AutomationElement? found = null;
        if (!string.IsNullOrEmpty(name))
        {
            var hits = new List<(AutomationElement El, string Hit)>();
            foreach (var el in typeMatches)
            {
                var n = SafeRead(() => el.Name ?? "", "");
                var a = SafeRead(() => el.Properties.AutomationId.ValueOrDefault ?? "", "");
                if (n.Contains(name, StringComparison.OrdinalIgnoreCase) || a.Contains(name, StringComparison.OrdinalIgnoreCase))
                    hits.Add((el, string.IsNullOrEmpty(n) ? a : n));
            }
            if (hits.Count == 0)
                throw new UiException(invoke
                    ? $"未找到名称/文本含「{name}」的控件{TypeSuffix(typeFilter)}。先 ui_find 看可用控件清单。"
                    : $"未找到名称/文本含「{name}」的容器{TypeSuffix(typeFilter)}。先 ui_find 看可用控件清单。");
            if (hits.Count > 1)
            {
                var sb = new StringBuilder();
                sb.Append($"「{name}」命中 {hits.Count} 个控件，无法唯一确定：");
                for (var i = 0; i < hits.Count && i < 5; i++)
                {
                    var (el, hitText) = hits[i];
                    sb.Append($"[{i}] {SafeRead(() => el.ControlType.ToString(), "?")} Name={hitText} ");
                }
                if (hits.Count > 5) sb.Append("…");
                sb.Append("请先 ui_find 定位后按 index 操作。");
                throw new UiException(sb.ToString());
            }
            found = hits[0].El;
            return (found, window, $"{SafeRead(() => found.ControlType.ToString(), "?")}「{hits[0].Hit}」");
        }

        if (typeMatches.Count == 0)
            throw new UiException($"未找到类型为「{typeFilter}」的控件。先 ui_find 看实际控件类型。");
        if (typeMatches.Count > 1)
            throw new UiException($"类型「{typeFilter}」命中 {typeMatches.Count} 个控件，无法唯一确定。请给出 name 或用 ui_find 的 index。");
        found = typeMatches[0];
        return (found, window, $"{typeFilter}「{SafeRead(() => found.Name ?? "", "")}」");
    }

    private FindEntry GetCachedEntry(int pid, int index)
    {
        if (_lastFindPid != pid || _lastFind.Count == 0)
        {
            _lastFind.Clear();
            _lastFindPid = -1;
            throw new UiException($"index={index} 引用的不是进程 pid={pid} 的 ui_find 结果（当前缓存 pid={_lastFindPid}）。请先对该进程执行 ui_find。");
        }
        if (index < 0 || index >= _lastFind.Count)
            throw new UiException($"index={index} 超出最近 ui_find 结果范围（0..{_lastFind.Count - 1}）。请重新 ui_find。");
        return _lastFind[index];
    }

    private static string DisplayLabel(UiElementInfo info)
        => string.IsNullOrEmpty(info.Name)
            ? $"{info.Type} (AutoId={info.AutoId})"
            : $"{info.Type}「{info.Name}」";

    // ===== Wait =====

    /// <summary>单次轮询：命中返回结果，未命中返回 null。</summary>
    private UiWaitResult? PollWaitOnce(string process, string text, string type, string textChangedFrom, string textChangedTo)
    {
        var (pid, _) = ResolveProcess(process);
        var typeFilter = ResolveControlType(type);
        var window = FindWindow(GetAutomation(), pid, "")
            ?? throw new UiException($"进程 {process}（pid={pid}）没有 UIA 可见顶层窗口。");

        if (!string.IsNullOrEmpty(text))
        {
            var hit = FindFirstName(window, text, typeFilter);
            if (hit is not null)
                return new UiWaitResult("出现", $"控件已出现：{TypeSuffix(typeFilter)}文本「{hit}」。");
            return null;
        }

        if (!string.IsNullOrEmpty(textChangedFrom) && !string.IsNullOrEmpty(textChangedTo))
        {
            // 状态切换：查 To 文本是否已出现（From 已不再代表当前态）。type 过滤下首个匹配即判定。
            var to = FindFirstName(window, textChangedTo, typeFilter);
            if (to is not null)
                return new UiWaitResult("已变化", $"控件文本已变为「{to}」（原「{textChangedFrom}」）。");
            return null;
        }

        throw new UiException("ui_wait 需给出 text（等控件出现）或 textChangedFrom+textChangedTo 成对（等文本变化）。");
    }

    /// <summary>超时返回时的当前状态摘要（null=相关控件当前均不可见）。</summary>
    private string? PollCurrentTextCore(string process, string text, string type, string from, string to)
    {
        var (pid, _) = ResolveProcess(process);
        var typeFilter = ResolveControlType(type);
        var window = FindWindow(GetAutomation(), pid, "");
        if (window is null) return "目标窗口不可见";
        var probe = !string.IsNullOrEmpty(text) ? text : to;
        var hit = FindFirstName(window, probe, typeFilter);
        return hit is null ? null : $"{probe}→已见「{hit}」";
    }

    private static string? FindFirstName(AutomationElement window, string probe, string? typeFilter)
    {
        try
        {
            var all = window.FindAllDescendants();
            foreach (var el in all)
            {
                if (typeFilter is not null)
                {
                    var elType = SafeRead(() => el.ControlType.ToString(), "");
                    if (!string.Equals(elType, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
                }
                var n = SafeRead(() => el.Name ?? "", "");
                if (n.Contains(probe, StringComparison.OrdinalIgnoreCase)) return n;
            }
            return null;
        }
        catch
        {
            return null; // 单次查询失败视作未命中，下轮再试（UIA 跨进程瞬断自愈）
        }
    }

    // ===== 共享底座 =====

    /// <summary>gate 内串行执行核心逻辑并套 5s 兜底。</summary>
    private async Task<T> RunGateAsync<T>(Func<T> core, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await UiaBoundAsync(core, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<T> UiaBoundAsync<T>(Func<T> core, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = Task.Run(core, cts.Token);
        var winner = await Task.WhenAny(task, Task.Delay(UiaTimeout, cts.Token)).ConfigureAwait(false);
        if (winner != task)
            throw new UiException($"UIA 调用超过 {UiaTimeout.TotalSeconds:0}s 未响应（目标进程可能挂起或 UIA 繁忙）。可稍后重试。");
        return await task; // 原异常（UiException/其它）原样上抛
    }

    private UIA3Automation GetAutomation()
    {
        if (_automation is null)
        {
            _automation = new UIA3Automation
            {
                ConnectionTimeout = UiaTimeout,
                TransactionTimeout = UiaTimeout,
            };
        }
        return _automation;
    }

    /// <summary>"1234"=pid；否则进程名子串 → pid。找不到/多进程歧义给中文提示。</summary>
    internal static (int Pid, string Name) ResolveProcess(string process)
    {
        var query = process.Trim();
        if (query.Length == 0)
            throw new UiException("目标进程不能为空：给 pid 或进程名（如 CoreMes、UiSampleApp）。");

        if (int.TryParse(query, out var pid))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                return (p.Id, p.ProcessName);
            }
            catch (Exception)
            {
                throw new UiException($"进程 pid={pid} 不存在（可能已退出）。用 debug_processes 或任务管理器确认。");
            }
        }

        var matches = Process.GetProcesses()
            .Where(p => p.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0)
            throw new UiException($"未找到进程名含「{query}」的运行进程。确认进程已启动；用 debug_processes 列本机 .NET 进程。");
        // 有窗口的进程优先（多数目标 UI 应用），其次取第一个
        var best = matches.FirstOrDefault(p => Safe(() => p.MainWindowHandle != IntPtr.Zero)) ?? matches[0];
        if (matches.Count > 1)
        {
            var extra = matches.Where(m => m.Id != best.Id).Select(m => $"{m.ProcessName}({m.Id})").Take(3);
            throw new UiException($"进程名「{query}」匹配 {matches.Count} 个进程（{string.Join("、", extra)}…），请用 pid 精确指定（如 {best.Id}）。");
        }
        return (best.Id, best.ProcessName);
    }

    private static AutomationElement? FindWindow(UIA3Automation automation, int pid, string title)
    {
        try
        {
            var desktop = automation.GetDesktop();
            var windows = title.Length == 0
                ? desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window).And(cf.ByProcessId(pid)))
                : desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)
                    .And(cf.ByProcessId(pid))
                    .And(cf.ByName(title)));
            return windows.Length == 0 ? null : windows[0];
        }
        catch (Exception ex)
        {
            throw new UiException($"读取窗口失败：{ex.Message}");
        }
    }

    /// <summary>控件类型解析：常见 UI 框架别名归一后按 ControlType 枚举忽略大小写匹配；空=不限；未知给中文提示。</summary>
    private static string? ResolveControlType(string type)
    {
        var t = type.Trim();
        if (t.Length == 0) return null;
        var normalized = t.ToLowerInvariant() switch
        {
            "textblock" => "Text",      // WPF TextBlock → UIA ControlType.Text
            "textbox" => "Edit",        // WinForms/WPF TextBox → UIA Edit
            "listbox" => "List",        // ListBox → UIA List
            "datagrid" => "DataGrid",
            "gridview" => "DataGrid",
            _ => t,
        };
        if (!Enum.TryParse<ControlType>(normalized, ignoreCase: true, out _))
            throw new UiException($"控件类型无效：{type}。UIA 类型名如 Button/Text/Edit/List/ListItem/CheckBox/RadioButton/ComboBox/Window；用 ui_find 不带 type 可看到实际类型。");
        return normalized;
    }

    private static string TypeSuffix(string? typeFilter) => typeFilter is null ? "" : $"（类型 {typeFilter}）";

    /// <summary>物理动作前激活窗口：最小化先还原（WindowPattern），再 SetForegroundWindow（尽力而为，失败不致命——左键点击本身会激活窗口）。</summary>
    private static void ActivateWindow(AutomationElement window)
    {
        try
        {
            if (window.Patterns.Window.PatternOrDefault is { } wp)
            {
                try
                {
                    if (wp.WindowVisualState.ValueOrDefault == WindowVisualState.Minimized)
                        wp.SetWindowVisualState(WindowVisualState.Normal);
                }
                catch { /* 还原失败忽略，点击坐标依然有效 */ }
            }
        }
        catch { /* 无 WindowPattern 的窗口忽略 */ }
        try
        {
            var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
            if (hwnd != IntPtr.Zero) User32.SetForegroundWindow(hwnd);
        }
        catch { /* 前台切换受 Windows 约束，失败时点击/滚动仍尽力执行 */ }
    }

    private static bool TryGetTargetPoint(AutomationElement element, out Point point)
    {
        try
        {
            if (element.TryGetClickablePoint(out point)) return true;
        }
        catch { /* 落 BoundingRectangle 中心 */ }
        var rect = SafeRead(() => element.BoundingRectangle, Rectangle.Empty);
        if (!rect.IsEmpty)
        {
            point = new Point((int)Math.Round(rect.X + rect.Width / 2.0), (int)Math.Round(rect.Y + rect.Height / 2.0));
            return true;
        }
        point = default;
        return false;
    }

    private static Point CenterPoint(AutomationElement? target, AutomationElement window)
    {
        if (target is not null && TryGetTargetPoint(target, out var p)) return p;
        var wRect = SafeRead(() => window.BoundingRectangle, Rectangle.Empty);
        return wRect.IsEmpty
            ? new Point(0, 0)
            : new Point((int)Math.Round(wRect.X + wRect.Width / 2.0), (int)Math.Round(wRect.Y + wRect.Height / 2.0));
    }

    private static string FormatPoint(Point p) => $"{p.X:0},{p.Y:0}";

    private static void WaitAfterAction() => Thread.Sleep(150); // 等目标 UI 线程处理完 SendInput 再返回（快照刷新）

    /// <summary>目标 pid 的元数据模块路径：apphost 主模块是原生 exe 无 CLI 元数据 → 同名 .dll 优先（现代 .NET 布局）。</summary>
    private static string? TryGetMetadataModule(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            var main = p.MainModule?.FileName;
            if (string.IsNullOrEmpty(main)) return null;
            if (string.Equals(Path.GetExtension(main), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                var dll = Path.ChangeExtension(main, ".dll");
                return File.Exists(dll) ? dll : main;
            }
            return main;
        }
        catch
        {
            return null; // 目标权限/位数导致读不到 PEB——降级为无语义标注
        }
    }

    private static T SafeRead<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }

    private static T Safe<T>(Func<T> f)
    {
        try { return f(); }
        catch { return default!; }
    }
}
