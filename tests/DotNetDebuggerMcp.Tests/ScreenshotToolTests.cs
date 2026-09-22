using System.Diagnostics;
using DotNetDebuggerMcp.Tools.Debugger;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// screenshot 工具单测（screenshot 计划 T6；spec §3 契约、§5 错误全表、§6.3 测试策略）。
/// 经 MCP 协议真实往返（DebugMcpToolsTests.ConnectAsync/CallAsync 同款基建）。
/// screen/region 真实抓取在锁屏/安全桌面环境会被系统拒绝 BitBlt——按 spec §6.4 探测 Skip 不红
/// （window 模式走 WGC/PrintWindow 不受影响，锁屏下仍恒跑）。
/// [Collection("AppServices")]：类内静态 seam（InlineImageLimitBytes）+ MCP 连接，按 tests/AGENTS 纪律串行。
/// </summary>
[Collection("AppServices")]
public sealed class ScreenshotToolTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] JpegMagic = [0xFF, 0xD8];

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
        Assert.Equal("mode 仅支持 window/screen/region（当前 \"box\"）。", r.Text());
    }

    [Fact]
    public async Task InvalidFormat_ReturnsSpecMessage()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
        {
            ["format"] = "gif",
        });
        Assert.True(r.IsError != true, r.Text());
        Assert.Equal("format 仅支持 png/jpeg（当前 \"gif\"）。", r.Text());
    }

    [Fact]
    public async Task RegionMalformed_AllThreeForms_ReturnSpecMessage()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        const string msg = "region 格式应为 \"x,y,w,h\"（mode=screen 返回图像素空间，原点左上）。";
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
        var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>());
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("请提供 processId 或 windowTitle 定位窗口", r.Text());
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

    [Fact]
    public async Task JpegWindow_EncodesJpegImageBlock()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        using var app = LaunchUiSampleApp();
        try
        {
            var r = await DebugMcpToolsTests.CallAsync(mcp, "screenshot", new Dictionary<string, object?>
            {
                ["processId"] = app.Id,
                ["format"] = "jpeg",
                ["quality"] = 60,
                ["timeoutSeconds"] = 5,
            });
            Assert.True(r.IsError != true, r.Text());
            var img = ImageOf(r);
            Assert.NotNull(img);
            Assert.Equal("image/jpeg", img!.MimeType);
            Assert.True(img.DecodedData.Length > 2 && img.DecodedData.Span[..2].SequenceEqual(JpegMagic));
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
    public void ResolveScreenshotPath_DefaultDirPattern_And_FilePathOverride()
    {
        // 默认目录+文件名模式（spec §5.1：screenshot-{ts}-{mode}[-{pid}].{ext}，pid 仅 window）
        var def = DebugScreenshotTool.ResolveScreenshotPath("", "window", "png", 12345);
        Assert.StartsWith(DotNetDebuggerMcp.Configuration.AppConfig.ScreenshotsDir, def);
        Assert.Matches(@"screenshot-\d{8}-\d{9}-window-12345\.png$", def);
        var scr = DebugScreenshotTool.ResolveScreenshotPath("", "screen", "jpg", 0);
        Assert.Matches(@"-screen\.jpg$", scr);
        // filePath 分支：绝对化 + 创建父目录（目录创建副作用在此清理）
        var customDir = Path.Combine(Path.GetTempPath(), $"screenshot-dirtest-{Guid.NewGuid():N}");
        try
        {
            var custom = DebugScreenshotTool.ResolveScreenshotPath(
                Path.Combine(customDir, "a.png"), "window", "png", 1);
            Assert.Equal(Path.Combine(customDir, "a.png"), custom);
            Assert.True(Directory.Exists(customDir));
        }
        finally { try { Directory.Delete(customDir, true); } catch { /* 忽略 */ } }
    }
}
