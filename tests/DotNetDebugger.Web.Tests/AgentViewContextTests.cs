using DotNetDebugger.Web.Services;
using Xunit;

namespace DotNetDebugger.Web.Tests;

/// <summary>AgentViewContext 服务端测试：写入/变化检测/事件通知（纯服务端，无宿主依赖）。</summary>
public sealed class AgentViewContextTests
{
    [Fact]
    public void Update_首次写入_推进Revision并触发Changed()
    {
        var ctx = new AgentViewContext();
        AgentViewSnapshot? fired = null;
        ctx.Changed += s => fired = s;

        ctx.Update(@"C:\x\App.dll", "MyApp.Program");

        Assert.NotNull(fired);
        Assert.Equal(@"C:\x\App.dll", fired!.AssemblyPath);
        Assert.Equal("MyApp.Program", fired.TypeFullName);
        Assert.Equal(1, fired.Revision);
        Assert.Equal(1, ctx.Snapshot().Revision);
    }

    [Fact]
    public void Update_相同上下文_不触发Changed()
    {
        var ctx = new AgentViewContext();
        ctx.Update("A.dll", "T1");
        var count = 0;
        ctx.Changed += _ => count++;
        ctx.Snapshot(); // 建立基线

        ctx.Update("A.dll", "T1"); // 完全重复
        ctx.Update("A.DLL", "T1"); // 仅 assembly 大小写不同（忽略大小写）

        Assert.Equal(0, count);
        Assert.Equal(1, ctx.Snapshot().Revision);
    }

    [Fact]
    public void Update_任一变化_触发Changed()
    {
        var ctx = new AgentViewContext();
        ctx.Update("A.dll", "T1");
        var count = 0;
        ctx.Changed += _ => count++;

        ctx.Update("A.dll", "T2"); // type 变
        ctx.Update("B.dll", "T2"); // assembly 变
        ctx.Update("B.dll", "T2", "M1"); // member 变

        Assert.Equal(3, count);
        Assert.Equal(4, ctx.Snapshot().Revision);
    }

    [Fact]
    public void Clear_清空并推进Revision()
    {
        var ctx = new AgentViewContext();
        ctx.Update("A.dll", "T1");
        var snap = ctx.Snapshot();
        Assert.NotNull(snap.AssemblyPath);

        ctx.Clear();

        var cleared = ctx.Snapshot();
        Assert.Null(cleared.AssemblyPath);
        Assert.Null(cleared.TypeFullName);
        Assert.True(cleared.Revision > snap.Revision);
    }

    [Fact]
    public void Clear_空上下文_不推进()
    {
        var ctx = new AgentViewContext();
        ctx.Clear();
        ctx.Clear();

        Assert.Equal(0, ctx.Snapshot().Revision);
    }
}
