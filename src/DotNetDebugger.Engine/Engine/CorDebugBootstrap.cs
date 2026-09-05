using System.Runtime.InteropServices;
using ClrDebug;

namespace DotNetDebugger.Engine.Engine;

/// <summary>结果：引导产物（CorDebug + 目标进程信息）。</summary>
public sealed record BootstrapResult(CorDebug CorDebug, int ProcessId);

/// <summary>
/// 手动引导序列（research/06 §7 Manual 模式 + spike 实测）：
/// - Attach：EnumerateCLRs → CreateVersionStringFromModule → CreateDebuggingInterfaceFromVersionEx
///   → Initialize → SetManagedHandler → DebugActiveProcess
/// - Launch：CreateProcessForLaunch(suspend) → GetStartupNotificationEvent → ResumeProcess → 等 CLR 就绪
///   → EnumerateCLRs → ... → DebugActiveProcess → SetEvent(continue) → CloseCLREnumeration/CloseResumeHandle
/// </summary>
public static class CorDebugBootstrap
{
    /// <summary>附加到已运行进程。</summary>
    public static BootstrapResult Attach(DbgShim dbgshim, int pid, CorDebugManagedCallback handler)
    {
        var clrs = dbgshim.EnumerateCLRs(pid);
        try
        {
            if (clrs.Items.Length == 0)
                throw new InvalidOperationException($"目标进程 {pid} 未加载 .NET CLR（可能启动过早或非 .NET 进程）");
            var clr = clrs.Items[0];
            var version = dbgshim.CreateVersionStringFromModule(pid, clr.Path);
            var corDebug = dbgshim.CreateDebuggingInterfaceFromVersionEx(
                CorDebugInterfaceVersion.CorDebugVersion_4_0, version);
            corDebug.Initialize();
            corDebug.SetManagedHandler(handler);
            corDebug.DebugActiveProcess(pid, win32Attach: false);
            return new BootstrapResult(corDebug, pid);
        }
        finally
        {
            dbgshim.CloseCLREnumeration(clrs);
        }
    }

    /// <summary>以挂起方式启动新进程并附加（早期断点能力，CLR 启动前可设断点）。</summary>
    public static BootstrapResult Launch(DbgShim dbgshim, string commandLine, CorDebugManagedCallback handler,
        int timeoutMs = 15000, string? workingDirectory = null)
    {
        var proc = dbgshim.CreateProcessForLaunch(commandLine, bSuspendProcess: true,
            lpCurrentDirectory: workingDirectory ?? Directory.GetCurrentDirectory());
        try
        {
            var ready = dbgshim.GetStartupNotificationEvent(proc.ProcessId);
            dbgshim.ResumeProcess(proc.ResumeHandle);
            // 等 CLR 启动通知（可超时）
            var started = NativeMethods.WaitForSingleObject(ready, timeoutMs);
            if (started != 0)
                throw new TimeoutException($"等待目标进程 {proc.ProcessId} CLR 启动超时（{timeoutMs}ms）");

            var clrs = dbgshim.EnumerateCLRs(proc.ProcessId);
            if (clrs.Items.Length == 0)
                throw new InvalidOperationException($"目标进程 {proc.ProcessId} 未加载 .NET CLR");
            var clr = clrs.Items[0];
            var version = dbgshim.CreateVersionStringFromModule(proc.ProcessId, clr.Path);
            var corDebug = dbgshim.CreateDebuggingInterfaceFromVersionEx(
                CorDebugInterfaceVersion.CorDebugVersion_4_0, version);
            corDebug.Initialize();
            corDebug.SetManagedHandler(handler);
            corDebug.DebugActiveProcess(proc.ProcessId, win32Attach: false);
            dbgshim.CloseCLREnumeration(clrs);

            // 放行 CLR 继续启动（coreclr 已加载完成；此处可在设早期断点后再 SetEvent）
            NativeMethods.SetEvent(clr.Handle);
            return new BootstrapResult(corDebug, proc.ProcessId);
        }
        finally
        {
            dbgshim.CloseResumeHandle(proc.ResumeHandle);
        }
    }
}

/// <summary>dbgshim 引导用到的少量 Win32（WaitForSingleObject/SetEvent）。</summary>
internal static class NativeMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern int WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetEvent(IntPtr hEvent);
}
