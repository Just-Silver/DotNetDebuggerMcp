using System.Diagnostics;
using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Processes;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildProcessTests
{
    private static string Target => Path.Combine(TestDataPaths.RepositoryRoot,
        "tests", "TestData", "ChildSessionTarget", "ChildSessionTarget.exe");

    [Fact]
    public async Task 参数中的空格中文引号和工作环境保持原样()
    {
        await using var setup = new Setup();
        var expected = new[] { "a b", "中文", "a\"b", @"尾部\" };
        var started = await setup.Start(["report", .. expected], new { CHILD_TEST_SAMPLE = "限定值" }, TestContext.Current.CancellationToken);
        var id = started.Data.GetProperty("taskId").GetString()!;
        await setup.WaitExited(id);
        var output = await setup.Call(new { action = "output", taskId = id, limit = 32768 }, TestContext.Current.CancellationToken);
        var stdout = Collect(output, "stdout");
        using var report = JsonDocument.Parse(stdout);
        Assert.Equal(expected, report.RootElement.GetProperty("arguments").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(setup.Root, report.RootElement.GetProperty("directory").GetString());
        Assert.Equal("限定值", report.RootElement.GetProperty("sample").GetString());
    }

    [Fact]
    public async Task 取消一个任务不会结束另一个任务()
    {
        await using var setup = new Setup();
        var first = await setup.Start(["spawn"], ct: TestContext.Current.CancellationToken);
        var second = await setup.Start(["sleep"], ct: TestContext.Current.CancellationToken);
        var firstId = first.Data.GetProperty("taskId").GetString()!;
        var secondId = second.Data.GetProperty("taskId").GetString()!;
        await setup.Call(new { action = "cancel", taskId = firstId }, TestContext.Current.CancellationToken);
        var ended = await setup.WaitExited(firstId);
        var alive = await setup.Call(new { action = "status", taskId = secondId }, TestContext.Current.CancellationToken);
        Assert.False(ended.Data.GetProperty("running").GetBoolean());
        Assert.True(alive.Data.GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task 日志达到上限仍会排空且进程可以正常退出()
    {
        await using var setup = new Setup();
        var start = await setup.Start(["flood"], ct: TestContext.Current.CancellationToken);
        var id = start.Data.GetProperty("taskId").GetString()!;
        var end = await setup.WaitExited(id);
        var output = await setup.Call(new { action = "output", taskId = id, limit = 32768 }, TestContext.Current.CancellationToken);
        Assert.Equal(0, end.Data.GetProperty("exitCode").GetInt32());
        Assert.True(output.Data.GetProperty("truncated").GetBoolean());
        Assert.True(output.Data.GetProperty("nextCursor").GetInt64() > 0);
    }

    [Fact]
    public async Task 启动请求取消等待不终止已接受任务()
    {
        await using var setup = new Setup();
        using var caller = new CancellationTokenSource();
        var started = await setup.Start(["sleep"], ct: caller.Token);
        await caller.CancelAsync();
        var status = await setup.Call(new { action = "status", taskId = started.Data.GetProperty("taskId").GetString() },
            TestContext.Current.CancellationToken);
        Assert.True(status.Data.GetProperty("running").GetBoolean());
    }

    [Fact]
    public async Task 任务标准输入结束不会被自身继承的写端保持打开()
    {
        await using var setup = new Setup();
        var start = await setup.Start(["stdin"], ct: TestContext.Current.CancellationToken);
        var end = await setup.WaitExited(start.Data.GetProperty("taskId").GetString()!);
        Assert.Equal(0, end.Data.GetProperty("exitCode").GetInt32());
    }

    private static string Collect(ChildReply output, string stream)
    {
        var result = new System.Text.StringBuilder();
        foreach (var line in output.Data.GetProperty("text").GetString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.GetProperty("stream").GetString() == stream)
                result.Append(document.RootElement.GetProperty("text").GetString());
        }
        return result.ToString();
    }

    private sealed class Setup : IAsyncDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "child-process-" + Guid.NewGuid().ToString("N"));
        private readonly ChildTaskRunner _runner;
        internal Setup()
        {
            Directory.CreateDirectory(Root);
            _runner = new("process-tests", Root, Process.GetCurrentProcess().SessionId);
        }
        internal Task<ChildReply> Start(string[] arguments, object? environment = null, CancellationToken ct = default)
            => Call(new { action = "start", executable = Target, arguments = JsonSerializer.Serialize(arguments),
                workingDirectory = Root, environment = JsonSerializer.Serialize(environment ?? new { }) }, ct);
        internal Task<ChildReply> Call(object args, CancellationToken ct = default)
            => _runner.ExecuteAsync(new("process-tests", Guid.NewGuid().ToString("N"), "child_process",
                JsonSerializer.SerializeToElement(args), DateTimeOffset.UtcNow.AddSeconds(30)),
                ct == default ? TestContext.Current.CancellationToken : ct);
        internal async Task<ChildReply> WaitExited(string id)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var status = await Call(new { action = "status", taskId = id });
                if (!status.Data.GetProperty("running").GetBoolean()) return status;
                await Task.Delay(30, TestContext.Current.CancellationToken);
            }
            throw new TimeoutException("夹具未退出。");
        }
        public async ValueTask DisposeAsync()
        {
            await _runner.DisposeAsync();
            Directory.Delete(Root, true);
        }
    }
}
