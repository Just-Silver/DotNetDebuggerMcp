using System.Diagnostics;
using System.Globalization;
using SharpSight.ChildSession.Lifecycle;

namespace SharpSight.ChildSession.Input;

public sealed class ChildWindowsInputPlatform : IChildInputPlatform
{
    private const int ActivateTimeoutMs = 1500;
    private const int ForegroundPollMs = 25;

    private readonly int _expectedSession;
    private readonly Func<bool> _isReady;
    private readonly ChildTargetResolver _resolver;

    public ChildWindowsInputPlatform(int sessionId, Func<bool> isReady)
    {
        // 进程的 Session 运行期不会改变，缓存实际值用于持续与期望值对照。
        using var current = Process.GetCurrentProcess();
        SessionId = current.SessionId;
        _expectedSession = sessionId;
        _isReady = isReady ?? throw new ArgumentNullException(nameof(isReady));
        _resolver = new ChildTargetResolver(sessionId);
    }

    public int SessionId { get; }

    public bool IsInteractive
    {
        get
        {
            if (_expectedSession == 0 || SessionId != _expectedSession) return false;
            try
            {
                return _isReady() && WtsNative.Active(_expectedSession) &&
                    ChildInputNative.InputDesktopMatchesThread() && ChildInputNative.InWinSta0();
            }
            catch (Exception)
            {
                // 任一身分查询失败都意味着无法证明处于合法交互环境，一律按不可交互处理。
                return false;
            }
        }
    }

    public ResolvedChildTarget Resolve(int processId, string hwnd) => _resolver.Resolve(processId, hwnd);

    public async Task<ResolvedChildTarget> ActivateAsync(ResolvedChildTarget target, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var hwnd = ParseHwnd(target.Hwnd);
        if (hwnd == 0) throw new InvalidOperationException("目标窗口句柄无效，拒绝激活。");
        EnsureInteractive();
        RecheckTarget(target, hwnd);
        if (ChildInputNative.IsMinimized(hwnd) && !ChildInputNative.ShowRestore(hwnd))
            throw new InvalidOperationException("无法请求还原最小化的目标窗口，未提交输入。");
        ChildInputNative.BringForeground(hwnd);
        var deadline = Environment.TickCount64 + ActivateTimeoutMs;
        while (!ForegroundMatches(hwnd))
        {
            if (Environment.TickCount64 >= deadline)
                throw new InvalidOperationException("目标窗口未在期限内成为前台，未提交输入。");
            await Task.Delay(ForegroundPollMs, ct).ConfigureAwait(false);
            EnsureInteractive();
            RecheckTarget(target, hwnd);
        }
        // 还原/置前会改变窗口矩形，几何必须以激活后的实测值为准。
        return _resolver.Resolve(target.ProcessId, target.Hwnd);
    }

    public bool IsForeground(ResolvedChildTarget target)
    {
        var hwnd = ParseHwnd(target.Hwnd);
        return hwnd != 0 && ForegroundMatches(hwnd);
    }

    public bool PointBelongsTo(ResolvedChildTarget target, Point point)
    {
        var hwnd = ParseHwnd(target.Hwnd);
        return hwnd != 0 && target.Bounds.Contains(point) &&
            ChildInputNative.RootOfPoint(point.X, point.Y) == hwnd;
    }

    public bool IsKeyDown(ushort key) => ChildInputNative.KeyDown(key);

    public bool Submit(ChildInputEvent input, ulong marker) => ChildInputNative.Submit(Build(input, marker));

    private void EnsureInteractive()
    {
        if (!IsInteractive)
            throw new InvalidOperationException("当前child会话、input desktop或window station不是预期的交互环境，拒绝输入。");
    }

