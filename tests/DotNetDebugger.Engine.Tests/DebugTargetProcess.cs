using System.Diagnostics;
using System.Text;

namespace DotNetDebugger.Engine.Tests;

/// <summary>DebugTarget 进程 + 排空的 stdout/stderr 收集器。</summary>
internal sealed class DebugTargetProcess : IDisposable
{
    public Process Process { get; }
    public StringBuilder Output { get; } = new();
    public StringBuilder Error { get; } = new();

    private DebugTargetProcess(Process process) => Process = process;

    public int Id => Process.Id;
    public bool HasExited => Process.HasExited;

    /// <summary>
    /// 启动 DebugTarget 测试目标进程并自动排空 stdout/stderr——避免子进程 Console 写满重定向管道缓冲而阻塞
    /// （MCP/调试子进程的经典坑——AGENTS.md「宿主必须持续排空子进程 stderr」同源教训）。
    /// </summary>
    public static DebugTargetProcess Start(string args)
    {
        var psi = new ProcessStartInfo(TestPaths.DebugTargetExe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var process = Process.Start(psi)!;
        var wrapper = new DebugTargetProcess(process);
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (wrapper.Output) wrapper.Output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (wrapper.Error) wrapper.Error.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return wrapper;
    }

    /// <summary>等待进程退出（内部持续排空由异步事件保证）；超时返回 false。</summary>
    public bool WaitForExit(int timeoutMs = 10000) => Process.WaitForExit(timeoutMs);

    public void Dispose()
    {
        try { Process.Dispose(); } catch { }
    }
}
