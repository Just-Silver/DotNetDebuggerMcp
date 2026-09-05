using DotNetDebugger.Engine.Engine;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// BreakpointManager 纯内存单测（无进程/COM）：pending 断点登记（模块未加载）、
/// 未绑定断点永不命中、模块名匹配语义（文件名/全路径/忽略大小写）。
/// </summary>
public sealed class BreakpointManagerTests
{
    [Fact]
    public void Add_ModuleNotLoaded_RegistersPendingWithoutBinding()
    {
        var manager = new BreakpointManager();
        var bp = manager.Add("NoSuchModule.dll", 0x06000003, 0);

        Assert.True(bp.Id > 0);
        Assert.False(bp.IsBound); // pending：等 LoadModule 重绑
        Assert.Single(manager.Breakpoints, bp);
    }

    [Fact]
    public void MatchContent_UnboundBreakpoint_NeverMatches()
    {
        var manager = new BreakpointManager();
        manager.Add("NoSuchModule.dll", 0x06000003, 0);

        Assert.Null(manager.MatchContent("NoSuchModule.dll", 0x06000003, 0)); // 未绑定不命中
        Assert.Null(manager.MatchContent(null, 0x06000003, 0));               // 模块名取不到也不命中
    }

    [Fact]
    public void ModuleMatches_FileNameOrFullPath_CaseInsensitive()
    {
        // 文件名匹配（CorDebugModule.Name 实际返回全路径，按文件名归一化）
        Assert.True(BreakpointManager.ModuleMatches("DebugTarget.dll", @"C:\app\DebugTarget.dll"));
        // 全路径匹配（断点名传全路径的双键场景）
        Assert.True(BreakpointManager.ModuleMatches(@"C:\app\DebugTarget.dll", @"C:\app\DebugTarget.dll"));
        // 忽略大小写
        Assert.True(BreakpointManager.ModuleMatches("debugtarget.dll", @"C:\app\DEBUGTARGET.DLL"));
        // 不同模块不匹配（跨模块同 token 防误判的关键）
        Assert.False(BreakpointManager.ModuleMatches("DebugTarget.dll", @"C:\app\Other.dll"));
        Assert.False(BreakpointManager.ModuleMatches("DebugTarget", @"C:\app\DebugTarget.dll"));
    }
}
