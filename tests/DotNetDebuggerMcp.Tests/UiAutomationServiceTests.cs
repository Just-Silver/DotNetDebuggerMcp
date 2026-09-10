using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Services.Ui;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// U1 UiAutomationService / UiSemanticResolver 纯单测（可脱机部分）：
/// 语义反查（倒排索引构建/正负路径/缓存）在此覆盖；真实 UIA 调用（Find/Invoke/Scroll/Wait）走 e2e（DebugUiToolsTests）。
/// </summary>
public sealed class UiSemanticResolverTests
{
    /// <summary>语义反查素材：DebugTarget.dll（apphost 的元数据在同名 dll，非 exe——与线上布局一致）。</summary>
    private static string DebugTargetDll => Path.Combine(
        Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, "DebugTarget.dll");

    [Fact]
    public void Lookup_WorkBagOnDebugTargetDll_ReturnsProgramWorkBagCandidate()
    {
        var dll = DebugTargetDll;
        Assert.True(File.Exists(dll), "DebugTarget.dll 不存在，请先运行 generate-testdata.ps1");

        var candidates = UiSemanticResolver.Lookup(dll, "WorkBag");

        Assert.NotNull(candidates); // 有匹配返回非 null
        Assert.NotEmpty(candidates!);
        // 候选格式：类型全名.成员；DebugTarget.Program.WorkBag 含 "Program.WorkBag" 子串
        Assert.Contains(candidates!, c => c.Contains("Program.WorkBag", StringComparison.Ordinal));
    }

    [Fact]
    public void Lookup_UnknownName_ReturnsNull()
    {
        var dll = DebugTargetDll;
        Assert.True(File.Exists(dll), "DebugTarget.dll 不存在，请先运行 generate-testdata.ps1");

        var candidates = UiSemanticResolver.Lookup(dll, "NoSuchMember_xyz_987");

        Assert.Null(candidates);
    }

