using SharpSight.ChildSession.Connection;
using SharpSight.ChildSession.Lifecycle;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildDisplaySettingsTests
{
    [Theory]
    [InlineData(1024, 271u)]
    [InlineData(768, 203u)]
    [InlineData(1920, 508u)]
    [InlineData(1, 10u)]
    public void 物理尺寸按96DPI换算成毫米(int pixels, uint expected)
        => Assert.Equal(expected, RdpActiveXHost.ToMillimeters(pixels));

    [Theory]
    [InlineData(1024, 768, 0, 0, 2.0, true)]
    [InlineData(1024, 768, 1024, 768, 2.0, false)]
    [InlineData(1024, 768, 0, 0, 1.0, false)]
    [InlineData(199, 768, 0, 0, 2.0, false)]
    [InlineData(8193, 768, 0, 0, 2.0, false)]
    public void 动态分辨率只在连接稳定且尺寸变化时下发(int width, int height, int appliedWidth,
        int appliedHeight, double seconds, bool expected)
        => Assert.Equal(expected, RdpActiveXHost.ShouldUpdateDisplay(width, height, appliedWidth,
            appliedHeight, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void 诊断日志放临时目录而不是实例状态目录()
    {
        var instance = Guid.NewGuid().ToString("N");
        var path = ChildDiagnosticLog.PathFor(instance, "worker");
        Assert.StartsWith(Path.GetTempPath(), path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(instance, path);
        Assert.EndsWith("worker.log", path);
    }
}
