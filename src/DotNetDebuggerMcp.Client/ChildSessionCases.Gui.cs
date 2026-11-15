using System.Diagnostics;
using System.Drawing;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;

namespace DotNetDebuggerMcp.Client;

internal sealed partial class ChildSessionCases
{
    private async Task CrossProcessAsync(JsonElement identity, int round)
    {
        var fixture = Path.Combine(TestDataHelper.RepoRoot, "tests", "TestData", "ChildSessionTarget", "ChildSessionTarget.exe");
        var task = await StartTaskAsync(fixture, ["gui", _roundDirectory]);
        var taskId = P(task.Data, "taskId").GetString()!;
        var pid = P(task.Data, "processId").GetInt32();
        var ready = await WaitEventAsync(e => P(e, "kind").GetString() == "ready");
        Require(P(ready, "pid").GetInt32() == pid, "GUI消息来自另一进程。");
        Require(P(ready, "sessionId").GetInt32() == P(identity, "childSessionId").GetInt32(), "GUI未运行于自有child。");
        var listed = await CliAsync("child_process", new { action = "list", instance = _instance });
        Require(P(listed.Data, "tasks").EnumerateArray().Any(t => P(t, "taskId").GetString() == taskId), "跨CLI未找到MCP任务。");
        var holderWindow = await ParentSessionObserver.AwaitReviewableAsync(
            P(identity, "holderPid").GetInt32(), P(identity, "holderStartedTicks").GetInt64(),
            P(identity, "parentSessionId").GetInt32());
        if (round == 1) await VerifyWindowRestoreAsync(identity, holderWindow, pid);
        var observer = ParentSessionObserver.Start(P(identity, "holderPid").GetInt32(), holderWindow);
        JsonElement isolation = default;
        try
        {
            var find = await CliAsync("ui_find", new { instance = _instance, process = $"{pid}", limit = 100 });
            Require(find.Message.Contains("InputText", StringComparison.Ordinal), "UIA未找到目标控件。");
            await ExerciseInputAsync(pid, find);
            await VerifyPicturesAsync(fixture, pid);
            await VerifyDebugAsync(pid);
        }
        finally
        {
            isolation = await StopParentObservationAsync(observer);
        }
        // 判定放在 finally 之后：业务已抛错时保留原异常及已记录证据；业务成功但隔离未确认才在这里判本轮失败。
        Require(P(isolation, "confirmed").GetBoolean(),
            $"父侧隔离未确认，本轮不得视为通过：{P(isolation, "isolation").GetString()}");
        await CliAsync("child_process", new { action = "output", instance = _instance, taskId });
        await McpAsync("child_process", new { action = "cancel", instance = _instance, taskId });
        await RequireTaskStoppedAsync(taskId);
        if (round == 2) await VerifyDisconnectedAsync(identity, fixture);
        if (round == 3) await VerifyWorkerExitAsync(identity);
    }

    private async Task<JsonElement> StopParentObservationAsync(ParentSessionObserver observer)
    {
        var evidence = await observer.StopAsync();
        Record("parent-isolation", evidence);
        Console.WriteLine(P(evidence, "confirmed").GetBoolean()
            ? $"[信息] {P(evidence, "isolation").GetString()}"
            : $"[注意] {P(evidence, "isolation").GetString()}");
        return evidence;
    }

    /// <summary>最小化已核实身份的连接窗，发一次 ui_find 触发正式还原，单列记录前后证据后交回严格采样。</summary>
    private async Task VerifyWindowRestoreAsync(JsonElement identity, nint holderWindow, int pid)
    {
        var evidence = await ConnectionWindowRestore.RunAsync(P(identity, "holderPid").GetInt32(), holderWindow, async () =>
        {
            var find = await CliAsync("ui_find", new { instance = _instance, process = $"{pid}", limit = 100 });
            Require(find.Message.Contains("InputText", StringComparison.Ordinal), "还原后UIA未找到目标控件。");
            return "ui_find 命中 InputText";
        });
        Record("connection-restore", evidence);
        Console.WriteLine($"[信息] 连接窗还原检查：{P(evidence, "business").GetString()}，父侧前台与光标未变。");
    }

