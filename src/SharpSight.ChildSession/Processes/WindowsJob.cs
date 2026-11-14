using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SharpSight.ChildSession.Lifecycle;

namespace SharpSight.ChildSession.Processes;

internal sealed class WindowsJob : IDisposable
{
    private static readonly object CreationGate = new();
    private readonly SafeFileHandle _job = ProcessNative.CreateJobObject(0, null);
    internal Process Process { get; private set; } = null!;
    internal long StartedTicks { get; private set; }
    internal StreamReader Output { get; private set; } = null!;
    internal StreamReader Error { get; private set; } = null!;

    internal WindowsJob(string executable, string[] arguments, string directory, Dictionary<string, string> environment)
    {
        try
        {
            if (_job.IsInvalid) throw ErrorFor("创建Job失败。");
            var limits = new ProcessNative.ExtendedLimits { Basic = new ProcessNative.BasicLimits { Flags = 0x2000 } };
            if (!ProcessNative.SetInformationJobObject(_job, 9, ref limits, Marshal.SizeOf<ProcessNative.ExtendedLimits>()))
                throw ErrorFor("配置Job失败。");
            lock (CreationGate) Start(executable, arguments, directory, environment);
        }
        catch { _job.Dispose(); throw; }
    }

    private void Start(string executable, string[] arguments, string directory, Dictionary<string, string> environment)
    {
        using var stdout = new InheritedPipe();
        using var stderr = new InheritedPipe();
        using var stdin = new InheritedPipe(parentReads: false);
        var startup = new ProcessNative.StartupInfo { Size = Marshal.SizeOf<ProcessNative.StartupInfo>(),
            Flags = 0x100, Input = stdin.Read.DangerousGetHandle(),
            Output = stdout.Write.DangerousGetHandle(), Error = stderr.Write.DangerousGetHandle() };
        var command = new ChildHostCommand(executable, []).ArgumentString([executable, .. arguments]);
        var block = string.Join('\0', environment.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key + "=" + p.Value)) + "\0\0";
        var pointer = Marshal.StringToHGlobalUni(block);
        ProcessNative.ProcessInformation info = default;
        try
        {
            if (!ProcessNative.CreateProcess(executable, new StringBuilder(command), 0, 0, true,
                0x08000000 | 0x400 | 4, pointer, directory, ref startup, out info))
                throw ErrorFor("创建任务进程失败。");
            if (!ProcessNative.AssignProcessToJobObject(_job, info.Process)) throw ErrorFor("任务未能归入自有Job。");
            Process = Process.GetProcessById(info.Pid);
            StartedTicks = Process.StartTime.ToUniversalTime().Ticks;
            Output = new StreamReader(new FileStream(stdout.TakeRead(), FileAccess.Read), Encoding.UTF8);
            Error = new StreamReader(new FileStream(stderr.TakeRead(), FileAccess.Read), Encoding.UTF8);
            if (ProcessNative.ResumeThread(info.Thread) == uint.MaxValue) throw ErrorFor("恢复任务运行失败。");
        }
        catch
        {
            if (info.Process != 0) ProcessNative.TerminateProcess(info.Process, 1);
            Output?.Dispose(); Error?.Dispose(); Process?.Dispose();
            throw;
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
            if (info.Thread != 0) ProcessNative.CloseHandle(info.Thread);
            if (info.Process != 0) ProcessNative.CloseHandle(info.Process);
        }
    }

    internal bool HasProcesses
    {
        get
        {
            if (!ProcessNative.QueryInformationJobObject(_job, 1, out var value, Marshal.SizeOf<ProcessNative.Accounting>(), 0))
                throw ErrorFor("无法确认Job进程状态。");
            return value.Active != 0;
        }
    }

    internal void Cancel()
    {
        if (HasProcesses && !ProcessNative.TerminateJobObject(_job, 1)) throw ErrorFor("取消自有Job失败。");
    }

    public void Dispose()
    {
        _job.Dispose();
        Output?.Dispose(); Error?.Dispose(); Process?.Dispose();
    }

    private static Win32Exception ErrorFor(string message) => new(Marshal.GetLastWin32Error(), message);

    private sealed class InheritedPipe : IDisposable
    {
        internal SafeFileHandle Read;
        internal SafeFileHandle Write;
        internal InheritedPipe(bool parentReads = true)
        {
            var attributes = new ProcessNative.SecurityAttributes
                { Length = Marshal.SizeOf<ProcessNative.SecurityAttributes>(), Inherit = 1 };
            if (!ProcessNative.CreatePipe(out Read, out Write, ref attributes, 0)) throw ErrorFor("创建输出管道失败。");
            // 子进程不能继承父端，尤其不能用自己继承的stdin写端阻止EOF。
            if (!ProcessNative.SetHandleInformation(parentReads ? Read : Write, 1, 0))
            {
                Read.Dispose(); Write.Dispose();
                throw ErrorFor("隔离父端管道句柄失败。");
            }
        }
        internal SafeFileHandle TakeRead()
        {
            if (!ProcessNative.SetHandleInformation(Read, 1, 0)) throw ErrorFor("设置父端管道权限失败。");
            var result = Read;
            Read = new SafeFileHandle(0, false);
            return result;
        }
        public void Dispose() { Read.Dispose(); Write.Dispose(); }
    }
}
