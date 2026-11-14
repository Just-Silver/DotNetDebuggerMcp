using System.IO.Pipes;
using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildIpcTests
{
    [Fact]
    public async Task 错误实例和凭据不能写入且后续正常请求不受影响()
    {
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken);
        var request = host.Request("ui_input", new { value = "安全写入" });
        var wrongInstance = await host.Client.SendAsync(request with { Instance = "other" },
            TestContext.Current.CancellationToken);
        var wrongSecret = new ChildPipeClient(host.PipeName, "wrong", ChildPeer.Current);
        var rejected = await wrongSecret.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", wrongInstance.Status);
        Assert.Equal("not-executed", rejected.Status);
        Assert.False(File.Exists(host.OutputPath));
        var accepted = await host.Client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal("completed", accepted.Status);
        Assert.Equal("安全写入", await File.ReadAllTextAsync(host.OutputPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 未知操作和过大请求不会写入()
    {
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken);
        var unknown = await host.SendAsync("invoke_any_method", new { value = "危险" }, TestContext.Current.CancellationToken);
        var large = await host.SendAsync("ui_input", new { value = new string('x', 1_048_577) }, TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", unknown.Status);
        Assert.Equal("not-executed", large.Status);
        Assert.False(File.Exists(host.OutputPath));
    }

    [Fact]
    public async Task 客户端提交截断帧不会终止其他客户端服务()
    {
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken);
        await using (var pipe = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await pipe.ConnectAsync(TestContext.Current.CancellationToken);
            await pipe.WriteAsync(BitConverter.GetBytes(100), TestContext.Current.CancellationToken);
            await pipe.WriteAsync(new byte[] { (byte)'{' }, TestContext.Current.CancellationToken);
        }
        var accepted = await host.SendAsync("ui_input", new { value = "仍可写入" }, TestContext.Current.CancellationToken);
        Assert.Equal("completed", accepted.Status);
        Assert.Equal("仍可写入", await File.ReadAllTextAsync(host.OutputPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task 产物引用不能读取实例目录外的文件()
    {
        var root = Path.Combine(Path.GetTempPath(), "child-store-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ChildInstanceStore(root);
            store.Create("one");
            await File.WriteAllTextAsync(Path.Combine(root, "private.png"), "秘密", TestContext.Current.CancellationToken);
            var artifact = new ChildArtifact("../private.png", "image/png", 6, JsonSerializer.SerializeToElement(new { }));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.ReadArtifactAsync("one", artifact, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
