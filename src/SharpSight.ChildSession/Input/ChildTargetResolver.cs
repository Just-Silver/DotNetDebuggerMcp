using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;

namespace SharpSight.ChildSession.Input;

public sealed record ResolvedChildTarget(int ProcessId, long StartedTicks, int SessionId, string Hwnd, Rectangle Bounds);

public sealed class ChildTargetResolver(int sessionId)
{
    public ResolvedChildTarget ReadProcess(int processId)
    {
        using var process = Process.GetProcessById(processId);
        using var worker = Process.GetCurrentProcess();
        if (worker.SessionId != sessionId || sessionId == 0 || process.SessionId != sessionId)
            throw new InvalidOperationException("目标进程不属于worker实际子会话，拒绝操作。");
        return new(process.Id, process.StartTime.ToUniversalTime().Ticks, sessionId, "", Rectangle.Empty);
    }

    public Task<ResolvedChildTarget> ResolveAsync(int processId, string hwnd, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Resolve(processId, hwnd));
    }

    public ResolvedChildTarget Resolve(int processId, string hwnd)
    {
        var identity = ReadProcess(processId);
        var previous = SetThreadDpiAwarenessContext(new(-4));
        try
        {
            var candidates = new List<nint>();
            if (hwnd.Length != 0)
            {
                if (!long.TryParse(hwnd, CultureInfo.InvariantCulture, out var raw) || raw == 0)
                    throw new ArgumentException("hwnd必须是非零十进制窗口句柄。");
                candidates.Add(new(raw));
            }
            else
                EnumWindows((window, _) =>
                {
                    GetWindowThreadProcessId(window, out var pid);
                    if (pid == processId && IsWindowVisible(window) && GetAncestor(window, 2) == window)
                        candidates.Add(window);
                    return true;
                }, 0);
            if (candidates.Count != 1) throw new InvalidOperationException("目标没有唯一可见顶层窗口，请显式给hwnd。");
            var handle = candidates[0];
            GetWindowThreadProcessId(handle, out var owner);
            if (owner != processId || !IsWindowVisible(handle) || GetAncestor(handle, 2) != handle ||
                !GetWindowRect(handle, out var rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
                throw new InvalidOperationException("窗口已失效、不可见或不属于目标进程。");
            if (ReadProcess(processId).StartedTicks != identity.StartedTicks)
                throw new InvalidOperationException("目标进程创建时间已变化。");
            return identity with
            {
                Hwnd = handle.ToInt64().ToString(CultureInfo.InvariantCulture),
                Bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom)
            };
        }
        finally { if (previous != 0) SetThreadDpiAwarenessContext(previous); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { internal int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(nint window, nint extra);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint extra);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
}
