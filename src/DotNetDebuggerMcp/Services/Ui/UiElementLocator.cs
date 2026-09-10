using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using System.Text;

namespace DotNetDebuggerMcp.Services.Ui;

/// <summary>
/// index 缓存的身份条件（纯数据）：AutoId/Name/ControlType 三元组精确锁定同一实体，Ordinal 为该三元组在
/// 全树遍历中的出现序号（0 起）。重解析时必须用<b>三者全部</b>一致判定（<see cref="UiElementLocator.SameIdentity"/>），
/// 否则「同名跨类型且无 AutomationId」会命中错误控件。
/// </summary>
internal sealed record TargetDescriptor(string AutoId, string Name, string ControlType, int Ordinal);

/// <summary>
/// U1A 元素定位（spec §8）：进程/窗口解析、条件过滤 + 能力探测 + 语义标注、**定位条件缓存**（index 映射到
/// pid + 窗口标题 + AutoId/Name/ControlType + 序号），动作前按条件重解析 + 有限重试；虚拟化项经
/// ItemContainerPattern → VirtualizedItemPattern.Realize → ScrollItemPattern.ScrollIntoView 实体化。
/// 不长期持有 live AutomationElement（缓存仅存定位条件）。
/// </summary>
[SupportedOSPlatform("windows7.0")]
internal sealed class UiElementLocator
{
    private sealed record CachedEntry(int Pid, string WindowTitle, TargetDescriptor Descriptor);

    private readonly List<CachedEntry> _lastFind = new();
    private int _lastFindPid = -1;
    private string _lastFindWindowTitle = "";
    private bool _lastFindTruncated;

    /// <summary>最近一次 ui_find 的目标 pid（供工具头部展示）。</summary>
    public int LastFindPid => _lastFindPid;

    /// <summary>最近一次 ui_find 命中的窗口标题（供工具头部展示）。</summary>
    public string LastFindWindowTitle => _lastFindWindowTitle;

    /// <summary>最近一次 ui_find 是否因 limit 截断（供工具层如实报告总量）。</summary>
    public bool LastFindTruncated => _lastFindTruncated;

    /// <summary>
    /// 重解析身份匹配（纯函数，可单测）：AutoId、Name、ControlType 三者<b>全部</b>一致才算同一实体——
    /// 与 <see cref="Find"/> 的 ordinal 计数口径一致（计数键同由这三个字段构成）。
    /// </summary>
    internal static bool SameIdentity(TargetDescriptor descriptor, string autoId, string name, string controlType)
        => string.Equals(autoId, descriptor.AutoId, StringComparison.Ordinal)
        && string.Equals(name, descriptor.Name, StringComparison.Ordinal)
        && string.Equals(controlType, descriptor.ControlType, StringComparison.OrdinalIgnoreCase);

    /// <summary>ordinal 计数键（纯函数，可单测）：同 AutoId+Name+ControlType 三元组内按遍历顺序计数。</summary>
    internal static string OrdinalKey(string autoId, string name, string controlType)
        => autoId + "\u001F" + name + "\u001F" + controlType;

