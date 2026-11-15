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
        var find = await CliAsync("ui_find", new { instance = _instance, process = $"{pid}", limit = 100 });
        Require(find.Message.Contains("InputText", StringComparison.Ordinal), "UIA未找到目标控件。");
        await ExerciseInputAsync(pid, find);
        await VerifyPicturesAsync(fixture, pid);
        await VerifyDebugAsync(pid);
        await CliAsync("child_process", new { action = "output", instance = _instance, taskId });
        await McpAsync("child_process", new { action = "cancel", instance = _instance, taskId });
        await RequireTaskStoppedAsync(taskId);
        if (round == 2) await VerifyDisconnectedAsync(identity, fixture);
        if (round == 3) await VerifyWorkerExitAsync(identity);
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
                foreach (var line in (await File.ReadAllTextAsync(path)).Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    // 写入中的最后一行尚不是完整事件，不能把它判成应用业务失败。
                    if (!line.TrimEnd().EndsWith('}')) continue;
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
            await McpAsync("ui_wait", new { instance = _instance, process = $"{pid}", text = "ChildSessionTarget", timeoutSeconds = 10 });
            var capture = await _mcp.CallRawAsync("screenshot",
                Arguments(new { instance = _instance, mode = "window", processId = pid, clientArea = true }), CancellationToken.None);
            var image = capture.Content.OfType<ImageContentBlock>().Single();
            var bytes = image.Data.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(_roundDirectory, "lime.png"), bytes);
            using var stream = new MemoryStream(bytes);
            using var decoded = new Bitmap(stream);
            Require(decoded.GetPixel(decoded.Width / 2, decoded.Height / 2).ToArgb() == Color.Lime.ToArgb(), "MCP PNG实际像素不是child标记。");
            Record("png-pixels", new { pid, decoded.Width, decoded.Height, argb = Color.Lime.ToArgb() });
        }
        finally { await CliAsync("child_process", new { action = "cancel", instance = _instance, taskId = P(marker.Data, "taskId").GetString() }); }
    }
}
