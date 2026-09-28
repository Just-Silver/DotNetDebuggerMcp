using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

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

    [Fact]
    public void FindMainWindow_ByPid_PrefersTitledBigWindow_OverToolWindow()
    {
        // 回归（2026-09-28，P0）：资源管理器等进程的 Z 序最前是 1×1 缩略图/任务栏类助手窗（无标题、WS_EX_TOOLWINDOW）；
        // pid 检索必须择优（非工具窗 → 有标题 → 面积最大），不得再选中 1×1 或空标题窗。
        var explorer = System.Diagnostics.Process.GetProcessesByName("explorer").FirstOrDefault();
        if (explorer is null) Assert.Skip("本环境没有 explorer 进程（非交互桌面会话），spec §6.4 预案");

        var w = ScreenCapture.FindMainWindow(explorer!.Id, "");
        if (w is null) Assert.Skip("explorer 无可见根窗（锁屏/无桌面），spec §6.4 预案");

        Assert.False(string.IsNullOrEmpty(w!.Title));                      // 曾经会选中无标题的 1×1 助手窗
        // 主窗可能处于最小化（rect 160x28）——只断言「不是 1×1 助手窗」，不假设窗口已还原。
        Assert.True(w.Rect.Width > 1 && w.Rect.Height > 1, $"{w.Rect.Width}x{w.Rect.Height}");
        Assert.True(w.MatchCount >= 1);
    }
}
