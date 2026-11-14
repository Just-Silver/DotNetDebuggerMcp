using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Requests;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildIpcCancellationTests
{
    [Fact]
    public async Task 等待中的客户端断开后本请求协作取消且不晚写文件()
    {
        var ct = TestContext.Current.CancellationToken;
        var outputPath = TempPath();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct, async (request, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                await File.WriteAllTextAsync(outputPath, "晚写", token);
                return ChildReply.Create(request, "completed", "已写入");
            });
            var request = host.Request("ui_input", new { value = "晚写" });
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = host.Client.SendAsync(request, abort.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await abort.CancelAsync();
            var clientReply = await pending.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("unknown", clientReply.Status);
            var terminal = await WaitForTerminalAsync(host.Journal, request.RequestId, ct);
            Assert.Equal("cancelled", terminal.Status);
            await Task.Delay(200, ct);
            Assert.False(File.Exists(outputPath));
        }
        finally { File.Delete(outputPath); }
    }

    [Fact]
    public async Task 一个客户端断开不影响另一客户端的请求完成()
    {
        var ct = TestContext.Current.CancellationToken;
        var outputPath = TempPath();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct, async (request, token) =>
            {
                var value = request.Arguments.GetProperty("value").GetString()!;
                if (value == "阻塞")
                {
                    started.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
                await File.WriteAllTextAsync(outputPath, value, token);
                return ChildReply.Create(request, "completed", "已写入");
            });
            var blocked = host.Request("ui_input", new { value = "阻塞" });
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = host.Client.SendAsync(blocked, abort.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await abort.CancelAsync();
            await pending.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal("cancelled", (await WaitForTerminalAsync(host.Journal, blocked.RequestId, ct)).Status);

            var accepted = await host.SendAsync("ui_input", new { value = "仍可写入" }, ct);
            Assert.Equal("completed", accepted.Status);
            Assert.Equal("仍可写入", await File.ReadAllTextAsync(outputPath, ct));
        }
        finally { File.Delete(outputPath); }
    }

    [Fact]
    public async Task 取消后的请求保留终态且重放不再次执行()
    {
        const string enteredRecord = "开始\n";
        var ct = TestContext.Current.CancellationToken;
        var outputPath = TempPath();
        var enteredPath = TempPath();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await using var host = await ChildTestHost.StartAsync(ct, async (request, token) =>
            {
                await File.AppendAllTextAsync(enteredPath, enteredRecord, token);
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                await File.WriteAllTextAsync(outputPath, "晚写", token);
                return ChildReply.Create(request, "completed", "已写入");
            });
            var request = host.Request("ui_input", new { value = "晚写" });
            using var abort = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pending = host.Client.SendAsync(request, abort.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await abort.CancelAsync();
            await pending.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var terminal = await WaitForTerminalAsync(host.Journal, request.RequestId, ct);
            Assert.Equal("cancelled", terminal.Status);
            Assert.Equal(enteredRecord, await File.ReadAllTextAsync(enteredPath, ct));

            var replay = await host.Client.SendAsync(request, ct);
            Assert.Equal("cancelled", replay.Status);
            await Task.Delay(200, ct);
            Assert.Equal(enteredRecord, await File.ReadAllTextAsync(enteredPath, ct));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
            File.Delete(enteredPath);
        }
    }

    private static async Task<ChildReply> WaitForTerminalAsync(RequestJournal journal, string requestId,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var reply = journal.Find(requestId);
            if (reply.Status is "completed" or "failed" or "cancelled" or "not-executed") return reply;
            await Task.Delay(25, ct);
        }
        return journal.Find(requestId);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "child-cancel-" + Guid.NewGuid().ToString("N"));
}