    private void RecheckTarget(ResolvedChildTarget target, nint hwnd)
    {
        if (!ChildInputNative.IsWindowAlive(hwnd) || ChildInputNative.WindowOwner(hwnd) != target.ProcessId)
            throw new InvalidOperationException("目标窗口已失效或不属于目标进程，未提交输入。");
        long started;
        try
        {
            started = _resolver.ReadProcess(target.ProcessId).StartedTicks;
        }
        catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("目标进程已退出或无法核对创建时间，未提交输入。", ex);
        }
        if (started != target.StartedTicks)
            throw new InvalidOperationException("目标进程创建时间已变化，未提交输入。");
    }

    private static bool ForegroundMatches(nint hwnd) => ChildInputNative.ForegroundWindow() == hwnd;

    private static nint ParseHwnd(string hwnd)
        => long.TryParse(hwnd, NumberStyles.None, CultureInfo.InvariantCulture, out var raw) && raw > 0
            ? new nint(raw) : 0;

    private static ChildInputNative.Input Build(ChildInputEvent input, ulong marker) => input.Kind switch
    {
        "move" => Move(input, marker),
        "button" => Button(input, marker),
        "wheel" => Wheel(input, marker),
        "key" => Key(input, marker),
        "unicode" => Unicode(input, marker),
        _ => throw new InvalidOperationException($"不支持的输入类型：{input.Kind}。")
    };

    private static ChildInputNative.Input Move(ChildInputEvent input, ulong marker)
    {
        var (left, top, width, height) = ChildInputNative.VirtualScreenMetrics();
        return MouseEvent(
            ChildInputNative.MouseMove | ChildInputNative.MouseAbsolute | ChildInputNative.MouseVirtualDesk, 0, marker,
            VirtualCoordinate(input.X - left, width), VirtualCoordinate(input.Y - top, height));
    }

    private static int VirtualCoordinate(int offset, int extent)
        => (int)Math.Clamp((long)offset * 65535 / Math.Max(1, extent - 1), 0, 65535);

    private static ChildInputNative.Input Button(ChildInputEvent input, ulong marker)
    {
        var flag = (input.Button, input.Up) switch
        {
            ("left", false) => ChildInputNative.MouseLeftDown,
            ("left", true) => ChildInputNative.MouseLeftUp,
            ("right", false) => ChildInputNative.MouseRightDown,
            ("right", true) => ChildInputNative.MouseRightUp,
            ("middle", false) => ChildInputNative.MouseMiddleDown,
            ("middle", true) => ChildInputNative.MouseMiddleUp,
            _ => throw new InvalidOperationException($"不支持的鼠标按钮：{input.Button}。")
        };
        return MouseEvent(flag, 0, marker);
    }

    private static ChildInputNative.Input Wheel(ChildInputEvent input, ulong marker)
        => MouseEvent(ChildInputNative.MouseWheel, unchecked((uint)input.Delta), marker);

    private static ChildInputNative.Input MouseEvent(uint flags, uint data, ulong marker, int x = 0, int y = 0) => new()
    {
        Type = ChildInputNative.InputMouse,
        Data = new()
        {
            Mouse = new() { Dx = x, Dy = y, MouseData = data, Flags = flags, ExtraInfo = (nuint)marker }
        }
    };

    private static ChildInputNative.Input Key(ChildInputEvent input, ulong marker)
    {
        var flags = (input.Up ? ChildInputNative.KeyUp : 0u) |
            (ChildInputNative.IsExtendedKey(input.Key) ? ChildInputNative.KeyExtended : 0u);
        return KeyboardEvent(input.Key, 0, flags, marker);
    }

    private static ChildInputNative.Input Unicode(ChildInputEvent input, ulong marker)
        => KeyboardEvent(0, input.Key, ChildInputNative.KeyUnicode | (input.Up ? ChildInputNative.KeyUp : 0u), marker);

    private static ChildInputNative.Input KeyboardEvent(ushort vk, ushort scan, uint flags, ulong marker) => new()
    {
        Type = ChildInputNative.InputKeyboard,
        Data = new() { Keyboard = new() { Vk = vk, Scan = scan, Flags = flags, ExtraInfo = (nuint)marker } }
    };
}
