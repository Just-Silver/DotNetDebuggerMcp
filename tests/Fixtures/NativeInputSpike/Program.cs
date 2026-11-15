using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace NativeInputSpike;

internal static class Program
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            if (args.Length == 0 || args[0] == "probe") { Print(Native.Probe()); return 0; }
            if (args[0] is "enable" or "disable")
            {
                if (!Native.WTSEnableChildSessions(args[0] == "enable"))
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                Print(Native.Probe()); return 0;
            }
            var root = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(root);
            return Run(args, root);
        }
        catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
    }

    private static int Run(string[] args, string root)
    {
        switch (args[0])
        {
            case "connect": Application.Run(new ConnectionForm(root)); return 0;
            case "launch-worker": ChildLauncher.Launch(root); return 0;
            case "stop-child": ChildLauncher.Stop(root); return 0;
            case "worker":
                Application.Run(new ProbeForm(root, int.Parse(args[2]), args[3] == "child")); return 0;
            case "background": BackgroundRun.Run(root); return 0;
            case "observe": ParentObserver.Run(root, int.Parse(args[2])); return 0;
            case "request": Print(Request(root, args[2])); return 0;
            default: throw new ArgumentException("未知命令。");
        }
    }

    internal static JsonElement Request(string root, string action)
    {
        var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "worker.json"))).RootElement;
        using var process = Process.GetProcessById(state.GetProperty("pid").GetInt32());
        if (process.StartTime.ToUniversalTime().Ticks != state.GetProperty("startedTicks").GetInt64() ||
            process.SessionId != state.GetProperty("sessionId").GetInt32())
            throw new InvalidOperationException("worker 身份已失效。");
        using var pipe = new NamedPipeClientStream(".", state.GetProperty("pipe").GetString()!, PipeDirection.InOut);
        pipe.Connect(5000);
        using var reader = new StreamReader(pipe);
        using var writer = new StreamWriter(pipe) { AutoFlush = true };
        writer.WriteLine(action);
        var line = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(40)).GetAwaiter().GetResult();
        return JsonDocument.Parse(line ?? throw new IOException("worker 未返回结果。")).RootElement.Clone();
    }

    internal static void Save(string path, object value) => File.WriteAllText(path, JsonSerializer.Serialize(value, JsonOptions));
    internal static void Print(object value) => Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
}
