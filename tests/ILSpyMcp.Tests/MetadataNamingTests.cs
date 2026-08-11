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
}
