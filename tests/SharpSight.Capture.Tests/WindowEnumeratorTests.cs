using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// 可见顶层窗口枚举单测（宿主 screenshot_windows 的数据源）：只出根窗口、每条带有效 hwnd/pid，
/// MatchCount 无选择器语义恒 1。不读屏幕 DC，锁屏/无头会话亦可用（条目数可为 0，不做非空断言）。
/// </summary>
public sealed class WindowEnumeratorTests
{
    [Fact]
    public void EnumerateVisibleWindows_ReturnsRootWindowsWithHwndAndPid()
    {
        var windows = ScreenCapture.EnumerateVisibleWindows();

        Assert.All(windows, w =>
        {
            Assert.NotEqual(IntPtr.Zero, w.Hwnd);
            Assert.True(w.Pid > 0, $"窗口 {w.Hwnd} 的 pid 应 > 0");
            Assert.Equal(1, w.MatchCount);   // 无选择器语义：全列时无「命中数」
        });
    }
}
