using System.Text.Json;
using DotNetDebuggerMcp.ChildCli;
using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Services.Child;
using SharpSight.ChildSession.Contracts;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildCliTests
{
    [Fact]
    public async Task 取消读取参数文件时明确未发送而不是结果未知()
    {
        var file = Path.GetTempFileName();
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        try
        {
            await File.WriteAllTextAsync(file, "{\"instance\":\"test-instance\",\"process\":\"3\",\"value\":\"不可写\"}",
                TestContext.Current.CancellationToken);
            var cli = new ChildCliCommand(new(FeatureFamily.All, host.Client.SendAsync), output, error);
            Assert.Equal(1, await cli.ExecuteAsync(["call", "ui_input", "--json-file", file], cancelled.Token));
            using var reply = JsonDocument.Parse(output.ToString());
            Assert.Equal("not-executed", reply.RootElement.GetProperty("status").GetString());
            Assert.False(File.Exists(host.OutputPath));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData("unknown", "{}", "")]
    [InlineData("ui_input", "{", "")]
    [InlineData("ui_input", "{}", "anything.json")]
    public async Task 无效CLI请求输出单个失败JSON且不产生副作用(string operation, string jsonText, string file)
    {
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new ChildCliCommand(new ChildCallPolicy(FeatureFamily.All, host.Client.SendAsync), output, error);
        var args = new List<string> { "call", operation, "--json", jsonText };
        if (file.Length != 0) args.AddRange(["--json-file", file]);
        var exit = await cli.ExecuteAsync(args.ToArray(), TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, exit);
        Assert.Equal("not-executed", json.RootElement.GetProperty("status").GetString());
        Assert.False(File.Exists(host.OutputPath));
    }

    [Fact]
    public async Task CLI不能用空实例操作主会话()
    {
        await using var host = await ChildTestHost.StartAsync(TestContext.Current.CancellationToken);
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new ChildCliCommand(new ChildCallPolicy(FeatureFamily.All, host.Client.SendAsync), output, error);
        var exit = await cli.ExecuteAsync(["call", "ui_input", "--json", "{\"value\":\"不可写入\"}"],
            TestContext.Current.CancellationToken);
        Assert.Equal(1, exit);
        Assert.False(File.Exists(host.OutputPath));
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("start")]
    [InlineData("status")]
    [InlineData("login")]
    [InlineData("logout")]
    public async Task 生命周期查询允许空实例并原样转发(string action)
    {
        ChildRequest? received = null;
        var caller = new ChildCallPolicy(FeatureFamily.All, (request, _) =>
        {
            received = request;
            return Task.FromResult(ChildReply.Create(request, "completed", "只读查询。"));
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new ChildCliCommand(caller, output, error);
        var exit = await cli.ExecuteAsync(["call", "child_session", "--json", $"{{\"action\":\"{action}\"}}"],
            TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exit);
        Assert.Equal("completed", json.RootElement.GetProperty("status").GetString());
        Assert.NotNull(received);
        Assert.Equal("", received!.Instance);
    }

    [Fact]
    public async Task CLI登录子命令路由到child_session登录动作()
    {
        ChildRequest? received = null;
        var caller = new ChildCallPolicy(FeatureFamily.All, (request, _) =>
        {
            received = request;
            return Task.FromResult(ChildReply.Create(request, "completed", "凭据已保存"));
        });
        using var output = new StringWriter();
        using var error = new StringWriter();
        var cli = new ChildCliCommand(caller, output, error);
        var exit = await cli.ExecuteAsync(["login"], TestContext.Current.CancellationToken);
        Assert.Equal(0, exit);
        Assert.NotNull(received);
        Assert.Equal("", received!.Instance);
        Assert.Equal("login", received.Arguments.GetProperty("action").GetString());
        Assert.Equal(300, received.Arguments.GetProperty("timeoutSeconds").GetInt32());
    }

    [Fact]
    public async Task JSON文件参数按调用端工作目录解析()
    {
        var file = Path.Combine(Path.GetTempPath(), "child-cli-" + Guid.NewGuid().ToString("N") + ".json");
        await File.WriteAllTextAsync(file, "{\"instance\":\"test-instance\",\"value\":\"来自文件\"}",
            TestContext.Current.CancellationToken);
        try
        {
            string? seen = null;
            var caller = new ChildCallPolicy(FeatureFamily.All, (request, _) =>
            {
                seen = request.Arguments.GetProperty("value").GetString();
                return Task.FromResult(ChildReply.Create(request, "completed", "已写入"));
            });
            using var output = new StringWriter();
            using var error = new StringWriter();
            var cli = new ChildCliCommand(caller, output, error);
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, file);
            var exit = await cli.ExecuteAsync(["call", "ui_input", "--json-file", relative],
                TestContext.Current.CancellationToken);
            Assert.Equal(0, exit);
            Assert.Equal("来自文件", seen);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task 真实CLI子进程对无效请求只输出单个失败JSON()
    {
        var cases = new[]
        {
            new[] { "child", "call", "not_an_operation", "--json", "{}" },
            new[] { "child", "call", "ui_input", "--json", "{" },
            new[] { "child", "call", "ui_input", "--json", "{\"value\":\"不可写入\"}" },
        };
        foreach (var args in cases)
        {
            var result = await RunCliAsync(args, TestContext.Current.CancellationToken);
            using var json = JsonDocument.Parse(result.Stdout);
            Assert.Equal(1, result.Exit);
            Assert.Equal("not-executed", json.RootElement.GetProperty("status").GetString());
        }
    }

    [Fact]
    public async Task 真实CLI子进程可只读查询系统状态()
    {
        var result = await RunCliAsync(["child", "call", "child_session", "--json", "{\"action\":\"probe\"}"],
            TestContext.Current.CancellationToken);
        using var json = JsonDocument.Parse(result.Stdout);
        Assert.Equal(0, result.Exit);
        Assert.Equal("completed", json.RootElement.GetProperty("status").GetString());
    }

    private static async Task<(int Exit, string Stdout, string Stderr)> RunCliAsync(string[] args, CancellationToken ct)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "DotNetDebuggerMcp.exe");
        Assert.True(File.Exists(executable), $"未找到宿主 exe：{executable}");
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TimeoutException($"child CLI 未在期限内退出：{executable}");
        }
        return (process.ExitCode, await stdout, await stderr);
    }
}
