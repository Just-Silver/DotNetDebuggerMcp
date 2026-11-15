using System.Runtime.InteropServices;
using System.Text;

namespace SharpSight.ChildSession.Lifecycle;

/// <summary>
/// 持有器与 worker 都是长期后台进程，没人读它们的控制台：诊断输出必须落盘。
/// 日志放系统临时目录，实例状态目录只保留生命周期数据（instance.json、IPC 参数）。
/// </summary>
internal static class ChildDiagnosticLog
{
    private const long MaximumBytes = 256 * 1024;

    internal static string PathFor(string instance, string role)
        => Path.Combine(Path.GetTempPath(), "DotNetDebuggerMcp", "child", instance, role + ".log");

    internal static void Attach(string instance, string role)
    {
        try
        {
            var path = PathFor(instance, role);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) && new FileInfo(path).Length > MaximumBytes)
                File.Move(path, path + ".1", true);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var writer = TextWriter.Synchronized(new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetOut(writer);
            Console.SetError(writer);
        }
        catch (Exception ex)
        {
            // 日志只是诊断能力，实例登记与执行仍应按原流程继续。
            Console.Error.WriteLine($"子会话诊断日志不可用：{ex.Message}");
        }
    }

    /// <summary>
    /// 计划任务投送的控制台程序会带出可见终端窗口（Windows 11 默认宿主是 Windows Terminal），
    /// 而 worker 平时不输出任何内容；脱离控制台让该窗口关闭，诊断输出已转到文件。
    /// </summary>
    internal static void DetachConsole()
    {
        try { FreeConsole(); }
        catch (Exception ex) { Console.Error.WriteLine($"worker 脱离控制台失败：{ex.Message}"); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();
}