    /// <summary>按进程/窗口条件查找控件清单（返回前 limit 条并更新 index 条件缓存）。</summary>
    public IReadOnlyList<UiElementInfo> Find(UIA3Automation automation, string process, string title, string text, string type, string automationId, int limit)
    {
        var (pid, _) = ResolveProcess(process);
        var typeFilter = ResolveControlType(type); // 先校验类型（fail-fast，不触碰 UIA）
        var window = FindWindow(automation, pid, title)
            ?? throw new UiException($"进程 {process}（pid={pid}）没有 UIA 可见顶层窗口。确认目标 UI 应用已启动且主窗口已创建；若窗口开在其它桌面/以管理员运行，本进程需同权限才能访问。");

        var cap = Math.Clamp(limit <= 0 ? 50 : limit, 1, 500);

        AutomationElement[] all;
        try { all = window.FindAllDescendants(); }
        catch (Exception ex) { throw new UiException($"读取窗口控件树失败：{ex.Message}"); }

        var entries = new List<CachedEntry>(Math.Min(cap, 64));
        var infos = new List<UiElementInfo>(Math.Min(cap, 64));
        var semanticModule = TryGetMetadataModule(pid);
        var semanticByName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var truncated = false;

        foreach (var el in all)
        {
            if (infos.Count >= cap) { truncated = true; break; }

            var elType = SafeRead(() => el.ControlType.ToString(), "");
            var name = SafeRead(() => el.Name ?? "", "");
            var autoId = SafeRead(() => el.Properties.AutomationId.ValueOrDefault ?? "", "");

            // 序号按「全部后代」累计（重解析时同样遍历全树）；键与重解析的 SameIdentity 完全同源（AutoId+Name+ControlType），
            // 过滤掉同名先前项也不会错位。
            var key = OrdinalKey(autoId, name, elType);
            ordinals.TryGetValue(key, out var ordinal);
            ordinals[key] = ordinal + 1;

            if (typeFilter is not null && !string.Equals(elType, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(automationId) && !string.Equals(autoId, automationId, StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(text)
                && !name.Contains(text, StringComparison.OrdinalIgnoreCase)
                && !autoId.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;

            var caps = UiPatternCapabilities.Probe(el);
            var rect = SafeRead(() => el.BoundingRectangle, Rectangle.Empty);
            var rectText = rect.IsEmpty ? "不可见/无几何" : $"{rect.X:0},{rect.Y:0} {rect.Width:0}x{rect.Height:0}";

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

            var index = infos.Count;
            infos.Add(new UiElementInfo(index, name, elType, autoId, rectText, caps.Describe(), semantic));
            entries.Add(new CachedEntry(pid, SafeRead(() => window.Name ?? "", ""), new TargetDescriptor(autoId, name, elType, ordinal)));
        }

        _lastFind.Clear();
        _lastFind.AddRange(entries);
        _lastFindPid = pid;
        _lastFindWindowTitle = entries.Count > 0 ? entries[0].WindowTitle : SafeRead(() => window.Name ?? "", "");
        _lastFindTruncated = truncated;

        return infos;
    }

    /// <summary>解析动作目标（index 优先按缓存条件重解析；否则 name/type 即时唯一定位）。含 1-2 次失效重试。</summary>
    public (AutomationElement Element, AutomationElement Window, string Label) ResolveForAction(
        UIA3Automation automation, int pid, int index, string name, string type)
    {
        if (index >= 0)
        {
            var entry = GetCachedEntry(pid, index);
            return ResolveByDescriptor(automation, pid, entry);
        }

        return ResolveByName(automation, pid, name, type);
    }

    /// <summary>解析该进程首个顶层窗口（windowstate 用；不参与 index/name/type 定位）。</summary>
    public static AutomationElement ResolveTopWindow(UIA3Automation automation, string process)
    {
        var (pid, _) = ResolveProcess(process);
        return FindWindow(automation, pid, "")
            ?? throw new UiException($"进程 {process}（pid={pid}）没有 UIA 可见顶层窗口。");
    }

    private (AutomationElement Element, AutomationElement Window, string Label) ResolveByDescriptor(
        UIA3Automation automation, int pid, CachedEntry entry)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            UIA3Automation aut = automation;
            var window = FindWindow(aut, pid, entry.WindowTitle) ?? FindWindow(aut, pid, "");
            if (window is not null)
            {
                try
                {
                    var all = window.FindAllDescendants();
                    var matches = new List<AutomationElement>();
                    foreach (var el in all)
                        if (Matches(el, entry.Descriptor)) matches.Add(el);
                    if (matches.Count > entry.Descriptor.Ordinal)
                    {
                        var el = matches[entry.Descriptor.Ordinal];
                        return (el, window, DisplayLabel(entry.Descriptor));
                    }
                }
                catch { /* 瞬时失效：重试 */ }
            }
            Thread.Sleep(150);
        }
        throw new UiException("目标已变化，请重新 ui_find。");
    }

    private (AutomationElement Element, AutomationElement Window, string Label) ResolveByName(
        UIA3Automation automation, int pid, string name, string type)
    {
        var typeFilter = ResolveControlType(type);
        var window = FindWindow(automation, pid, "")
            ?? throw new UiException($"进程 pid={pid} 没有 UIA 可见顶层窗口。");

        var found = FindUniqueByName(window, name, typeFilter, allowVirtualize: true);
        if (found.Ambiguous)
            throw new UiException(found.Message);
        if (found.Element is null)
        {
            if (!string.IsNullOrEmpty(name))
                throw new UiException($"未找到名称/文本含「{name}」的控件{TypeSuffix(typeFilter)}。先 ui_find 看可用控件清单。");
            if (typeFilter is null)
                throw new UiException("ui_action/ui_input/ui_get 需要 index、name 或 type 之一来定位目标。");
            throw new UiException($"未找到类型为「{typeFilter}」的控件。先 ui_find 看实际控件类型。");
        }

        return (found.Element, window, DisplayLabelOrDefault(found.Element, name));
    }

    private sealed record FindResult(AutomationElement? Element, bool Ambiguous, string Message);

    private static FindResult FindUniqueByName(AutomationElement window, string name, string? typeFilter, bool allowVirtualize)
    {
        AutomationElement[] all;
        try { all = window.FindAllDescendants(); }
        catch (Exception ex) { throw new UiException($"读取窗口控件树失败：{ex.Message}"); }

        var typed = new List<AutomationElement>();
        foreach (var el in all)
        {
            if (typeFilter is not null)
            {
                var elType = SafeRead(() => el.ControlType.ToString(), "");
                if (!string.Equals(elType, typeFilter, StringComparison.OrdinalIgnoreCase)) continue;
            }
            typed.Add(el);
        }

        if (!string.IsNullOrEmpty(name))
        {
            var hits = new List<(AutomationElement El, string Hit)>();
            foreach (var el in typed)
            {
                var n = SafeRead(() => el.Name ?? "", "");
                var a = SafeRead(() => el.Properties.AutomationId.ValueOrDefault ?? "", "");
                if (n.Contains(name, StringComparison.OrdinalIgnoreCase) || a.Contains(name, StringComparison.OrdinalIgnoreCase))
                    hits.Add((el, n.Length > 0 ? n : a));
            }

            if (hits.Count == 0 && allowVirtualize)
            {
                var realized = TryRealizeVirtualized(window, name);
                if (realized is not null)
                {
                    var hitText = SafeRead(() => realized.Name ?? "", name);
                    return new FindResult(realized, false, hitText);
                }
            }

            if (hits.Count == 0) return new FindResult(null, false, "");
            if (hits.Count > 1)
            {
                var sb = new StringBuilder();
                sb.Append($"「{name}」命中 {hits.Count} 个控件，无法唯一确定：");
                for (var i = 0; i < hits.Count && i < 5; i++)
                    sb.Append($"[{i}] {SafeRead(() => hits[i].El.ControlType.ToString(), "?")} Name={hits[i].Hit} ");
                if (hits.Count > 5) sb.Append("…");
                sb.Append("请先 ui_find 定位后按 index 操作。");
                return new FindResult(null, true, sb.ToString());
            }
            return new FindResult(hits[0].El, false, hits[0].Hit);
        }

        // 无 name：要求 type 唯一
        if (typed.Count == 0) return new FindResult(null, false, "");
        if (typed.Count > 1)
            return new FindResult(null, true, $"类型「{typeFilter}」命中 {typed.Count} 个控件，无法唯一确定。请给出 name 或用 ui_find 的 index。");
        return new FindResult(typed[0], false, "");
    }

    /// <summary>虚拟化实体化：容器 ItemContainerPattern 按 Name 找项 → Realize → ScrollIntoView。失败/不支持返回 null。</summary>
    private static AutomationElement? TryRealizeVirtualized(AutomationElement window, string name)
    {
        try
        {
            var nameProperty = window.Automation.PropertyLibrary.Element.Name;
            AutomationElement[] all;
            try { all = window.FindAllDescendants(); }
            catch { return null; }

            foreach (var container in all)
            {
                if (!SafeRead(() => container.Patterns.ItemContainer.IsSupported, false)) continue;
                var item = SafeReadNullable(() =>
                {
                    var pattern = container.Patterns.ItemContainer.PatternOrDefault;
                    return pattern?.FindItemByProperty(null, nameProperty, name);
                });
                if (item is null) continue;
                try { item.Patterns.VirtualizedItem.PatternOrDefault?.Realize(); } catch { }
                try { item.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView(); } catch { }
                return item;
            }
        }
        catch { /* provider 不支持/瞬时失效：降级为未命中 */ }
        return null;
    }

    private static bool Matches(AutomationElement el, TargetDescriptor descriptor)
        => SameIdentity(
            descriptor,
            SafeRead(() => el.Properties.AutomationId.ValueOrDefault ?? "", ""),
            SafeRead(() => el.Name ?? "", ""),
            SafeRead(() => el.ControlType.ToString(), ""));

    private CachedEntry GetCachedEntry(int pid, int index)
    {
        if (_lastFindPid != pid || _lastFind.Count == 0)
        {
            var current = _lastFindPid;
            _lastFind.Clear();
            _lastFindPid = -1;
            throw new UiException($"index={index} 引用的不是进程 pid={pid} 的 ui_find 结果（当前缓存 pid={current}）。请先对该进程执行 ui_find。");
        }
        if (index < 0 || index >= _lastFind.Count)
            throw new UiException($"index={index} 超出最近 ui_find 结果范围（0..{_lastFind.Count - 1}）。请重新 ui_find。");
        return _lastFind[index];
    }

    private static string DisplayLabel(TargetDescriptor descriptor)
    {
        if (!string.IsNullOrEmpty(descriptor.Name)) return $"控件「{descriptor.Name}」";
        if (!string.IsNullOrEmpty(descriptor.AutoId)) return $"控件 AutoId={descriptor.AutoId}";
        return $"控件 ({descriptor.ControlType})";
    }

    private static string DisplayLabelOrDefault(AutomationElement element, string fallback)
    {
        var name = SafeRead(() => element.Name ?? "", "");
        if (name.Length > 0) return $"{SafeRead(() => element.ControlType.ToString(), "?")}「{name}」";
        var autoId = SafeRead(() => element.Properties.AutomationId.ValueOrDefault ?? "", "");
        if (autoId.Length > 0) return $"{SafeRead(() => element.ControlType.ToString(), "?")} (AutoId={autoId})";
        return string.IsNullOrEmpty(fallback) ? SafeRead(() => element.ControlType.ToString(), "控件") : fallback;
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
        var best = matches.FirstOrDefault(p => SafeRead(() => p.MainWindowHandle != IntPtr.Zero, false)) ?? matches[0];
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
    internal static string? ResolveControlType(string type)
    {
        var t = type.Trim();
        if (t.Length == 0) return null;
        var normalized = t.ToLowerInvariant() switch
        {
            "textblock" => "Text",
            "textbox" => "Edit",
            "listbox" => "List",
            "datagrid" => "DataGrid",
            "gridview" => "DataGrid",
            _ => t,
        };
        if (!Enum.TryParse<ControlType>(normalized, ignoreCase: true, out _))
            throw new UiException($"控件类型无效：{type}。UIA 类型名如 Button/Text/Edit/List/ListItem/CheckBox/RadioButton/ComboBox/Window；用 ui_find 不带 type 可看到实际类型。");
        return normalized;
    }

    private static string TypeSuffix(string? typeFilter) => typeFilter is null ? "" : $"（类型 {typeFilter}）";

    /// <summary>反查命中候选文本（前 3 条 + …）。</summary>
    private static string? BuildSemanticText(string modulePath, string elementName)
    {
        var candidates = UiSemanticResolver.Lookup(modulePath, elementName);
        if (candidates is null || candidates.Count == 0) return null;
        var shown = candidates.Take(3);
        return candidates.Count > 3 ? string.Join("；", shown) + "；…" : string.Join("；", shown);
    }

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
            return null;
        }
    }

    private static T SafeRead<T>(Func<T> f, T fallback)
    {
        try { return f(); }
        catch { return fallback; }
    }

    private static AutomationElement? SafeReadNullable(Func<AutomationElement?> f)
    {
        try { return f(); }
        catch { return null; }
    }
}
