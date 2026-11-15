using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotNetDebuggerMcp.Client;

/// <summary>
/// 显式验收专用：把已核实身份的本实例持有器连接窗最小化，再发一次 GUI 请求，单列记录「不激活还原」前后证据。
/// 只对照最小化与还原前后的父侧光标/前台快照与窗口状态，不做持续采样、不冒称无瞬态；
/// 严格隔离采样从本检查确认还原之后才开始。除最小化该连接窗外不给父会话发任何键鼠输入。
/// </summary>
internal static class ConnectionWindowRestore
{
    private const int SwMinimize = 6;

    internal static async Task<JsonElement> RunAsync(int holderPid, nint holderWindow, Func<Task<string>> business)
    {
        Require(holderWindow != 0 && !IsIconic(holderWindow), "还原检查前连接窗口不是已核实的非最小化状态。");
        ShowWindow(holderWindow, SwMinimize);
        Require(await WaitMinimizedAsync(holderWindow, true), "无法最小化本实例连接窗口，未进行还原检查。");
        var before = ParentSessionObserver.CaptureSnapshot();
        Require(!before.ForegroundIsHolder(holderPid), "最小化后父侧前台属于持有器，无法判定还原是否抢前台。");
        var businessResult = await business();
        var after = ParentSessionObserver.CaptureSnapshot();
        var restored = await WaitMinimizedAsync(holderWindow, false);
        var cursorUnchanged = before.Cursor is { } cursor && after.Cursor == cursor;
        var foregroundUnchanged = before.Foreground != 0 && after.Foreground == before.Foreground;
        var evidence = JsonSerializer.SerializeToElement(new
        {
            kind = "connection-restore",
            holderPid,
            holderWindow = holderWindow.ToInt64(),
            minimizedBeforeRequest = true,
            restoredWithoutActivation = restored,
            beforeCursor = Cursor(before),
            afterCursor = Cursor(after),
            beforeForegroundHwnd = before.Foreground.ToInt64(),
            afterForegroundHwnd = after.Foreground.ToInt64(),
            foregroundUnchanged,
            cursorUnchanged,
            holderForegroundAfter = after.ForegroundIsHolder(holderPid),
            business = businessResult,
            scope = "只对照最小化与还原前后；不采样间隔内瞬态，严格隔离采样从还原确认后开始。"
        });
        Require(restored, $"连接窗口最小化后未被 GUI 请求还原：{evidence}");
        Require(!after.ForegroundIsHolder(holderPid), $"还原把父侧前台交给了持有器连接窗口：{evidence}");
        Require(foregroundUnchanged, $"还原前后观察到父侧前台变化，原因未确认：{evidence}");
        Require(cursorUnchanged, $"还原前后观察到父侧光标变化，原因未确认：{evidence}");
        return evidence;
    }

    private static object? Cursor(ParentSessionObserver.SessionSnapshot snapshot)
        => snapshot.Cursor is { } value ? new { x = value.X, y = value.Y } : null;

    private static async Task<bool> WaitMinimizedAsync(nint window, bool minimized)
    {
        for (var i = 0; i < 20; i++)
        {
            if (IsIconic(window) == minimized) return true;
            await Task.Delay(50);
        }
        return IsIconic(window) == minimized;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);
}
