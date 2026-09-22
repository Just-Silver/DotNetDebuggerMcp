using DotNetDebugger.Engine.Capture;
using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Drawing;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// screenshot 独立截图工具（spec：docs/planning/specs/2026-09-22-screenshot-tool-design.md §3/§5）：
/// 不要求调试会话、不入缓存不过 ToolPipeline、不写 Actions/AgentView（D9/Web 冻结）。
/// 职责分层：宿主=参数校验、等窗轮询、头部组装、落盘、CallToolResult 组装；
/// 坐标换算/缩放/编码/裁剪/纯黑检测唯一在 Engine（spec §4.2，宿主不碰）。
/// 返回 Task&lt;CallToolResult&gt; 是「工具返回 Task&lt;string&gt;」铁律的图片类例外（spec §3.3），
/// 错误仍为纯文本 content 且不设 IsError（与现有工具行为一致，agent 按文案识别）。
/// </summary>
[McpServerToolType]
public static class DebugScreenshotTool
{
    /// <summary>截取窗口/屏幕画面返回图片（window/screen/region 三模式；参数语义与默认值见各参数 <c>[Description]</c>）。</summary>
    [McpServerTool]
    [Description("截取窗口/屏幕画面返回图片，供多态模型观察 UI 状态做自动化冒烟。独立工具，不要求调试会话。" +
        "mode=window（默认）按 processId 或 windowTitle 定位目标主窗口（窗口未出现会等 timeoutSeconds 秒，默认 5）；" +
        "mode=screen 截全屏；mode=region 按 region=\"x,y,w,h\"（mode=region 时必填）截局部，坐标以 mode=screen 返回的图像素为准" +
        "（原点左上）——建议先 screen 看全景再裁局部。format 默认 png，jpeg+quality 可压体积；图片过大自动改为落盘返回绝对路径。" +
        "坐标/状态判断仍以 debug_state/debug_stack 为准，本工具只提供视觉观察。")]
    public static async Task<CallToolResult> Screenshot(
        [Description("截图模式：window（默认，截目标主窗口）/ screen（全屏）/ region（局部）。")] string mode = "window",
        [Description("window 定位：目标进程 pid（0=未提供）；非 0 时优先于 windowTitle。")] int processId = 0,
        [Description("window 定位：窗口标题子串（忽略大小写）；processId=0 时生效。")] string windowTitle = "",
        [Description("mode=region 时必填，\"x,y,w,h\"（mode=screen 返回图像素空间，原点左上）。")] string region = "",
        [Description("输出格式：png（默认）/ jpeg。")] string format = "png",
        [Description("jpeg 质量 0-100（默认 80，越界自动收紧）；png 忽略。")] int quality = 80,
        [Description("仅 window：等窗口出现秒数（默认 5，0-30；0=立即试一次）。")] int timeoutSeconds = 5,
        [Description("非空=强制落盘到该路径；空=仅图片超 2MB 时落盘到本地 screenshots 目录。")] string filePath = "",
        CancellationToken cancellationToken = default)
    {
        try
        {
            // —— 参数校验（spec §5.2 文案逐字）——
            mode = (mode ?? "").Trim().ToLowerInvariant();
            if (mode is not ("window" or "screen" or "region"))
                return TextOnly($"mode 仅支持 window/screen/region（当前 \"{mode}\"）。");
            format = (format ?? "").Trim().ToLowerInvariant();
            if (format is not ("png" or "jpeg" or "jpg"))
                return TextOnly($"format 仅支持 png/jpeg（当前 \"{format}\"）。");
            if (format == "jpg") format = "jpeg";
            quality = Math.Clamp(quality, 0, 100);
            timeoutSeconds = Math.Clamp(timeoutSeconds, 0, 30);
            cancellationToken.ThrowIfCancellationRequested();

            Rectangle? clip = null;
            if (mode == "region")
            {
                var parts = (region ?? "").Split(',');
                var nums = parts.Length == 4 && parts.All(p => int.TryParse(p.Trim(), out _))
                    ? parts.Select(p => int.Parse(p.Trim())).ToArray()
                    : null;
                if (nums is null || nums[2] <= 0 || nums[3] <= 0)
                    return TextOnly("region 格式应为 \"x,y,w,h\"（mode=screen 返回图像素空间，原点左上）。");
                clip = new Rectangle(nums[0], nums[1], nums[2], nums[3]);
            }

            // —— 抓取（等窗轮询在宿主，换算/缩放/编码唯一在 Engine，spec §4.4）——
            CaptureResult result;
            string targetLine;
            var ext = format == "jpeg" ? "jpg" : "png";
            if (mode == "window")
            {
                var located = await LocateWindowAsync(processId, windowTitle, timeoutSeconds, cancellationToken);
                if (located.Error is not null) return TextOnly(located.Error);   // spec §5.2 双空/超时文案
                var info = located.Info!;
                targetLine = $"目标:   窗口 \"{info.Title}\" (pid={info.Pid})";
                if (info.MatchCount > 1) targetLine += $"（命中 {info.MatchCount} 个可见窗口，已截主窗口）";
                result = ScreenCapture.CaptureWindow(info.Hwnd,
                    AppConfig.ScreenshotMaxDimension, format, quality);
            }
            else
            {
                result = ScreenCapture.CaptureScreen(clip,
                    AppConfig.ScreenshotMaxDimension, format, quality);
                targetLine = mode == "screen"
                    ? "目标:   屏幕"
                    : $"目标:   屏幕区域 ({clip!.Value.X},{clip.Value.Y},{clip.Value.Width},{clip.Value.Height})";
            }

            // —— 头部（spec §3.3；展示算术宿主做，坐标换算不碰）——
            var header = new StringBuilder();
            header.AppendLine(targetLine);
            var scaled = result.Width != result.NativeWidth || result.Height != result.NativeHeight;
            var pct = (int)Math.Round(100.0 * result.Width / Math.Max(1, result.NativeWidth));
            if (mode == "region")
            {
                if (scaled || result.ClippedToScreen)
                {
                    header.Append($"尺寸:   区域 {result.Width}x{result.Height}（{pct}%）");
                    if (result.ClippedToScreen) header.Append("，已裁至屏幕交集");
                    header.AppendLine();
                }
            }
            else if (scaled)
            {
                header.AppendLine($"尺寸:   原生 {result.NativeWidth}x{result.NativeHeight} → {result.Width}x{result.Height} ({pct}%)");
            }
            if (mode == "window") header.AppendLine($"来源:   {result.Source}");
            if (result.WasAllBlack) header.AppendLine("备注:   画面为纯黑（目标可能未渲染）");

            // —— 双轨：内联 image 块 vs 落盘（spec §5.1；落盘失败仍附块，两害相权保 agent 能看到画面）——
            var b64Len = ((long)result.Image.Length + 2) / 3 * 4;
            var wantFile = !string.IsNullOrWhiteSpace(filePath) || b64Len >= AppConfig.InlineImageBase64Bytes;
            var attachImage = true;
            if (wantFile)
            {
                try
                {
                    var full = ResolveScreenshotPath(filePath, mode, ext, processId);
                    File.WriteAllBytes(full, result.Image);
                    header.AppendLine($"已落盘: {full}");
                    attachImage = false;
                }
                catch (Exception ex)
                {
                    header.AppendLine($"已尝试落盘失败: {ex.Message}");   // 仍附块
                }
            }
            header.AppendLine("---");

            var content = new List<ContentBlock> { new TextContentBlock { Text = header.ToString() } };
            if (attachImage)
                // FromBytes（非 Data setter）：SDK 语义 Data=base64 文本字节，FromBytes 存原始字节并懒编码
                content.Add(ImageContentBlock.FromBytes(result.Image, $"image/{format}"));
            return new CallToolResult { Content = content };
        }
        catch (OperationCanceledException)
        {
            return TextOnly("screenshot 已取消（可重试）。");
        }
        catch (CaptureException ex)
        {
            return TextOnly(ex.Message);   // Engine 生成的 spec §5.2 约定文案（屏外/全失败）
        }
        catch (Exception ex)
        {
            return TextOnly($"截图失败：{ex.Message}");   // 防御兜底，不抛（铁律）
        }
    }

