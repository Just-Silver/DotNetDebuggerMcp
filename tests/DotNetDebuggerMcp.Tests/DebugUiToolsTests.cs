using ModelContextProtocol.Client;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// U1 ui_find/ui_invoke(action)/ui_wait/ui_scroll 端到端：真实起 UiSampleApp（WinForms，tests/TestData/UiSampleApp），
/// 经 MCP server 文本契约验证——找按钮→左键切状态（ui_wait 确认）→物理右键→双击列表项→滚动。
/// 依赖交互式桌面：UIA 读树/InvokePattern 任意会话可用，但物理右键/双击/滚轮走 SendInput——断开/非交互桌面会话
/// SendInput 被系统拒绝（access denied），此类断言自探测后跳过（env-dependent，交互桌面会话可验）。
/// </summary>
[Collection("UiTools")]
public sealed class DebugUiToolsTests
{
    internal static string UiSampleAppExe => Path.Combine(
        Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, "UiSampleApp", "UiSampleApp.exe");

    [Fact]
    public async Task UiFindInvokeWait_LeftClickToggleButton_StateManualToAuto()
    {
        Assert.True(File.Exists(UiSampleAppExe), "UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1");

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            // 1. ui_find type=Button：等窗口就绪并返回含「手动」按钮行（index + Invoke✓ + 语义候选）
            var found = await WaitFindAsync(mcp, "手动");
            Assert.Contains("UI 控件清单", found);
            Assert.Contains("手动", found);
            Assert.Contains("Invoke✓", found);
            Assert.Contains("Button", found);
            // 语义标注（务实成员反查 UiSampleApp.dll）：toggleState AutoId → OnToggleState 同名成员候选
            Assert.Contains("语义候选", found);
            Assert.Contains("UiSampleApp.MainForm", found);
            Assert.Contains("OnToggleState", found);

            // 2. 左键（Invoke 优先）→ 返回注明实际动作
            var invoke = await DebugMcpToolsTests.CallAsync(mcp, "ui_invoke", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["name"] = "手动", ["type"] = "Button",
            });
            Assert.True(invoke.IsError != true, invoke.Text());
            Assert.True(invoke.Text().Contains("已 Invoke") || invoke.Text().Contains("已物理左键点击"), invoke.Text());

