using System.Diagnostics;
using System.Drawing;
using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Tools.Screenshot;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using SharpSight.Capture;
using SharpSight.UiAutomation;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// screenshot 工具单测（screenshot 计划 T6；spec §3 契约、§5 错误全表、§6.3 测试策略）。
/// 经 MCP 协议真实往返（DebugMcpToolsTests.ConnectAsync/CallAsync 同款基建）。
/// screen/region 真实抓取在锁屏/安全桌面环境会被系统拒绝 BitBlt——按 spec §6.4 探测 Skip 不红
/// （window 模式走 WGC/PrintWindow 不受影响，锁屏下仍恒跑）。
/// [Collection("AppServices")]：MCP 连接与 AppServices 静态状态（含 `AppConfig` 的落盘阈值/目录 seam
/// `ConfigureForTest`），按 tests/AGENTS 纪律串行。
/// </summary>
[Collection("AppServices")]
public sealed class ScreenshotToolTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static string UiSampleAppExe => Path.Combine(
        Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, "UiSampleApp", "UiSampleApp.exe");

    private static ImageContentBlock? ImageOf(CallToolResult r)
        => r.Content.OfType<ImageContentBlock>().FirstOrDefault();

    // ===== 启动/清理（UiSampleApp 独立副本；UseShellExecute=true 不继承管道句柄=排空纪律，spike 实测）=====

    private static Process LaunchUiSampleApp()
    {
        KillLeftoverUiSampleApps();
        if (!File.Exists(UiSampleAppExe))
            throw new FileNotFoundException("UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1", UiSampleAppExe);
        return Process.Start(new ProcessStartInfo(UiSampleAppExe)
        {
            WorkingDirectory = Path.GetDirectoryName(UiSampleAppExe)!,
            UseShellExecute = true,
        })!;
    }

    private static void KillUiSampleApp(Process app)
    {
        try { if (!app.HasExited) app.Kill(entireProcessTree: true); } catch { /* 已退出忽略 */ }
        KillLeftoverUiSampleApps();
    }

    private static void KillLeftoverUiSampleApps()
    {
        foreach (var p in Process.GetProcessesByName("UiSampleApp"))
            try { p.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
    }

    // ===== screen 可用性探测（锁屏/无头 Skip，spec §6.4）=====

    private static bool? _screenAvailable;

    private static async Task<bool> ScreenAvailableAsync(McpClient mcp)
    {
        if (_screenAvailable is null)
        {
            var probe = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
            {
                ["mode"] = "screen",
            });
            _screenAvailable = probe.Text().Contains("目标:   屏幕", StringComparison.Ordinal);
        }
        return _screenAvailable.Value;
    }

    private static async Task SkipIfScreenUnavailableAsync(McpClient mcp)
    {
        if (!await ScreenAvailableAsync(mcp))
            Assert.Skip("屏幕不可用（锁屏/安全桌面/无头环境拒绝 BitBlt），spec §6.4 预案");
    }

    // ===== 参数校验（spec §5.2 逐条文案）=====

    [Fact]
    public async Task InvalidMode_ReturnsSpecMessage()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
        {
            ["mode"] = "box",
        });
        Assert.True(r.IsError != true, r.Text());
        Assert.Equal("mode 无效：\"box\"（可选 auto/screen/display/window/foreground/region/element）。", r.Text());
    }

    [Fact]
    public async Task RegionMalformed_AllThreeForms_ReturnSpecMessage()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        const string msg = "region 格式应为 \"x,y,w,h\"（坐标为 mode=screen 返回图像素，原点左上）。";
        foreach (var bad in new[] { "1,2,3", "a,b,c,d", "1,2,-3,4", "" })
        {
            var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
            {
                ["mode"] = "region",
                ["region"] = bad,
            });
            Assert.True(r.IsError != true, r.Text());
            Assert.Equal(msg, r.Text());
        }
    }

    [Fact]
    public async Task Window_NoSelector_NoSession_ReturnsPrompt()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();   // 新连接默认无活动会话
        var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
        {
            ["mode"] = "window",   // 默认已改为 auto；此处显式 window 验证无选择器提示
        });
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("请提供 processId 或 windowTitle 定位窗口", r.Text());
    }

    // ===== 模式推断 / 显示器映射 / 兼容性（纯逻辑 + MCP 校验，无 GUI）=====

    [Theory]
    [InlineData("element", "", "", "", "", "element")]
    [InlineData("auto", "", "123456", "", "", "window")]
    [InlineData("auto", "primary", "", "", "", "display")]
    [InlineData("auto", "", "", "", "10,10,50,50", "region")]
    [InlineData("auto", "", "", "", "", "screen")]
    [InlineData("auto", "", "", "notepad", "", "window")]
    [InlineData("auto", "2", "", "", "", "display")]
    public void ResolveMode_Matrix(string mode, string display, string hwnd, string title, string region, string expected)
        => Assert.Equal(expected, ScreenshotTool.ResolveMode(mode, display, hwnd, title, region));

    [Fact]
    public void ResolveMode_ElementAndProcessId_AndPriority()
    {
        Assert.Equal("element", ScreenshotTool.ResolveMode("auto", "", "", "", "", "7"));
        Assert.Equal("window", ScreenshotTool.ResolveMode("auto", "", "", "", "", processId: 42));
        // element 优先级最高（即使同时给了 display/hwnd）
        Assert.Equal("element", ScreenshotTool.ResolveMode("auto", "primary", "123", "", "", "7"));
        // 显式 mode 覆盖推断
        Assert.Equal("screen", ScreenshotTool.ResolveMode("screen", "primary", "123", "", "", "7"));
    }

    [Fact]
    public void TryParseRegion_FourInts_PositiveSizes()
    {
        Assert.True(ScreenshotTool.TryParseRegion("10,20,30,40", out var rect));
        Assert.Equal(new Rectangle(10, 20, 30, 40), rect);
        Assert.True(ScreenshotTool.TryParseRegion(" 1 , 2 , 3 , 4 ", out var trimmed));
        Assert.Equal(new Rectangle(1, 2, 3, 4), trimmed);
        Assert.False(ScreenshotTool.TryParseRegion("1,2,3", out _));
        Assert.False(ScreenshotTool.TryParseRegion("1,2,0,4", out _));
        Assert.False(ScreenshotTool.TryParseRegion("1,2,3,-4", out _));
        Assert.False(ScreenshotTool.TryParseRegion("a,b,c,d", out _));
    }

    private static DisplayInfo Display(int index, bool primary, int x, int y, int w, int h)
        => new(index, $@"\\.\DISPLAY{index + 1}", primary,
            new Rectangle(x, y, w, h), new Rectangle(x, y, w, h), 1.0);

    [Fact]
    public void ResolveDisplayIndex_PrimaryLeftRightOneBasedAndErrors()
    {
        var displays = new[]
        {
            Display(0, true, 0, 0, 1920, 1080),
            Display(1, false, 1920, 0, 1280, 1024),
            Display(2, false, -1280, 0, 1280, 1024),
        };

        Assert.True(ScreenshotTool.TryResolveDisplayIndex(displays, "", out var i, out _));
        Assert.Equal(0, i);                                        // 空 = 主屏
        Assert.True(ScreenshotTool.TryResolveDisplayIndex(displays, "primary", out i, out _));
        Assert.Equal(0, i);
        Assert.True(ScreenshotTool.TryResolveDisplayIndex(displays, "2", out i, out _));
        Assert.Equal(1, i);                                        // 1 基对外编号 → 0 基库索引
        Assert.True(ScreenshotTool.TryResolveDisplayIndex(displays, "3", out i, out _));
        Assert.Equal(2, i);
        Assert.True(ScreenshotTool.TryResolveDisplayIndex(displays, "right", out i, out _));
        Assert.Equal(1, i);
        Assert.True(ScreenshotTool.TryResolveDisplayIndex(displays, "left", out i, out _));
        Assert.Equal(2, i);

        Assert.False(ScreenshotTool.TryResolveDisplayIndex(displays, "0", out _, out var err0));
        Assert.Contains("display 无效", err0);
        Assert.False(ScreenshotTool.TryResolveDisplayIndex(displays, "9", out _, out var err9));
        Assert.Contains("display 无效", err9);
        Assert.False(ScreenshotTool.TryResolveDisplayIndex(displays, "abc", out _, out var errA));
        Assert.Contains("display 无效", errA);
    }

    [Fact]
    public void ResolveDisplayIndex_LeftRightWithoutAdjacent_ReturnsError()
    {
        var single = new[] { Display(0, true, 0, 0, 800, 600) };
        Assert.False(ScreenshotTool.TryResolveDisplayIndex(single, "left", out _, out var err));
        Assert.Contains("没有相邻显示器", err);
    }

    [Fact]
    public async Task IncompatibleCombos_ReturnError_NotSilentDrop()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var cases = new (Dictionary<string, object?> Args, string Expect)[]
        {
            (new() { ["element"] = "保存", ["clientArea"] = true }, "clientArea"),
            (new() { ["mode"] = "window", ["region"] = "1,2,3,4" }, "region"),
            (new() { ["mode"] = "display", ["region"] = "1,2,3,4" }, "region"),
            (new() { ["mode"] = "screen", ["hwnd"] = "123" }, "hwnd"),
            (new() { ["mode"] = "screen", ["processId"] = 123 }, "processId"),
            (new() { ["mode"] = "display", ["clientArea"] = true }, "clientArea"),
            (new() { ["mode"] = "screen", ["element"] = "x" }, "element"),
            (new() { ["mode"] = "foreground", ["processId"] = 123 }, "processId"),
        };
        foreach (var (args, expect) in cases)
        {
            var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", args);
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains("参数不兼容", r.Text());
            Assert.Contains(expect, r.Text());
        }
    }

    [Fact]
    public async Task ElementMode_NoElement_ReturnsExplicitError()
    {
        // R24：mode=element 未给 element 必须显式报错，不得静默截取首个元素。
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
        {
            ["mode"] = "element",
        });
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("需提供 element", r.Text());
    }

    // ===== 代际护栏（ui_* frameId，spec §7.4；无 GUI，校验先于进程解析）=====

    [Fact]
    public async Task StaleFrameId_IsRejected_WithTeachingMessage()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var frames = UiAutomationService.Instance.Frames;
        var stale = frames.Next();
        frames.Next();                                   // 推进代际：stale 变旧帧

        var argsByTool = new Dictionary<string, Dictionary<string, object?>>
        {
            ["ui_action"] = new() { ["process"] = "no-such-proc", ["verb"] = "invoke", ["frameId"] = stale },
            ["ui_input"] = new() { ["process"] = "no-such-proc", ["value"] = "x", ["frameId"] = stale },
            ["ui_get"] = new() { ["process"] = "no-such-proc", ["what"] = "name", ["frameId"] = stale },
        };
        foreach (var (tool, args) in argsByTool)
        {
            var r = await DebugMcpToolsTests.CallAsync(mcp, tool, args);
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains("旧", r.Text());
            Assert.Contains("ui_find", r.Text());
        }
    }

    [Fact]
    public async Task Screenshot_StaleFrameId_IsRejected()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var frames = UiAutomationService.Instance.Frames;
        var stale = frames.Next();
        frames.Next();
        var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
        {
            ["mode"] = "screen", ["frameId"] = stale,
        });
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("旧", r.Text());
        Assert.Contains("ui_find", r.Text());
    }

    // ===== window 模式（WGC/PrintWindow 锁屏可用，恒跑）=====

    [Fact]
    public async Task Window_PidHit_ReturnsPngImageBlock_WithHeader()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
            {
                ["processId"] = app.Id,
                ["timeoutSeconds"] = 5,
            });
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains($"目标:   窗口 \"UiSample\" (pid={app.Id})", r.Text());
            Assert.Contains("来源:   ", r.Text());                       // WGC/PrintWindow/BitBlt 任一，不过度断言（spec §6.2）
            Assert.EndsWith("---", r.Text().TrimEnd('\r', '\n'));        // 行尾跨环境（\r\n/\n）

            var img = ImageOf(r);
            Assert.NotNull(img);
            Assert.Equal("image/png", img!.MimeType);
            Assert.True(img.DecodedData.Length > 8 && img.DecodedData.Span[..8].SequenceEqual(PngMagic));
        }
        finally { KillUiSampleApp(app); }
    }

    [Fact]
    public async Task Window_PidMiss_TimesOut_ReturnsSpecMessage()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        const int deadPid = 4185100;
        var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
        {
            ["processId"] = deadPid,
            ["timeoutSeconds"] = 0,
        });
        Assert.True(r.IsError != true, r.Text());
        Assert.Equal($"0 秒内未找到匹配的可见窗口（processId={deadPid}）。", r.Text());
    }

    // ===== element 模式端到端（spec §10：mode=element 对真实 UiSampleApp）=====

    [Fact]
    public async Task Element_ByName_ReturnsPngImageBlock_WithFrameId()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
            {
                ["mode"] = "element",
                ["processId"] = app.Id,
                ["element"] = "countButton",      // UiSampleApp 契约 AutomationId（Program.cs；定位按 Name/AutoId 子串）
                ["timeoutSeconds"] = 5,
            });
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains("目标:   元素 ", r.Text());
            Assert.Contains("帧:", r.Text());               // element 回填 FrameId（与 ui_find/ui_* 旧帧护栏闭环）
            Assert.EndsWith("---", r.Text().TrimEnd('\r', '\n'));

            var img = ImageOf(r);
            Assert.NotNull(img);
            Assert.Equal("image/png", img!.MimeType);
            Assert.True(img.DecodedData.Length > 8 && img.DecodedData.Span[..8].SequenceEqual(PngMagic));
        }
        finally { KillUiSampleApp(app); }
    }

    // ===== screen/region 真实抓取（锁屏探测 Skip）=====

    [Fact]
    public async Task Screen_ReturnsPngImageBlock_WithTargetLine()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        await SkipIfScreenUnavailableAsync(mcp);
        var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
        {
            ["mode"] = "screen",
        });
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("目标:   屏幕", r.Text());
        var img = ImageOf(r);
        Assert.NotNull(img);
        Assert.True(img!.DecodedData.Span[..8].SequenceEqual(PngMagic));
    }

    [Fact]
    public async Task Region_FullyOutside_ReturnsSpecMessage()
    {
        // 屏外判定在 Engine 换算段（GetDC 之前），锁屏下恒可跑
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
        {
            ["mode"] = "region",
            ["region"] = "99999,99999,10,10",
        });
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("完全在屏幕范围", r.Text());
        Assert.Contains("之外", r.Text());
    }

    // ===== 落盘双轨（spec §5.1；window 模式锁屏安全）=====

    [Fact]
    public async Task ForcedFilePath_WritesFile_NoImageBlock()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        var path = Path.Combine(Path.GetTempPath(), $"screenshot-test-{Guid.NewGuid():N}.png");
        try
        {
            var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
            {
                ["processId"] = app.Id,
                ["filePath"] = path,
                ["timeoutSeconds"] = 5,
            });
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains($"已落盘: {Path.GetFullPath(path)}", r.Text());
            Assert.Single(r.Content);                                   // 仅文本块，无 image
            Assert.True(File.Exists(path), $"落盘文件不存在: {path}（返回文本: {r.Text()}）");
            var bytes = File.ReadAllBytes(path);
            Assert.True(bytes.AsSpan(0, 8).SequenceEqual(PngMagic), $"文件 magic 不符: {Convert.ToHexString(bytes.Take(8).ToArray())}");
        }
        finally
        {
            KillUiSampleApp(app);
            try { File.Delete(path); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public async Task AutoFallback_OverThreshold_WritesDefaultDir_NoImageBlock()
    {
        // 阈值降为 1 字节 → 任何截图都触发「超限自动落盘」分支（spec §10 落盘/2MB seam；spike C 结论）。
        // 直调工具方法而非经 MCP：宿主测试的 MCP 传输是 StdioClientTransport（server 为子进程），
        // 进程内注入的 AppConfig seam 影响不到子进程——只有 in-process 直调才能覆盖该分支。
        var dir = Path.Combine(Path.GetTempPath(), $"screenshot-autofallback-{Guid.NewGuid():N}");
        AppConfig.ConfigureForTest(inlineImageBase64Bytes: 1, screenshotsDirOverride: dir);
        try
        {
            using var app = LaunchUiSampleApp();
            try
            {
                var r = await ScreenshotTool.Screenshot(processId: app.Id, timeoutSeconds: 5,
                    cancellationToken: TestContext.Current.CancellationToken);
                Assert.True(r.IsError != true, r.Text());
                Assert.Null(ImageOf(r));                                  // 自动落盘 → 仅文本，无 image 块（未指定 filePath）
                Assert.Contains("已落盘: ", r.Text());

                var line = r.Text().Split('\n').First(l => l.StartsWith("已落盘: "));
                var file = line["已落盘: ".Length..].Trim('\r', '\n', ' ');
                Assert.StartsWith(Path.GetFullPath(dir), file);
                Assert.True(File.Exists(file), $"落盘文件不存在: {file}（返回文本: {r.Text()}）");
                Assert.True(File.ReadAllBytes(file).AsSpan(0, 8).SequenceEqual(PngMagic));
            }
            finally { KillUiSampleApp(app); }
        }
        finally
        {
            AppConfig.ResetForTest();
            try { Directory.Delete(dir, true); } catch { /* 忽略 */ }
        }
    }

    [Fact]
    public void ResolveScreenshotPath_DefaultDirPattern_And_FilePathOverride()
    {
        // 默认目录+文件名模式（spec §5.1：screenshot-{ts}-{mode}[-{pid}].png，pid 仅 window）
        var def = ScreenshotTool.ResolveScreenshotPath("", "window", 12345);
        Assert.StartsWith(DotNetDebuggerMcp.Configuration.AppConfig.ScreenshotsDir, def);
        Assert.Matches(@"screenshot-\d{8}-\d{9}-window-12345\.png$", def);
        var scr = ScreenshotTool.ResolveScreenshotPath("", "screen", 0);
        Assert.Matches(@"-screen\.png$", scr);
        // 相对 filePath：以截图根目录（临时目录）为基准，绝不落到进程工作目录（防污染调用方项目）
        var rel = ScreenshotTool.ResolveScreenshotPath(Path.Combine("relsub", "b.png"), "window", 1);
        Assert.Equal(Path.Combine(AppConfig.ScreenshotsDir, "relsub", "b.png"), rel);
        Assert.NotEqual(Path.GetFullPath(Path.Combine("relsub", "b.png")), rel);   // 证明不是按 CWD 解析
        try { Directory.Delete(Path.Combine(AppConfig.ScreenshotsDir, "relsub"), true); } catch { /* 忽略 */ }

        // filePath 分支：绝对化 + 创建父目录（目录创建副作用在此清理）
        var customDir = Path.Combine(Path.GetTempPath(), $"screenshot-dirtest-{Guid.NewGuid():N}");
        try
        {
            var custom = ScreenshotTool.ResolveScreenshotPath(
                Path.Combine(customDir, "a.png"), "window", 1);
            Assert.Equal(Path.Combine(customDir, "a.png"), custom);
            Assert.True(Directory.Exists(customDir));
        }
        finally { try { Directory.Delete(customDir, true); } catch { /* 忽略 */ } }
    }
}