    private Task<Reply> StartTaskAsync(string executable, string[] args)
        => McpAsync("child_process", new
        {
            action = "start", instance = _instance, executable, arguments = JsonSerializer.Serialize(args),
            workingDirectory = Path.GetDirectoryName(executable)!
        });

    private async Task ExerciseInputAsync(int pid, Reply find)
    {
        var input = Element(find.Message, "InputText");
        var commit = Element(find.Message, "CommitButton");
        var canvas = Element(find.Message, "InputCanvas");
        var frame = P(find.Data, "frameId").GetInt32();
        var observation = P(find.Data, "observationId").GetString()!;
        await McpAsync("ui_input", new
        {
            instance = _instance, process = $"{pid}", index = input.Index, value = "UIA初值", commit = false,
            frameId = frame, observationId = observation
        });
        var read = await CliAsync("ui_get", new { instance = _instance, process = $"{pid}", index = input.Index, what = "value", frameId = frame });
        Require(read.Message.Contains("UIA初值", StringComparison.Ordinal), "UIA写读结果不一致。");
        await PointerAsync(pid, "click", input.Bounds.X + 15, input.Bounds.Y + 10);
        await KeyboardAsync(pid, "chord", keys: "Ctrl+A");
        await KeyboardAsync(pid, "text", text: "中文😀原生输入");
        await KeyboardAsync(pid, "key", keys: "Enter");
        await WaitEventAsync(e => P(e, "kind").GetString() == "text" && P(e, "text").GetString() == "中文😀原生输入");
        await PointerAsync(pid, "click", commit.Bounds.X + 20, commit.Bounds.Y + 10);
        await WaitEventAsync(e => P(e, "kind").GetString() == "commit" && P(e, "text").GetString() == "中文😀原生输入");
        await KeyboardAsync(pid, "chord", keys: "Ctrl+K");
        await WaitEventAsync(e => P(e, "kind").GetString() == "chord");
        await PointerAsync(pid, "doubleclick", canvas.Bounds.X + 40, canvas.Bounds.Y + 40);
        await WaitEventAsync(e => P(e, "kind").GetString() == "doubleclick");
        await CliAsync("child_pointer", new
        {
            instance = _instance, processId = pid, action = "drag",
            x = canvas.Bounds.X + 80, y = canvas.Bounds.Y + 80,
            endX = canvas.Bounds.X + 220, endY = canvas.Bounds.Y + 140, durationMs = 300
        });
        var drag = await WaitEventAsync(e => P(e, "kind").GetString() == "drag");
        Require(P(drag, "endX").GetInt32() - P(drag, "x").GetInt32() == 140, "真实拖拽终点不符。");
        await CliAsync("child_pointer", new
        {
            instance = _instance, processId = pid, action = "scroll",
            x = canvas.Bounds.X + 100, y = canvas.Bounds.Y + 100, delta = 120
        });
        await WaitEventAsync(e => P(e, "kind").GetString() == "wheel" && P(e, "delta").GetInt32() == 120);
    }

    private Task<Reply> PointerAsync(int pid, string action, int x, int y)
        => CliAsync("child_pointer", new { instance = _instance, processId = pid, action, x, y });

    private Task<Reply> KeyboardAsync(int pid, string action, string text = "", string keys = "")
        => McpAsync("child_keyboard", new { instance = _instance, processId = pid, action, text, keys });

    private static (int Index, Rectangle Bounds) Element(string text, string id)
    {
        var row = text.Split('\n').Single(line => line.Contains($"AutoId={id} ", StringComparison.Ordinal));
        var m = Regex.Match(row, @"\[(\d+)\].*Rect=\((-?\d+),(-?\d+)\s+(\d+)x(\d+)\)");
        Require(m.Success, $"无法读取真实UIA矩形：{row}");
        var n = m.Groups.Cast<Group>().Skip(1).Select(g => int.Parse(g.Value)).ToArray();
        return (n[0], new(n[1], n[2], n[3], n[4]));
    }

