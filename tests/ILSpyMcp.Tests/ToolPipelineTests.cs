using ILSpyMcp;
using Xunit;

namespace ILSpyMcp.Tests;

public class ToolPipelineTests
{
    private static readonly string AssemblyPath = typeof(ToolPipelineTests).Assembly.Location;

    private static ToolPipeline Create(FakeProcessRunner fake, DecompileCache? cache = null)
        => new(fake, cache ?? new DecompileCache());

    [Fact]
    public async Task 首次调用_回源并返回格式化结果()
    {
        var fake = new FakeProcessRunner { Stdout = "a\nb\n" };
        var pipeline = Create(fake);

        var result = await pipeline.ExecuteAsync(AssemblyPath, new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig")), "");

        Assert.Equal(1, fake.CallCount);
        Assert.Equal("1\ta\n2\tb", result.Text);
    }

    [Fact]
    public async Task 二次调用_命中缓存不再回源()
    {
        var fake = new FakeProcessRunner { Stdout = "a\nb\n" };
        var pipeline = Create(fake);
        var command = new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig"));

        await pipeline.ExecuteAsync(AssemblyPath, command, "");
        await pipeline.ExecuteAsync(AssemblyPath, command, "");

        Assert.Equal(1, fake.CallCount);
    }

    [Fact]
    public async Task 不同签名_各自独立回源()
    {
        var fake = new FakeProcessRunner { Stdout = "x\n" };
        var pipeline = Create(fake);

        await pipeline.ExecuteAsync(AssemblyPath, new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig1")), "");
        await pipeline.ExecuteAsync(AssemblyPath, new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig2")), "");

        Assert.Equal(2, fake.CallCount);
    }

    [Fact]
    public async Task 指定lines_按行号切片()
    {
        var fake = new FakeProcessRunner { Stdout = "a\nb\nc\n" };
        var pipeline = Create(fake);

        var result = await pipeline.ExecuteAsync(AssemblyPath, new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig")), "2-3");

        Assert.Equal("2\tb\n3\tc", result.Text);
    }

    [Fact]
    public async Task 退出码非0_返回错误提示不抛异常()
    {
        var fake = new FakeProcessRunner { Code = 1, Stderr = "boom" };
        var pipeline = Create(fake);

        var result = await pipeline.ExecuteAsync(AssemblyPath, new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig")), "");

        Assert.Contains("ilspycmd 退出码: 1", result.Text);
        Assert.Contains("boom", result.Text);
    }

    [Fact]
    public async Task 并发同key_只回源一次()
    {
        var fake = new FakeProcessRunner { Stdout = "a\n", Delay = TimeSpan.FromMilliseconds(50) };
        var pipeline = Create(fake);
        var command = new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig"));

        var tasks = Enumerable.Range(0, 20).Select(_ => pipeline.ExecuteAsync(AssemblyPath, command, "")).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, fake.CallCount);
        Assert.All(results, r => Assert.Equal("1\ta", r.Text));
    }

    [Fact]
    public async Task 指定timeout_传给子进程执行器()
    {
        var fake = new FakeProcessRunner { Stdout = "a\n" };
        var pipeline = Create(fake);
        var command = new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig"));

        await pipeline.ExecuteAsync(AssemblyPath, command, "", TimeSpan.FromSeconds(99));

        Assert.Equal(TimeSpan.FromSeconds(99), fake.Timeout);
    }

    [Fact]
    public async Task 未指定timeout_使用全局默认超时()
    {
        var fake = new FakeProcessRunner { Stdout = "a\n" };
        var pipeline = Create(fake);
        var command = new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig"));

        await pipeline.ExecuteAsync(AssemblyPath, command, "");

        Assert.Equal(AppConfig.DefaultTimeout, fake.Timeout);
    }

    [Fact]
    public async Task 回源失败后_再次调用会重试()
    {
        var fake = new FakeProcessRunner { Stdout = "a\n" };
        var pipeline = Create(fake);
        var command = new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig"));

        fake.Code = 1;
        var first = await pipeline.ExecuteAsync(AssemblyPath, command, "");
        Assert.Contains("退出码", first.Text);

        fake.Code = 0;
        var second = await pipeline.ExecuteAsync(AssemblyPath, command, "");
        Assert.Equal(2, fake.CallCount);
        Assert.Equal("1\ta", second.Text);
    }

    [Fact]
    public void ToolCommand_命令行与签名由参数结构统一派生()
    {
        var cmd = new ToolCommand("tool", AssemblyPath,
            new ToolParameter("-t", "A"),
            ToolParameter.Switch("-p", true),
            ToolParameter.Optional("-lv", ""));

        Assert.Equal("tool", cmd.Executable);
        Assert.Equal(new[] { "-t", "A", "-p", AssemblyPath }, cmd.Args);
        Assert.Equal("-t\u001FA\u001F-p", cmd.Signature);
    }

    [Fact]
    public void ToolCommand_不同参数同值_签名互不相同()
    {
        var viaType = new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "A"));
        var viaMember = new ToolCommand("tool", AssemblyPath, new ToolParameter("-m", "A"));

        Assert.Equal(new[] { "-t", "A", AssemblyPath }, viaType.Args);
        Assert.Equal(new[] { "-m", "A", AssemblyPath }, viaMember.Args);
        Assert.NotEqual(viaType.Signature, viaMember.Signature);
    }

    [Fact]
    public async Task BuildKey抛异常_返回提示文本不抛异常()
    {
        // 绕过 ToolPreflight 直接调 pipeline，传空路径触发 Path.GetFullPath 抛 ArgumentException
        var fake = new FakeProcessRunner { Stdout = "a\n" };
        var pipeline = Create(fake);

        var result = await pipeline.ExecuteAsync("", new ToolCommand("tool", "", new ToolParameter("-t", "sig")), "");

        Assert.Contains("反编译失败", result.Text);
        Assert.Equal(0, fake.CallCount); // 未进入回源
    }

    [Fact]
    public async Task CancellationToken_原样传给子进程执行器()
    {
        var fake = new FakeProcessRunner { Stdout = "a\n" };
        var pipeline = Create(fake);
        var command = new ToolCommand("tool", AssemblyPath, new ToolParameter("-t", "sig"));
        using var cts = new CancellationTokenSource();

        await pipeline.ExecuteAsync(AssemblyPath, command, "", null, cts.Token);

        Assert.Equal(cts.Token, fake.LastToken);
    }
}
