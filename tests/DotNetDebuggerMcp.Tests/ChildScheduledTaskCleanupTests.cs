using SharpSight.ChildSession.Lifecycle;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildScheduledTaskCleanupTests
{
    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, true)]
    [InlineData("Stopped", false, true)]
    [InlineData("Stopped", true, true)]
    [InlineData("Starting", true, false)]
    [InlineData("Ready", true, false)]
    [InlineData("Starting", false, true)]
    [InlineData("Ready", false, true)]
    public void 只清理没有实例记录或持有器已退出的残留任务(string? state, bool holderAlive, bool expected)
        => Assert.Equal(expected, ChildScheduledTaskCleanup.ShouldRemove(state, holderAlive));

    [Fact]
    public void 清理前缀与投送任务名同源()
    {
        var instance = Guid.NewGuid().ToString("N");
        Assert.Equal("SharpSight-child-" + instance, ChildScheduledTaskCleanup.TaskNameFor(instance));
        Assert.StartsWith(ChildScheduledTaskCleanup.TaskPrefix, ChildScheduledTaskCleanup.TaskNameFor(instance));
    }
}