    /// <summary>
    /// window 定位（spec §3.1 择一语义）：pid&gt;0 仅按 pid（忽略标题）→ 标题子串 → 活动会话目标 pid
    /// 兜底 → 中文提示二选一。轮询 50ms 间隔至 timeoutSeconds（0=立即一次）；超时返回 spec §5.2 文案。
    /// </summary>
    private static async Task<(WindowHandleInfo? Info, string? Error)> LocateWindowAsync(
        int processId, string windowTitle, int timeoutSeconds, CancellationToken ct)
    {
        var byPid = processId > 0;
        var byTitle = !byPid && !string.IsNullOrWhiteSpace(windowTitle);
        if (!byPid && !byTitle)
        {
            var sessionPid = DebugSessionService.Manager.Active?.ProcessId ?? 0;
            if (sessionPid > 0) { byPid = true; processId = sessionPid; }
        }
        if (!byPid && !byTitle)
            return (null, "请提供 processId 或 windowTitle 定位窗口（两者皆空时也可先 debug_launch 建立会话自动取目标 pid）。");

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        WindowHandleInfo? hit;
        do
        {
            ct.ThrowIfCancellationRequested();
            hit = byPid
                ? ScreenCapture.FindMainWindow(processId, "")
                : ScreenCapture.FindMainWindow(0, windowTitle);
            if (hit is not null) return (hit, null);
            if (DateTime.UtcNow >= deadline) break;
            await Task.Delay(50, ct);
        } while (true);

        var selector = byPid ? $"processId={processId}" : $"标题含 \"{windowTitle}\"";
        return (null, $"{timeoutSeconds} 秒内未找到匹配的可见窗口（{selector}）。");
    }

    internal static string ResolveScreenshotPath(string filePath, string mode, string ext, int processId)
    {
        if (!string.IsNullOrWhiteSpace(filePath))
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            return Path.GetFullPath(filePath);
        }
        Directory.CreateDirectory(AppConfig.ScreenshotsDir);
        var pidSeg = mode == "window" ? $"-{processId}" : "";
        var name = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmssfff}-{mode}{pidSeg}.{ext}";
        return Path.Combine(AppConfig.ScreenshotsDir, name);
    }

    private static CallToolResult TextOnly(string text)
        => new() { Content = [new TextContentBlock { Text = text }] };
}
