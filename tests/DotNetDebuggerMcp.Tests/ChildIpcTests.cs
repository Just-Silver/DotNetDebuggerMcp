using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;
using SharpSight.ChildSession.Lifecycle;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildIpcTests
{
    private static string TargetDirectory => Path.Combine(TestDataPaths.RepositoryRoot,
        "tests", "TestData", "ChildSessionTarget");

    private static readonly string[] ClientFiles =
    [
        "ChildSessionTarget.exe", "ChildSessionTarget.dll", "ChildSessionTarget.runtimeconfig.json",
        "ChildSessionTarget.deps.json", "SharpSight.ChildSession.dll"
    ];

    [Theory]
    [InlineData("apphost")]
    [InlineData("dotnet")]
    public async Task 不同映像和安装路径的客户端可消费同一实例(string hostForm)
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "child-ipc-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyClient(root);
            await using var host = await ChildTestHost.StartAsync(ct);
            var reply = await RunClientAsync(host, root, hostForm, "跨映像客户端", ct);
            Assert.Equal("completed", reply.Status);
            Assert.Equal("跨映像客户端", await File.ReadAllTextAsync(host.OutputPath, ct));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 固定持有器身份时不接受其他映像客户端()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "child-ipc-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyClient(root);
            await using var host = await ChildTestHost.StartAsync(ct, expectedClient: ChildPeer.Current);
            var reply = await RunClientAsync(host, root, "apphost", "不应写入", ct);
            Assert.Equal("not-executed", reply.Status);
            Assert.Contains("非本实例连接持有器", reply.Message);
            Assert.False(File.Exists(host.OutputPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task 伪造信封映像与实际进程不符时拒绝且不写入()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "child-ipc-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyClient(root);
            await using var host = await ChildTestHost.StartAsync(ct);
            var reply = await RunClientAsync(host, root, "apphost", "不应写入", ct, @"C:\伪造\other.exe");
            Assert.Equal("not-executed", reply.Status);
            Assert.Contains("管道客户端身份不符", reply.Message);
            Assert.False(File.Exists(host.OutputPath));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void CopyClient(string root)
    {
        Directory.CreateDirectory(root);
        foreach (var file in ClientFiles)
            File.Copy(Path.Combine(TargetDirectory, file), Path.Combine(root, file));
    }

    private static async Task<(string Status, string Message)> RunClientAsync(ChildTestHost host,
        string root, string hostForm, string value, CancellationToken ct, string? forgeImage = null)
    {
        var output = Path.Combine(root, "client-reply.txt");
        var server = ChildPeer.Current;
        var payload = new Dictionary<string, object?>
        {
            ["pipeName"] = host.PipeName,
            ["secret"] = ChildTestHost.Secret,
            ["instance"] = "test-instance",
            ["server"] = new
            {
                processId = server.ProcessId, startedTicks = server.StartedTicks,
                sessionId = server.SessionId, sid = server.Sid, image = server.Image
            },
            ["value"] = value,
            ["output"] = output
        };
        if (forgeImage is not null) payload["forgeImage"] = forgeImage;
        var executable = hostForm == "apphost"
            ? Path.Combine(root, "ChildSessionTarget.exe")
            : Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (hostForm != "apphost") info.ArgumentList.Add(Path.Combine(root, "ChildSessionTarget.dll"));
        info.ArgumentList.Add("ipc-client");
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(payload));
        process.StandardInput.Close();
        await process.WaitForExitAsync(ct);
        Assert.True(File.Exists(output),
            $"客户端未产生回复（退出码 {process.ExitCode}）：{await stdout} {await stderr}");
        var lines = (await File.ReadAllTextAsync(output, ct)).Split('\n', 2);
        return (lines[0], lines.ElementAtOrDefault(1) ?? "");
    }

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
