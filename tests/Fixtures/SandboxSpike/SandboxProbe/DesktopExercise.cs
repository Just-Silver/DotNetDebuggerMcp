using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using SharpSight.Capture;

namespace SandboxProbe;

internal sealed class DesktopExercise
{
    private readonly List<object> _checks = [];
    private readonly string _folder;
    private bool _passed = true;

    private DesktopExercise(string label)
    {
        if (!Regex.IsMatch(label, "^[a-z0-9-]{1,40}$")) throw new ArgumentException("label 无效。");
        _folder = Path.Combine(Program.OutputRoot, label + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    internal static int Run(string label)
    {
        var exercise = new DesktopExercise(label);
        var clock = Stopwatch.StartNew();
        try { exercise.Exercise(); }
        catch (Exception ex) { exercise._passed = false; exercise._checks.Add(new { stage = "exercise", error = ex.ToString() }); }
        var report = new
        {
            label, instanceId = Environment.GetEnvironmentVariable("SANDBOX_SPIKE_INSTANCE"),
            passed = exercise._passed, durationMs = clock.ElapsedMilliseconds,
            observedAt = DateTimeOffset.UtcNow, checks = exercise._checks, artifacts = exercise._folder
        };
        Program.WriteJson(Path.Combine(exercise._folder, "report.json"), report);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, Program.Json));
        return exercise._passed ? 0 : 1;
    }

    private void Exercise()
    {
        var before = Program.ReadSnapshot();
        using var process = Process.GetProcessById(before.ProcessId);
        if (process.ProcessName != "SandboxProbe" || process.StartTime.ToUniversalTime().Ticks != before.ProcessStartedTicks)
            throw new InvalidOperationException("GUI 进程身份已改变。");
        using var automation = new UIA3Automation();
        var window = automation.FromHandle(process.MainWindowHandle);
        var button = window.FindFirstDescendant(cf => cf.ByAutomationId("CountButton"))
            ?? throw new InvalidOperationException("未找到计数按钮。");
        var input = window.FindFirstDescendant(cf => cf.ByAutomationId("Input"))
            ?? throw new InvalidOperationException("未找到输入框。");
        Record("uia", () =>
        {
            input.Patterns.Value.Pattern.SetValue("uia-value");
            button.Patterns.Invoke.Pattern.Invoke();
            WaitFor(s => s.Text == "uia-value" && s.Clicks == before.Clicks + 1);
            return Program.ReadSnapshot();
        });
        Capture("screen-before", () => ScreenCapture.CaptureScreen(new CaptureOptions()));
        PhysicalInput(process.MainWindowHandle, button, input);
        Capture("screen-after", () => ScreenCapture.CaptureScreen(new CaptureOptions()));
        Capture("window-after", () => ScreenCapture.CaptureWindow(process.MainWindowHandle, new CaptureOptions()));
        _checks.Add(new { stage = "display", displays = ScreenCapture.EnumerateDisplays(), snapshot = Program.ReadSnapshot() });
    }

    private void PhysicalInput(IntPtr hwnd, AutomationElement button, AutomationElement input)
    {
        if (GetForegroundWindow() != hwnd) SetForegroundWindow(hwnd);
        if (GetForegroundWindow() != hwnd) throw new InvalidOperationException("guest 目标未获得前台；拒绝向未知窗口注入输入。");
        Record("physical-click", () =>
        {
            var before = Program.ReadSnapshot();
            Mouse.Click(Center(button.BoundingRectangle));
            WaitFor(s => s.Clicks == before.Clicks + 1 && s.MouseDowns == before.MouseDowns + 1);
            return Program.ReadSnapshot();
        });
        Record("keyboard", () =>
        {
            var before = Program.ReadSnapshot();
            Mouse.Click(Center(input.BoundingRectangle));
            WaitFor(s => s.Focus == "Input");
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Wait.UntilInputIsProcessed();
            Keyboard.Type("sandbox-keys");
            WaitFor(s => s.Text == "sandbox-keys" && s.KeyDowns > before.KeyDowns);
            return Program.ReadSnapshot();
        });
        Record("drag", () =>
        {
            var before = Program.ReadSnapshot();
            var rect = before.DragBounds;
            Mouse.Drag(new Point(rect.Left + 30, rect.Top + 40), new Point(rect.Left + 170, rect.Top + 80));
            WaitFor(s => s.Drags == before.Drags + 1);
            return Program.ReadSnapshot();
        });
    }

    private void Record(string stage, Func<object> action)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            var result = action();
            _checks.Add(new { stage, passed = true, durationMs = clock.ElapsedMilliseconds, result });
        }
        catch (Exception ex) { _passed = false; _checks.Add(new { stage, passed = false, durationMs = clock.ElapsedMilliseconds, error = ex.Message }); }
    }

    private void Capture(string name, Func<CaptureResult> capture)
        => Record(name, () =>
        {
            var clock = Stopwatch.StartNew();
            var result = capture();
            File.WriteAllBytes(Path.Combine(_folder, name + ".png"), result.Image);
            if (result.WasAllBlack) throw new InvalidOperationException("截图为纯黑。");
            return new
            {
                result.Width, result.Height, result.OriginX, result.OriginY, result.Source,
                elapsedMs = clock.ElapsedMilliseconds, bytes = result.Image.Length,
                sha256 = Convert.ToHexString(SHA256.HashData(result.Image))
            };
        });

    private static Point Center(Rectangle rect) => new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);

    private static void WaitFor(Func<ProbeSnapshot, bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed.TotalSeconds < 3)
        {
            if (predicate(Program.ReadSnapshot())) return;
            Thread.Sleep(50);
        }
        var last = Program.ReadSnapshot();
        throw new TimeoutException($"3s 内 GUI 未记录预期输入结果：frame={last.Frame} clicks={last.Clicks} mouseDowns={last.MouseDowns} keys={last.KeyDowns} drags={last.Drags} text={last.Text}; panelDowns={last.PanelDowns} panelUps={last.PanelUps} dx={last.LastDragDeltaX}; keys=[{string.Join(";", last.RecentKeys)}]");
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
