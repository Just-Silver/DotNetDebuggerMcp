using System.Diagnostics;

namespace SandboxProbe;

internal sealed class ProbeForm : Form
{
    private readonly TextBox _input = new() { Name = "Input", AccessibleName = "Input", Width = 460, ImeMode = ImeMode.Disable };
    private readonly Button _count = new() { Name = "CountButton", AccessibleName = "CountButton", Text = "Count", Width = 180 };
    private readonly Panel _drag = new() { Name = "DragArea", AccessibleName = "DragArea", BackColor = Color.LightSkyBlue, Size = new(460, 140) };
    private readonly Label _status = new() { AutoSize = false, Size = new(480, 100) };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 200 };
    private readonly long _startedTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private int _clicks, _mouseDowns, _keyDowns, _drags;
    private Point _dragStart;
    private bool _dragging;
    private long _frame;
    private readonly Queue<string> _recentKeys = new();
    private int _panelDowns, _panelUps, _lastDragDeltaX;

    internal ProbeForm()
    {
        Text = "Sandbox Input Isolation Probe";
        Name = "SandboxProbe";
        StartPosition = FormStartPosition.Manual;
        Location = new Point(60, 60);
        ClientSize = new Size(520, 430);
        KeyPreview = true;
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new(20), WrapContents = false };
        layout.Controls.AddRange([_input, _count, _drag, _status]);
        Controls.Add(layout);
        _count.Click += (_, _) => _clicks++;
        _count.MouseDown += (_, _) => _mouseDowns++;
        KeyDown += (_, e) =>
        {
            _keyDowns++;
            _recentKeys.Enqueue(e.KeyData.ToString());
            while (_recentKeys.Count > 32) _recentKeys.Dequeue();
        };
        _drag.MouseDown += (_, e) => { _panelDowns++; _dragStart = e.Location; _dragging = true; _drag.Capture = true; };
        _drag.MouseUp += (_, e) =>
        {
            _panelUps++;
            _lastDragDeltaX = e.X - _dragStart.X;
            if (_dragging && Math.Abs(e.X - _dragStart.X) >= 50) _drags++;
            _dragging = false;
            _drag.Capture = false;
        };
        _timer.Tick += (_, _) => PublishSnapshot();
        Shown += (_, _) => { PublishSnapshot(); _timer.Start(); };
    }

    private void PublishSnapshot()
    {
        var focus = ActiveControl?.Name ?? "";
        var snapshot = new ProbeSnapshot(Environment.ProcessId, _startedTicks, _clicks, _mouseDowns,
            _keyDowns, _drags, _input.Text, focus, DeviceDpi, ++_frame, Cursor.Position,
            _drag.RectangleToScreen(_drag.ClientRectangle), DateTimeOffset.UtcNow)
        {
            RecentKeys = _recentKeys.ToArray(), PanelDowns = _panelDowns,
            PanelUps = _panelUps, LastDragDeltaX = _lastDragDeltaX
        };
        _status.Text = $"Clicks: {_clicks}; physical downs: {_mouseDowns}; keys: {_keyDowns}\n"
            + $"Drags: {_drags}; focus: {focus}; DPI: {DeviceDpi}\n"
            + $"Pointer: {Cursor.Position}; frame: {_frame}";
        Program.WriteSnapshot(snapshot);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
