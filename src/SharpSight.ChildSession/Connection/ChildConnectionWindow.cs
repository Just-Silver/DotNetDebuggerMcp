namespace SharpSight.ChildSession.Connection;

internal sealed class ChildConnectionWindow : Form
{
    private readonly RdpActiveXHost _rdp = new() { Dock = DockStyle.Fill };
    internal TaskCompletionSource ShownAndConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool WasClosed { get; private set; }

    internal ChildConnectionWindow()
    {
        Text = "DotNetDebuggerMcp — 子会话（需要时在此认证；关闭会断开连接）";
        ClientSize = new Size(1024, 768);
        Controls.Add(_rdp);
        Shown += (_, _) =>
        {
            try { _rdp.ConnectChild(); ShownAndConnected.TrySetResult(); }
            catch (Exception ex) { ShownAndConnected.TrySetException(ex); Close(); }
        };
        FormClosed += (_, _) => WasClosed = true;
    }

    protected override bool ShowWithoutActivation => true;

    internal Task<bool> IsConnectedAsync()
    {
        if (WasClosed || IsDisposed) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() =>
            {
                try { completion.TrySetResult(_rdp.Connected); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException) { completion.TrySetResult(false); }
        return completion.Task;
    }

    internal void RequestClose()
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(Close);
    }
}
