using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotNetDebuggerMcp.Client;

internal sealed partial class ChildSessionCases
{
    private async Task VerifyDebugAsync(int pid)
    {
        var attached = false;
        try
        {
            await DebugAsync("debug_attach", new { processId = pid }, "附加");
            attached = true;
            await DebugAsync("debug_breakpoint", new
            {
                action = "set", moduleName = "ChildSessionTarget.dll",
                typeName = "ChildSessionTarget.ProbeForm", memberName = "OnCommit"
            }, "断点已设");
            await DebugAsync("debug_continue", new { }, "继续");
            var find = await CliAsync("ui_find", new { instance = _instance, process = $"{pid}", limit = 100 });
            var button = Element(find.Message, "CommitButton").Bounds;
            await PointerAsync(pid, "click", button.X + 20, button.Y + 10);
            await DebugAsync("debug_wait", new { waitSeconds = 10 }, "Stopped");
            await DebugAsync("debug_frame", new { }, "OnCommit");
            var blocked = await _mcp.CallRawAsync("child_session",
                Arguments(new { action = "stop", instance = _instance }), CancellationToken.None);
            Require(Text(blocked).Contains("请先debug_session disconnect", StringComparison.Ordinal), "本服务器附加时stop未被保护。");
        }
        finally
        {
            if (attached) await DebugAsync("debug_session", new { action = "disconnect" }, "断开");
        }
    }

    private async Task VerifyDisconnectedAsync(JsonElement identity, string fixture)
    {
        var task = await StartTaskAsync(fixture, ["sleep", "60000"]);
        var session = P(identity, "childSessionId").GetInt32();
        using var self = Process.GetCurrentProcess();
        Require(session != self.SessionId && session > 0, "拒绝断开非自有child。");
        Require(WTSDisconnectSession(0, session, false), $"断连失败：{Marshal.GetLastWin32Error()}");
        for (var i = 0; i < 50; i++)
        {
            var status = await CliAsync("child_session", new { action = "status", instance = _instance });
            if (P(status.Data, "state").GetString() == "Disconnected") break;
            await Task.Delay(100);
            Require(i < 49, "未观察到Disconnected。");
        }
        await CliAsync("child_process", new { action = "list", instance = _instance });
        await CliAsync("child_process", new { action = "output", instance = _instance, taskId = P(task.Data, "taskId").GetString() });
        await CliAsync("child_process", new { action = "cancel", instance = _instance, taskId = P(task.Data, "taskId").GetString() });
        await RequireTaskStoppedAsync(P(task.Data, "taskId").GetString()!);
        var denied = await _mcp.CallRawAsync("ui_find",
            Arguments(new { instance = _instance, process = $"{P(task.Data, "processId").GetInt32()}" }), CancellationToken.None);
        Require(Text(denied).Contains("不回退", StringComparison.Ordinal), "断连GUI未拒绝。");
        Record("disconnect-verified", new { session });
    }

    private async Task VerifyWorkerExitAsync(JsonElement identity)
    {
        var pid = P(identity, "workerPid").GetInt32();
        using var process = Process.GetProcessById(pid);
        Require(process.StartTime.ToUniversalTime().Ticks == P(identity, "workerStartedTicks").GetInt64() &&
            process.SessionId == P(identity, "childSessionId").GetInt32(), "拒绝结束身份不匹配的worker。");
        process.Kill();
        await process.WaitForExitAsync();
        var result = await _mcp.CallRawAsync("child_process",
            Arguments(new { instance = _instance, action = "list", timeoutSeconds = 1 }), CancellationToken.None);
        var reply = JsonSerializer.Deserialize<Reply>(Text(result), Json)!;
        Require(reply.Status is "not-executed" or "unknown", "worker退出后仍宣称完成。");
        Record("worker-exit", reply);
    }

    private static async Task RequireRolesExitedAsync(JsonElement identity)
    {
        foreach (var role in new[] { "holder", "worker" })
        {
            var pid = P(identity, role + "Pid").GetInt32();
            var ticks = P(identity, role + "StartedTicks").GetInt64();
            for (var i = 0; i < 100; i++)
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != ticks) break;
                    Require(i < 99, $"stop后自有{role}仍存活：{pid}");
                }
                catch (ArgumentException) { break; }
                await Task.Delay(100);
            }
        }
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSDisconnectSession(nint server, int session, [MarshalAs(UnmanagedType.Bool)] bool wait);
}
