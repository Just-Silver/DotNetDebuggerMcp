using DotNetDebugger.Engine.Engine;
using System.Runtime.InteropServices;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// D2 进程树父子快照助手（只读，无会话关联）：kernel32 Toolhelp 单次快照拿全表 pid → parentPid，
/// 与 <see cref="ClrProcessFinder.List()"/>（.NET 进程权威枚举）交集得到某 root 的 .NET 子孙进程链。
/// 快照失败（句柄异常/权限/P/Invoke 损坏）降级返回空字典——调用方提示「无法判定父子关系」。
/// Windows 专属（本仓库调试器依赖 DbgShim.win-x64、测试跑 win32，无需 #if）。
/// </summary>
internal static class ProcessTreeSnapshot
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>全表 pid → parentPid 快照；失败（句柄异常/权限）返回空字典。</summary>
    internal static IReadOnlyDictionary<int, int> QueryParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero) return map; // 失败降级：空
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return map;
            do { map[checked((int)entry.th32ProcessID)] = checked((int)entry.th32ParentProcessID); }
            while (Process32Next(snapshot, ref entry));
            return map;
        }
        catch
        {
            return map; // 损坏快照：空（调用方提示无法判定父子）
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    /// <summary>
    /// root 的 .NET 子孙进程链（BFS 层序、父先于子，Depth=1 起、真实 ParentPid 由入队时父 pid 携带）。
    /// 防环：visited（PID 复用竞态理论兜底，同一快照内无复用）；parentMap 空或 dotnet 空 → 返回空。
    /// </summary>
    internal static IReadOnlyList<(ClrProcessInfo Info, int Depth, int ParentPid)> DescendantsOf(
        int rootPid, IReadOnlyList<ClrProcessInfo> dotnet, IReadOnlyDictionary<int, int> parentMap)
    {
        if (parentMap.Count == 0 || dotnet.Count == 0) return [];

        // 反索引：parentPid → 直接子进程 pid 列表（单次快照全表）
        var children = new Dictionary<int, List<int>>();
        foreach (var kv in parentMap)
        {
            if (!children.TryGetValue(kv.Value, out var list)) children[kv.Value] = list = new();
            list.Add(kv.Key);
        }

        var byPid = dotnet.ToDictionary(p => p.ProcessId);
        var result = new List<(ClrProcessInfo, int, int)>();
        var visited = new HashSet<int> { rootPid };
        var queue = new Queue<(int Pid, int Depth, int ParentPid)>();
        foreach (var c in children.GetValueOrDefault(rootPid) ?? []) queue.Enqueue((c, 1, rootPid));

        while (queue.Count > 0)
        {
            var (pid, depth, parentPid) = queue.Dequeue();
            if (!visited.Add(pid)) continue; // 防环
            if (byPid.TryGetValue(pid, out var info))
                result.Add((info, depth, parentPid)); // 仅 .NET 进程入链（可 attach 才有意义）
            foreach (var c in children.GetValueOrDefault(pid) ?? []) queue.Enqueue((c, depth + 1, pid));
        }
        return result;
    }
}
