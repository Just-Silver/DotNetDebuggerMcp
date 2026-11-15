using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpSight.Capture;

namespace NativeInputSpike;

internal sealed class ProbeForm : Form
{
    private readonly string _root;
    private readonly int _parentSession;
    private readonly bool _child;
    private readonly InputCanvas _canvas = new() { Location = new Point(15, 15) };
    private readonly TextBox _text = new() { Location = new Point(30, 290), Width = 540 };
    private readonly CancellationTokenSource _closing = new();
    private readonly string _pipe = "native-input-spike-" + Guid.NewGuid().ToString("N");
    private int _shortcuts;
    private int _keyDowns;
    private int _functionShortcuts;
    private readonly List<string> _keys = [];

    internal ProbeForm(string root, int parentSession, bool child)
    {
        _root = root; _parentSession = parentSession; _child = child;
        Text = "Native GUI Input Spike";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(60, 60);
        ClientSize = new Size(640, 350);
        Controls.AddRange([_canvas, _text]);
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            _keyDowns++;
            if (_keys.Count < 80) _keys.Add($"{e.KeyCode};modifiers={e.Modifiers};asyncCtrl={Native.GetAsyncKeyState(0x11)}");
            if (e.KeyCode == Keys.K && e.Control && Native.GetForegroundWindow() == Handle) _shortcuts++;
            if (e.KeyCode == Keys.F6 && e.Control && Native.GetForegroundWindow() == Handle) _functionShortcuts++;
        };
        Shown += (_, _) => StartWorker();
        FormClosed += (_, _) => _closing.Cancel();
    }

    protected override bool ShowWithoutActivation => !_child;
    protected override CreateParams CreateParams
    {
        get { var value = base.CreateParams; if (!_child) value.ExStyle |= 0x08000000; return value; }
    }

    private void StartWorker()
    {
        var process = Process.GetCurrentProcess();
        Program.Save(Path.Combine(_root, "worker.json"), new { pid = process.Id,
            startedTicks = process.StartTime.ToUniversalTime().Ticks, pipe = _pipe, parentSession = _parentSession,
            sessionId = process.SessionId, child = _child, path = Environment.ProcessPath });
        _ = ServeAsync();
    }

    private async Task ServeAsync()
    {
        try
        {
            while (!_closing.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(_pipe, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_closing.Token).ConfigureAwait(false);
                using var reader = new StreamReader(pipe);
                using var writer = new StreamWriter(pipe) { AutoFlush = true };
                var action = await reader.ReadLineAsync(_closing.Token).ConfigureAwait(false);
                object result;
                try { result = await ExecuteAsync(action ?? "status").ConfigureAwait(false); }
                catch (Exception ex) { result = new { error = ex.Message, action }; }
                await writer.WriteLineAsync(JsonSerializer.Serialize(result)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception ex) { Program.Save(Path.Combine(_root, "worker-error.json"), new { error = ex.ToString() }); }
    }

    private async Task<object> ExecuteAsync(string action)
    {
        using var input = new RealInput(Invoke(() => Handle));
        if (action == "capture")
        {
            var hwnd = Invoke(() => Handle);
            return CaptureFrame(hwnd);
        }
        if (action == "real")
        {
            ValidateChild();
            Invoke(() => { Show(); WindowState = FormWindowState.Normal; Native.SetForegroundWindow(Handle); _canvas.Focus(); });
            await Task.Delay(150).ConfigureAwait(false);
            RequireForeground();
            await input.ExerciseAsync(this, _canvas, _text).ConfigureAwait(false);
            return Invoke(Snapshot);
        }
        if (action == "key-probe")
        {
            ValidateChild();
            RequireForeground();
            var trace = await input.ProbeKeyAsync().ConfigureAwait(false);
            return new { trace, snapshot = Invoke(Snapshot) };
        }
        if (action == "unicode")
        {
            ValidateChild();
            RequireForeground();
            Invoke(() => _text.Focus());
            await input.TypeUnicodeAsync("隔离Input").ConfigureAwait(false);
            return Invoke(Snapshot);
        }
        if (action == "shortcut")
        {
            ValidateChild();
            RequireForeground();
            await input.FunctionShortcutAsync().ConfigureAwait(false);
            return Invoke(Snapshot);
        }
        if (action == "scan-probe")
        {
            ValidateChild();
            RequireForeground();
            await input.ScanProbeAsync().ConfigureAwait(false);
            return Invoke(Snapshot);
        }
        if (action == "commit")
        {
            ValidateChild();
            RequireForeground();
            await input.CommitAsync().ConfigureAwait(false);
            return Invoke(Snapshot);
        }
        return Invoke(() => action switch
        {
            "status" => Snapshot(),
            "messages" => PostMessages(),
            "hide" => ChangeView("hide"),
            "minimize" => ChangeView("minimize"),
            "show" => ChangeView("show"),
            "stop" => ScheduleClose(),
            _ => throw new ArgumentException("未知 worker 操作。")
        });
    }

    private void ValidateChild()
    {
        var actual = Process.GetCurrentProcess().SessionId;
        if (!_child || actual == _parentSession || actual == 0)
            throw new InvalidOperationException("真实输入仅允许在与父会话不同的已投送 child worker 执行。");
        var holder = JsonDocument.Parse(File.ReadAllText(Path.Combine(_root, "connection.json"))).RootElement;
        if (holder.GetProperty("childId").GetInt32() != actual || holder.GetProperty("parentSession").GetInt32() != _parentSession)
            throw new InvalidOperationException("实际 Session 与本轮连接记录不符，拒绝注入。");
        var input = Native.OpenInputDesktop(0, false, 0x0001);
        if (input == 0) throw new InvalidOperationException("无法确认输入 Desktop，拒绝注入。");
        try
        {
            var inputName = Native.DesktopName(input);
            var threadName = Native.DesktopName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
            if (inputName == "unavailable" || threadName == "unavailable" || inputName != threadName)
                throw new InvalidOperationException("worker Desktop 不是 input Desktop，拒绝注入。");
        }
        finally { Native.CloseDesktop(input); }
    }

    private void RequireForeground()
    {
        if (!Invoke(() => Visible && WindowState == FormWindowState.Normal && Native.GetForegroundWindow() == Handle))
            throw new InvalidOperationException("测试窗未在 child 前台，拒绝向其他窗口发送输入。");
    }

    private object Snapshot() => new { pid = Environment.ProcessId, sessionId = Process.GetCurrentProcess().SessionId,
        foreground = Native.GetForegroundWindow() == Handle, visible = Visible, state = WindowState.ToString(),
        clicks = _canvas.Clicks, strictClicks = _canvas.StrictClicks, drags = _canvas.Drags,
        moves = _canvas.Hovers, dragEnd = _canvas.DragEnd, text = _text.Text, shortcuts = _shortcuts,
        keyDowns = _keyDowns, keys = _keys.ToArray(), functionShortcuts = _functionShortcuts,
        textFocused = _text.Focused, canvasFocused = _canvas.Focused,
        runtimePath = typeof(object).Assembly.Location, executablePath = Environment.ProcessPath };

    private object PostMessages()
    {
        Native.Post(_canvas.Handle, 0x0201, 1, 50, 60);
        Native.Post(_canvas.Handle, 0x0202, 0, 50, 60);
        Native.Post(_canvas.Handle, 0x0201, 1, 400, 60);
        Native.Post(_canvas.Handle, 0x0202, 0, 400, 60);
        Native.Post(_canvas.Handle, 0x0201, 1, 50, 190);
        for (var x = 70; x <= 230; x += 20) Native.Post(_canvas.Handle, 0x0200, 1, x, 190);
        Native.Post(_canvas.Handle, 0x0202, 0, 230, 190);
        foreach (var c in "message-input") Native.Post(_text.Handle, 0x0102, c);
        Native.Post(_text.Handle, 0x0100, 0x11);
        Native.Post(_text.Handle, 0x0100, 0x4b);
        Native.Post(_text.Handle, 0x0101, 0x4b);
        Native.Post(_text.Handle, 0x0101, 0x11);
        return new { posted = true, route = "PostMessage", realInput = false };
    }

    private object CaptureFrame(nint hwnd)
    {
        var frame = ScreenCapture.CaptureWindow(hwnd, new CaptureOptions());
        var path = Path.Combine(_root, $"capture-{DateTime.UtcNow:HHmmssfff}.png");
        File.WriteAllBytes(path, frame.Image);
        return new { path, frame.Source, frame.Width, frame.Height, frame.WasAllBlack };
    }

    private object ChangeView(string state)
    {
        if (state == "hide") Hide();
        else if (state == "minimize") WindowState = FormWindowState.Minimized;
        else { Show(); WindowState = FormWindowState.Normal; }
        return Snapshot();
    }

    private object ScheduleClose()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => { timer.Dispose(); Close(); };
        timer.Start();
        return new { stopping = true };
    }
}
