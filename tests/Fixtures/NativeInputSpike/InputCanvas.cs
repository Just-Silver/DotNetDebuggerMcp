namespace NativeInputSpike;

internal sealed class InputCanvas : Control
{
    private bool _pressed;
    private Point _start;
    internal int Clicks, StrictClicks, Drags, Hovers;
    internal Point End;
    internal object DragEnd => new { End.X, End.Y };

    internal InputCanvas()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(600, 260);
        AccessibleName = "Input-only canvas";
        TabStop = true;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        _start = e.Location;
        if (e.X < 280) Clicks++;
        else if (Native.GetForegroundWindow() == FindForm()!.Handle && Native.GetAsyncKeyState(1) < 0) StrictClicks++;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Hovers++;
        if (_pressed) { End = e.Location; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed && Math.Abs(e.X - _start.X) > 50) Drags++;
        _pressed = false;
        End = e.Location;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(Color.White);
        e.Graphics.FillRectangle(Brushes.LightBlue, new Rectangle(15, 20, 240, 80));
        e.Graphics.FillRectangle(Brushes.LightSalmon, new Rectangle(310, 20, 260, 80));
        e.Graphics.DrawString($"Message-driven clicks: {Clicks}", Font, Brushes.Black, 25, 40);
        e.Graphics.DrawString($"Foreground + key-state clicks: {StrictClicks}", Font, Brushes.Black, 320, 40);
        e.Graphics.DrawString($"Drag: {Drags} end=({End.X},{End.Y}); moves={Hovers}", Font, Brushes.Black, 25, 140);
        e.Graphics.DrawLine(Pens.DarkGreen, 50, 190, 230, 190);
        e.Graphics.FillEllipse(Brushes.Green, End.X - 5, End.Y - 5, 10, 10);
    }
}