    [Fact]
    public void Lookup_IgnoreCaseSubstring_Matches()
    {
        var dll = DebugTargetDll;
        Assert.True(File.Exists(dll));

        // 忽略大小写子串：workbag / WORK / "bag" 都应命中 WorkBag 所在候选
        foreach (var query in new[] { "workbag", "WORK", "Bag" })
        {
            var candidates = UiSemanticResolver.Lookup(dll, query);
            Assert.NotNull(candidates);
            Assert.Contains(candidates!, c => c.EndsWith(".WorkBag", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Lookup_MissingOrUnreadableAssembly_ReturnsNull()
    {
        Assert.Null(UiSemanticResolver.Lookup(@"Z:\No\Such\Path\missing.dll", "WorkBag"));
        Assert.Null(UiSemanticResolver.Lookup("", "WorkBag"));
        Assert.Null(UiSemanticResolver.Lookup(DebugTargetDll, ""));
    }

    [Fact]
    public void Lookup_RepeatedQuery_HitsCacheWithoutReread()
    {
        var dll = DebugTargetDll;
        Assert.True(File.Exists(dll));

        // 两次查询结果一致；索引按 assemblyPath 缓存（第二次不再读盘——以结果等价与耗时不放大为观察，不断言内部计数）
        var first = UiSemanticResolver.Lookup(dll, "WorkBag");
        var second = UiSemanticResolver.Lookup(dll, "WorkBag");
        Assert.NotNull(first);
        Assert.Equal(first!, second!);
    }
}

/// <summary>
/// U1A UiPatternDispatcher 纯 Choose 决策（spec §6 逐行）：吃能力快照、不触碰 COM/WinForms。
/// Execute 真实 pattern 调用由 DebugUiToolsTests 跨进程 e2e 覆盖。
/// </summary>
public sealed class UiPatternDispatcherTests
{
    private static UiPatternCapabilities Caps(
        bool invoke = false, bool toggle = false, bool selectionItem = false, bool expandCollapse = false,
        bool value = false, bool rangeValue = false, bool scroll = false, bool scrollItem = false,
        bool window = false, bool legacy = false)
        => new(invoke, toggle, selectionItem, expandCollapse, value, rangeValue, scroll, scrollItem, window, legacy);

    [Theory]
    [InlineData("invoke", true, false, false, "Invoke", "InvokePattern")]
    [InlineData("invoke", false, false, true, "LegacyDefaultAction", "LegacyIAccessiblePattern")]
    [InlineData("toggle", false, true, false, "Toggle", "TogglePattern")]
    [InlineData("toggle", true, false, false, "Invoke", "InvokePattern")]
    [InlineData("select", false, false, true, "SelectionItem", "SelectionItemPattern")]
    [InlineData("select", true, false, false, "Invoke", "InvokePattern")]
    [InlineData("select", false, false, false, "LegacySelect", "LegacyIAccessiblePattern")]
    public void Choose_PrimaryAndFallback(string verb, bool invoke, bool toggle, bool selectionItem, string kind, string pattern)
    {
        var caps = Caps(invoke: invoke, toggle: toggle, selectionItem: selectionItem, legacy: true);
        // Select 的 Legacy 兜底优先级低于 Invoke：仅当无 Invoke 才落 Legacy（上面的 select/invoke/legacy 用例断言 Invoke）
        var chosen = UiPatternDispatcher.Choose(verb, caps, "", 3, "");
        Assert.Equal(kind, chosen.Kind.ToString());
        Assert.Equal(pattern, chosen.PatternName);
    }

    [Fact]
    public void Choose_ExpandCollapse_RequiresPattern()
    {
        Assert.Equal(UiActionKind.ExpandCollapse, UiPatternDispatcher.Choose("expand", Caps(expandCollapse: true), "", 3, "").Kind);
        Assert.Equal(UiActionKind.ExpandCollapse, UiPatternDispatcher.Choose("collapse", Caps(expandCollapse: true), "", 3, "").Kind);
        var ex = Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("expand", Caps(invoke: true), "", 3, ""));
        Assert.Contains("不可展开", ex.Message);
        var ex2 = Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("collapse", Caps(), "", 3, ""));
        Assert.Contains("不可折叠", ex2.Message);
    }

    [Fact]
    public void Choose_NoEntry_ChineseRefusals()
    {
        Assert.Contains("不支持无鼠标触发", Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("invoke", Caps(), "", 3, "")).Message);
        Assert.Contains("不支持选中", Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("select", Caps(), "", 3, "")).Message);
        Assert.Contains("不支持滚动", Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("scroll", Caps(), "down", 3, "")).Message);
        Assert.Contains("不可滚入视野", Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("scrollintoview", Caps(), "", 3, "")).Message);
    }

    [Fact]
    public void Choose_Focus_OnWindow_RedirectsToWindowState()
    {
        Assert.Equal(UiActionKind.Focus, UiPatternDispatcher.Choose("focus", Caps(value: true), "", 3, "").Kind);
        var ex = Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("focus", Caps(window: true), "", 3, ""));
        Assert.Contains("windowstate", ex.Message);
        Assert.Contains("抢前台", ex.Message);
    }

    [Fact]
    public void Choose_Scroll_DirectionValidation_AndPattern()
    {
        var ex = Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("scroll", Caps(scroll: true), "sideways", 3, ""));
        Assert.Contains("direction 无效", ex.Message);
        Assert.Contains("up / down", ex.Message);
        var chosen = UiPatternDispatcher.Choose("scroll", Caps(scroll: true), "down", 7, "");
        Assert.Equal(UiActionKind.Scroll, chosen.Kind);
        Assert.Equal("down", chosen.Direction);
        Assert.Equal(7, chosen.Lines);
    }

    [Fact]
    public void Choose_WindowState_ValidatesStateAndPattern()
    {
        var ex = Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("windowstate", Caps(window: true), "", 0, "sideways"));
        Assert.Contains("windowstate 无效", ex.Message);
        var noPattern = Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("windowstate", Caps(), "", 0, "normal"));
        Assert.Contains("无 WindowPattern", noPattern.Message);
        var chosen = UiPatternDispatcher.Choose("windowstate", Caps(window: true), "", 0, "maximized");
        Assert.Equal(UiActionKind.Window, chosen.Kind);
        Assert.Equal("maximized", chosen.WindowState);
    }

    [Fact]
    public void Choose_UnknownVerb_ChineseError()
    {
        var ex = Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("hover", Caps(), "", 3, ""));
        Assert.Contains("verb 无效", ex.Message);
        Assert.Contains("物理输入已移除", ex.Message);
    }

    [Fact]
    public void ChooseInput_PriorityValueThenRangeThenLegacy()
    {
        Assert.Equal(UiInputKind.Value, UiPatternDispatcher.ChooseInput(Caps(value: true, rangeValue: true, legacy: true)).Kind);
        Assert.Equal(UiInputKind.RangeValue, UiPatternDispatcher.ChooseInput(Caps(rangeValue: true, legacy: true)).Kind);
        Assert.Equal(UiInputKind.LegacySetValue, UiPatternDispatcher.ChooseInput(Caps(legacy: true)).Kind);
        var ex = Assert.Throws<UiException>(() => UiPatternDispatcher.ChooseInput(Caps(invoke: true)));
        Assert.Contains("不支持写值", ex.Message);
    }

    [Fact]
    public void Capabilities_Describe_ListsOnlySupportedInOrder()
    {
        var caps = Caps(invoke: true, toggle: true, value: true, window: true);
        Assert.Equal("invoke,toggle,value,window", caps.Describe());
        Assert.Equal("", Caps().Describe());
    }
}

