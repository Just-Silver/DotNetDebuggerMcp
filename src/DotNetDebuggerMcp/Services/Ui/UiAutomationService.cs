using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

using System.Runtime.Versioning;

namespace DotNetDebuggerMcp.Services.Ui;

/// <summary>
/// U1A UI 自动化 facade（spec §13）：gate + 5s 双层超时 + 编排 Find/Action/Input/Get/Wait。纯 UIA 语义 pattern
/// （无光标移动 / 无输入注入 / 不抢前台；仅窗口最小化时经 WindowPattern 还原）。单 UIA3Automation 实例 +
/// SemaphoreSlim(1,1) 串行；跨进程调用 5s 兜底（Invoke 阻塞按「放弃等待」）。写操作由调用方打 AgentActionLog。
/// </summary>
[SupportedOSPlatform("windows7.0")]
internal sealed class UiAutomationService
{
    internal static UiAutomationService Instance { get; } = new();

    private static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly UiElementLocator _locator = new();
    private readonly UiEventWaiter _waiter = new();
    private UIA3Automation? _automation;

    private UiAutomationService()
    {
    }

    /// <summary>最近一次 ui_find 的目标 pid（供工具头部展示）。</summary>
    public int LastFindPid => _locator.LastFindPid;

    /// <summary>最近一次 ui_find 命中的窗口标题（供工具头部展示）。</summary>
    public string LastFindWindowTitle => _locator.LastFindWindowTitle;

    /// <summary>最近一次 ui_find 是否因 limit 截断（供工具层如实报告总量）。</summary>
    public bool LastFindTruncated => _locator.LastFindTruncated;

    /// <summary>按进程/窗口条件查找控件清单（返回前 limit 条并更新 index 条件缓存）。</summary>
    public Task<IReadOnlyList<UiElementInfo>> FindAsync(string process, string title, string text, string type, string automationId, int limit, CancellationToken ct)
        => RunGateAsync(() => _locator.Find(GetAutomation(), process.Trim(), title.Trim(), text.Trim(), type.Trim(), automationId.Trim(), limit), ct);

    /// <summary>对目标控件执行语义动作（verb；windowstate 忽略 index/name/type 定位顶层窗口）。</summary>
    public Task<UiActionResult> ActionAsync(string process, string verb, int index, string name, string type, string direction, int lines, string windowstate, CancellationToken ct)
        => RunGateAsync(() => ActionCore(process, verb, index, name, type, direction, lines, windowstate), ct);

    /// <summary>对目标控件写入值（Value → RangeValue → LegacyIAccessible）。</summary>
    public Task<UiActionResult> InputAsync(string process, string value, int index, string name, string type, CancellationToken ct)
        => RunGateAsync(() => InputCore(process, value, index, name, type), ct);

    /// <summary>读取目标控件状态（what 映射；返回未脱敏原始值，脱敏由调用方输出层做）。</summary>
    public Task<UiStateResult> GetAsync(string process, string what, int index, string name, string type, CancellationToken ct)
        => RunGateAsync(() => GetCore(process, what, index, name, type), ct);

    /// <summary>事件化等待控件出现/文本变化（事件订阅 + 200ms 轮询兜底；超时返回当前状态不报错）。</summary>
    public async Task<UiWaitResult> WaitAsync(string process, string text, string type, string textChangedFrom, string textChangedTo, int timeoutSeconds, CancellationToken ct)
    {
        var timeout = Math.Clamp(timeoutSeconds, 1, 300);
        var modeText = !string.IsNullOrEmpty(text);
        if (!modeText && (string.IsNullOrEmpty(textChangedFrom) || string.IsNullOrEmpty(textChangedTo)))
            throw new UiException("ui_wait 需给出 text（等控件出现）或 textChangedFrom+textChangedTo 成对（等文本变化）。");

        var probeText = modeText ? text : textChangedTo;

        // 1) 解析窗口 + 类型过滤 + 首次探测（gate 内 + 5s 兜底）。
        var (window, typeFilter, immediate) = await RunGateAsync(() =>
        {
            var w = UiElementLocator.ResolveTopWindow(GetAutomation(), process);
            var tf = UiElementLocator.ResolveControlType(type);
            return (w, tf, FindFirstName(w, probeText, tf));
        }, ct);
        if (immediate is not null)
            return HitResult(modeText, immediate, typeFilter, textChangedFrom);

        // 2) 订阅（gate 内）→ 等待循环（**期间不持 gate**，每轮探测取放一次）→ 退订（gate 内）。
        //    旧实现整段等待都持 gate（最长 300s），会阻塞并发 ui_*（含本应触发等待条件的 ui_action）——此处改为 U1 的每轮取放锁。
        IDisposable? subscription = null;
        var signal = NewSignal();
        try
        {
            subscription = await RunGateAsync(() => _waiter.Subscribe(window, () => signal.TrySetResult(true)), ct);

            var deadline = DateTime.UtcNow.AddSeconds(timeout);
            while (true)
            {
                string? hit;
                try { hit = await RunGateAsync(() => FindFirstName(window, probeText, typeFilter), ct); }
                catch (UiException) { hit = null; } // 单轮探测超时视为未命中（继续等，整体受 timeout 约束）
                if (hit is not null)
                    return HitResult(modeText, hit, typeFilter, textChangedFrom);

                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) break;

                var wait = (int)Math.Min(200, remaining.TotalMilliseconds);
                var wake = await Task.WhenAny(signal.Task, Task.Delay(wait, ct)).ConfigureAwait(false);
                if (wake == signal.Task) signal = NewSignal(); // 事件可能与本条件无关，重置后继续等
            }
        }
        finally
        {
            if (subscription is not null)
            {
                var sub = subscription;
                try { await RunGateAsync(() => { sub.Dispose(); return true; }, CancellationToken.None); } catch { /* 退订失败忽略 */ }
            }
        }

