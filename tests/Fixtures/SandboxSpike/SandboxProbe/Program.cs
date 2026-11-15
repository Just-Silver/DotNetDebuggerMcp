using System.Diagnostics;
using System.Text.Json;

namespace SandboxProbe;

internal static class Program
{
    internal const string OutputRoot = @"C:\SandboxSpike\Outputs";
    internal const string SnapshotPath = @"C:\SandboxSpike\gui.json";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        var guestValidated = false;
        try
        {
            RequireGuest();
            guestValidated = true;
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (args is ["gui"])
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                Application.EnableVisualStyles();
                using var form = new ProbeForm();
                Application.Run(form);
                return 0;
            }
            if (args is ["exercise", var label])
                return RunExercise(label);
            throw new ArgumentException("用法：gui / exercise <label>");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (guestValidated && args is ["gui"] && Directory.Exists(OutputRoot))
                File.WriteAllText(Path.Combine(OutputRoot, "gui-error.txt"), ex.ToString());
            return 1;
        }
    }

    private static int RunExercise(string label)
    {
        var exitCode = 1;
        var thread = new Thread(() => exitCode = DesktopExercise.Run(label));
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        thread.Join();
        return exitCode;
    }

    private static void RequireGuest()
    {
        var instance = Environment.GetEnvironmentVariable("SANDBOX_SPIKE_INSTANCE");
        var host = Environment.GetEnvironmentVariable("SANDBOX_SPIKE_HOST");
        if (Environment.UserName != "WDAGUtilityAccount" || string.IsNullOrEmpty(host)
            || Environment.MachineName.Equals(host, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(instance)
            || File.ReadAllText(@"C:\SandboxSpike\instance.txt").Trim() != instance)
            throw new InvalidOperationException("拒绝执行：不是此 PoC 的 Sandbox 交互环境，绝不回退宿主输入。");
    }

    internal static void WriteJson(string path, object value)
    {
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }

    internal static void WriteSnapshot(ProbeSnapshot snapshot)
    {
        using var mutex = SnapshotMutex();
        if (!mutex.WaitOne(TimeSpan.FromSeconds(3))) throw new TimeoutException("GUI 快照写锁超时。");
        try { WriteJson(SnapshotPath, snapshot); }
        finally { mutex.ReleaseMutex(); }
    }

    internal static ProbeSnapshot ReadSnapshot()
    {
        using var mutex = SnapshotMutex();
        if (!mutex.WaitOne(TimeSpan.FromSeconds(3))) throw new TimeoutException("GUI 快照读锁超时。");
        try
        {
            using var stream = new FileStream(SnapshotPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var snapshot = JsonSerializer.Deserialize<ProbeSnapshot>(stream, Json)
                ?? throw new InvalidOperationException("GUI 快照为空。");
            if (snapshot.ProcessId <= 0 || snapshot.Frame <= 0 || snapshot.DragBounds.Width <= 0 || snapshot.DragBounds.Height <= 0)
                throw new InvalidOperationException("snapshot_invalid：GUI 快照缺少有效身份或几何。");
            if (DateTimeOffset.UtcNow - snapshot.ObservedAt > TimeSpan.FromSeconds(5))
                throw new InvalidOperationException("observation_stalled：GUI 快照超过 5s 未更新。");
            return snapshot;
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static Mutex SnapshotMutex()
        => new(false, @"Local\SandboxSpike.GuiSnapshot." + Environment.GetEnvironmentVariable("SANDBOX_SPIKE_INSTANCE"));
}

internal sealed record ProbeSnapshot(
    int ProcessId, long ProcessStartedTicks, int Clicks, int MouseDowns, int KeyDowns,
    int Drags, string Text, string Focus, int Dpi, long Frame, Point Pointer,
    Rectangle DragBounds, DateTimeOffset ObservedAt)
{
    public string[] RecentKeys { get; init; } = [];
    public int PanelDowns { get; init; }
    public int PanelUps { get; init; }
    public int LastDragDeltaX { get; init; }
}
