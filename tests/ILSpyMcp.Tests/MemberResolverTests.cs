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
        var (typeFound, matches, _) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Formatting.OutputFormatter", "Format");

        Assert.True(typeFound);
        Assert.NotEmpty(matches);
        Assert.Contains(matches, m => m.Name == "Format");
        Assert.All(matches, m => Assert.Matches("^0x06[0-9a-f]{6}$", m.Token));
    }

    [Fact]
    public void FindMembers_大小写不敏感()
    {
        var (typeFound, matches, _) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Formatting.OutputFormatter", "format");

        Assert.True(typeFound);
        Assert.Contains(matches, m => m.Name == "Format");
    }

    [Fact]
    public void FindMembers_空子串_命中全部方法()
    {
        var (typeFound, matches, _) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Formatting.OutputFormatter", "");

        Assert.True(typeFound);
        Assert.Contains(matches, m => m.Name == "FormatHead");
        Assert.Contains(matches, m => m.Name == "SliceLines");
    }

    [Fact]
    public void FindMembers_类型不存在_TypeFound为false()
    {
        var (typeFound, matches, similar) = MemberResolver.FindMembers(AssemblyPath, "No.Such.Type", "x");

        Assert.False(typeFound);
        Assert.Empty(matches);
        Assert.Empty(similar); // 类型未命中时不计算相近名
    }

    [Fact]
    public void FindMembers_类型存在但无匹配_返回空列表()
    {
        var (typeFound, matches, _) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Formatting.OutputFormatter", "ZzzNoSuch");

        Assert.True(typeFound);
        Assert.Empty(matches);
    }

    [Fact]
    public void FindMembers_TestSamplesBigClass_命中BigMethod与BigHelper()
    {
        // 真实测试程序集：验证工具链路使用的目标（BigMethod 子串匹配 BigMethod/BigHelper/BigHelper2 三个）
        var (typeFound, matches, _) = MemberResolver.FindMembers(
            TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "Big");

        Assert.True(typeFound);
        Assert.Equal(3, matches.Count);
        Assert.Contains(matches, m => m.Name == "BigMethod");
        Assert.Contains(matches, m => m.Name == "BigHelper");
    }

    [Fact]
    public void FindMembers_默认排除访问器_includeAccessors为true时包含()
    {
        // ToolCommand 含 Assembly/Signature/Executable/Args 等属性，元数据层对应 get_ 访问器方法
        var (typeFound, matches, _) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Pipeline.ToolCommand", "get");

        Assert.True(typeFound);
        Assert.DoesNotContain(matches, m => m.Name.StartsWith("get_"));

        var (_, withAccessors, _) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Pipeline.ToolCommand", "get", includeAccessors: true);
        Assert.Contains(withAccessors, m => m.Name == "get_Assembly");
    }

    [Fact]
    public void FindMembers_无匹配_返回相近成员名()
    {
        // FormatZz 与 Format 编辑距离为 2（删除 Zz）且共享 6 字符前缀；与 FormatHead 共享 Format 前缀
        var (typeFound, matches, similar) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Formatting.OutputFormatter", "FormatZz");

        Assert.True(typeFound);
        Assert.Empty(matches);
        Assert.Contains("Format", similar);
        Assert.Contains("FormatHead", similar);
        Assert.InRange(similar.Count, 1, 5);
    }

    [Fact]
    public void FindMembers_嵌套类型_加号与点分隔均可定位()
    {
        // 定位改经 MetadataNaming.FindType（+ 归一化为 . 比较），嵌套类型两种分隔写法都应命中
        var (plusFound, _, _) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Caching.DecompileCache+CacheEntry", "");
        var (dotFound, _, _) = MemberResolver.FindMembers(AssemblyPath, "ILSpyMcp.Caching.DecompileCache.CacheEntry", "");

        Assert.True(plusFound);
        Assert.True(dotFound);
    }
}
