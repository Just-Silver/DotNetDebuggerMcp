using System.Runtime.InteropServices;
using SharpSight.Capture;
using Xunit;

namespace SharpSight.Capture.Tests;

/// <summary>
/// 窗口标题读取回归（2026-09-28，batch3 D6）：此前固定 <c>StringBuilder(256)</c> 且**不加标记**，超长标题被
/// 静默截到 255、看似"标题就这么长"。修法=**有界截断（255）+ 末尾 `…` 明示**：既不静默误导，也不返回完整
/// 超长标题（完整标题对定位无价值、却会灌爆 agent 上下文）。用进程内自建真实顶层窗做 GetWindowText 往返。
/// </summary>
public sealed class CaptureWindowTitleTests
{
    [Fact]
    public void FindWindowByHwnd_LongTitle_TruncatedWithEllipsis_NotSilent()
    {
        var title = new string('L', 320) + "尾";
        var hwnd = TestWindow.Create(title);
        try
        {
            var info = ScreenCapture.FindWindowByHwnd(hwnd);
            Assert.NotNull(info);
            Assert.Equal(new string('L', 255) + "…", info!.Title);   // 有界(255)+明示
            Assert.NotEqual(title, info.Title);                      // 绝不返回完整超长标题
        }
        finally
        {
            TestWindow.Destroy(hwnd);
        }
    }

    [Fact]
    public void FindWindowByHwnd_ShortTitle_NoEllipsis()
    {
        var title = "短标题-abc-123";
        var hwnd = TestWindow.Create(title);
        try
        {
            var info = ScreenCapture.FindWindowByHwnd(hwnd);
            Assert.NotNull(info);
            Assert.Equal(title, info!.Title);                        // 未达上限 → 原样、无 …
        }
        finally
        {
            TestWindow.Destroy(hwnd);
        }
    }

    [Fact]
    public void EnumerateVisibleWindows_LongTitle_TruncatedWithEllipsis()
    {
        var title = new string('M', 300) + "-长标题";
        var hwnd = TestWindow.Create(title);
        try
        {
            var found = ScreenCapture.EnumerateVisibleWindows().FirstOrDefault(w => w.Hwnd == hwnd);
            Assert.NotNull(found);
            Assert.Equal(new string('M', 255) + "…", found!.Title);
        }
        finally
        {
            TestWindow.Destroy(hwnd);
        }
    }
}

/// <summary>最小真实顶层 Win32 窗（无边框/可见/带标题），用于标题读取往返；测试结束显式 DestroyWindow。</summary>
internal static class TestWindow
{
    private const uint WsPopup = 0x80000000;
    private const uint WsVisible = 0x10000000;

    private static readonly string ClassName = "SharpSightTitleTestWnd_" + Guid.NewGuid().ToString("N");
    private static readonly ushort ClassAtom;

    // 委托与函数指针须保活（静态字段），否则 USER32 回调到已回收的存根会崩。
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProc Proc = (h, m, w, l) => DefWindowProcW(h, m, w, l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WndClass lpWndClass);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? name);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClass
    {
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        public string? MenuName;
        public string ClassName;
    }

    static TestWindow()
    {
        var wc = new WndClass
        {
            WndProc = Marshal.GetFunctionPointerForDelegate(Proc),
            Instance = GetModuleHandleW(null),
            ClassName = ClassName,
        };
        ClassAtom = RegisterClassW(ref wc);
    }

    public static IntPtr Create(string title)
    {
        if (ClassAtom == 0) Assert.Skip("RegisterClass 失败（无交互桌面会话），spec §6.4 预案");
        var hwnd = CreateWindowExW(0, ClassName, title, WsPopup | WsVisible,
            0, 0, 200, 80, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) Assert.Skip("CreateWindowEx 失败（无交互桌面会话），spec §6.4 预案");
        return hwnd;
    }

    public static void Destroy(IntPtr hwnd) => DestroyWindow(hwnd);
}
