using ILSpyMcp.Services;
using Xunit;

namespace ILSpyMcp.Tests;

public class ToolExecutorTests : IDisposable
{
    private readonly string _xDll = Path.Combine(Environment.CurrentDirectory, "x.dll");

    public ToolExecutorTests()
    {
        File.WriteAllText(_xDll, "test");
    }

    public void Dispose()
    {
        File.Delete(_xDll);
    }

    [Fact]
    public async Task 非法assembly_返回提示且不调用work()
    {
        var called = false;
        var r = await Probe("bad:\u0000path", "out", 30, (a, o, _) => { called = true; return "ok"; });
        Assert.Contains("路径非法", r);
        Assert.False(called);
    }

    [Fact]
    public async Task 非法outputDir_返回提示且不调用work()
    {
        var called = false;
        var r = await Probe("x.dll", "\u0000", 30, (a, o, _) => { called = true; return "ok"; });
        Assert.Contains("路径非法", r);
        Assert.False(called);
    }

    [Fact]
    public async Task 非法timeout_返回提示且不调用work()
    {
        var called = false;
        var r = await Probe("x.dll", "out", 0, (a, o, _) => { called = true; return "ok"; });
        Assert.Contains("timeoutSeconds", r);
        Assert.False(called);
    }

    [Fact]
    public async Task 正常_调用work并透传解析路径()
    {
        var r = await Probe("x.dll", "out", 30, (a, o, _) => $"done:{a}:{o}");
        Assert.StartsWith("done:", r);
        Assert.Contains($"done:{Path.GetFullPath("x.dll")}:{Path.GetFullPath("out")}", r);
    }

    private static async Task<string> Probe(string assembly, string outputDir, int timeout, Func<string, string, CancellationToken, string> work)
                        => await ToolExecutor.RunToDisk(assembly, outputDir, timeout, default, work);
}