using System.Runtime.InteropServices;
using System.Security.Principal;
using SharpSight.ChildSession.Lifecycle;

namespace SharpSight.ChildSession.Connection;

internal static class ChildWorkerLauncher
{
    internal static async Task LaunchAsync(ChildInstanceStore store, ChildBootstrap state,
        ChildHostCommand command, CancellationToken ct)
    {
        var taskName = ChildScheduledTaskCleanup.TaskNameFor(state.Identity.Instance);
        var objects = new List<object>();
        dynamic service = Track(Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!, objects);
        try
        {
            service.Connect();
            dynamic folder = Track(service.GetFolder("\\"), objects);
            dynamic task = Track(service.NewTask(0), objects);
            Configure(task, command, store.Root, state.Identity.Instance, objects);
            using var user = WindowsIdentity.GetCurrent();
            dynamic registered = Track(folder.RegisterTaskDefinition(taskName, task, 2, user.Name, null, 3, null), objects);
            try
            {
                Track(registered.RunEx(null, 4, state.Identity.ChildSessionId, null), objects);
                var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    var current = await store.ReadAsync(state.Identity.Instance, ct);
                    if (current.State is "Stopping" or "Stopped") return;
                    if (current.Worker is not null && current.State == "Ready") return;
                    await Task.Delay(100, ct);
                }
                throw new TimeoutException($"worker未就绪；计划任务结果={registered.LastTaskResult}。");
            }
            finally { folder.DeleteTask(taskName, 0); }
        }
        finally
        {
            foreach (var value in objects.AsEnumerable().Reverse())
                if (Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
        }
    }

    private static void Configure(dynamic task, ChildHostCommand command, string root, string instance, List<object> objects)
    {
        using var user = WindowsIdentity.GetCurrent();
        dynamic principal = Track(task.Principal, objects);
        principal.UserId = user.Name;
        principal.LogonType = 3;
        principal.RunLevel = 0;
        dynamic settings = Track(task.Settings, objects);
        settings.DisallowStartIfOnBatteries = false;
        settings.StopIfGoingOnBatteries = false;
        settings.ExecutionTimeLimit = "PT0S";
        dynamic actions = Track(task.Actions, objects);
        dynamic action = Track(actions.Create(0), objects);
        action.Path = command.Executable;
        action.Arguments = command.ArgumentString("--child-internal", "worker", root, instance);
        action.WorkingDirectory = AppContext.BaseDirectory;
    }

    private static object Track(object value, List<object> objects) { objects.Add(value); return value; }
}
