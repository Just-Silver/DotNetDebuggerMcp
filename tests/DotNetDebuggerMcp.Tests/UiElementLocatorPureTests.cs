using DotNetDebuggerMcp.Services.Ui;
using System.Runtime.Versioning;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// R4 <see cref="UiElementLocator"/> 纯逻辑单测（不触碰 COM/WinForms）：重解析身份匹配（AutoId+Name+ControlType
/// 三者全等）、ordinal 计数键、以及「同名跨类型且无 AutomationId」的 ordinal 定位——修复 Critical-1（旧谓词在
/// Name 非空时忽略 ControlType，会让 index 命中错误控件）。条件缓存/失效重试/虚拟化仍由 DebugUiToolsTests e2e 覆盖。
/// </summary>
[SupportedOSPlatform("windows7.0")]
public sealed class UiElementLocatorPureTests
{
    private readonly record struct Identity(string AutoId, string Name, string ControlType);

    [Fact]
    public void SameIdentity_RequiresAllThreeFields()
    {
        var descriptor = new TargetDescriptor("", "保存", "MenuItem", 0);

        Assert.True(UiElementLocator.SameIdentity(descriptor, "", "保存", "MenuItem"));
        // 同名跨类型（AutoId 为空）必须判为不同实体——这是 Critical-1 的核心。
        Assert.False(UiElementLocator.SameIdentity(descriptor, "", "保存", "Button"));
        // Name 不同（如控件文本变化）→ 视为已变化（e2e stale 重解析依据）。
        Assert.False(UiElementLocator.SameIdentity(descriptor, "", "取消", "MenuItem"));
        // ControlType 忽略大小写。
        Assert.True(UiElementLocator.SameIdentity(descriptor, "", "保存", "menuitem"));
    }

    [Fact]
    public void SameIdentity_WithAutoId_StillRequiresNameAndType()
    {
        var descriptor = new TargetDescriptor("toggleState", "手动", "Button", 0);

        Assert.True(UiElementLocator.SameIdentity(descriptor, "toggleState", "手动", "Button"));
        // AutoId 相同但文本变化（invoke 后 手动→自动）→ 不再视为同一实体（重解析报「目标已变化」）。
        Assert.False(UiElementLocator.SameIdentity(descriptor, "toggleState", "自动", "Button"));
        Assert.False(UiElementLocator.SameIdentity(descriptor, "toggleState", "手动", "CheckBox"));
    }

    [Fact]
    public void OrdinalKey_IncludesAllThreeFields()
    {
        Assert.Equal(UiElementLocator.OrdinalKey("", "保存", "Button"), UiElementLocator.OrdinalKey("", "保存", "Button"));
        Assert.NotEqual(UiElementLocator.OrdinalKey("", "保存", "Button"), UiElementLocator.OrdinalKey("", "保存", "MenuItem"));
        Assert.NotEqual(UiElementLocator.OrdinalKey("a", "x", "Button"), UiElementLocator.OrdinalKey("b", "x", "Button"));
        Assert.NotEqual(UiElementLocator.OrdinalKey("a", "x", "Button"), UiElementLocator.OrdinalKey("a", "y", "Button"));
    }

    [Fact]
    public void OrdinalMatching_SameNameCrossType_SelectsCorrectElement()
    {
        // 遍历顺序：先 Button「保存」，再 MenuItem「保存」，再 Button「保存」；三者 AutoId 均为空（WPF 常见）。
        var identities = new List<Identity>
        {
            new("", "保存", "Button"),
            new("", "保存", "MenuItem"),
            new("", "保存", "Button"),
        };

        // ui_find 缓存 MenuItem（位置 1）的定位条件：其 ordinal = 同名同类型先前的数量 = 0。
        var descriptor = Describe(identities, position: 1);
        Assert.Equal(new TargetDescriptor("", "保存", "MenuItem", 0), descriptor);

        // 重解析：修好后 SameIdentity 只认 MenuItem → matches[0] 即正确实体。
        var resolved = Resolve(descriptor, identities);
        Assert.Equal(identities[1], resolved);

        // 回归证据：旧谓词（AutoId 空则只比 Name）会匹配全部 3 个，ordinal 0 命中 Button——错误控件。
        var buggyMatches = identities.Where(id => id.Name == descriptor.Name).ToList();
        Assert.Equal(identities[0], buggyMatches[descriptor.Ordinal]);
    }

    [Fact]
    public void OrdinalMatching_DuplicateIdentities_PicksRightOccurrence()
    {
        var identities = new List<Identity>
        {
            new("list", "Item 1", "ListItem"),
            new("list", "Item 1", "ListItem"), // 完全同名同类型（如同名列表项）——ordinal 区分
            new("list", "Item 1", "ListItem"),
        };

        var descriptor = Describe(identities, position: 2);
        Assert.Equal(2, descriptor.Ordinal);
        Assert.Equal(identities[2], Resolve(descriptor, identities));
    }

    /// <summary>按 <see cref="UiElementLocator.Find"/> 的计数口径（同三元组遍历序号）构造位置 <paramref name="position"/> 的 descriptor。</summary>
    private static TargetDescriptor Describe(List<Identity> identities, int position)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i <= position; i++)
        {
            var id = identities[i];
            var key = UiElementLocator.OrdinalKey(id.AutoId, id.Name, id.ControlType);
            counts.TryGetValue(key, out var ordinal);
            if (i == position)
                return new TargetDescriptor(id.AutoId, id.Name, id.ControlType, ordinal);
            counts[key] = ordinal + 1;
        }
        throw new ArgumentOutOfRangeException(nameof(position));
    }

    /// <summary>按 <see cref="UiElementLocator.ResolveByDescriptor"/> 的匹配口径（SameIdentity + ordinal）解析目标身份。</summary>
    private static Identity? Resolve(TargetDescriptor descriptor, List<Identity> identities)
    {
        var matches = identities
            .Where(id => UiElementLocator.SameIdentity(descriptor, id.AutoId, id.Name, id.ControlType))
            .ToList();
        return matches.Count > descriptor.Ordinal ? matches[descriptor.Ordinal] : null;
    }
}