            // 3. ui_wait 文本从 手动 → 自动（状态切换确认）
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
    public async Task UiInvokeRightClick_ToggleButton_StatusLabelChanges()
    {
        Assert.True(File.Exists(UiSampleAppExe));
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            // 先切到 自动（左键闭环；右键目标按钮文本随状态变）——Invoke 走 UIA 语义，非交互会话也可用
            await WaitFindAsync(mcp, "手动");
            var left = await DebugMcpToolsTests.CallAsync(mcp, "ui_invoke", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["name"] = "手动", ["type"] = "Button",
            });
            Assert.True(left.IsError != true, left.Text());
            var changed = await DebugMcpToolsTests.CallAsync(mcp, "ui_wait", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["textChangedFrom"] = "手动", ["textChangedTo"] = "自动", ["timeoutSeconds"] = 15,
            });
            Assert.Contains("已变化", changed.Text());

            // 物理右键切换按钮 → 返回注明物理右键
            var right = await DebugMcpToolsTests.CallAsync(mcp, "ui_invoke", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["name"] = "自动", ["type"] = "Button", ["action"] = "rightClick",
            });
            Assert.True(right.IsError != true, right.Text());
            if (!PhysicalInputAvailable())
            {
                // 断开/非交互桌面会话：SendInput 被系统拒绝——物理动作无法验证；但失败文案路径应如实返回中文原因
                Assert.Contains("物理右键点击失败", right.Text());
                Assert.Skip(PhysicalInputSkipReason);
            }
            Assert.Contains("已物理右键点击", right.Text());

            // 右击 Label 从 右键:0 → 右键:1
            var waitRight = await DebugMcpToolsTests.CallAsync(mcp, "ui_wait", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["textChangedFrom"] = "右键:0", ["textChangedTo"] = "右键:1", ["timeoutSeconds"] = 15,
            });
            Assert.True(waitRight.IsError != true, waitRight.Text());
            Assert.Contains("已变化", waitRight.Text());
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiInvokeDoubleClick_ListItem_CounterLabelChanges()
    {
        Assert.True(File.Exists(UiSampleAppExe));
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            // 等窗口就绪：双击计数 Label（双击:0）已出现——Type=Text（Label 在 UIA 里是 Text）
            await WaitUiFindAsync(mcp, new Dictionary<string, object?> { ["type"] = "Text", ["text"] = "双击" }, "双击:0");

            // 定位具体列表项：ListItem Name=Item 5（精确行匹配防 Item 5x 干扰）
            var itemFind = await DebugMcpToolsTests.CallAsync(mcp, "ui_find", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["type"] = "ListItem", ["text"] = "Item 5", ["limit"] = 20,
            });
            Assert.True(itemFind.IsError != true, itemFind.Text());
            var itemRow = FirstRow(itemFind.Text(), r => r.Contains("Name=Item 5 ", StringComparison.Ordinal) || r.Contains("Name=Item 5 AutoId", StringComparison.Ordinal));
            Assert.NotNull(itemRow);
            var itemIndex = RowIndex(itemRow!);

            // 物理双击该项 → 返回注明物理双击
            var dbl = await DebugMcpToolsTests.CallAsync(mcp, "ui_invoke", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["index"] = itemIndex, ["action"] = "doubleClick",
            });
            Assert.True(dbl.IsError != true, dbl.Text());
            if (!PhysicalInputAvailable())
            {
                Assert.Contains("物理双击失败", dbl.Text());
                Assert.Skip(PhysicalInputSkipReason);
            }
            Assert.Contains("已物理双击", dbl.Text());

            // 双击计数 双击:0 → 双击:1
            var waitDbl = await DebugMcpToolsTests.CallAsync(mcp, "ui_wait", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["textChangedFrom"] = "双击:0", ["textChangedTo"] = "双击:1", ["timeoutSeconds"] = 15,
            });
            Assert.True(waitDbl.IsError != true, waitDbl.Text());
            Assert.Contains("已变化", waitDbl.Text());
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiScroll_ListBox_DownLines_ReportsPhysicalWheel()
    {
        Assert.True(File.Exists(UiSampleAppExe));
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");

            // 容器定位：ListBox（WinForms ListBox = UIA List 类型；Name/AutoId 可能空，用类型唯一定位）
            var listFind = await DebugMcpToolsTests.CallAsync(mcp, "ui_find", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["type"] = "List", ["limit"] = 5,
            });
            Assert.True(listFind.IsError != true, listFind.Text());
            var listRow = FirstRow(listFind.Text(), r => r.Contains("] List ", StringComparison.Ordinal));
            Assert.NotNull(listRow);

            // 物理滚轮向下 5 行（返回注明方向与行数）
            var scroll = await DebugMcpToolsTests.CallAsync(mcp, "ui_scroll", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["index"] = RowIndex(listRow!), ["direction"] = "down", ["lines"] = 5,
            });
            Assert.True(scroll.IsError != true, scroll.Text());
            if (!PhysicalInputAvailable())
            {
                // 非交互会话：滚轮同样被拒——失败文案路径应如实返回中文原因
                Assert.Contains("物理滚轮失败", scroll.Text());
                Assert.Skip(PhysicalInputSkipReason);
            }
            Assert.Contains("已向下滚动 5 行", scroll.Text());
            Assert.Contains("物理滚轮", scroll.Text());

            // 方向生效自检：WinForms ListBox 不暴露 ScrollPattern → VerticalScrollPercent 前后对比不可行
            // （环境相关：无 ScrollPattern 容器无法可靠断言滚动的方向/位移；ScrollPattern 支持的目标可补 percent 对比。
            //   本行保留为文档化限制——真实方向翻转由 CoreMes 类目标或人工复验。）
            Console.WriteLine("[UiTools] 滚动方向自检：UiSampleApp 的 WinForms ListBox 无 ScrollPattern，跳过 percent 断言（env-dependent）。");
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    [Fact]
    public async Task UiTools_InvalidArgsAndMissingTargets_ReturnChineseHints()
    {
        Assert.True(File.Exists(UiSampleAppExe));
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            await WaitFindAsync(mcp, "手动");

            // 非法 action → 可选清单
            var badAction = await DebugMcpToolsTests.CallAsync(mcp, "ui_invoke", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["name"] = "手动", ["action"] = "hover",
            });
            Assert.True(badAction.IsError != true, badAction.Text());
            Assert.Contains("action 无效", badAction.Text());
            Assert.Contains("click / rightClick / doubleClick", badAction.Text());

            // 非法 direction → 可选清单
            var badDir = await DebugMcpToolsTests.CallAsync(mcp, "ui_scroll", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["name"] = "listBox", ["direction"] = "sideways",
            });
            Assert.True(badDir.IsError != true, badDir.Text());
            Assert.Contains("direction 无效", badDir.Text());
            Assert.Contains("up / down", badDir.Text());

            // 不存在的控件 → 中文提示引导先 ui_find
            var missing = await DebugMcpToolsTests.CallAsync(mcp, "ui_invoke", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["name"] = "NoSuchControlXyz",
            });
            Assert.True(missing.IsError != true, missing.Text());
            Assert.Contains("未找到", missing.Text());

            // 非法控件类型 → 中文提示（附合法方向）
            var badType = await DebugMcpToolsTests.CallAsync(mcp, "ui_find", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["type"] = "NoSuchControlType",
            });
            Assert.True(badType.IsError != true, badType.Text());
            Assert.Contains("控件类型无效", badType.Text());

            // 进程不存在 → 中文提示
            var noProc = await DebugMcpToolsTests.CallAsync(mcp, "ui_find", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp_NoSuch_12345",
            });
            Assert.True(noProc.IsError != true, noProc.Text());
            Assert.Contains("未找到进程名含", noProc.Text());

            // ui_wait 超时不报错：2s 内目标文本不出现 → 超时提示
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
    public async Task UiInvokeClick_WhileDebugSessionOnToggleState_HitsBreakpoint()
    {
        // 与 debug 编排冒烟（计划步骤 7）：先起 UiSampleApp 等窗口就绪 → debug_attach → 断点设 OnToggleState →
        // ui_invoke 点切换按钮 → debug_wait 命中。click 走 InvokePattern（UIA 语义），非交互会话亦可闭环。
        // 说明：debug_launch（CLR 启动即冻结）对 WinForms 目标有「continue 后主窗口不出现」的引擎时序现象——属
        // Engine 行为、U1 不改 Engine，故冒烟用 attach 现成窗口路径（同样验证 UIA 与调试同进程协作）。
        var exe = UiSampleAppExe;
        Assert.True(File.Exists(exe), "UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1");
        var toggleToken = DebugMcpToolsTests.ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "OnToggleState");
        Assert.True(toggleToken > 0, "UiSampleApp 未找到 OnToggleState 方法");

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            // 独立运行起窗（主循环已跑、模块已加载）
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

            // ui_invoke 点击切换按钮（Invoke 语义优先）。注意：点击会命中 OnToggleState 入口断点——目标 UI 线程
            // 被调试器冻结，正在进行的 UIA Invoke 跨进程往返可能超过 5s 护栏并返回超时提示（产品预期行为，
            // 点击实际已发生）。故不苛求 invoke 文本，以 debug_wait 的停点命中为准。
            var invoke = await DebugMcpToolsTests.CallAsync(mcp, "ui_invoke", new Dictionary<string, object?>
            {
                ["process"] = "UiSampleApp", ["name"] = "手动", ["type"] = "Button",
            });
            Assert.True(invoke.IsError != true, invoke.Text());

            // debug_wait：OnToggleState 入口断点命中（目标 UI 线程停住）
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
        KillLeftoverUiSampleApps(); // 同进程名双保险（退出前进程可能残留窗口）
    }

    private static void KillLeftoverUiSampleApps()
    {
        foreach (var p in Process.GetProcessesByName("UiSampleApp"))
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* 权限/已退出忽略 */ }
        }
    }

    /// <summary>反复 ui_find 直到窗口/按钮出现（等 UiSampleApp 起窗），返回最近一次结果文本。</summary>
    private static Task<string> WaitFindAsync(McpClient mcp, string mustContain, int timeoutSeconds = 30)
        => WaitUiFindAsync(mcp, new Dictionary<string, object?> { ["type"] = "Button" }, mustContain, timeoutSeconds);

    private static async Task<string> WaitUiFindAsync(McpClient mcp, IReadOnlyDictionary<string, object?> extra, string mustContain, int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            var args = new Dictionary<string, object?> { ["process"] = "UiSampleApp" };
            foreach (var (k, v) in extra) args[k] = v;
            var r = await DebugMcpToolsTests.CallAsync(mcp, "ui_find", args);
            Assert.True(r.IsError != true, r.Text());
            last = r.Text();
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

    // ===== 物理输入可用性探测（SendInput）：断开/非交互桌面会话会被系统拒绝（ERROR_ACCESS_DENIED） =====

    private const string PhysicalInputSkipReason =
        "当前会话非交互（SendInput 被系统拒绝，通常为断开/服务会话）——物理右键/双击/滚轮属 env-dependent，需在交互桌面会话验证；"
        + "已断言失败文案路径如实返回中文原因。";

    private const uint InputMouse = 0;
    private const uint MouseEventFMove = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint DwFlags;
        public uint Time;
        public IntPtr DwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public MouseInput Mi;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    /// <summary>SendInput 是否可用（无副作用探测：一条零位移 MOUSE_MOVE）。交互会话 true；断开/非交互桌面 false。</summary>
    private static bool PhysicalInputAvailable()
    {
        try
        {
            var input = new Input { Type = InputMouse, Mi = new MouseInput { DwFlags = MouseEventFMove } };
            return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>UiTools 集合定义：真实 UI/单进程目标，串行执行（不与其他集合并行共享 UiSampleApp 进程名/UIA）。</summary>
[CollectionDefinition("UiTools", DisableParallelization = true)]
public sealed class UiToolsCollection
{
}
