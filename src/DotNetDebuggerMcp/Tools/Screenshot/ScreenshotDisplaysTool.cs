using System.ComponentModel;
using DotNetDebuggerMcp.Services.Gui;
using ModelContextProtocol.Server;

namespace DotNetDebuggerMcp.Tools.Screenshot;

/// <summary>先发现显示器再选择截图目标，避免调用者猜测编号和布局。</summary>
[McpServerToolType]
public static class ScreenshotDisplaysTool
{
    /// <summary>发现操作保持只读，不触发目标应用交互。</summary>
    [Description("列出本机显示器清单（1 基编号、是否主屏、物理边界、缩放比），用于填 screenshot 的 display：编号=第 N 台、primary=主屏、left/right=主屏左/右侧相邻显示器。只读，不截图、不要求调试会话。")]
    public static Task<string> ScreenshotDisplays(CancellationToken cancellationToken = default)
        => ScreenshotDisplayDiscovery.ScreenshotDisplays(cancellationToken);
}
