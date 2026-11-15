using System.Runtime.InteropServices;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;

namespace SharpSight.ChildSession.Lifecycle;

/// <summary>
/// worker 由临时计划任务投送，正常路径在握完手（或失败）后立即删除；进程崩溃会留下无触发器的同名任务。
/// 启动时清理这些残留，但绝不碰仍属活动实例的任务，也不按前缀批量杀进程。
/// </summary>
internal static class ChildScheduledTaskCleanup
{
    internal const string TaskPrefix = "SharpSight-child-";
    private const int RootFolderTasks = 0;

    internal static string TaskNameFor(string instance) => TaskPrefix + instance;

    /// <summary>无实例记录=孤儿；实例已停止=残留；持有器已退出=残留。其余（启动中/就绪且持有器健在）一律跳过。</summary>
    internal static bool ShouldRemove(string? instanceState, bool holderAlive)
        => instanceState is null || instanceState == "Stopped" || !holderAlive;

    /// <summary>返回删除的任务数；COM/权限失败只记 stderr，不影响宿主启动。</summary>
    internal static int Sweep(ChildInstanceStore store)
    {
        var states = ReadStates(store);
        var removed = 0;
        dynamic? service = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
            service.Connect();
            dynamic folder = service.GetFolder("\\");
            foreach (var name in Names((object)folder))
            {
                var instance = name[TaskPrefix.Length..];
                var state = states.GetValueOrDefault(instance);
                if (!ShouldRemove(state?.State, state is not null && HolderAlive(state.Holder))) continue;
                try
                {
                    folder.DeleteTask(name, 0);
                    removed++;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"删除残留子会话计划任务 {name} 失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"清理残留子会话计划任务失败：{ex.Message}");
        }
        finally
        {
            if (service is not null && Marshal.IsComObject(service)) Marshal.FinalReleaseComObject(service);
        }
        return removed;
    }

    /// <summary>COM 集合是 1 基索引，dynamic 下不能直接 foreach（不是 IEnumerable）。</summary>
    private static List<string> Names(dynamic folder)
    {
        var names = new List<string>();
        dynamic tasks = folder.GetTasks(RootFolderTasks);
        var count = (int)tasks.Count;
        for (var index = 1; index <= count; index++)
        {
            var name = (string)tasks.Item(index).Name;
            if (name.StartsWith(TaskPrefix, StringComparison.Ordinal)) names.Add(name);
        }
        return names;
    }

    private static Dictionary<string, ChildBootstrap> ReadStates(ChildInstanceStore store)
    {
        try
        {
            return store.ListAsync(CancellationToken.None).GetAwaiter().GetResult()
                .GroupBy(state => state.Identity.Instance, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            // 读不到实例记录时宁可不清（无法判断哪些任务仍属活动实例）。
            Console.Error.WriteLine($"读取子会话实例记录失败，跳过残留计划任务清理：{ex.Message}");
            return [];
        }
    }

    private static bool HolderAlive(ChildPeer holder)
    {
        try { return holder.Matches(ChildPeer.Capture(holder.ProcessId)); }
        catch (Exception) { return false; }
    }
}
