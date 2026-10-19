using DotNetDebuggerMcp.Tools.Screenshot;
using System.Reflection;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// screenshot(mode=list-displays) 单测（视觉族寻址发现）：输出须含台数、主屏标记与「display 取值」提示
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

    /// <summary>发现入口已并入 screenshot：mode 取值必须在 agent 可见的参数说明里（否则等于不可发现）。</summary>
    [Fact]
    public void 发现入口在screenshot的mode参数说明里()
    {
        var method = typeof(ScreenshotTool).GetMethod(nameof(ScreenshotTool.Screenshot))!;
        var mode = method.GetParameters().First(p => p.Name == "mode");
        var description = mode.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()!.Description!;
        Assert.Contains("list-displays", description);
        Assert.Contains("list-windows", description);
    }
}
