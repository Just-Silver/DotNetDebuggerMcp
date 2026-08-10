using ILSpyMcp.Infrastructure;
using System.Text;
using Xunit;

namespace ILSpyMcp.Tests;

public class ProcessRunnerTests
{
    private readonly IProcessRunner _runner = new ProcessRunner();

    [Fact]
    public async Task 成功执行_返回退出码0并捕获stdout()
    {
        var result = await _runner.RunAsync("cmd", new[] { "/c", "echo hello" }, Environment.CurrentDirectory);
        Assert.Equal(0, result.Code);
        Assert.Contains("hello", result.Stdout);
    }

    [Fact]
    public async Task 非零退出码_原样返回()
    {
        var result = await _runner.RunAsync("cmd", new[] { "/c", "exit 3" }, Environment.CurrentDirectory);
        Assert.Equal(3, result.Code);
    }

    [Fact]
    /// <summary>
    /// 命令不存在时返回退出码 -1 并附「无法启动」提示。
    /// </summary>
    public async Task CommandNotFound_ReturnsNegativeOneWithHint()
    {
        var result = await _runner.RunAsync("ilspymcp-no-such-cmd-xyz", Array.Empty<string>(), Environment.CurrentDirectory);
        Assert.Equal(-1, result.Code);
        Assert.Contains("无法启动", result.Stderr);
    }

    [Fact]
    public async Task 超时_终止进程并返回提示()
    {
        var result = await _runner.RunAsync(
            "powershell",
            new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 5" },
            Environment.CurrentDirectory,
            TimeSpan.FromMilliseconds(500));

        Assert.Equal(-1, result.Code);
        Assert.Contains("超时", result.Stderr);
    }

    [Fact]
    public async Task 外部取消_返回进程执行被取消而非超时()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await _runner.RunAsync(
            "powershell",
            new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 5" },
            Environment.CurrentDirectory,
            TimeSpan.FromSeconds(30),
            cts.Token);

        Assert.Equal(-1, result.Code);
        Assert.Contains("取消", result.Stderr);
        Assert.DoesNotContain("超时", result.Stderr);
    }

    [Fact]
    public async Task ReadCappedAsync_未超限_返回完整文本()
    {
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("hello world")));
        var (text, over) = await ProcessRunner.ReadCappedAsync(reader, 1024, CancellationToken.None);
        Assert.False(over);
        Assert.Equal("hello world", text);
    }

    [Fact]
    public async Task ReadCappedAsync_超限_标记OverCap且不抛异常()
    {
        // 24 字符（48 字节 UTF-16），上限 4 字节 → 首次读即超限
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("hello world hello world")));
        var (text, over) = await ProcessRunner.ReadCappedAsync(reader, 4, CancellationToken.None);
        Assert.True(over);
        // 超限后丢弃输出，不抛异常即通过
    }

    [Fact]
    public async Task ReadCappedAsync_边界等于上限_不超限()
    {
        // 4 字符 * 2 = 8 字节，上限 8 → (4 * 2) 不 > 8 → 不超限
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("aaaa")));
        var (text, over) = await ProcessRunner.ReadCappedAsync(reader, 8, CancellationToken.None);
        Assert.False(over);
        Assert.Equal("aaaa", text);
    }

    [Fact]
    public async Task ReadCappedAsync_已取消_抛OperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("hello")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ProcessRunner.ReadCappedAsync(reader, 1024, cts.Token));
    }
}