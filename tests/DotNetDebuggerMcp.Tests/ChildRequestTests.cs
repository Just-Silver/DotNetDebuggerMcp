using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Requests;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildRequestTests
{
    private static ChildRequest Request(string id, DateTimeOffset? deadline = null)
        => new("instance-a", id, "ui_input", JsonSerializer.SerializeToElement(new { value = "完成" }),
            deadline ?? DateTimeOffset.UtcNow.AddSeconds(10));

    private static ChildReply Done(ChildRequest request, string text)
        => new(request.Instance, request.RequestId, "completed", text,
            JsonSerializer.SerializeToElement(new { text }));

    [Fact]
    public async Task 同一请求只产生一次写入且能查回已完成结果()
    {
        var file = Path.GetTempFileName();
        try
        {
            var journal = new RequestJournal();
            var executor = new ChildRequestExecutor(journal);
            var request = Request("write-once");
            async Task<ChildReply> Write(CancellationToken ct)
            {
                await File.AppendAllTextAsync(file, "一次", ct);
                return Done(request, "已写入");
            }
            var first = await executor.ExecuteAsync(request, Write, TestContext.Current.CancellationToken);
            var second = await executor.ExecuteAsync(request, Write, TestContext.Current.CancellationToken);
            Assert.Equal("一次", await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken));
            Assert.Equal("completed", first.Status);
            Assert.Equal("已写入", second.Message);
            Assert.Equal("已写入", journal.Find(request.RequestId).Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task 排队截止后不会在前一个操作结束后迟到写入()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var journal = new RequestJournal();
        var executor = new ChildRequestExecutor(journal);
        var first = Request("blocked");
        var running = executor.ExecuteAsync(first, async _ =>
        {
            entered.SetResult();
            await release.Task;
            return Done(first, "完成");
        }, TestContext.Current.CancellationToken);
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var queued = Request("expired", DateTimeOffset.UtcNow.AddMilliseconds(50));
            var result = await executor.ExecuteAsync(queued, async ct =>
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "late.txt"), "不应发生", ct);
                return Done(queued, "错误");
            }, TestContext.Current.CancellationToken);
            Assert.Equal("not-executed", result.Status);
            release.SetResult();
            await running;
            Assert.False(File.Exists(Path.Combine(directory, "late.txt")));
        }
        finally
        {
            release.TrySetResult();
            await running;
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task 丢失原响应后可列请求并查询结果且查询不会重做写入()
    {
        var journal = new RequestJournal();
        var executor = new ChildRequestExecutor(journal);
        var request = Request("lost-response");
        await executor.ExecuteAsync(request, _ => Task.FromResult(Done(request, "最终文本")),
            TestContext.Current.CancellationToken);
        var listing = journal.List(0, 20);
        var item = Assert.Single(listing.GetProperty("items").EnumerateArray());
        Assert.Equal("lost-response", item.GetProperty("requestId").GetString());
        Assert.False(item.TryGetProperty("arguments", out _));
        Assert.Equal("最终文本", journal.Find(item.GetProperty("requestId").GetString()!).Message);
    }

    [Fact]
    public async Task 已淘汰请求的结果未知且不会自动重做()
    {
        var journal = new RequestJournal(capacity: 1);
        var executor = new ChildRequestExecutor(journal);
        foreach (var id in new[] { "old", "new" })
        {
            var request = Request(id);
            await executor.ExecuteAsync(request, _ => Task.FromResult(Done(request, id)),
                TestContext.Current.CancellationToken);
        }
        Assert.Equal("unknown", journal.Find("old").Status);
        Assert.Equal("new", journal.Find("new").Message);
    }
}
