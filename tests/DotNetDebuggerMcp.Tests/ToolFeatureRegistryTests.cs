using DotNetDebuggerMcp.Configuration;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// 能力族 → 工具类型 注册表：覆盖率（新增工具必须登记，防「默默不可发现」）与按族过滤。
/// </summary>
public sealed class ToolFeatureRegistryTests
{
    [Fact]
    public void 每个工具类型_都在注册表内()
    {
        var all = ToolFeatureRegistry.AllToolTypes();
        Assert.NotEmpty(all);
        foreach (var t in all)
            Assert.True(Enum.IsDefined(ToolFeatureRegistry.FamilyOf(t)), $"{t.Name} 未登记能力族");
    }

    [Fact]
    public void 默认族_只含反编译与元数据()
    {
        var names = ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Decompile).Select(t => t.Name).ToHashSet();
        Assert.Contains("DecompileTool", names);
        Assert.Contains("DecompileMemberTool", names);
        Assert.Contains("ListTypesTool", names);
        Assert.Contains("CacheStatsTool", names);
        Assert.DoesNotContain("DebugSessionTool", names);
        Assert.DoesNotContain("UiTools", names);
        Assert.DoesNotContain("ScreenshotTool", names);
    }

    [Fact]
    public void 各族过滤_并集为全部()
    {
        var debug = ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Debug).Select(t => t.Name).ToHashSet();
        var ui = ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Ui).Select(t => t.Name).ToHashSet();
        var shot = ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Screenshot).Select(t => t.Name).ToHashSet();

        Assert.Contains("DebugSessionTool", debug);
        Assert.Contains("DebugVerifyTool", debug);
        Assert.Contains("WebOpenTool", debug);        // web_open 随调试族
        Assert.Contains("UiTools", ui);
        Assert.Contains("ScreenshotTool", shot);
        Assert.Contains("ScreenshotWindowsTool", shot);
        Assert.Contains("ScreenshotDisplaysTool", shot);

        // 各族并集 == 全部（无未归属、无重复归属到多族之外）
        var all = ToolFeatureRegistry.AllToolTypes().Select(t => t.Name).ToHashSet();
        var union = new HashSet<string>(debug.Concat(ui).Concat(shot).Concat(
            ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Decompile).Select(t => t.Name)));
        Assert.Equal(all, union);
    }

    [Fact]
    public void 零能力_不注册任何工具()
        => Assert.Empty(ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.None));

    [Fact]
    public void 全族_等于全部工具()
        => Assert.Equal(
            ToolFeatureRegistry.AllToolTypes().Count,
            ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.All).Count);
}
