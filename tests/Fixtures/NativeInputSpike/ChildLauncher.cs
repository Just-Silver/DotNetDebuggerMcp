using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;

namespace NativeInputSpike;

internal static class ChildLauncher
{
    internal static void Stop(string root)
    {
        var holder = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "connection.json"))).RootElement;
        var worker = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "worker.json"))).RootElement;
        var parentSession = Process.GetCurrentProcess().SessionId;
        if (holder.GetProperty("parentSession").GetInt32() != parentSession ||
            !Native.WTSGetChildSessionId(out var id) || id != holder.GetProperty("childId").GetUInt32() ||
            id != worker.GetProperty("sessionId").GetInt32() || id == parentSession)
            throw new InvalidOperationException("无法确认本轮 child 所有权，拒绝注销。");
        using var target = Process.GetProcessById(worker.GetProperty("pid").GetInt32());
        if (target.StartTime.ToUniversalTime().Ticks != worker.GetProperty("startedTicks").GetInt64() || target.SessionId != id)
            throw new InvalidOperationException("worker 身份已变化，拒绝注销。");
        if (!Native.WTSLogoffSession(0, checked((int)id), false))
            throw new System.ComponentModel.Win32Exception(System.Runtime.InteropServices.Marshal.GetLastWin32Error());
        WaitForChildExit(id);
        CloseHolder(holder);
        Program.Print(new { stoppedChildId = id, holderExited = true });
    }

    private static void CloseHolder(JsonElement holder)
    {
        Process owner;
        try { owner = Process.GetProcessById(holder.GetProperty("pid").GetInt32()); }
        catch (ArgumentException) { return; }
        using (owner)
        {
            if (owner.StartTime.ToUniversalTime().Ticks != holder.GetProperty("startedTicks").GetInt64() ||
                owner.SessionId != holder.GetProperty("parentSession").GetInt32())
                throw new InvalidOperationException("child 已注销，但 holder 身份变化，未关闭该进程。");
            owner.CloseMainWindow();
            if (!owner.WaitForExit(5000)) throw new InvalidOperationException("child 已注销，但连接窗口未退出。");
        }
    }

    private static void WaitForChildExit(uint id)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (Native.WTSGetChildSessionId(out var current) && current != id) return;
            if (current == uint.MaxValue && System.Runtime.InteropServices.Marshal.GetLastWin32Error() == 1168) return;
            Thread.Sleep(100);
        }
        throw new TimeoutException("已请求注销 child，但 15 秒内未确认完成；不重复注销其他会话。");
    }

    internal static void Launch(string root)
    {
        var holder = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "connection.json"))).RootElement;
        using var parent = Process.GetCurrentProcess();
        if (!Native.WTSGetChildSessionId(out var id) || id == uint.MaxValue || id == parent.SessionId)
            throw new InvalidOperationException("没有可投送的 Child Session。");
        if (holder.GetProperty("parentSession").GetInt32() != parent.SessionId)
            throw new InvalidOperationException("连接持有器不属于当前父会话。");
        using var owner = Process.GetProcessById(holder.GetProperty("pid").GetInt32());
        if (holder.GetProperty("childId").GetUInt32() != id ||
            owner.StartTime.ToUniversalTime().Ticks != holder.GetProperty("startedTicks").GetInt64() || owner.SessionId != parent.SessionId)
            throw new InvalidOperationException("连接持有器或 child 身份已变化，拒绝投送。");
        if (File.Exists(Path.Combine(root, "worker.json"))) throw new InvalidOperationException("该目录已有 worker 状态，不重复投送。");
        var taskName = "NativeInputSpike-" + Guid.NewGuid().ToString("N");
        using var identity = WindowsIdentity.GetCurrent();
        dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        scheduler.Connect();
        dynamic folder = scheduler.GetFolder("\\");
        dynamic task = scheduler.NewTask(0);
        task.Principal.UserId = identity.Name;
        task.Principal.LogonType = 3;
        task.Principal.RunLevel = 0;
        task.Settings.DisallowStartIfOnBatteries = false;
        task.Settings.StopIfGoingOnBatteries = false;
        task.Settings.ExecutionTimeLimit = "PT0S";
        dynamic action = task.Actions.Create(0);
        action.Path = Environment.ProcessPath;
        action.Arguments = $"worker \"{root}\" {parent.SessionId} child";
        action.WorkingDirectory = AppContext.BaseDirectory;
        dynamic registered = folder.RegisterTaskDefinition(taskName, task, 2, identity.Name, null, 3, null);
        try
        {
            registered.RunEx(null, 4, checked((int)id), null);
            WaitForWorker(root, id);
            Program.Print(new { taskName, childId = id, parentSession = parent.SessionId });
        }
        finally { folder.DeleteTask(taskName, 0); }
    }

    private static void WaitForWorker(string root, uint id)
    {
        var path = Path.Combine(root, "worker.json");
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                var worker = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
                using var process = Process.GetProcessById(worker.GetProperty("pid").GetInt32());
                if (process.SessionId == id && process.StartTime.ToUniversalTime().Ticks == worker.GetProperty("startedTicks").GetInt64()) return;
                throw new InvalidOperationException("启动的 worker 身份不符。");
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException("任务已提交，但 20 秒未收到 worker 就绪；核对 child 登录和交互令牌。");
    }
}
