using FlaUI.UIA3;
using SharpSight.UiAutomation;
using System.Diagnostics;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// Task 6（spec §7.1 元素身份 / §7.4 代际一致性）：
/// <see cref="FrameRegistry"/> 是纯逻辑护栏（单调递增 + 旧帧拒绝，恒跑）；
/// <see cref="UiElementLocator.FindForCapture"/> 需要真实 UIA 窗口，环境不可用（共享桌面/无头/争用）时
/// <see cref="Assert.Skip"/> 不红（spec §6.4）。
/// </summary>
[Collection("UiTools")]
public sealed class UiFrameRegistryTests
{
    private static string UiSampleAppExe => DebugUiToolsTests.UiSampleAppExe;

    // ===== FrameRegistry：纯逻辑，恒跑 =====

    [Fact]
    public void FrameRegistry_StartsAtZero_AndNextIsMonotonic()
    {
        var reg = new FrameRegistry();

        Assert.Equal(0, reg.Current);          // 初始代际号 0（0=不校验哨兵）

        var f1 = reg.Next();
        var f2 = reg.Next();

        Assert.Equal(1, f1);                   // 从 1 起
        Assert.Equal(2, f2);
        Assert.True(f2 > f1);
        Assert.Equal(f2, reg.Current);         // Current 跟到最新
    }

    [Fact]
    public void FrameRegistry_RejectsStaleFrame()
    {
        var reg = new FrameRegistry();
        var f1 = reg.Next();
        var f2 = reg.Next();
        Assert.True(f2 > f1);

        var ex = Assert.Throws<StaleFrameException>(() => reg.Validate(f1));   // 旧帧 → 拒绝
        Assert.Contains("旧", ex.Message);                                      // 中文教学提示
        Assert.Contains("ui_find", ex.Message);                                 // 指明补救入口
    }

    [Fact]
    public void FrameRegistry_ZeroAndCurrent_DoNotThrow()
    {
        var reg = new FrameRegistry();

        reg.Validate(0);                       // 0=不校验（向后兼容，spec §4.1）
        var f = reg.Next();
        reg.Validate(f);                       // 当前帧通过
        reg.Validate(0);                       // 再校验 0 仍通过
    }

    // ===== FindForCapture：真实 UIA（环境不可用则 Skip） =====

    [Fact]
    public void FindForCapture_ReturnsStructuredRectAndTopLevelHwnd()
    {
        Assert.True(File.Exists(UiSampleAppExe), "UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1");
        using var app = LaunchUiSampleApp();
        try
        {
            using var automation = new UIA3Automation();
            var locator = new UiElementLocator();

            // 窗口/控件树就绪是异步的——轮询至超时；期间任一次 UIA 失败都按「未就绪」重试。
            IReadOnlyList<UiElementInfo>? infos = null;
            string? lastError = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    infos = locator.FindForCapture(automation, app.Id.ToString(), "", "手动", "Button", "", 50);
                    break;
                }
                catch (UiException ex)
                {
                    lastError = ex.Message;
                    Thread.Sleep(300);
                }
            }
            if (infos is null)
                Assert.Skip($"UIA 目标窗口 60s 内不可用（共享桌面/无头/争用环境）：{lastError}");

            var hit = infos!.FirstOrDefault(i => i.AutoId == "toggleState");
            Assert.NotNull(hit);
            Assert.False(hit!.RectPx.IsEmpty);                         // 结构化物理像素矩形（不再只是字符串）
            Assert.True(hit.RectPx.Width > 0 && hit.RectPx.Height > 0);
            Assert.True(hit.TopLevelHwnd != IntPtr.Zero);             // 元素 GetAncestor(GA_ROOT)

            // TopLevelHwnd 必须是可见顶层主窗（FindWindowByHwnd 内含 GA_ROOT==自身 + 可见校验）
            var root = SharpSight.Capture.ScreenCapture.FindWindowByHwnd(hit.TopLevelHwnd);
            Assert.NotNull(root);
            Assert.Equal("UiSample", root!.Title);

            // 元素几何应落在该窗口可见帧内（UIA 几何不受遮挡影响，可确定性断言）
            var bounds = SharpSight.Capture.ScreenCapture.GetWindowBounds(hit.TopLevelHwnd);
            Assert.NotNull(bounds);
            Assert.True(bounds!.ExtendedFrame.Contains(hit.RectPx),
                $"元素 {hit.RectPx} 应落在可见帧 {bounds.ExtendedFrame} 内");
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    // ===== R13：ui_find 与 screenshot element 共用同一采集路径（离屏过滤/ordinal 同源）=====

    [Fact]
    public void Find_And_FindForCapture_ShareSameIndexSequence()
    {
        Assert.True(File.Exists(UiSampleAppExe), "UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1");
        using var app = LaunchUiSampleApp();
        try
        {
            using var automation = new UIA3Automation();
            var locator = new UiElementLocator();

            IReadOnlyList<UiElementInfo>? listed = null;
            IReadOnlyList<UiElementInfo>? capture = null;
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    listed = locator.Find(automation, app.Id.ToString(), "", "", "", "", 500);
                    capture = locator.FindForCapture(automation, app.Id.ToString(), "", "", "", "", 500);
                    break;
                }
                catch (UiException) { Thread.Sleep(300); }
            }
            if (listed is null || capture is null)
                Assert.Skip("UIA 目标窗口 60s 内不可用（共享桌面/无头/争用环境）");

            // 两条路径过滤/ordinal 同源 ⇒ 元素身份序列逐项一致（离屏过滤只加在共用 FindCore 一处）。
            static string Id(UiElementInfo e) => $"{e.AutoId}\u001F{e.Name}\u001F{e.Type}";
            Assert.Equal(listed!.Select(Id), capture!.Select(Id));
        }
        finally
        {
            KillUiSampleApp(app);
        }
    }

    // ===== 起停辅助（与 DebugUiToolsTests 同口径；UiSampleApp 进程名全局唯一，串行 Collection） =====

    private static Process LaunchUiSampleApp()
    {
        foreach (var p in Process.GetProcessesByName("UiSampleApp"))
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 已退出忽略 */ }
        }
        var exe = UiSampleAppExe;
        return Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)! })!;
    }

    private static void KillUiSampleApp(Process app)
    {
        try { if (!app.HasExited) app.Kill(entireProcessTree: true); } catch { /* 已退出忽略 */ }
        foreach (var p in Process.GetProcessesByName("UiSampleApp"))
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 已退出忽略 */ }
        }
    }
}
