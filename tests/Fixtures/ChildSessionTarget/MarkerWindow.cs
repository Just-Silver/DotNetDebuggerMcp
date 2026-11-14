namespace ChildSessionTarget;

internal sealed class MarkerWindow : Form
{
    private MarkerWindow(string color)
    {
        Text = "ChildSessionTarget — " + color;
        BackColor = color == "blue" ? Color.Blue : Color.Lime;
        ClientSize = new Size(400, 280);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(30, 40);
        ShowInTaskbar = false;
    }

    protected override bool ShowWithoutActivation => true;

    internal static Task RunAsync(string color)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
                using var window = new MarkerWindow(color);
                Application.Run(window);
                done.TrySetResult();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }
}
