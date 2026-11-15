using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotNetDebuggerMcp.Client;

/// <summary>
/// 只读采样父会话光标、前台与持有器窗口状态，用于复核真实 child 输入期间「父侧是否被扰动」。
/// 只调用 GetCursorPos / GetForegroundWindow / GetWindowThreadProcessId / IsIconic，不采集键盘文本、
/// 不调用 SetForegroundWindow/SendInput、不给父会话发任何测试输入；是否确认隔离只在采样停止后判定。
/// 证据只代表采样点未见变化，不能排除采样间隔内的瞬态。
/// </summary>
internal sealed class ParentSessionObserver
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(50);
    private const int MaxChangeDetails = 50;

    private readonly int _holderPid;
    private readonly nint _holderWindow;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<CursorChange> _cursorChanges = [];
    private readonly List<ForegroundChange> _foregroundChanges = [];
    private readonly (int X, int Y)? _startCursor;
    private readonly nint _startForeground;
    private readonly Task _sampling;
    private int _samples;
    private int _readFailures;
    private int _cursorChangeCount;
    private int _foregroundChangeCount;
    private bool _holderForeground;
    private bool _holderIconized;
    private (int X, int Y)? _lastCursor;
    private nint _lastForeground;

    private ParentSessionObserver(int holderPid, nint holderWindow)
    {
        _holderPid = holderPid;
        _holderWindow = holderWindow;
        _startCursor = ReadCursor();
        _startForeground = GetForegroundWindow();
        if (_startCursor is null || _startForeground == 0) _readFailures++;
        else if (HolderOwns(_startForeground)) _holderForeground = true;
        _holderIconized = _holderWindow != 0 && IsIconic(_holderWindow);
        _lastCursor = _startCursor;
        _lastForeground = _startForeground;
        _sampling = Task.Run(SampleUntilStoppedAsync);
    }

    internal static ParentSessionObserver Start(int holderPid, nint holderWindow) => new(holderPid, holderWindow);

    /// <summary>只读快照：光标、前台窗口与其进程归属，供单列前后对照复用同一组 Win32 读取。</summary>
    internal readonly record struct SessionSnapshot((int X, int Y)? Cursor, nint Foreground, int ForegroundPid)
    {
        internal bool ForegroundIsHolder(int holderPid) => ForegroundPid == holderPid;
    }

    internal static SessionSnapshot CaptureSnapshot()
    {
        var cursor = ReadCursor();
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var pid);
        return new(cursor, foreground, pid);
    }

    /// <summary>
    /// 观察前置：核对持有器身份（PID/创建时间/父会话），有界等待连接窗口可见且非最小化、父侧前台不属于持有器。
    /// 只提示用户自行切回前台，绝不代替用户抢焦点；超时即明确未验收。
    /// </summary>
    internal static async Task<nint> AwaitReviewableAsync(int holderPid, long holderStartedTicks, int parentSessionId)
    {
        using var holder = System.Diagnostics.Process.GetProcessById(holderPid);
        Require(holder.StartTime.ToUniversalTime().Ticks == holderStartedTicks,
            "持有器创建时间与状态不符，拒绝在未核实身份时观察父会话。");
        Require(holder.SessionId == parentSessionId, "持有器不在父会话，拒绝观察父会话。");
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        Require(self.SessionId == parentSessionId, "当前验收进程不在父会话，拒绝观察父会话。");

        var until = DateTimeOffset.UtcNow.AddSeconds(120);
        var promptedAt = DateTimeOffset.MinValue;
        while (DateTimeOffset.UtcNow < until)
        {
            holder.Refresh();
            var window = holder.MainWindowHandle;
            var foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out var foregroundPid);
            if (window != 0 && !IsIconic(window) && foreground != 0 && foregroundPid != holderPid) return window;
            if (DateTimeOffset.UtcNow - promptedAt > TimeSpan.FromSeconds(5))
            {
                Console.WriteLine("请保持子会话连接窗口未最小化，并把焦点切回当前窗口/聊天（不会替您抢前台）；认证完成后切回即可。");
                promptedAt = DateTimeOffset.UtcNow;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("未确认可复核的父会话前置：连接窗口须可见且非最小化，父侧前台不得属于持有器。");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>取消采样并等待采样任务真正退出；返回的 evidence 只包含已结束区间的结果。</summary>
    internal async Task<JsonElement> StopAsync()
    {
        await _stop.CancelAsync();
        await _sampling;
        _stop.Dispose();
        var confirmed = _readFailures == 0 && _samples > 0 && !_holderForeground && !_holderIconized &&
            _cursorChangeCount == 0 && _foregroundChangeCount == 0;
        return JsonSerializer.SerializeToElement(new
        {
            startedAt = _startedAt,
            endedAt = DateTimeOffset.UtcNow,
            samples = _samples,
            sampleIntervalMs = (int)Interval.TotalMilliseconds,
            readFailures = _readFailures,
            startCursor = _startCursor is { } c ? new { x = c.X, y = c.Y } : (object?)null,
            startForegroundHwnd = _startForeground.ToInt64(),
            holderPid = _holderPid,
            holderWindow = _holderWindow.ToInt64(),
            cursorChanges = _cursorChangeCount,
            foregroundChanges = _foregroundChangeCount,
            cursorChangeDetails = _cursorChanges,
            foregroundChangeDetails = _foregroundChanges,
            holderForeground = _holderForeground,
            holderIconized = _holderIconized,
            confirmed,
            isolation = Isolation(confirmed)
        });
    }

    private string Isolation(bool confirmed)
    {
        var granularity = $"{Interval.TotalMilliseconds:0}ms 采样点";
        if (confirmed)
            return $"未观察到父会话光标/前台变化（{granularity}，共 {_samples} 个样本）；未观察到不等于未发生，采样间隔内的瞬态变化无法排除。";
        var reasons = new List<string>(5);
        if (_samples == 0) reasons.Add("采样期内没有取得样本");
        if (_readFailures > 0) reasons.Add($"采样读取失败或无前台 {_readFailures} 次");
        if (_holderForeground) reasons.Add("父侧前台为/曾出现持有器连接窗口");
        if (_holderIconized) reasons.Add("持有器连接窗口被最小化");
        if (_cursorChangeCount > 0) reasons.Add($"父会话光标变化 {_cursorChangeCount} 次");
        if (_foregroundChangeCount > 0) reasons.Add($"父会话前台变化 {_foregroundChangeCount} 次");
        return $"隔离未确认（{granularity}）：{string.Join("、", reasons)}（不归因为用户，也不视为通过）。";
    }

    private async Task SampleUntilStoppedAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Sample();
            try { await Task.Delay(Interval, _stop.Token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Sample()
    {
        _samples++;
        if (ReadCursor() is { } cursor) ReportCursor(cursor); else _readFailures++;
        var foreground = GetForegroundWindow();
        if (foreground == 0) _readFailures++; else ReportForeground(foreground);
        if (_holderWindow != 0 && IsIconic(_holderWindow)) _holderIconized = true;
    }

    private bool HolderOwns(nint window)
    {
        GetWindowThreadProcessId(window, out var pid);
        return pid == _holderPid;
    }

    private static (int X, int Y)? ReadCursor() => GetCursorPos(out var point) ? (point.X, point.Y) : null;

    private void ReportCursor((int X, int Y) cursor)
    {
        if (_lastCursor is { } last)
        {
            if (last.X == cursor.X && last.Y == cursor.Y) return;
            _cursorChangeCount++;
            if (_cursorChanges.Count < MaxChangeDetails)
                _cursorChanges.Add(new(DateTimeOffset.UtcNow, last.X, last.Y, cursor.X, cursor.Y));
        }
        _lastCursor = cursor;
    }

    private void ReportForeground(nint foreground)
    {
        if (foreground == _lastForeground) return;
        GetWindowThreadProcessId(foreground, out var pid);
        _foregroundChangeCount++;
        if (_foregroundChanges.Count < MaxChangeDetails)
            _foregroundChanges.Add(new(DateTimeOffset.UtcNow, _lastForeground.ToInt64(),
                foreground.ToInt64(), pid, pid == _holderPid));
        if (pid == _holderPid) _holderForeground = true;
        _lastForeground = foreground;
    }

    private sealed record CursorChange(DateTimeOffset At, int FromX, int FromY, int ToX, int ToY);

    private sealed record ForegroundChange(DateTimeOffset At, long FromHwnd, long ToHwnd, int ToPid, bool HolderWindow);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        internal int X;
        internal int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out int processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint hwnd);
}
