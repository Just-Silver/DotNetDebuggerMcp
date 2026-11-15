using System.Diagnostics;
using FlaUI.UIA3;

namespace NativeInputSpike;

internal static class BackgroundRun
{
    internal static void Run(string root)
    {
        var before = Native.Observe();
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "worker", root, Process.GetCurrentProcess().SessionId.ToString(), "background" }) info.ArgumentList.Add(arg);
        using var worker = Process.Start(info) ?? throw new InvalidOperationException("启动测试窗失败。");
        worker.BeginOutputReadLine(); worker.BeginErrorReadLine();
        var observations = new List<object>();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!File.Exists(Path.Combine(root, "worker.json")) && DateTime.UtcNow < deadline) Thread.Sleep(100);
            var initial = Program.Request(root, "status");
            using var automation = new UIA3Automation();
            var window = automation.GetDesktop().FindFirstChild(c => c.ByProcessId(worker.Id));
            var canvas = window?.FindFirstDescendant(c => c.ByName("Input-only canvas"));
            var uia = new { found = canvas is not null, invokeSupported = canvas?.Patterns.Invoke.IsSupported,
                valueSupported = canvas?.Patterns.Value.IsSupported };
            foreach (var state in new[] { "show", "minimize", "hide" })
            {
                var view = Program.Request(root, state);
                var sent = Program.Request(root, "messages");
                Thread.Sleep(150);
                var result = Program.Request(root, "status");
                var capture = Program.Request(root, "capture");
                observations.Add(new { state, view, sent, result, capture, parent = Native.Observe() });
            }
            var refused = Program.Request(root, "real");
            var report = new { route = "PostMessage", realInput = false, before, initial, uia,
                observations, parentRealInputRefused = refused, after = Native.Observe() };
            Program.Save(Path.Combine(root, "background-report.json"), report);
            Program.Print(report);
        }
        finally
        {
            if (!worker.HasExited)
            {
                try { Program.Request(root, "stop"); }
                catch (Exception ex) { Console.Error.WriteLine($"测试窗正常关闭失败：{ex.Message}"); }
                if (!worker.WaitForExit(3000)) worker.Kill();
            }
        }
    }
}
