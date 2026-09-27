using DotNetDebuggerMcp.Configuration;
using ModelContextProtocol.Server;
using SharpSight.Capture;

using System.ComponentModel;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Screenshot;

/// <summary>
/// 显示器发现工具（截图域寻址，spec §4.2）：列本机显示器清单，供 <c>screenshot</c> 的
/// <c>display=1/2…|primary|left|right</c> 取值——agent 不必靠传错值读报错来发现台数/主屏/布局。
/// 只读：不截图、不要求调试会话、不入缓存、不过 ToolPipeline、不写 Actions。
/// </summary>
[McpServerToolType]
public static class ScreenshotDisplaysTool
{
    /// <summary>列出本机显示器（1 基编号/主屏/物理边界/缩放），供 screenshot 的 display 参数取值。</summary>
    [McpServerTool]
    [Description("列出本机显示器清单（1 基编号、是否主屏、物理边界、缩放比），用于填 screenshot 的 display：编号=第 N 台、primary=主屏、left/right=主屏左/右侧相邻显示器。只读，不截图、不要求调试会话。")]
    public static Task<string> ScreenshotDisplays(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var displays = ScreenCapture.EnumerateDisplays();
            if (displays.Length == 0) return Task.FromResult(CaptureText.NoDisplays);

            var sb = new StringBuilder();
            sb.Append($"显示器: {displays.Length} 台");
            foreach (var d in displays)
            {
                var b = d.Bounds;   // 库侧 0 基枚举序 → 对外 1 基编号（与 screenshot 的 display 一致）
                sb.AppendLine();
                sb.Append($"  {d.Index + 1}{(d.IsPrimary ? " [主屏]" : "      ")}  \"{d.DeviceName}\"  ({b.X},{b.Y}) {b.Width}x{b.Height}");
                if (Math.Abs(d.Scale - 1.0) > 1e-9) sb.Append($"  缩放 {d.Scale * 100:0}%");
            }
            sb.AppendLine();
            sb.Append($"display 取值：1..{displays.Length} 或 primary；left/right 取主屏左/右侧相邻显示器。");
            return Task.FromResult(sb.ToString());
        }
        catch (OperationCanceledException)
        {
            return Task.FromResult(CaptureText.CanceledGeneric);
        }
        catch (Exception ex)
        {
            return Task.FromResult($"列出显示器失败：{ex.Message}");
        }
    }
}
