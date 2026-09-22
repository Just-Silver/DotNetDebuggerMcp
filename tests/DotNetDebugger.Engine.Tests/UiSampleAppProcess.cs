using System.Diagnostics;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// UiSampleApp 起停（screenshot Capture 测试用）。UseShellExecute=true 不重定向即满足
/// 「排空子进程 stdout/stderr」纪律：GUI 子进程不继承本进程管道句柄，继承了会让调用方
/// 管道永不关闭（2026-09-22 spike 实测教训）；UiSampleApp 无控制台输出，无管道可卡。
/// </summary>
internal sealed class UiSampleAppProcess : IDisposable
{
    public Process Process { get; }

    private UiSampleAppProcess(Process p) => Process = p;

    public static UiSampleAppProcess Start()
    {
        KillLeftovers();
        var exe = TestPaths.UiSampleAppExe;
        if (!File.Exists(exe))
            throw new FileNotFoundException("UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1", exe);
        var p = Process.Start(new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = true,
        })!;
        return new UiSampleAppProcess(p);
    }

    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); }
        catch { /* 已退出忽略 */ }
        KillLeftovers();
    }

    private static void KillLeftovers()
    {
        foreach (var p in Process.GetProcessesByName("UiSampleApp"))
            try { p.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
    }
}
