using System.ComponentModel;
using DotNetDebuggerMcp.Services.Gui;
using ModelContextProtocol.Server;

namespace DotNetDebuggerMcp.Tools.Screenshot;

/// <summary>窗口发现与调试目标无关，允许定位非.NET应用。</summary>
[McpServerToolType]
public static class ScreenshotWindowsTool
{
    /// <summary>只列可寻址窗口，避免对不可截取的助手窗口继续派发请求。</summary>
    [Description("列出本机可见顶层窗口（Z 序：hwnd 十进制、pid、标题、是否前台/最小化），用于填 screenshot 的 hwnd / windowTitle（标题子串）/ processId。枚举不限 .NET 进程（debug_processes 只列可附加的 .NET 进程）。只列**有标题且尺寸大于 0**的可截窗口（系统 shell 助手/阴影层/消息泵停车窗等不可寻址目标已过滤；前台窗口即使无标题也保留并标 (无标题)）。只读，不截图、不要求调试会话。")]
    public static Task<string> ScreenshotWindows(
        [Description("标题子串过滤（忽略大小写），默认空=列出全部可见顶层窗口。")] string contains = "",
        CancellationToken cancellationToken = default)
        => ScreenshotWindowDiscovery.ScreenshotWindows(contains, cancellationToken);
}
