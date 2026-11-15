using System.Runtime.InteropServices;

namespace SharpSight.ChildSession.Connection;

/// <summary>
/// 连接窗（父会话、holder STA）专用最小 Win32 封装：只用不激活的 4 还原。
/// child 内业务目标窗的激活式还原（ChildInputNative.SW_RESTORE + 前台）属于另一窗口、另一目的，不在此复用。
/// </summary>
internal static class ChildConnectionWindowNative
{
    internal const int SwShowNoActivate = 4;

    internal static bool IsMinimized(nint window) => window != 0 && IsIconic(window);

    internal static void ShowWithoutActivation(nint window) => ShowWindow(window, SwShowNoActivate);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
}
