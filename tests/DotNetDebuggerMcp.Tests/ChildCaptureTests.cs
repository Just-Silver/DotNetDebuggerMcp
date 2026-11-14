using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using DotNetDebuggerMcp.Services.Child;
using DotNetDebuggerMcp.Services.Gui;
using ModelContextProtocol.Protocol;
using SharpSight.Capture;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Lifecycle;
using SharpSight.UiAutomation;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildCaptureTests
{
    [Fact]
    public async Task 每次采集读取对应PNG且产物失效不会返回旧图()
    {
        var root = Path.Combine(Path.GetTempPath(), "child-capture-" + Guid.NewGuid().ToString("N"));
        var store = new ChildInstanceStore(root);
        await using var ui = new ChildUiExecutionService(new(), Process.GetCurrentProcess().SessionId);
        var service = new ChildScreenshotService(ui, store, "capture-tests",
            Process.GetCurrentProcess().SessionId, new ColoredCapture());
        try
        {
            var first = await service.ExecuteAsync(Request("screen"), TestContext.Current.CancellationToken);
            var second = await service.ExecuteAsync(Request("screen"), TestContext.Current.CancellationToken);
            Assert.Equal(Color.Blue.ToArgb(), await Pixel(first, store));
            Assert.Equal(Color.Lime.ToArgb(), await Pixel(second, store));
            var artifact = second.Data.GetProperty("artifact").Deserialize<ChildArtifact>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            File.Delete(Path.Combine(root, "capture-tests", artifact.RelativePath));
            var unavailable = await ScreenshotPresenter.FromChildAsync(second, store, "",
                TestContext.Current.CancellationToken);
            Assert.DoesNotContain(unavailable.Content, block => block is ImageContentBlock);
            Assert.Contains("产物", Assert.IsType<TextContentBlock>(unavailable.Content[0]).Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 实际会话不符时不会生成截图产物()
    {
        var root = Path.Combine(Path.GetTempPath(), "child-capture-" + Guid.NewGuid().ToString("N"));
        await using var ui = new ChildUiExecutionService(new(), Process.GetCurrentProcess().SessionId);
        var service = new ChildScreenshotService(ui, new(root), "capture-tests",
            Process.GetCurrentProcess().SessionId + 1, new ColoredCapture());
        try
        {
            var reply = await service.ExecuteAsync(Request("screen"), TestContext.Current.CancellationToken);
            Assert.Equal("not-executed", reply.Status);
            Assert.False(reply.Data.TryGetProperty("artifact", out _));
            Assert.False(Directory.Exists(Path.Combine(root, "capture-tests", "artifacts")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 零秒截图在GUI忙时立即拒绝而不是排队迟到采集()
    {
        var root = Path.Combine(Path.GetTempPath(), "child-capture-" + Guid.NewGuid().ToString("N"));
        await using var ui = new ChildUiExecutionService(new(), Process.GetCurrentProcess().SessionId);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = ui.RunGuiAsync(async _ => { await release.Task; return true; },
            DateTimeOffset.MaxValue, TestContext.Current.CancellationToken);
        var service = new ChildScreenshotService(ui, new(root), "capture-tests",
            Process.GetCurrentProcess().SessionId, new ColoredCapture());
        var request = Request("screen") with
        {
            Arguments = JsonSerializer.SerializeToElement(new { mode = "screen", timeoutSeconds = 0 })
        };
        var pending = service.ExecuteAsync(request, TestContext.Current.CancellationToken);
        try
        {
            var reply = await pending.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.Equal("not-executed", reply.Status);
            Assert.False(Directory.Exists(Path.Combine(root, "capture-tests", "artifacts")));
        }
        finally
        {
            release.TrySetResult();
            await blocked;
            await pending;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task 授权真实子会话的PNG包含child标记而非父侧画面()
    {
        var instance = Environment.GetEnvironmentVariable("DOTNETDEBUGGER_CHILD_TEST_INSTANCE");
        if (string.IsNullOrEmpty(instance))
        {
            Assert.Skip("须显式授权并提供DOTNETDEBUGGER_CHILD_TEST_INSTANCE；不自动启用系统开关。");
            return;
        }
        var store = new ChildInstanceStore(ChildServices.Root);
        var state = await store.ReadAsync(instance, TestContext.Current.CancellationToken);
        var client = ChildServices.CreateClient();
        var executable = Path.Combine(TestDataPaths.RepositoryRoot, "tests", "TestData",
            "ChildSessionTarget", "ChildSessionTarget.exe");
        using var parent = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, ArgumentList = { "marker", "blue" }
        })!;
        string taskId = "";
        try
        {
            var started = await client.SendAsync(new(instance, Guid.NewGuid().ToString("N"), "child_process",
                JsonSerializer.SerializeToElement(new
                {
                    action = "start", executable, arguments = "[\"marker\",\"lime\"]"
                }), DateTimeOffset.UtcNow.AddSeconds(10)), TestContext.Current.CancellationToken);
            taskId = started.Data.GetProperty("taskId").GetString()!;
            var pid = started.Data.GetProperty("processId").GetInt32();
            var child = await client.SendAsync(Request("window", instance, pid), TestContext.Current.CancellationToken);
            Assert.Equal("completed", child.Status);
            var artifact = child.Data.GetProperty("artifact").Deserialize<ChildArtifact>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(state.Identity.ChildSessionId, artifact.Metadata.GetProperty("sessionId").GetInt32());
            var parentCapture = await ScreenshotCaptureService.ExecuteAsync(
                new(Mode: "window", ProcessId: parent.Id, TimeoutSeconds: 10), TestContext.Current.CancellationToken);
            Assert.NotNull(parentCapture.Image);
            using var parentStream = new MemoryStream(parentCapture.Image.Image);
            using var parentImage = new Bitmap(parentStream);
            var parentPixel = parentImage.GetPixel(parentImage.Width / 2, parentImage.Height / 2).ToArgb();
            var childPixel = await Pixel(child, store);
            Assert.Equal(Color.Blue.ToArgb(), parentPixel);
            Assert.Equal(Color.Lime.ToArgb(), childPixel);
            Assert.NotEqual(parentPixel, childPixel);
        }
        finally
        {
            if (!parent.HasExited) parent.Kill(entireProcessTree: true);
            await parent.WaitForExitAsync(TestContext.Current.CancellationToken);
            if (taskId.Length != 0)
                await client.SendAsync(new(instance, Guid.NewGuid().ToString("N"), "child_process",
                    JsonSerializer.SerializeToElement(new { action = "cancel", taskId }),
                    DateTimeOffset.UtcNow.AddSeconds(5)), TestContext.Current.CancellationToken);
        }
    }

    private static ChildRequest Request(string mode, string instance = "capture-tests", int processId = 0)
        => new(instance, Guid.NewGuid().ToString("N"), "screenshot",
            JsonSerializer.SerializeToElement(new { mode, processId, timeoutSeconds = 10 }),
            DateTimeOffset.UtcNow.AddSeconds(15));

    private static async Task<int> Pixel(ChildReply reply, ChildInstanceStore store)
    {
        var artifact = reply.Data.GetProperty("artifact").Deserialize<ChildArtifact>(new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var bytes = await store.ReadArtifactAsync(reply.Instance, artifact, TestContext.Current.CancellationToken);
        using var stream = new MemoryStream(bytes);
        using var image = new Bitmap(stream);
        return image.GetPixel(image.Width / 2, image.Height / 2).ToArgb();
    }

    private sealed class ColoredCapture : IChildCaptureBoundary
    {
        private int _next;
        public Task<ScreenshotExecution> CaptureAsync(ScreenshotRequest request, UiAutomationService automation, CancellationToken ct)
        {
            using var image = new Bitmap(24, 16);
            using (var graphics = Graphics.FromImage(image))
                graphics.Clear(Interlocked.Increment(ref _next) == 1 ? Color.Blue : Color.Lime);
            using var stream = new MemoryStream();
            image.Save(stream, ImageFormat.Png);
            return Task.FromResult(new ScreenshotExecution(
                new CaptureResult(stream.ToArray(), 24, 16, null, "受控图像边界", false),
                "测试采集", "screen", [], 0));
        }
    }
}