        // 3) 超时：读一次当前状态（gate 内 + 5s 兜底；失败不报错）。
        string? still = null;
        try { still = await RunGateAsync(() => FindFirstName(window, probeText, typeFilter), ct); } catch { }
        return new UiWaitResult("超时", still is null
            ? $"等待 {timeout}s 未命中（事件化等待 + 200ms 轮询兜底）。"
            : $"等待 {timeout}s 未命中（当前仍: {still}）。");
    }

    private static UiWaitResult HitResult(bool modeText, string hit, string? typeFilter, string textChangedFrom)
        => modeText
            ? new UiWaitResult("出现", $"控件已出现：{TypeSuffix(typeFilter)}文本「{hit}」。")
            : new UiWaitResult("已变化", $"控件文本已变为「{hit}」（原「{textChangedFrom}」）。");

    private static TaskCompletionSource<bool> NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // ===== 核心（gate 内串行 + 5s 兜底） =====

    private UiActionResult ActionCore(string process, string verb, int index, string name, string type, string direction, int lines, string windowstate)
    {
        var verbNorm = verb.Trim().ToLowerInvariant();
        if (verbNorm is "rightclick" or "doubleclick")
            throw new UiException("UIA 无该语义入口，物理输入已移除；请改用等价菜单/命令的 ui_action verb=invoke。");
        if (verbNorm.Length == 0)
            throw new UiException($"verb 不能为空（可选 {string.Join("/", UiPatternDispatcher.Verbs)}）。");
        if (verbNorm != "windowstate" && !UiPatternDispatcher.Verbs.Contains(verbNorm))
            throw new UiException($"verb 无效：{verb}（可选 {string.Join("/", UiPatternDispatcher.Verbs)}；无右键/双击——物理输入已移除）。");

        // 参数先于 UIA 目标解析校验（fail-fast：纯参数错误不触碰跨进程 UIA，避免环境慢时误报超时）。
        if (verbNorm == "scroll")
        {
            var dir = direction.Trim().ToLowerInvariant();
            if (dir is not ("up" or "down"))
                throw new UiException($"direction 无效：{direction}（可选 up / down）。");
        }
        if (verbNorm == "windowstate")
        {
            var state = windowstate.Trim().ToLowerInvariant();
            if (state is not ("normal" or "maximized" or "minimized"))
                throw new UiException($"windowstate 无效：{windowstate}（可选 normal / maximized / minimized）。");
        }

        var automation = GetAutomation();
        var (pid, _) = UiElementLocator.ResolveProcess(process);

        AutomationElement element;
        AutomationElement window;
        if (verbNorm == "windowstate")
        {
            window = UiElementLocator.ResolveTopWindow(automation, process);
            element = window;
        }
        else
        {
            (element, window, _) = _locator.ResolveForAction(automation, pid, index, name.Trim(), type.Trim());
            EnsureWindowRestored(window);
        }

        var caps = UiPatternCapabilities.Probe(element);
        var chosen = UiPatternDispatcher.Choose(verbNorm, caps, direction, lines, windowstate);
        UiPatternDispatcher.Execute(element, chosen);
        var verbDisplay = chosen.Kind == UiActionKind.Window ? $"windowstate={chosen.WindowState}" : verbNorm;
        return new UiActionResult(true, $"已 {verbDisplay}（{chosen.PatternName}）");
    }

    private UiActionResult InputCore(string process, string value, int index, string name, string type)
    {
        if (string.IsNullOrEmpty(value))
            throw new UiException("value 不能为空：ui_input 需给出要写入的值。");
        var automation = GetAutomation();
        var (pid, _) = UiElementLocator.ResolveProcess(process);
        var (element, window, _) = _locator.ResolveForAction(automation, pid, index, name.Trim(), type.Trim());
        EnsureWindowRestored(window);

        var caps = UiPatternCapabilities.Probe(element);
        var candidates = UiPatternDispatcher.ChooseInputCandidates(caps);
        UiException? readOnly = null;
        foreach (var choice in candidates)
        {
            var before = TryReadInputValue(element, choice);
            try { UiPatternDispatcher.ExecuteInput(element, choice, value); }
            catch (UiException ex) when (ex.Message.Contains("只读", StringComparison.Ordinal))
            {
                readOnly ??= ex; // 该 pattern 只读：记下并尝试下一候选（如 Value 只读但 RangeValue 可写）
                continue;
            }

            var after = TryReadInputValue(element, choice);
            // 无法读回（provider 不暴露读值）时信任写入；可读回则要求值确有反映，否则退下一候选——
            // 防 WinForms 滚动条经 MSAA-UIA 桥暴露的 ValuePattern.IsReadOnly 误报 false 而 SetValue 静默 no-op
            //（返回假成功；2026-09-10 远程 CI 实证）。
            if (before is null || after is null || Reflected(before, after, value))
                return new UiActionResult(true, $"已 input（{choice.PatternName}）");
        }

        if (readOnly is not null) throw readOnly;
        throw new UiException("写入未生效：控件暴露的写值 pattern（Value/RangeValue/LegacyIAccessible）均未反映新值。");
    }

    /// <summary>读回当前值（Value/RangeValue；Legacy 不读值返回 null 表示不可校验）。读取失败返回 null（不可校验）。</summary>
    private static string? TryReadInputValue(AutomationElement element, ChosenInput choice)
    {
        try
        {
            return choice.Kind switch
            {
                UiInputKind.Value => element.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault,
                UiInputKind.RangeValue => element.Patterns.RangeValue.PatternOrDefault?.Value.ValueOrDefault
                    .ToString(System.Globalization.CultureInfo.InvariantCulture),                _ => null,
            };
        }
        catch { return null; }
    }

    /// <summary>写值是否已反映：等于期望、或相比写入前发生变化、或数值等价。</summary>
    private static bool Reflected(string before, string after, string desired)
        => string.Equals(after, desired, StringComparison.Ordinal)
        || !string.Equals(before, after, StringComparison.Ordinal)
        || (double.TryParse(after, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var a)
            && double.TryParse(desired, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            && a == d);

    private UiStateResult GetCore(string process, string what, int index, string name, string type)
    {
        var whatNorm = what.Trim().ToLowerInvariant();
        if (whatNorm.Length == 0)
            throw new UiException($"what 不能为空（可选 {string.Join("/", UiStateReader.Whats)}）。");
        var automation = GetAutomation();
        var (pid, _) = UiElementLocator.ResolveProcess(process);
        var (element, _, _) = _locator.ResolveForAction(automation, pid, index, name.Trim(), type.Trim());

        var caps = UiPatternCapabilities.Probe(element);
        var chosen = UiStateReader.Choose(whatNorm, caps);
        return UiStateReader.Execute(element, chosen);
    }

    /// <summary>仅当目标窗口最小化时经 WindowPattern 还原（不抢前台；还原是否激活取决于目标应用，spec §9）。</summary>
    private static void EnsureWindowRestored(AutomationElement window)
    {
        try
        {
            if (window.Patterns.Window.PatternOrDefault is { } wp
                && wp.WindowVisualState.ValueOrDefault == WindowVisualState.Minimized)
                wp.SetWindowVisualState(WindowVisualState.Normal);
        }
        catch { /* 无 WindowPattern / 还原失败忽略（语义动作不依赖前台） */ }
    }

    /// <summary>窗口后代中首个 Name 含 probe 的控件名（type 过滤可选）；未命中返回 null（不抛）。</summary>
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
            return null;
        }
    }

    private static string TypeSuffix(string? typeFilter) => typeFilter is null ? "" : $"（类型 {typeFilter}）";

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
        return await task;
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

    private static T SafeRead<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }
}