/// <summary>U1A UiStateReader 纯 Choose 决策（ui_get what → 品类）与失败文案。</summary>
public sealed class UiStateReaderTests
{
    private static UiPatternCapabilities Caps(bool value = false, bool rangeValue = false, bool toggle = false,
        bool selectionItem = false, bool expandCollapse = false, bool legacy = false)
        => new(false, toggle, selectionItem, expandCollapse, value, rangeValue, false, false, false, legacy);

    [Theory]
    [InlineData("name", "Name")]
    [InlineData("enabled", "Enabled")]
    [InlineData("offscreen", "Offscreen")]
    [InlineData("rect", "Rect")]
    [InlineData("helptext", "HelpText")]
    public void Choose_GenericProperties_AlwaysAvailable(string what, string kind)
        => Assert.Equal(kind, UiStateReader.Choose(what, Caps()).Kind.ToString());

    [Fact]
    public void Choose_PatternBackedProperties()
    {
        Assert.Equal(UiStateKind.Value, UiStateReader.Choose("value", Caps(value: true)).Kind);
        Assert.Equal(UiStateKind.LegacyValue, UiStateReader.Choose("value", Caps(legacy: true)).Kind);
        Assert.Equal(UiStateKind.Toggle, UiStateReader.Choose("toggle", Caps(toggle: true)).Kind);
        Assert.Equal(UiStateKind.Selected, UiStateReader.Choose("selected", Caps(selectionItem: true)).Kind);
        Assert.Equal(UiStateKind.ExpandState, UiStateReader.Choose("expandstate", Caps(expandCollapse: true)).Kind);
        Assert.Equal(UiStateKind.RangeValue, UiStateReader.Choose("rangevalue", Caps(rangeValue: true)).Kind);
    }

    [Fact]
    public void Choose_UnsupportedPatterns_ChineseErrors()
    {
        Assert.Contains("不支持读取值", Assert.Throws<UiException>(() => UiStateReader.Choose("value", Caps())).Message);
        Assert.Contains("TogglePattern", Assert.Throws<UiException>(() => UiStateReader.Choose("toggle", Caps())).Message);
        Assert.Contains("SelectionItemPattern", Assert.Throws<UiException>(() => UiStateReader.Choose("selected", Caps())).Message);
        Assert.Contains("ExpandCollapsePattern", Assert.Throws<UiException>(() => UiStateReader.Choose("expandstate", Caps())).Message);
        Assert.Contains("RangeValuePattern", Assert.Throws<UiException>(() => UiStateReader.Choose("rangevalue", Caps())).Message);
        Assert.Contains("what 无效", Assert.Throws<UiException>(() => UiStateReader.Choose("mystery", Caps())).Message);
    }
}

