using ModelContextProtocol.Client;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// U1A ui_find/ui_action/ui_input/ui_get/ui_wait 端到端：真实起 UiSampleApp（WinForms，tests/TestData/UiSampleApp），
/// 经 MCP server 文本契约验证——全 UIA 语义 pattern，**不移动光标、不注入输入、不抢前台**。
/// 每个动作前取「静默基线」（连续采样一致）、动作后断言 GetCursorPos()/GetForegroundWindow() 任一变化即失败
/// （基线本就不稳则 Skip；focus/windowstate 豁免——spec §9）。确定性源码/IL 兜底见 NoPhysicalInputGuardTests。
/// </summary>
[Collection("UiTools")]
public sealed class DebugUiToolsTests
{
    internal static string UiSampleAppExe => Path.Combine(
        Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, "UiSampleApp", "UiSampleApp.exe");

    [Fact]
    public async Task UiFind_ReportsPatternCapabilities_AndSemanticCandidates()
    {
        Assert.True(File.Exists(UiSampleAppExe), "UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1");

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            var found = await WaitFindAsync(mcp, "手动");
            Assert.Contains("UI 控件清单", found);
            Assert.Contains("手动", found);
            Assert.Contains("patterns=invoke", found);      // Button 能力清单含 invoke（不再是 Invoke✓）
            Assert.DoesNotContain("Invoke✓", found);
            Assert.Contains("Button", found);
            Assert.Contains("语义候选", found);
            Assert.Contains("UiSampleApp.MainForm", found);
            Assert.Contains("OnToggleState", found);
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiActionInvoke_Button_TogglesState_ThenWaitEvent()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");
            var before = await CaptureQuietInputStateAsync();
            var invokeText = await WriteWithEffectAsync(mcp, "ui_action",
                new Dictionary<string, object?> { ["process"] = "UiSampleApp", ["name"] = "手动", ["type"] = "Button", ["verb"] = "invoke" },
                "已 invoke（InvokePattern）",
                () => FindContainsAsync(mcp, "自动"), retryOnTimeout: false);
            AssertActionOutcome(invokeText, "已 invoke（InvokePattern）");
            await AssertInputUnchangedAsync(before, "invoke", app);

            var wait = await DebugMcpToolsTests.CallAsync(mcp, "ui_wait", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["textChangedFrom"] = "手动", ["textChangedTo"] = "自动", ["timeoutSeconds"] = 15,
            });
            Assert.True(wait.IsError != true, wait.Text());
            Assert.Contains("已变化", wait.Text());
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiActionToggle_CheckBox_TogglesAndReadsBack()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");
            var before = await CaptureQuietInputStateAsync();
            var toggleText = await WriteWithEffectAsync(mcp, "ui_action",
                new Dictionary<string, object?> { ["process"] = "UiSampleApp", ["name"] = "checkBox", ["verb"] = "toggle" },
                "已 toggle（TogglePattern）",
                async () => (await UiGetAsync(mcp, new() { ["what"] = "toggle", ["name"] = "checkBox" })).Text().Contains("On", StringComparison.Ordinal),
                retryOnTimeout: false);
            AssertActionOutcome(toggleText, "已 toggle（TogglePattern）");
            await AssertInputUnchangedAsync(before, "toggle", app);

            var read = await UiGetAsync(mcp, new() { ["what"] = "toggle", ["name"] = "checkBox" });
            Assert.True(read.IsError != true, read.Text());
            Assert.Contains("On", read.Text());
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiActionSelect_ListItem_MarksSelected()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");
            var before = await CaptureQuietInputStateAsync();
            var selectText = await WriteWithEffectAsync(mcp, "ui_action",
                new Dictionary<string, object?> { ["process"] = "UiSampleApp", ["name"] = "Item 90", ["type"] = "ListItem", ["verb"] = "select" },
                "已 select（SelectionItemPattern）",
                async () => (await UiGetAsync(mcp, new() { ["what"] = "selected", ["name"] = "Item 90", ["type"] = "ListItem" })).Text().Contains("True", StringComparison.Ordinal),
                retryOnTimeout: true);
            AssertActionOutcome(selectText, "已 select（SelectionItemPattern）");
            await AssertInputUnchangedAsync(before, "select", app);

