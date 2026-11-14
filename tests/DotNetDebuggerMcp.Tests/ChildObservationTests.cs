using System.Text.Json;
using SharpSight.ChildSession.Observations;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildObservationTests
{
    private static JsonElement Source(string kind = "uia", int frame = 1, string instance = "one",
        long started = 100, string geometry = "10,20,300,200", string layout = "0,0,1024,768")
        => JsonSerializer.SerializeToElement(new
        {
            kind, frameId = frame, instance, workerPid = 10, workerStartedTicks = 20L,
            sessionId = 7, processId = 30, startedTicks = started, hwnd = "40", geometry, layout
        });

    [Fact]
    public void 登记普通窗口截图不会废弃原UIA清单引用()
    {
        var registry = new ObservationRegistry();
        var list = registry.Register(Source());
        registry.Register(Source("window", frame: 0));
        registry.Validate(list, Source("uia-index"), 1);
    }

    [Fact]
    public void 再次查找后旧index引用被拒绝()
    {
        var registry = new ObservationRegistry();
        var old = registry.Register(Source());
        registry.Register(Source(frame: 2));
        Assert.Throws<InvalidOperationException>(() => registry.Validate(old, Source("uia-index", frame: 2), 0));
    }

    [Theory]
    [InlineData("other", 100)]
    [InlineData("one", 101)]
    public void 别的实例或复用PID不能消费旧观察(string instance, long started)
    {
        var registry = new ObservationRegistry();
        var old = registry.Register(Source());
        Assert.Throws<InvalidOperationException>(() =>
            registry.Validate(old, Source(instance: instance, started: started), 1));
    }

    [Theory]
    [InlineData("11,20,300,200", "0,0,1024,768")]
    [InlineData("10,20,300,200", "0,0,1280,768")]
    public void 窗口几何或显示布局改变后拒绝旧观察(string geometry, string layout)
    {
        var registry = new ObservationRegistry();
        var old = registry.Register(Source("window", frame: 0));
        Assert.Throws<InvalidOperationException>(() =>
            registry.Validate(old, Source("window", frame: 0, geometry: geometry, layout: layout), 0));
    }

    [Fact]
    public void 视觉截图引用不能伪装为index清单或与另一帧混用()
    {
        var registry = new ObservationRegistry();
        var visual = registry.Register(Source("window", frame: 0));
        Assert.Throws<InvalidOperationException>(() => registry.Validate(visual, Source("uia-index"), 0));
        Assert.Throws<InvalidOperationException>(() => registry.Validate(visual, Source(), 1));
    }

    [Fact]
    public void 淘汰旧观察不会错误复用新记录()
    {
        var registry = new ObservationRegistry();
        var oldest = registry.Register(Source("window", frame: 0));
        string latest = "";
        for (var i = 0; i < 256; i++) latest = registry.Register(Source("window", frame: 0));
        Assert.Throws<InvalidOperationException>(() => registry.Validate(oldest, Source("window", frame: 0), 0));
        registry.Validate(latest, Source("window", frame: 0), 0);
    }
}
