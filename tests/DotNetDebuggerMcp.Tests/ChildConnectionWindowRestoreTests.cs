using SharpSight.ChildSession.Connection;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildConnectionWindowRestoreTests
{
    [Fact]
    public async Task 取消并释放预算后执行迟到回调不改窗口也不外泄异常()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TempPath();
        var queued = new List<Action>();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var run = ChildConnectionWindowRestore.RunAsync(queued.Add, _ => Restore(path), cancel.Token);
            await cancel.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.NotEmpty(queued);
            foreach (var callback in queued) callback();
            Assert.False(File.Exists(path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task 等待超时后执行迟到回调不改窗口()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TempPath();
        var queued = new List<Action>();
        try
        {
            var run = ChildConnectionWindowRestore.RunAsync(queued.Add, _ => Restore(path), ct);
            Assert.False(await run);
            Assert.NotEmpty(queued);
            foreach (var callback in queued) callback();
            Assert.False(File.Exists(path));
        }
        finally { Delete(path); }
    }

    [Fact]
    public async Task 还原边界抛出异常时不逃出投递回调且等待方取得原因()
    {
        var ct = TestContext.Current.CancellationToken;
        var queued = new List<Action>();
        var run = ChildConnectionWindowRestore.RunAsync(queued.Add,
            _ => throw new InvalidOperationException("窗口句柄已销毁"), ct);
        Assert.NotEmpty(queued);
        foreach (var callback in queued) callback();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        Assert.Contains("窗口句柄已销毁", error.Message);
    }

    [Fact]
    public async Task 已最小化时只做一次原生还原并复核()
    {
        var ct = TestContext.Current.CancellationToken;
        var path = TempPath();
        try
        {
            var run = ChildConnectionWindowRestore.RunAsync(callback => callback(), _ => Restore(path), ct);
            Assert.True(await run);
            Assert.Equal("restored", (await File.ReadAllTextAsync(path, ct)).Trim());
        }
        finally { Delete(path); }
    }

    private static bool Restore(string path)
    {
        File.AppendAllText(path, "restored\n");
        return true;
    }

    private static string TempPath()
        => Path.Combine(Path.GetTempPath(), "restore-" + Guid.NewGuid().ToString("N"));

    private static void Delete(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