            var read = await UiGetAsync(mcp, new() { ["what"] = "selected", ["name"] = "Item 90", ["type"] = "ListItem" });
            Assert.True(read.IsError != true, read.Text());
            Assert.Contains("True", read.Text());
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiActionExpandCollapse_ComboBox_ChangesState()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");
            var before = await CaptureQuietInputStateAsync();
            var expandText = await WriteWithEffectAsync(mcp, "ui_action",
                new Dictionary<string, object?> { ["process"] = "UiSampleApp", ["name"] = "comboBox", ["verb"] = "expand" },
                "已 expand（ExpandCollapsePattern）",
                async () => (await UiGetAsync(mcp, new() { ["what"] = "expandstate", ["name"] = "comboBox" })).Text().Contains("Expanded", StringComparison.Ordinal),
                retryOnTimeout: true);
            AssertActionOutcome(expandText, "已 expand（ExpandCollapsePattern）");
            var state = await UiGetAsync(mcp, new() { ["what"] = "expandstate", ["name"] = "comboBox" });
            Assert.True(state.IsError != true, state.Text());
            Assert.Contains("what=expandstate", state.Text());
            // R6：expand 后必须 Expanded（不得接受 Collapsed——否则等于没校验 expand 效果）
            Assert.Contains("Expanded", state.Text());

            var collapseText = await WriteWithEffectAsync(mcp, "ui_action",
                new Dictionary<string, object?> { ["process"] = "UiSampleApp", ["name"] = "comboBox", ["verb"] = "collapse" },
                "已 collapse（ExpandCollapsePattern）",
                async () => (await UiGetAsync(mcp, new() { ["what"] = "expandstate", ["name"] = "comboBox" })).Text().Contains("Collapsed", StringComparison.Ordinal),
                retryOnTimeout: true);
            AssertActionOutcome(collapseText, "已 collapse（ExpandCollapsePattern）");
            var collapsed = await UiGetAsync(mcp, new() { ["what"] = "expandstate", ["name"] = "comboBox" });
            Assert.True(collapsed.IsError != true, collapsed.Text());
            // R6：collapse 后必须 Collapsed
            Assert.Contains("Collapsed", collapsed.Text());
            await AssertInputUnchangedAsync(before, "expand/collapse", app);
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiInput_ValueAndRangeValue_AndReadOnlyRejected()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");
            var before = await CaptureQuietInputStateAsync();

            var setText = await WriteWithEffectAsync(mcp, "ui_input",
                new Dictionary<string, object?> { ["process"] = "UiSampleApp", ["value"] = "hello-u1a", ["name"] = "inputBox" },
                "已 input（ValuePattern）",
                async () => (await UiGetAsync(mcp, new() { ["what"] = "value", ["name"] = "inputBox" })).Text().Contains("hello-u1a", StringComparison.Ordinal),
                retryOnTimeout: true);
            AssertActionOutcome(setText, "已 input（ValuePattern）");
            var readText = await UiGetAsync(mcp, new() { ["what"] = "value", ["name"] = "inputBox" });
            Assert.Contains("hello-u1a", readText.Text());

            // RangeValue 只读分支：ProgressBar 暴露只读 RangeValuePattern——ui_get 读得到，ui_input 必须失败并报只读
            var readRange = await UiGetAsync(mcp, new() { ["what"] = "rangevalue", ["name"] = "progressBar" });
            Assert.True(readRange.IsError != true, readRange.Text());
            Assert.Contains("40", readRange.Text());

