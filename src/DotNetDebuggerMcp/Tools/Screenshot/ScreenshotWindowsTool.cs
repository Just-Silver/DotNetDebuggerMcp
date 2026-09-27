using DotNetDebuggerMcp.Configuration;
using ModelContextProtocol.Server;
using SharpSight.Capture;

using System.ComponentModel;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Screenshot;

/// <summary>
/// 窗口发现工具（截图域寻址，spec §4.2）：列本机可见顶层窗口（Z 序），供 <c>screenshot</c> 的
/// <c>hwnd</c>/<c>windowTitle</c>/<c>processId</c> 取值。枚举**不限 .NET 进程**（与 debug_processes
/// 只列可附加 .NET 进程互补；截图能截任意进程的窗口）。
/// 只读：不截图、不要求调试会话、不入缓存、不过 ToolPipeline、不写 Actions。
/// </summary>
[McpServerToolType]
public static class ScreenshotWindowsTool
{
    /// <summary>输出条数上限（防超长；超出提示用 filter 缩小）。</summary>
    private const int MaxRows = 200;

    /// <summary>列出可见顶层窗口（hwnd/pid/标题/最小化/前台），供 screenshot 的 hwnd/windowTitle/processId 取值。</summary>
    [McpServerTool]
    [Description("列出本机可见顶层窗口（Z 序：hwnd 十进制、pid、标题、是否前台/最小化），用于填 screenshot 的 hwnd / windowTitle（标题子串）/ processId。枚举不限 .NET 进程（debug_processes 只列可附加的 .NET 进程）。只读，不截图、不要求调试会话。")]
    public static Task<string> ScreenshotWindows(
        [Description("标题子串过滤（忽略大小写），默认空=列出全部可见顶层窗口。")] string filter = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var all = ScreenCapture.EnumerateVisibleWindows();
            if (all.Length == 0) return Task.FromResult("未枚举到任何可见顶层窗口（可能处于无桌面会话）。");

            var query = filter.Trim();
            var hits = string.IsNullOrEmpty(query)
                ? all
                : all.Where(w => w.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();

            if (hits.Length == 0)
                return Task.FromResult($"未找到标题含 \"{query}\" 的可见顶层窗口（当前共 {all.Length} 个）。");

            var foreground = ScreenCapture.FindForegroundWindow()?.Hwnd ?? IntPtr.Zero;
            var shown = hits.Length > MaxRows ? hits.Take(MaxRows).ToArray() : hits;

            var sb = new StringBuilder();
            sb.Append($"可见顶层窗口: {hits.Length} 个（▶=前台；各行可作 screenshot 的 hwnd / windowTitle / processId 取值）");
            foreach (var w in shown)
            {
                sb.AppendLine();
                sb.Append($"  {(w.Hwnd == foreground ? "▶" : " ")} hwnd={w.Hwnd.ToInt64()}  pid={w.Pid}  \"{w.Title}\"");
                if (w.IsIconic) sb.Append("  [最小化]");
            }
            if (hits.Length > shown.Length)
            {
                sb.AppendLine();
                sb.Append($"（已达上限 {MaxRows} 行，可用 filter 缩小范围）");
            }
            return Task.FromResult(sb.ToString());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(CaptureText.CanceledGeneric);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"列出窗口失败：{ex.Message}");
        }
    }
}
