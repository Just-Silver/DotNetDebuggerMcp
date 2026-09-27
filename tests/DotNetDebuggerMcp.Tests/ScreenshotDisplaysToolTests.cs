using DotNetDebuggerMcp.Tools.Screenshot;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// screenshot_displays 单测（视觉族寻址发现）：输出须含台数、主屏标记与「display 取值」提示
/// （agent 据此填 screenshot 的 display，无需靠传错值读报错）。无桌面会话（枚举为空）时跳过不红（spec §6.4 预案）。
/// </summary>
public sealed class ScreenshotDisplaysToolTests
{
    [Fact]
    public async Task ListsDisplays_WithOneBasedNumberingAndPrimaryMark()
    {
        var text = await ScreenshotDisplaysTool.ScreenshotDisplays(TestContext.Current.CancellationToken);

        if (text.StartsWith("未枚举到任何显示器"))
            Assert.Skip("无桌面会话（枚举不到显示器），spec §6.4 预案");

        Assert.Matches(@"显示器: \d+ 台", text);
        Assert.Contains("[主屏]", text);           // Windows 恒有一台主屏
        Assert.Contains("display 取值：", text);   // 把可用取值直接告诉 agent
    }

    /// <summary>agent 是靠 MCP 工具名发现它的——名字必须是 snake_case 的 `screenshot_displays`（SDK 由方法名派生）。</summary>
    [Fact]
    public async Task McpToolList_ExposesSnakeCaseName()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var tools = await mcp.ListToolsAsync(options: null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains(tools, t => t.Name == "screenshot_displays");
    }
}
