using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using Xunit;

namespace ILSpyMcp.Tests;

public class MemberResolverTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含 OutputFormatter 等公开类型；纯元数据读取，无需 TestData 路径
    private static readonly string AssemblyPath = typeof(OutputFormatter).Assembly.Location;

    [Fact]
    public void FindMembers_按子串命中方法_返回可用的metadata_token()
    {
        var (typeFound, matches) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Formatting.OutputFormatter", "Format");

        Assert.True(typeFound);
        Assert.NotEmpty(matches);
        Assert.Contains(matches, m => m.Name == "Format");
        Assert.All(matches, m => Assert.Matches("^0x06[0-9a-f]{6}$", m.Token));
    }

    [Fact]
    public void FindMembers_大小写不敏感()
    {
        var (typeFound, matches) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Formatting.OutputFormatter", "format");

        Assert.True(typeFound);
        Assert.Contains(matches, m => m.Name == "Format");
    }

    [Fact]
    public void FindMembers_空子串_命中全部方法()
    {
        var (typeFound, matches) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Formatting.OutputFormatter", "");

        Assert.True(typeFound);
        Assert.Contains(matches, m => m.Name == "FormatHead");
        Assert.Contains(matches, m => m.Name == "SliceLines");
    }

    [Fact]
    public void FindMembers_类型不存在_TypeFound为false()
    {
        var (typeFound, matches) = MemberResolver.FindMembers(AssemblyPath, "No.Such.Type", "x");

        Assert.False(typeFound);
        Assert.Empty(matches);
    }

    [Fact]
    public void FindMembers_类型存在但无匹配_返回空列表()
    {
        var (typeFound, matches) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Formatting.OutputFormatter", "ZzzNoSuch");

        Assert.True(typeFound);
        Assert.Empty(matches);
    }

    [Fact]
    public void FindMembers_TestSamplesBigClass_命中BigMethod与BigHelper()
    {
        // 真实测试程序集：验证工具链路使用的目标（BigMethod 子串匹配 BigMethod/BigHelper/BigHelper2 三个）
        var (typeFound, matches) = MemberResolver.FindMembers(
            TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "Big");

        Assert.True(typeFound);
        Assert.Equal(3, matches.Count);
        Assert.Contains(matches, m => m.Name == "BigMethod");
        Assert.Contains(matches, m => m.Name == "BigHelper");
    }
}
