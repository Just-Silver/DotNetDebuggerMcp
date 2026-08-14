using ILSpyMcp.Formatting;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>SectionBuilder 段落组装（标题+空占位语义）的单元测试。</summary>
public class SectionBuilderTests
{
    [Fact] public void 非空段_标题后接内容() {
        var t = new List<string>();
        SectionBuilder.Append(t, "标题:", new List<string> { "a", "b" });
        Assert.Equal(new[] { "标题:", "a", "b" }, t);
    }
    [Fact] public void 空段_默认输出无占位() {
        var t = new List<string>();
        SectionBuilder.Append(t, "标题:", new List<string>());
        Assert.Equal(new[] { "标题:", "（无）" }, t);
    }
    [Fact] public void 空段_omitWhenEmpty_只输出标题() {
        var t = new List<string>();
        SectionBuilder.Append(t, "标题:", new List<string>(), omitWhenEmpty: true);
        Assert.Equal(new[] { "标题:" }, t);
    }
}
