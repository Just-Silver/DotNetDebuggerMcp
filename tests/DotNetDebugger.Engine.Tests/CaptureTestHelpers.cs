using DotNetDebugger.Engine.Capture;

namespace DotNetDebugger.Engine.Tests;

/// <summary>Capture 系测试共享辅助：等窗出现轮询（窗口创建异步，50ms 间隔）。T2/T4/T5 用例共用。</summary>
internal static class CaptureTestHelpers
{
    /// <summary>按 pid 轮询等窗口出现，超时返回 null。</summary>
    public static WindowHandleInfo? WaitFound(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        WindowHandleInfo? w;
        do
        {
            w = ScreenCapture.FindMainWindow(pid, "");
            if (w is not null) return w;
            Thread.Sleep(50);
        } while (DateTime.UtcNow < deadline);
        return null;
    }

    /// <summary>按标题子串轮询（忽略大小写），超时返回 null。</summary>
    public static WindowHandleInfo? WaitFound(string title, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        WindowHandleInfo? w;
        do
        {
            w = ScreenCapture.FindMainWindow(0, title);
            if (w is not null) return w;
            Thread.Sleep(50);
        } while (DateTime.UtcNow < deadline);
        return null;
    }
}