    private async Task<JsonElement> WaitEventAsync(Func<JsonElement, bool> matches)
    {
        var path = Path.Combine(_roundDirectory, "events.jsonl");
        var until = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < until)
        {
            if (File.Exists(path))
            {
                // 写端持续追加：只消费换行结束的完整行，半行/未完成 UTF-8 留到下次轮询；读不到不等于事件已发生。
                foreach (var line in await FixtureEventLog.ReadCompleteLinesAsync(path))
                {
                    var value = JsonSerializer.Deserialize<JsonElement>(line);
                    if (matches(value)) { Record("fixture-event", value); return value; }
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException($"未观察到真实GUI事件：{path}");
    }

    private async Task RequireTaskStoppedAsync(string taskId)
    {
        for (var i = 0; i < 100; i++)
        {
            var state = await CliAsync("child_process", new { action = "status", instance = _instance, taskId });
            if (!P(state.Data, "running").GetBoolean()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("自有任务cancel后仍运行。");
    }

    private async Task VerifyPicturesAsync(string fixture, int guiPid)
    {
        var path = Path.Combine(_roundDirectory, "gui.png");
        await CliAsync("screenshot", new { instance = _instance, mode = "window", processId = guiPid, filePath = path });
        using (var gui = new Bitmap(path)) Require(gui.Width > 600 && gui.Height > 400, "GUI PNG无法解码或尺寸不符。");
        var marker = await StartTaskAsync(fixture, ["marker", "lime"]);
        var pid = P(marker.Data, "processId").GetInt32();
        try
        {
            var (png, decoded) = await CaptureMarkerAsync(pid);
            using (decoded)
            {
                Require(decoded.Width >= 400 && decoded.Height >= 280,
                    $"marker 截图尺寸不符：{decoded.Width}x{decoded.Height}。");
                await File.WriteAllBytesAsync(Path.Combine(_roundDirectory, "lime.png"), png);
                Record("png-pixels", new { pid, decoded.Width, decoded.Height, argb = Color.Lime.ToArgb() });
            }
        }
        finally { await CliAsync("child_process", new { action = "cancel", instance = _instance, taskId = P(marker.Data, "taskId").GetString() }); }
    }

    /// <summary>
    /// 整窗截图优先走 WGC/PrintWindow（遮挡安全、不依赖目标在前台）；若回退到 BitBlt 仍会受遮挡影响。
    /// marker 没有子控件，ui_wait 的 text 只匹配后代 Name，只会得到「超时但 status=completed」；
    /// 因此用截图工具自身的 timeout 等窗口出现，再以真实像素命中（中心 Lime）作为就绪判据。
    /// </summary>
    private async Task<(byte[] Png, Bitmap Image)> CaptureMarkerAsync(int pid)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(15);
        var detail = "尚未取得截图";
        while (true)
        {
            var capture = await _mcp.CallRawAsync("screenshot",
                Arguments(new { instance = _instance, mode = "window", processId = pid, timeoutSeconds = 10 }),
                CancellationToken.None);
            if (capture.Content.OfType<ImageContentBlock>().Count() == 1)
            {
                var candidate = TryDecodeMarker(capture, out detail);
                if (candidate is { } hit) return hit;
            }
            else detail = Text(capture);
            if (DateTimeOffset.UtcNow >= until)
                throw new InvalidOperationException($"未取得真正可解码且中心为 Lime 的 marker 截图：{detail}");
            await Task.Delay(200);
        }
    }

    private static (byte[] Png, Bitmap Image)? TryDecodeMarker(CallToolResult capture, out string detail)
    {
        detail = "";
        var png = ScreenshotImage.DecodeSinglePng(capture);
        Bitmap image;
        try { image = ScreenshotImage.DecodePng(png); }
        catch (InvalidOperationException ex) { detail = ex.Message; return null; }
        var center = image.GetPixel(image.Width / 2, image.Height / 2);
        if (image.Width >= 400 && image.Height >= 280 && center.ToArgb() == Color.Lime.ToArgb())
            return (png, image);
        detail = $"尺寸 {image.Width}x{image.Height}，中心 argb=0x{center.ToArgb():X8}（期望 Lime 0x{Color.Lime.ToArgb():X8}）";
        image.Dispose();
        return null;
    }
}