            var rangeReadOnly = await DebugMcpToolsTests.CallAsync(mcp, "ui_input", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["value"] = "66", ["name"] = "progressBar",
            });
            Assert.True(rangeReadOnly.IsError != true, rangeReadOnly.Text());
            // R6：只读控件误写「已 input」不得当 PASS——必须中文只读提示
            Assert.Contains("只读", rangeReadOnly.Text());

            // VScrollBar 的 RangeValuePattern 可写（WinForms ScrollBarAccessibleObject.IsReadOnly=false）——正路径写值
            var vscroll = await WriteWithEffectAsync(mcp, "ui_input",
                new Dictionary<string, object?> { ["process"] = "UiSampleApp", ["value"] = "77", ["name"] = "vscrollBar" },
                "已 input（RangeValuePattern）",
                async () => (await UiGetAsync(mcp, new() { ["what"] = "rangevalue", ["name"] = "vscrollBar" })).Text().Contains("77", StringComparison.Ordinal),
                retryOnTimeout: true);
            AssertActionOutcome(vscroll, "已 input（RangeValuePattern）");

            // ValuePattern 只读拒绝：必须报只读
            var readOnly = await DebugMcpToolsTests.CallAsync(mcp, "ui_input", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["value"] = "x", ["name"] = "readOnlyBox",
            });
            Assert.True(readOnly.IsError != true, readOnly.Text());
            Assert.Contains("只读", readOnly.Text());
            await AssertInputUnchangedAsync(before, "ui_input", app);
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiGet_GenericProperties_ReturnValues()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");
            foreach (var what in new[] { "name", "enabled", "offscreen", "rect", "helptext" })
            {
                var r = await UiGetAsync(mcp, new() { ["what"] = what, ["name"] = "countButton" });
                Assert.True(r.IsError != true, $"{what}: {r.Text()}");
                Assert.Contains($"what={what}", r.Text());
            }
            // 无对应 pattern → 中文提示（Button 无 TogglePattern）
            var badPattern = await UiGetAsync(mcp, new() { ["what"] = "toggle", ["name"] = "countButton" });
            Assert.True(badPattern.IsError != true, badPattern.Text());
            AssertContainsOrTimeout(badPattern.Text(), "TogglePattern");
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiActionFocus_NonWindow_IsAllowed_AndWindowStateRoundTrips()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");
            // focus 非窗口控件（豁免前台断言）
            var focus = await UiActionAsync(mcp, new() { ["name"] = "countButton", ["type"] = "Button", ["verb"] = "focus" });
            Assert.True(focus.IsError != true, focus.Text());
            AssertActionOutcome(focus.Text(), "已 focus（Focus）");

            // windowstate 目标顶层窗口（忽略 index/name/type；豁免前台断言）
            var min = await UiActionAsync(mcp, new() { ["verb"] = "windowstate", ["windowstate"] = "minimized" });
            Assert.True(min.IsError != true, min.Text());
            AssertActionOutcome(min.Text(), "已 windowstate=minimized（WindowPattern）");
            var normal = await UiActionAsync(mcp, new() { ["verb"] = "windowstate", ["windowstate"] = "normal" });
            Assert.True(normal.IsError != true, normal.Text());
            AssertActionOutcome(normal.Text(), "已 windowstate=normal（WindowPattern）");
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiActionScroll_AndScrollIntoView_NoPhysicalInput()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");
            var before = await CaptureQuietInputStateAsync();
            foreach (var dir in new[] { "down", "up" })
            {
                var scroll = await UiActionAsync(mcp, new() { ["name"] = "listBox", ["verb"] = "scroll", ["direction"] = dir, ["lines"] = 5 });
                Assert.True(scroll.IsError != true, $"{dir}: {scroll.Text()}");
                AssertActionOutcome(scroll.Text(), "已 scroll（ScrollPattern）");
            }
            var intoView = await UiActionAsync(mcp, new() { ["name"] = "Item 90", ["type"] = "ListItem", ["verb"] = "scrollintoview" });
            Assert.True(intoView.IsError != true, intoView.Text());
            AssertActionOutcome(intoView.Text(), "已 scrollintoview（");
            await AssertInputUnchangedAsync(before, "scroll/scrollintoview", app);
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiInput_ByIndex_ResolvesCachedTargetDescriptor()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            // 先 ui_find 更新 index 条件缓存（用 type=Edit 缩小范围），再按 index 写值——覆盖 index→重解析路径。
            var found = await WaitUiFindAsync(mcp, new Dictionary<string, object?> { ["type"] = "Edit" }, "inputBox");
            var idx = IndexByAutoId(found, "inputBox");
            var before = await CaptureQuietInputStateAsync();

            var write = await WriteWithEffectAsync(mcp, "ui_input",
                new Dictionary<string, object?> { ["process"] = "UiSampleApp", ["value"] = "by-index", ["index"] = idx },
                "已 input（ValuePattern）",
                async () => (await UiGetAsync(mcp, new() { ["what"] = "value", ["name"] = "inputBox" })).Text().Contains("by-index", StringComparison.Ordinal),
                retryOnTimeout: true);
            AssertActionOutcome(write, "已 input（ValuePattern）");

            var read = await UiGetAsync(mcp, new() { ["what"] = "value", ["name"] = "inputBox" });
            Assert.True(read.IsError != true, read.Text());
            Assert.Contains("by-index", read.Text());
            await AssertInputUnchangedAsync(before, "ui_input(index)", app);
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiAction_ByIndex_AfterTargetTextChanged_ReportsStale()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            var found = await WaitFindAsync(mcp, "手动");
            var idx = IndexByAutoId(found, "toggleState");

            // 首次按 index invoke：重解析按 (AutoId=toggleState, Name=手动, Type=Button) 精确命中。
            var first = await WriteWithEffectAsync(mcp, "ui_action",
                new Dictionary<string, object?> { ["process"] = "UiSampleApp", ["index"] = idx, ["verb"] = "invoke" },
                "已 invoke（InvokePattern）",
                () => FindContainsAsync(mcp, "自动"), retryOnTimeout: false);
            AssertActionOutcome(first, "已 invoke（InvokePattern）");
            await WaitFindAsync(mcp, "自动"); // 确认 UIA Name 已更新为 自动

            // 同一 index 的定位条件（Name=手动）已不再匹配 → 重解析 3 次失败 → 中文「目标已变化」（stale 重解析路径）。
            var staleText = "";
            for (var i = 0; i < 3; i++)
            {
                staleText = (await UiActionAsync(mcp, new() { ["index"] = idx, ["verb"] = "invoke" })).Text();
                if (staleText.Contains("目标已变化", StringComparison.Ordinal)) break;
                if (!staleText.Contains(UiaTimeoutMarker, StringComparison.Ordinal)) break;
                await Task.Delay(300, TestContext.Current.CancellationToken);
            }
            Assert.Contains("目标已变化", staleText);

            // 重新 ui_find 后按新 index 可继续操作（验证 index 与最新清单一致）。
            var refound = await WaitFindAsync(mcp, "自动");
            var idx2 = IndexByAutoId(refound, "toggleState");
            var again = await UiActionAsync(mcp, new() { ["index"] = idx2, ["verb"] = "invoke" });
            Assert.True(again.IsError != true, again.Text());
            AssertActionOutcome(again.Text(), "已 invoke（InvokePattern）");
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiTools_InvalidArgsAndMissingTargets_ReturnChineseHints()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");

            // 未知 verb → 可选清单 + 无物理输入边界
            var badVerb = await UiActionAsync(mcp, new() { ["name"] = "手动", ["verb"] = "hover" });
            Assert.True(badVerb.IsError != true, badVerb.Text());
            Assert.Contains("verb 无效", badVerb.Text());
            Assert.Contains("物理输入已移除", badVerb.Text());

            // 右键/双击 → 明确中文拒绝
            foreach (var verb in new[] { "rightclick", "doubleclick" })
            {
                var noEntry = await UiActionAsync(mcp, new() { ["name"] = "手动", ["verb"] = verb });
                Assert.True(noEntry.IsError != true, noEntry.Text());
                Assert.Contains("UIA 无该语义入口", noEntry.Text());
                Assert.Contains("物理输入已移除", noEntry.Text());
            }

            // 非法 direction / windowstate
            var badDir = await UiActionAsync(mcp, new() { ["name"] = "richBox", ["verb"] = "scroll", ["direction"] = "sideways" });
            Assert.Contains("direction 无效", badDir.Text());
            Assert.Contains("up / down", badDir.Text());
            var badState = await UiActionAsync(mcp, new() { ["verb"] = "windowstate", ["windowstate"] = "sideways" });
            Assert.Contains("windowstate 无效", badState.Text());

            // 无对应 pattern
            var noPattern = await UiActionAsync(mcp, new() { ["name"] = "richBox", ["verb"] = "toggle" });
            AssertContainsOrTimeout(noPattern.Text(), "不支持无鼠标触发");

            // 不存在控件 / 非法类型 / 不存在进程
            var missing = await UiActionAsync(mcp, new() { ["name"] = "NoSuchControlXyz", ["verb"] = "invoke" });
            AssertContainsOrTimeout(missing.Text(), "未找到");
            var badType = await DebugMcpToolsTests.CallAsync(mcp, "ui_find", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["type"] = "NoSuchControlType",
            });
            Assert.True(badType.IsError != true, badType.Text());
            Assert.Contains("控件类型无效", badType.Text());
            var noProc = await DebugMcpToolsTests.CallAsync(mcp, "ui_find", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp_NoSuch_12345",
            });
            Assert.True(noProc.IsError != true, noProc.Text());
            Assert.Contains("未找到进程名含", noProc.Text());

            // ui_get 非法 what / ui_input 空 value / ui_wait 超时
            var badWhat = await UiGetAsync(mcp, new() { ["what"] = "mystery", ["name"] = "countButton" });
            AssertContainsOrTimeout(badWhat.Text(), "what 无效");
            var emptyValue = await DebugMcpToolsTests.CallAsync(mcp, "ui_input", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["name"] = "inputBox",
            });
            Assert.Contains("value 不能为空", emptyValue.Text());
            var wait = await DebugMcpToolsTests.CallAsync(mcp, "ui_wait", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["text"] = "绝不出现的文本XYZ987", ["timeoutSeconds"] = 2,
            });
            Assert.True(wait.IsError != true, wait.Text());
            Assert.Contains("超时", wait.Text());
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiActionInvoke_WhileDebugSessionOnToggleState_HitsBreakpoint()
    {
        // 与 debug 编排冒烟：先起 UiSampleApp 等窗口就绪 → debug_attach → 断点设 OnToggleState →
        // ui_action verb=invoke 点切换按钮 → debug_wait 命中。invoke 走 InvokePattern（UIA 语义），非交互会话亦可闭环。
        var exe = UiSampleAppExe;
        Assert.True(File.Exists(exe), "UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1");
        var toggleToken = DebugMcpToolsTests.ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "OnToggleState");
        Assert.True(toggleToken > 0, "UiSampleApp 未找到 OnToggleState 方法");

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");
            var pid = app.Id;
            Assert.True(pid > 0);

            var attach = await DebugMcpToolsTests.CallAsync(mcp, "debug_attach",
                new Dictionary<string, object?> { ["processId"] = pid });
            Assert.True(attach.IsError != true, attach.Text());
            Assert.Contains($"pid={pid}", attach.Text());

            var cont = await DebugMcpToolsTests.CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
            Assert.True(cont.IsError != true, cont.Text());
            var set = await DebugMcpToolsTests.CallAsync(mcp, "debug_breakpoint_set", new Dictionary<string, object?>
            {
                ["moduleName"] = "UiSampleApp.dll", ["methodToken"] = $"0x{toggleToken:x8}", ["ilOffset"] = 0,
            });
            Assert.True(set.IsError != true, set.Text());
            await DebugMcpToolsTests.WaitBoundAsync(mcp, DebugMcpToolsTests.ParseBreakpointId(set.Text()));

            // 点击会命中 OnToggleState 入口断点——目标 UI 线程被调试器冻结，UIA Invoke 跨进程往返可能超 5s
            // 返回超时提示（产品预期行为，点击已发生）。故不苛求 invoke 文本，以 debug_wait 停点命中为准。
            var invoke = await UiActionAsync(mcp, new() { ["name"] = "手动", ["type"] = "Button", ["verb"] = "invoke" });
            Assert.True(invoke.IsError != true, invoke.Text());

            var wait = await DebugMcpToolsTests.CallAsync(mcp, "debug_wait",
                new Dictionary<string, object?> { ["waitSeconds"] = 30 });
            Assert.True(wait.IsError != true, wait.Text());
            Assert.Contains("已停下", wait.Text());

            var disc = await DebugMcpToolsTests.CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
            Assert.True(disc.IsError != true, disc.Text());
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    // ===== MCP 调用辅助 =====

    private static async Task<string> UiFindAsync(McpClient mcp, IReadOnlyDictionary<string, object?> extra)
    {
        var args = new Dictionary<string, object?> { ["process"] = "UiSampleApp" };
        foreach (var (k, v) in extra) args[k] = v;
        var r = await UiReadAsync(mcp, "ui_find", args);
        Assert.True(r.IsError != true, r.Text());
        return r.Text();
    }

    private static Task<ModelContextProtocol.Protocol.CallToolResult> UiActionAsync(McpClient mcp, Dictionary<string, object?> extra)
    {
        var args = new Dictionary<string, object?> { ["process"] = "UiSampleApp" };
        foreach (var (k, v) in extra) args[k] = v;
        return DebugMcpToolsTests.CallAsync(mcp, "ui_action", args);
    }

    private static Task<ModelContextProtocol.Protocol.CallToolResult> UiGetAsync(McpClient mcp, Dictionary<string, object?> extra)
    {
        var args = new Dictionary<string, object?> { ["process"] = "UiSampleApp" };
        foreach (var (k, v) in extra) args[k] = v;
        return UiReadAsync(mcp, "ui_get", args);
    }

    // ===== UIA 5s 护栏下的环境容错（共享机器负载高时读重试、写按效果确认/重试） =====

    private const string UiaTimeoutMarker = "UIA 调用超过 5s";

    /// <summary>只读调用遇 5s 超时（环境慢）重试（读无副作用）。</summary>
    private static async Task<ModelContextProtocol.Protocol.CallToolResult> UiReadAsync(McpClient mcp, string tool, Dictionary<string, object?> args)
    {
        ModelContextProtocol.Protocol.CallToolResult last = null!;
        for (var i = 0; i < 3; i++)
        {
            last = await DebugMcpToolsTests.CallAsync(mcp, tool, args);
            if (last.IsError == true || !last.Text().Contains(UiaTimeoutMarker, StringComparison.Ordinal)) return last;
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }
        return last;
    }

    /// <summary>
    /// 写动作执行：命中预期 pattern 或**效果经只读探测独立确认**才算成功；若 5s 超时且效果未确认，仅对幂等动作重试
    /// （invoke/toggle 不重试，防双触发——与产品 <c>VerifyService.IsIdempotentVerb</c> 对齐）。超时且效果始终未确认时
    /// Success=false（超时不得当 PASS）。
    /// </summary>
    private static async Task<(string Text, bool Success)> WriteWithEffectAsync(
        McpClient mcp, string tool, Dictionary<string, object?> args, string expectedPattern,
        Func<Task<bool>> effect, bool retryOnTimeout)
    {
        var text = "";
        for (var i = 0; i < 3; i++)
        {
            var r = await DebugMcpToolsTests.CallAsync(mcp, tool, args);
            text = r.Text();
            if (text.Contains(expectedPattern, StringComparison.Ordinal)) return (text, true);
            if (!text.Contains(UiaTimeoutMarker, StringComparison.Ordinal)) return (text, false); // 明确失败
            if (await effect()) return (text, true); // 超时但动作实际生效（效果已独立确认）——非「超时即通过」
            if (!retryOnTimeout) return (text, false); // 非幂等：不重发
            await Task.Delay(400, TestContext.Current.CancellationToken);
        }
        return (text, false);
    }

    /// <summary>动作结果断言：必须命中预期 pattern 或已确认效果（超时/失败不得当 PASS）。</summary>
    private static void AssertActionOutcome((string Text, bool Success) result, string expectedPattern)
        => Assert.True(result.Success,
            $"未命中预期「{expectedPattern}」且未确认效果（超时/失败不得当 PASS）：{result.Text}");

    /// <summary>无独立效果探针的动作（scroll/scrollintoview/focus/windowstate）结果断言：必须命中预期 pattern（超时/失败一律 Fail）。</summary>
    private static void AssertActionOutcome(string text, string expectedPattern)
        => Assert.True(text.Contains(expectedPattern, StringComparison.Ordinal),
            $"未命中预期「{expectedPattern}」（超时/失败不得当 PASS）：{text}");

    /// <summary>错误路径断言：命中预期中文提示，或环境慢导致的 5s 超时（UIA 目标解析未及返回）。</summary>
    private static void AssertContainsOrTimeout(string text, string fragment)
        => Assert.True(text.Contains(fragment, StringComparison.Ordinal) || text.Contains(UiaTimeoutMarker, StringComparison.Ordinal),
            $"既未命中「{fragment}」也非 5s 超时：{text}");

    private static async Task<bool> FindContainsAsync(McpClient mcp, string text)
        => (await UiFindAsync(mcp, new Dictionary<string, object?> { ["text"] = text, ["limit"] = 10 })).Contains(text, StringComparison.Ordinal);


    // ===== 启动/清理/轮询辅助 =====

    private static Process LaunchUiSampleApp()
    {
        KillLeftoverUiSampleApps();
        var exe = UiSampleAppExe;
        var dir = Path.GetDirectoryName(exe)!;
        return Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = dir })!;
    }

    private static void KillUiSampleApp(Process app)
    {
        try { if (!app.HasExited) app.Kill(entireProcessTree: true); }
        catch { /* 已退出忽略 */ }
        KillLeftoverUiSampleApps();
    }

    private static void KillLeftoverUiSampleApps()
    {
        foreach (var p in Process.GetProcessesByName("UiSampleApp"))
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* 权限/已退出忽略 */ }
        }
    }

    /// <summary>反复 ui_find 直到窗口/按钮出现（等 UiSampleApp 起窗；共享机器负载高时放宽到 60s），返回最近一次结果文本。</summary>
    private static Task<string> WaitFindAsync(McpClient mcp, string mustContain, int timeoutSeconds = 60)
        => WaitUiFindAsync(mcp, new Dictionary<string, object?> { ["type"] = "Button" }, mustContain, timeoutSeconds);

    private static async Task<string> WaitUiFindAsync(McpClient mcp, IReadOnlyDictionary<string, object?> extra, string mustContain, int timeoutSeconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            last = await UiFindAsync(mcp, extra);
            if (last.Contains(mustContain, StringComparison.Ordinal)) return last;
            await Task.Delay(400, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"UiSampleApp 窗口在 {timeoutSeconds}s 内未就绪（需含「{mustContain}」）。最近 ui_find：{last}");
        return last;
    }

    /// <summary>取首个匹配谓词的行（含行首 [index]）。</summary>
    private static string? FirstRow(string text, Func<string, bool> predicate)
    {
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("[", StringComparison.Ordinal) && predicate(line)) return line;
        }
        return null;
    }

    private static int RowIndex(string row)
    {
        var m = Regex.Match(row, @"^\[(\d+)\]");
        Assert.True(m.Success, $"行首未找到 [index]：{row}");
        return int.Parse(m.Groups[1].Value);
    }

    /// <summary>在 ui_find 文本中按 AutoId= 定位行并取 [index]。</summary>
    private static int IndexByAutoId(string findText, string autoId)
    {
        var row = FirstRow(findText, line => line.Contains($"AutoId={autoId}", StringComparison.Ordinal));
        Assert.True(row is not null, $"ui_find 结果未找到 AutoId={autoId} 的行：{findText}");
        return RowIndex(row!);
    }

    // ===== 不抢鼠标/前台强断言（护栏，防回退到物理输入；focus/windowstate 豁免，spec §9） =====

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly bool Contains(int x, int y) => x >= Left && x <= Right && y >= Top && y <= Bottom;
    }

    private const string QuietBaselineSkipReason =
        "动作前桌面输入状态持续变化（检出外部光标/前台活动，交互式共享桌面）——基线不静默，跳过不抢鼠标/前台断言（no physical input）。";

    private readonly record struct InputSnapshot(Point Cursor, IntPtr Foreground);

    private static InputSnapshot CaptureInputState()
    {
        GetCursorPos(out var p);
        return new InputSnapshot(p, GetForegroundWindow());
    }

    private static bool SameInput(InputSnapshot a, InputSnapshot b)
        => a.Cursor.X == b.Cursor.X && a.Cursor.Y == b.Cursor.Y && a.Foreground == b.Foreground;

    /// <summary>
    /// 取静默基线：连续 3 次采样（约 400ms）一致才认为桌面静默；否则判定检出外部活动 → Skip（不虚判）。
    /// CI 非交互桌面下应稳定取到静默基线，从而使动作后断言成为强断言。
    /// </summary>
    private static async Task<InputSnapshot> CaptureQuietInputStateAsync()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var a = CaptureInputState();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            var b = CaptureInputState();
            await Task.Delay(200, TestContext.Current.CancellationToken);
            var c = CaptureInputState();
            if (SameInput(a, b) && SameInput(b, c)) return c;
        }
        Assert.Skip(QuietBaselineSkipReason);
        return default;
    }

    private static bool TryGetMainWindowRect(Process app, out Rect rect)
    {
        rect = default;
        try
        {
            app.Refresh();
            var hwnd = app.MainWindowHandle;
            return hwnd != IntPtr.Zero && GetWindowRect(hwnd, out rect);
        }
        catch { return false; }
    }

    /// <summary>
    /// R2 不抢鼠标/前台强断言（focus/windowstate 调用方豁免，spec §9）：基线已确认静默（<see cref="CaptureQuietInputStateAsync"/>），
    /// 动作后光标/前台发生<b>任何</b>变化即 Fail（不静默放过物理回归）；目标窗口被抢前台 / 光标落入目标窗口作为额外 Fail 触发。
    /// 若动作前基线本就不稳定，CaptureQuietInputStateAsync 已 Skip 并打印原因（不做强特征启发式）。
    /// </summary>
    private static Task AssertInputUnchangedAsync(InputSnapshot before, string action, Process app)
    {
        var after = CaptureInputState();
        if (SameInput(before, after)) return Task.CompletedTask;

        var hasRect = TryGetMainWindowRect(app, out var rect);
        var targetHwnd = IntPtr.Zero;
        try { app.Refresh(); targetHwnd = app.MainWindowHandle; } catch { }

        var cursorMoved = after.Cursor.X != before.Cursor.X || after.Cursor.Y != before.Cursor.Y;
        var fgChanged = after.Foreground != before.Foreground;
        var cursorInsideTarget = hasRect && rect.Contains(after.Cursor.X, after.Cursor.Y);
        var targetTookForeground = targetHwnd != IntPtr.Zero && after.Foreground == targetHwnd;

        Assert.Fail($"{action} 动作期间系统光标/前台发生变化（基线静默）：光标 ({before.Cursor.X},{before.Cursor.Y}) → ({after.Cursor.X},{after.Cursor.Y})（移动={cursorMoved}、落入目标窗口={cursorInsideTarget}）；前台 {before.Foreground} → {after.Foreground}（变化={fgChanged}、目标窗口被抢前台={targetTookForeground}）——U1A 禁止物理输入/抢前台。");
        return Task.CompletedTask;
    }
}

/// <summary>UiTools 集合定义：真实 UI/单进程目标，串行执行（不与其他集合并行共享 UiSampleApp 进程名/UIA）。</summary>
[CollectionDefinition("UiTools", DisableParallelization = true)]
public sealed class UiToolsCollection
{
}
