using System.Diagnostics;
using System.Text.Json;
using DotNetDebuggerMcp.Services.Child;
using FlaUI.Core.AutomationElements;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Requests;
using SharpSight.UiAutomation;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildGuiExecutionTests
{
    [Fact]
    public async Task UIA超时后未完成前不能开始第二次GUI变更()
    {
        await using var setup = new Setup();
        var first = setup.Request("ui_action", DateTimeOffset.UtcNow.AddMilliseconds(100));
        var running = setup.Submit(first, setup.FirstFile, block: true);
        await setup.Native.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var timedOut = await running;
            Assert.Equal("running", timedOut.Status);
            Assert.Equal("running", setup.Journal.Find(first.RequestId).Status);
            var second = await setup.Submit(setup.Request("ui_action"), setup.SecondFile);
            Assert.Equal("not-executed", second.Status);
            Assert.False(File.Exists(setup.SecondFile));
            var control = setup.Journal.List(0, 20);
            Assert.NotEmpty(control.GetProperty("items").EnumerateArray());
        }
        finally { setup.Native.Release.TrySetResult(); }
        var result = await setup.EventualResult(first.RequestId);
        Assert.Equal("completed", result.Status);
        Assert.Equal("已变更", await File.ReadAllTextAsync(setup.FirstFile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 等待控件出现时仍可截图和操作且不推进清单帧()
    {
        await using var setup = new Setup();
        var frame = setup.Ui.Frames.Current;
        var request = setup.Request("ui_wait");
        var waiting = setup.Submit(request, "");
        await setup.Native.Probed.Task.WaitAsync(TestContext.Current.CancellationToken);
        var screenshotFile = Path.Combine(setup.Root, "capture.txt");
        var screenshot = await setup.Ui.RunGuiAsync(async ct =>
        {
            await File.WriteAllTextAsync(screenshotFile, "图像", ct);
            return "completed";
        }, DateTimeOffset.UtcNow.AddSeconds(3), TestContext.Current.CancellationToken);
        var action = await setup.Submit(setup.Request("ui_action"), setup.SecondFile);
        Assert.Equal("completed", screenshot);
        Assert.True(File.Exists(screenshotFile));
        Assert.Equal("completed", action.Status);
        Assert.Equal("completed", (await waiting).Status);
        Assert.Equal(frame, setup.Ui.Frames.Current);
    }

    [Fact]
    public async Task 队列到期后不会在先前操作结束后迟到执行()
    {
        await using var setup = new Setup();
        var first = setup.Request("ui_action");
        var running = setup.Submit(first, setup.FirstFile, block: true);
        await setup.Native.Entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            var queued = setup.Request("ui_action", DateTimeOffset.UtcNow.AddMilliseconds(80));
            var expired = await setup.Submit(queued, setup.SecondFile);
            Assert.Equal("not-executed", expired.Status);
        }
        finally { setup.Native.Release.TrySetResult(); }
        Assert.Equal("completed", (await running).Status);
        Assert.False(File.Exists(setup.SecondFile));
    }

    [Fact]
    public async Task 等待到期返回未命中而不是变更失败()
    {
        await using var setup = new Setup();
        var request = setup.Request("ui_wait", DateTimeOffset.UtcNow.AddMilliseconds(100));
        var reply = await setup.Submit(request, "");
        Assert.Equal("completed", reply.Status);
        Assert.Contains("未命中", reply.Message);
        Assert.False(File.Exists(setup.SecondFile));
    }

    [Fact]
    public async Task 实际会话与实例不符时不会执行GUI变更()
    {
        await using var setup = new Setup();
        await using var other = new ChildUiExecutionService(new ChildGuiExecutionGate(),
            Process.GetCurrentProcess().SessionId + 1, setup.Native);
        setup.Native.Context.Value = (setup.SecondFile, false);
        var reply = await other.ExecuteAsync(setup.Request("ui_action"), TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", reply.Status);
        Assert.False(File.Exists(setup.SecondFile));
    }

    private sealed class Setup : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "child-gui-" + Guid.NewGuid().ToString("N"));
        internal string FirstFile => Path.Combine(Root, "first.txt");
        internal string SecondFile => Path.Combine(Root, "second.txt");
        internal RequestJournal Journal { get; } = new();
        internal ControlledUia Native { get; }
        internal ChildUiExecutionService Ui { get; }
        private readonly ChildRequestExecutor _executor;

        internal Setup()
        {
            Directory.CreateDirectory(Root);
            Native = new(SecondFile);
            Ui = new(new ChildGuiExecutionGate(), Process.GetCurrentProcess().SessionId, Native);
            _executor = new(Journal, serialize: false);
        }

        internal ChildRequest Request(string operation, DateTimeOffset? deadline = null)
            => new("gui-tests", Guid.NewGuid().ToString("N"), operation,
                JsonSerializer.SerializeToElement(new
                {
                    process = Environment.ProcessId.ToString(), verb = "invoke", text = "完成",
                    timeoutSeconds = 3
                }), deadline ?? DateTimeOffset.UtcNow.AddSeconds(10));

        internal async Task<ChildReply> Submit(ChildRequest request, string file, bool block = false)
        {
            Native.Context.Value = (file, block);
            return await _executor.ExecuteAsync(request, ct => Ui.ExecuteAsync(request, ct),
                TestContext.Current.CancellationToken);
        }

        internal async Task<ChildReply> EventualResult(string id)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (Journal.Find(id).Status == "running")
                await Task.Delay(10, timeout.Token);
            return Journal.Find(id);
        }

        public async ValueTask DisposeAsync()
        {
            Native.Release.TrySetResult();
            await Ui.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }

    private sealed class ControlledUia(string conditionFile) : IUiExecutionBoundary
    {
        internal AsyncLocal<(string File, bool Block)> Context { get; } = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Probed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T> ExecuteAsync<T>(Func<T> core, TimeSpan timeout, CancellationToken ct)
        {
            object? result;
            if (typeof(T) == typeof(UiActionResult))
            {
                var (file, block) = Context.Value;
                Entered.TrySetResult();
                if (block) await Release.Task;
                await File.WriteAllTextAsync(file, "已变更", CancellationToken.None);
                result = new UiActionResult(true, "已变更");
            }
            else if (typeof(T) == typeof((AutomationElement, string, string)))
                result = ((AutomationElement)null!, (string?)null, (string?)null);
            else if (typeof(T) == typeof(IDisposable))
                result = new Subscription();
            else if (typeof(T) == typeof(string))
            {
                Probed.TrySetResult();
                result = File.Exists(conditionFile) ? "完成" : null;
            }
            else result = true;
            return (T)result!;
        }

        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
