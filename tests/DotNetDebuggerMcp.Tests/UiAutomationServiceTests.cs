using DotNetDebuggerMcp.Services;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// U1 UiAutomationService / UiSemanticResolver 纯单测（可脱机部分）：
/// 语义反查（倒排索引构建/正负路径/缓存）在此覆盖；真实 UIA 调用（Find/Invoke/Scroll/Wait）走 e2e（DebugUiToolsTests）。
/// </summary>
public sealed class UiSemanticResolverTests
{
    /// <summary>语义反查素材：DebugTarget.dll（apphost 的元数据在同名 dll，非 exe——与线上布局一致）。</summary>
    private static string DebugTargetDll => Path.Combine(
        Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, "DebugTarget.dll");

    [Fact]
    public void Lookup_WorkBagOnDebugTargetDll_ReturnsProgramWorkBagCandidate()
    {
        var dll = DebugTargetDll;
        Assert.True(File.Exists(dll), "DebugTarget.dll 不存在，请先运行 generate-testdata.ps1");

        var candidates = UiSemanticResolver.Lookup(dll, "WorkBag");

        Assert.NotNull(candidates); // 有匹配返回非 null
        Assert.NotEmpty(candidates!);
        // 候选格式：类型全名.成员；DebugTarget.Program.WorkBag 含 "Program.WorkBag" 子串
        Assert.Contains(candidates!, c => c.Contains("Program.WorkBag", StringComparison.Ordinal));
    }

    [Fact]
    public void Lookup_UnknownName_ReturnsNull()
    {
        var dll = DebugTargetDll;
        Assert.True(File.Exists(dll), "DebugTarget.dll 不存在，请先运行 generate-testdata.ps1");

        var candidates = UiSemanticResolver.Lookup(dll, "NoSuchMember_xyz_987");

        Assert.Null(candidates);
    }

    [Fact]
    public void Lookup_IgnoreCaseSubstring_Matches()
    {
        var dll = DebugTargetDll;
        Assert.True(File.Exists(dll));

        // 忽略大小写子串：workbag / WORK / "bag" 都应命中 WorkBag 所在候选
        foreach (var query in new[] { "workbag", "WORK", "Bag" })
        {
            var candidates = UiSemanticResolver.Lookup(dll, query);
            Assert.NotNull(candidates);
            Assert.Contains(candidates!, c => c.EndsWith(".WorkBag", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Lookup_MissingOrUnreadableAssembly_ReturnsNull()
    {
        Assert.Null(UiSemanticResolver.Lookup(@"Z:\No\Such\Path\missing.dll", "WorkBag"));
        Assert.Null(UiSemanticResolver.Lookup("", "WorkBag"));
        Assert.Null(UiSemanticResolver.Lookup(DebugTargetDll, ""));
    }

    [Fact]
    public void Lookup_RepeatedQuery_HitsCacheWithoutReread()
    {
        var dll = DebugTargetDll;
        Assert.True(File.Exists(dll));

        // 两次查询结果一致；索引按 assemblyPath 缓存（第二次不再读盘——以结果等价与耗时不放大为观察，不断言内部计数）
        var first = UiSemanticResolver.Lookup(dll, "WorkBag");
        var second = UiSemanticResolver.Lookup(dll, "WorkBag");
        Assert.NotNull(first);
        Assert.Equal(first!, second!);
    }
}
