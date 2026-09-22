using DotNetDebugger.Engine.Capture;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// 窗口定位单测（screenshot 计划 T2；spec §3.1 定位规则、§6.2 测试策略）：
/// 真实起 UiSampleApp（WinForms，tests/TestData/UiSampleApp），按 pid / 标题子串 / 未命中三态断言。
/// </summary>
public sealed class CaptureWindowFindTests
{
    [Fact]
    public void FindMainWindow_ByPid_HitsUiSampleWindow()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
        Assert.NotNull(w);
        Assert.Equal("UiSample", w!.Title);
        Assert.Equal(app.Process.Id, w.Pid);
        Assert.True(w.Rect.Width > 0 && w.Rect.Height > 0);
        Assert.True(w.MatchCount >= 1);
    }

    [Fact]
    public void FindMainWindow_ByTitleSubstring_Hits_IgnoringCase()
    {
        using var app = UiSampleAppProcess.Start();
        var w = CaptureTestHelpers.WaitFound(title: "uisample", TimeSpan.FromSeconds(5));   // 忽略大小写
        Assert.NotNull(w);
        Assert.Equal("UiSample", w!.Title);
    }

    [Fact]
    public void FindMainWindow_NoMatch_ReturnsNull()
    {
        // 不存在的 pid / 不存在的标题均应返回 null（双空由宿主保证不传，Engine 侧亦防御返回 null）
        Assert.Null(ScreenCapture.FindMainWindow(processId: 4185100, titleSubstring: ""));
        Assert.Null(ScreenCapture.FindMainWindow(0, "这个标题一定不存在-" + Guid.NewGuid()));
    }
}
