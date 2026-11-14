namespace ChildSessionTarget;

internal sealed class InputCanvas : Control
{
    private const int ClickSlop = 3;
    private readonly FixtureEvents _events;
    private bool _pressed;
    private bool _doubleClicked;
    private Point _start;
    private Point _end;

    internal InputCanvas(FixtureEvents events)
    {
        _events = events;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.StandardClick |
                 ControlStyles.StandardDoubleClick, true);
        AccessibleName = "拖拽画布";
        TabStop = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _start = e.Location;
        _end = e.Location;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;
        _end = e.Location;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressed || e.Button != MouseButtons.Left) return;
        _pressed = false;
        _end = e.Location;
        Invalidate();
        var doubleClicked = _doubleClicked;
        _doubleClicked = false;
        RecordRelease(doubleClicked);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        base.OnDoubleClick(e);
        _doubleClicked = true;
        _events.Record(new { kind = "doubleclick", x = _end.X, y = _end.Y });
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _events.Record(new { kind = "wheel", x = e.X, y = e.Y, delta = e.Delta });
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.White);
        e.Graphics.DrawString(Text, Font, Brushes.Gray, 10, 10);
        if (!_pressed && _start == _end) return;
        e.Graphics.DrawLine(Pens.DarkGreen, _start, _end);
        e.Graphics.FillEllipse(Brushes.Green, _end.X - 4, _end.Y - 4, 8, 8);
    }

    private void RecordRelease(bool doubleClicked)
    {
        if (doubleClicked) return;
        var dragged = Math.Abs(_end.X - _start.X) > ClickSlop || Math.Abs(_end.Y - _start.Y) > ClickSlop;
        _events.Record(dragged
            ? new { kind = "drag", x = _start.X, y = _start.Y, endX = _end.X, endY = _end.Y }
            : new { kind = "click", x = _start.X, y = _start.Y });
    }
}
