using System.Runtime.InteropServices;

namespace NativeInputSpike;

internal sealed class RealInput(nint targetWindow) : IDisposable
{
    private const nuint Marker = 0x4e495350;
    private readonly Dictionary<(ushort Key, ushort Scan, uint Flags), Native.Input> _heldKeys = [];
    private bool _leftDown;

    internal async Task CommitAsync()
    {
        Key(0x0d, false); Key(0x0d, true);
        await Task.Delay(150).ConfigureAwait(false);
        Key(0x11, false); await Task.Delay(30).ConfigureAwait(false);
        Key(0x4b, false); await Task.Delay(60).ConfigureAwait(false);
        Key(0x4b, true); Key(0x11, true);
        await Task.Delay(150).ConfigureAwait(false);
    }

    internal async Task ScanProbeAsync()
    {
        foreach (var key in new ushort[] { 0x52, 0x45, 0x41, 0x4c })
        {
            ScanKey(key, false); await Task.Delay(30).ConfigureAwait(false); ScanKey(key, true);
        }
        ScanKey(0x11, false); await Task.Delay(30).ConfigureAwait(false);
        ScanKey(0x4b, false); await Task.Delay(60).ConfigureAwait(false);
        ScanKey(0x4b, true); ScanKey(0x11, true);
        await Task.Delay(150).ConfigureAwait(false);
    }

    private void ScanKey(ushort key, bool up) => Submit(new Native.Input { Type = 1,
        Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput {
            Scan = checked((ushort)Native.MapVirtualKey(key, 0)), Flags = up ? 10u : 8u, ExtraInfo = Marker } } });

    internal async Task TypeUnicodeAsync(string text)
    {
        foreach (var c in text)
        {
            foreach (var flags in new uint[] { 4, 6 })
                Submit(new Native.Input { Type = 1, Data = new Native.InputUnion {
                    Keyboard = new Native.KeyboardInput { Scan = c, Flags = flags, ExtraInfo = Marker } } });
        }
        await Task.Delay(150).ConfigureAwait(false);
    }

    internal async Task FunctionShortcutAsync()
    {
        Key(0x11, false);
        await Task.Delay(30).ConfigureAwait(false);
        Key(0x75, false);
        await Task.Delay(60).ConfigureAwait(false);
        Key(0x75, true);
        Key(0x11, true);
        await Task.Delay(150).ConfigureAwait(false);
    }

    internal async Task<object> ProbeKeyAsync()
    {
        var before = Native.GetAsyncKeyState(0x41);
        Key(0x41, false);
        await Task.Delay(150).ConfigureAwait(false);
        var pressed = Native.GetAsyncKeyState(0x41);
        Key(0x41, true);
        await Task.Delay(150).ConfigureAwait(false);
        return new { before, pressed, after = Native.GetAsyncKeyState(0x41),
            inputSize = Marshal.SizeOf<Native.Input>(), dataOffset = Marshal.OffsetOf<Native.Input>(nameof(Native.Input.Data)).ToInt32() };
    }

    internal async Task ExerciseAsync(Form form, Control canvas, Control text)
    {
        var click = (Point)form.Invoke(() => canvas.PointToScreen(new Point(400, 60)));
        Move(click); SendMouse(2); await Task.Delay(100); SendMouse(4);
        var start = (Point)form.Invoke(() => canvas.PointToScreen(new Point(50, 190)));
        Move(start); SendMouse(2);
        for (var x = 70; x <= 230; x += 20)
        {
            Move((Point)form.Invoke(() => canvas.PointToScreen(new Point(x, 190))));
            await Task.Delay(30);
        }
        SendMouse(4);
        Move((Point)form.Invoke(() => text.PointToScreen(new Point(100, 10))));
        SendMouse(2); await Task.Delay(60); SendMouse(4);
        Key(0x11, false); Key(0x41, false); Key(0x41, true); Key(0x11, true);
        foreach (var c in "REALINPUT") { Key((ushort)c, false); Key((ushort)c, true); }
        Key(0x11, false); Key(0x4b, false); Key(0x4b, true); Key(0x11, true);
        await Task.Delay(200);
    }

    private void Move(Point point)
    {
        var left = Native.GetSystemMetrics(76); var top = Native.GetSystemMetrics(77);
        var width = Native.GetSystemMetrics(78); var height = Native.GetSystemMetrics(79);
        Submit(new Native.Input { Type = 0, Data = new Native.InputUnion { Mouse = new Native.MouseInput {
            X = (point.X - left) * 65535 / Math.Max(1, width - 1),
            Y = (point.Y - top) * 65535 / Math.Max(1, height - 1), Flags = 0xc001, ExtraInfo = Marker } } });
    }

    private void SendMouse(uint flags) => Submit(new Native.Input { Type = 0,
        Data = new Native.InputUnion { Mouse = new Native.MouseInput { Flags = flags, ExtraInfo = Marker } } });

    private void Key(ushort key, bool up) => Submit(new Native.Input { Type = 1,
        Data = new Native.InputUnion { Keyboard = new Native.KeyboardInput { Key = key, Flags = up ? 2u : 0u, ExtraInfo = Marker } } });

    private void Submit(Native.Input input)
    {
        if (Native.GetForegroundWindow() != targetWindow)
            throw new InvalidOperationException("child 前台已变化，已停止后续注入。");
        if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "SendInput 未成功提交。");
        TrackPressed(input);
    }

    private void TrackPressed(Native.Input input)
    {
        if (input.Type == 0)
        {
            if (input.Data.Mouse.Flags == 2) _leftDown = true;
            if (input.Data.Mouse.Flags == 4) _leftDown = false;
            return;
        }
        var key = input.Data.Keyboard;
        var identity = (key.Key, key.Scan, key.Flags & ~2u);
        if ((key.Flags & 2) != 0) _heldKeys.Remove(identity);
        else
        {
            input.Data.Keyboard.Flags |= 2;
            _heldKeys[identity] = input;
        }
    }

    public void Dispose()
    {
        // 焦点变化后只释放本轮已提交的按键，避免将 child 的系统键态留在按下状态。
        var releases = _heldKeys.Values.ToList();
        if (_leftDown) releases.Add(new Native.Input { Type = 0, Data = new Native.InputUnion {
            Mouse = new Native.MouseInput { Flags = 4, ExtraInfo = Marker } } });
        foreach (var input in releases)
            if (Native.SendInput(1, [input], Marshal.SizeOf<Native.Input>()) != 1)
                Console.Error.WriteLine("清理时未能释放本轮输入，child 键态需重新核对。");
    }
}
