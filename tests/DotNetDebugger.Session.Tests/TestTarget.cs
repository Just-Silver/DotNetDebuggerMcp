using System.Diagnostics;
using System.Text;

namespace DotNetDebugger.Session.Tests;

/// <summary>Session 测试辅助：DebugTarget 路径 + 进程启动（自动排空 stdout/stderr）。</summary>
internal static class TestTarget
{
    public static string DebugTargetExe => Locate("tests", "TestData", "DebugTarget.exe");

    private static string Locate(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotNetDebuggerMcp.slnx"))) break;
            dir = dir.Parent;
        }
        if (dir is null) throw new DirectoryNotFoundException("未找到仓库根目录（缺少 DotNetDebuggerMcp.slnx）");
        return Path.Combine([dir.FullName, .. segments]);
    }

    /// <summary>启动 DebugTarget（delay 秒供 attach 窗口），自动排空 stdout/stderr，返回包装对象。</summary>
    public static DebugTargetProcess StartDebugTarget(string args)
    {
        var psi = new ProcessStartInfo(DebugTargetExe, args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var p = Process.Start(psi)!;
        return new DebugTargetProcess(p);
    }
}

/// <summary>DebugTarget 进程包装（排空 stdout/stderr + IDisposable）。</summary>
internal sealed class DebugTargetProcess : IDisposable
{
    public Process Process { get; }
    public StringBuilder Output { get; } = new();

    public DebugTargetProcess(Process process)
    {
        Process = process;
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (Output) Output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { /* 排空 stderr 防阻塞 */ };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public int Id => Process.Id;
    public bool HasExited => Process.HasExited;
    public bool WaitForExit(int ms) => Process.WaitForExit(ms);

    public void Dispose() { try { Process.Dispose(); } catch { } }
}
