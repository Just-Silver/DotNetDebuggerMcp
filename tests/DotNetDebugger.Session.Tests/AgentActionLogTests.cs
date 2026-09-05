using Xunit;

namespace DotNetDebugger.Session.Tests;

public sealed class AgentActionLogTests
{
    [Fact]
    public void Log_Appends_AndSnapshot_ReturnsInOrder()
    {
        var log = new AgentActionLog();
        log.Log("debug_launch", "exe", "ok");
        log.Log("debug_state", "", "running");

        var snap = log.Snapshot();
        Assert.Equal(2, snap.Count);
        Assert.Equal("debug_launch", snap[0].Tool);
        Assert.Equal("debug_state", snap[1].Tool);
        Assert.True(snap[1].Sequence > snap[0].Sequence);
    }

    [Fact]
    public void Log_ExceedsMax_EvictsOldest()
    {
        var log = new AgentActionLog();
        for (var i = 0; i < AgentActionLog.MaxEntries + 50; i++)
            log.Log($"tool{i}", "", "");

        var snap = log.Snapshot();
        Assert.Equal(AgentActionLog.MaxEntries, snap.Count);
        // 最旧被逐出：第一条应是 MaxEntries 之后的起始
        Assert.Equal("tool50", snap[0].Tool);
    }

    [Fact]
    public void Clear_Empties()
    {
        var log = new AgentActionLog();
        log.Log("t", "", "");
        log.Clear();
        Assert.Empty(log.Snapshot());
    }
}
