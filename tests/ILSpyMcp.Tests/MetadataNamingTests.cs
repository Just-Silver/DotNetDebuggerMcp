using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

public class MetadataNamingTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含嵌套类型 CacheEntry 与编译器生成类型；纯元数据读取
    private static readonly string AssemblyPath = typeof(OutputFormatter).Assembly.Location;

    [Fact]
    public void FullName_顶层类型_命名空间加类名()
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var handle = MetadataNaming.FindType(reader, "ILSpyMcp.Formatting.OutputFormatter");

        Assert.True(handle.HasValue);
        var type = reader.GetTypeDefinition(handle!.Value);
        Assert.Equal("ILSpyMcp.Formatting.OutputFormatter", MetadataNaming.FullName(reader, type));
    }

    [Fact]
    public void FullName_嵌套类型_加号连接()
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        // DecompileCache 的私有嵌套类 CacheEntry：FullName 应为 命名空间.外层+内层
        var handle = MetadataNaming.FindType(reader, "ILSpyMcp.Caching.DecompileCache+CacheEntry");
        Assert.True(handle.HasValue);
    }

    [Fact]
    public void FindType_点号与加号分隔均可定位嵌套类型()
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        var plus = MetadataNaming.FindType(reader, "ILSpyMcp.Caching.DecompileCache+CacheEntry");
        var dot = MetadataNaming.FindType(reader, "ILSpyMcp.Caching.DecompileCache.CacheEntry");

        Assert.True(plus.HasValue);
        Assert.True(dot.HasValue);
        Assert.Equal(plus, dot);
    }

    [Fact]
    public void FindType_类型不存在_返回null()
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        Assert.Null(MetadataNaming.FindType(reader, "No.Such.Type"));
    }

    [Theory]
    [InlineData("class ILSpyMcp.Formatting.OutputFormatter")]
    [InlineData("Class ILSpyMcp.Formatting.OutputFormatter")] // 前缀大小写不敏感
    [InlineData("struct ILSpyMcp.Pipeline.ToolPipelineResult")]
    [InlineData("enum ILSpyMcp.Pipeline.DecompileKind")]
    public void FindType_行首类别前缀_定位成功(string input)
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        Assert.True(MetadataNaming.FindType(reader, input).HasValue);
    }

    [Fact]
    public void FindTypes_返回全部归一化候选()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        // 点号分隔的嵌套类型名归一化后命中 Container+Inner，首个候选与 FindType 一致
        var candidates = MetadataNaming.FindTypes(reader, "ILSpyMcp.Samples.Container.Inner");
        var viaFindType = MetadataNaming.FindType(reader, "ILSpyMcp.Samples.Container.Inner");

        Assert.NotEmpty(candidates);
        Assert.True(viaFindType.HasValue);
        Assert.Equal(viaFindType!.Value, candidates[0]);
    }

    [Fact]
    public void BuildNotFoundMessage_未找到时附相近类型名()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var message = MetadataNaming.BuildNotFoundMessage(reader, "BigClas");  // 短名编辑距离 1 → BigClass

        Assert.Contains("未找到类型 BigClas", message);
        Assert.Contains("相近类型", message);
        Assert.Contains("ILSpyMcp.Samples.BigClass", message);
    }

    [Fact]
    public void BuildNotFoundMessage_无相近类型_保持原文案()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        var message = MetadataNaming.BuildNotFoundMessage(reader, "No.Such.Type");

        Assert.Equal("未找到类型 No.Such.Type", message);
    }

    [Fact]
    public void FindType_前缀后无内容或不含空格_不误剥()
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        Assert.Null(MetadataNaming.FindType(reader, "class"));        // 前缀后无内容，不剥
        Assert.Null(MetadataNaming.FindType(reader, "interfaceX"));   // 无空格分隔，不剥
        Assert.Null(MetadataNaming.FindType(reader, "class No.Such"));// 剥前缀后类型仍不存在
    }
}
