using DotNetDebuggerMcp.Tools.Screenshot;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// screenshot_windows 单测（视觉族寻址发现）：输出须给出 <c>hwnd</c>/<c>pid</c>/标题三元组
/// （agent 据此填 screenshot 的 hwnd/windowTitle/processId），并验证 <c>filter</c> 正反向。
/// 无桌面会话（枚举为空）时跳过不红。
/// </summary>
public sealed class ScreenshotWindowsToolTests
{
    [Fact]
    public async Task ListsVisibleWindows_WithHwndPidTitle()
    {
        var text = await ScreenshotWindowsTool.ScreenshotWindows("", TestContext.Current.CancellationToken);

        if (text.StartsWith("未枚举到任何可见顶层窗口"))
            Assert.Skip("无桌面会话（枚举不到可见顶层窗口）");

        Assert.Matches(@"可见顶层窗口: \d+ 个", text);
        Assert.Contains("hwnd=", text);
        Assert.Contains("pid=", text);
        Assert.Contains("\"", text);   // 标题以双引号包裹
    }

    /// <summary>agent 是靠 MCP 工具名发现它的——名字必须是 snake_case 的 `screenshot_windows`（SDK 由方法名派生）。</summary>
    [Fact]
    public async Task McpToolList_ExposesSnakeCaseName()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var tools = await mcp.ListToolsAsync(options: null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "screenshot_windows");
    }

    [Fact]
    public async Task Filter_NoMatch_ReturnsHint()
    {
        var text = await ScreenshotWindowsTool.ScreenshotWindows(
            "__no_such_window_title__", TestContext.Current.CancellationToken);

        Assert.True(
            text.StartsWith("未找到标题含") || text.StartsWith("未枚举到任何可见顶层窗口"),
            text);
    }

    [Fact]
    public async Task Filter_ExistingTitle_KeepsRows()
    {
        var all = await ScreenshotWindowsTool.ScreenshotWindows("", TestContext.Current.CancellationToken);
        if (all.StartsWith("未枚举到任何可见顶层窗口"))
            Assert.Skip("无桌面会话（枚举不到可见顶层窗口）");

        // 取首行窗口标题（形如 `  hwnd=123  pid=456  "标题"`；标题可含引号，故取首/末引号之间）
        var row = all.Split('\n').First(l => l.Contains("hwnd="));
        var title = row[(row.IndexOf('"') + 1)..];
        title = title[..title.LastIndexOf('"')];
        if (title.Length == 0)
            Assert.Skip("首个窗口无标题，跳过 filter 阳性用例");

        var filtered = await ScreenshotWindowsTool.ScreenshotWindows(title, TestContext.Current.CancellationToken);

        Assert.Contains("可见顶层窗口: ", filtered);
        Assert.Contains(title, filtered);
    }
}
