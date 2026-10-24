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
        // 未登记 → FamilyOf 返回 None（旧实现的 Decompile 兜底会让此断言空过）
        foreach (var t in all)
            Assert.True(ToolFeatureRegistry.FamilyOf(t) != FeatureFamily.None,
                $"{t.FullName} 未登记能力族（新增工具必须显式归属，否则可能被误发布或漏门控）");
    }

    [Fact]
    public void 未启用族_不注册工具()
    {
        // 覆盖率之外：FamilyOf=None 的类型不得出现在任何启用集合里（防未登记项默认暴露）
        foreach (var t in ToolFeatureRegistry.AllToolTypes().Where(x => ToolFeatureRegistry.FamilyOf(x) == FeatureFamily.None))
            Assert.DoesNotContain(t, ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.All));
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
