using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
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
    public void BuildAmbiguityMessage_头部与候选行格式()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        // 任意两个真实类型 handle 作候选：验证头部说明行（含调用方传入的消歧解法）与每候选行格式（全名 + 0x02 开头 token，可直接用于 typeToken）
        var bigClass = MetadataNaming.FindType(reader, "ILSpyMcp.Samples.BigClass")!.Value;
        var class0001 = MetadataNaming.FindType(reader, "ILSpyMcp.Samples.Class0001")!.Value;
        var candidates = new[] { bigClass, class0001 };

        var message = MetadataNaming.BuildAmbiguityMessage(reader, "Probe.Ambiguous.Input", candidates, "可用 typeToken 精确定位");

        Assert.StartsWith("类型 Probe.Ambiguous.Input 有歧义，匹配以下类型（可用 typeToken 精确定位）：", message);
        Assert.Contains($"  ILSpyMcp.Samples.BigClass（token 0x{MetadataTokens.GetToken(bigClass):x8}）", message);
        Assert.Contains($"  ILSpyMcp.Samples.Class0001（token 0x{MetadataTokens.GetToken(class0001):x8}）", message);
    }

    [Fact]
    public void BuildAmbiguityMessage_无token工具_沿用归一化换名解法()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var bigClass = MetadataNaming.FindType(reader, "ILSpyMcp.Samples.BigClass")!.Value;
        var class0001 = MetadataNaming.FindType(reader, "ILSpyMcp.Samples.Class0001")!.Value;

        var message = MetadataNaming.BuildAmbiguityMessage(reader, "Probe.Ambiguous.Input", new[] { bigClass, class0001 }, "该类型名在归一化后存在同名类型，请换用不含歧义的完整类型名");

        Assert.StartsWith("类型 Probe.Ambiguous.Input 有歧义，匹配以下类型（该类型名在归一化后存在同名类型，请换用不含歧义的完整类型名）：", message);
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