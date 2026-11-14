using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ChildSessionTarget;

internal sealed class ProbeForm : Form
{
    private readonly FixtureEvents _events;
    private readonly TextBox _input = new();
    private readonly Button _commit = new();
    private readonly Label _status = new();
    private readonly InputCanvas _canvas;

    private ProbeForm(string outputDirectory)
    {
        _events = new FixtureEvents(outputDirectory);
        _canvas = new InputCanvas(_events);

        Text = "ChildSessionTarget — 输入验证";
        ClientSize = new Size(700, 500);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(30, 40);
        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        KeyPreview = true;

        _input.Name = "InputText";
        _input.AccessibleName = "输入文本";
        _input.SetBounds(30, 30, 400, 25);

        _commit.Name = "CommitButton";
        _commit.Text = "提交";
        _commit.AccessibleName = "提交";
        _commit.SetBounds(450, 28, 100, 30);

        _status.Name = "StatusText";
        _status.AccessibleName = "状态";
        _status.Text = "就绪";
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.BorderStyle = BorderStyle.Fixed3D;
        _status.SetBounds(30, 75, 620, 25);

        _canvas.Name = "InputCanvas";
        _canvas.Text = "拖拽画布";
        _canvas.SetBounds(30, 120, 640, 350);

        Controls.AddRange([_input, _commit, _status, _canvas]);

        _input.TextChanged += OnInputChanged;
        _commit.Click += OnCommit;
        KeyDown += OnKeyDown;
        Shown += OnShown;
    }

    protected override bool ShowWithoutActivation => true;

    internal static Task RunAsync(string outputDirectory)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                using var window = new ProbeForm(outputDirectory);
                Application.Run(window);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _events.Dispose();
        base.Dispose(disposing);
    }

    private void OnShown(object? sender, EventArgs e) => _events.Record(new
    {
        kind = "ready",
        pid = Environment.ProcessId,
        sessionId = Process.GetCurrentProcess().SessionId,
        hwnd = Handle.ToInt64(),
        controls = new
        {
            inputText = BoundsOf(_input),
            commitButton = BoundsOf(_commit),
            statusText = BoundsOf(_status),
            inputCanvas = BoundsOf(_canvas)
        }
    });

    private void OnInputChanged(object? sender, EventArgs e) =>
        _events.Record(new { kind = "text", text = _input.Text });

    private void OnCommit(object? sender, EventArgs e)
    {
        _status.Text = "已提交：" + _input.Text;
        _events.Record(new { kind = "commit", text = _input.Text });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Control || e.KeyCode != Keys.K) return;
        e.Handled = true;
        e.SuppressKeyPress = true;
        _status.Text = "快捷键 Ctrl+K";
        _events.Record(new { kind = "chord", text = "Ctrl+K" });
    }

    private static object BoundsOf(Control control) => new
    {
        x = control.Left, y = control.Top, width = control.Width, height = control.Height
    };
}

internal sealed class FixtureEvents : IDisposable
{
    private readonly StreamWriter _file;

    internal FixtureEvents(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        _file = new StreamWriter(Path.Combine(outputDirectory, "events.jsonl"), append: false,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    internal void Record(object payload)
    {
        var json = JsonSerializer.Serialize(payload, payload.GetType());
        _file.WriteLine(json);
        Console.Out.WriteLine(json);
        Console.Out.Flush();
    }

    public void Dispose() => _file.Dispose();
}
