using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

public class ReferenceExtractorTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含泛型实例化字段 Dictionary<CacheKey, CacheEntry> 与嵌套类型 CacheEntry；纯元数据读取
    private static readonly string AssemblyPath = typeof(OutputFormatter).Assembly.Location;

    [Fact]
    public void DecompileCache_泛型实例化字段_收集内部类型()
    {
        // DecompileCache._map 为 Dictionary<CacheKey, CacheEntry>：泛型实例化归约到定义，CacheKey 与嵌套 CacheEntry 均本程序集类型
        using var scope = new MetadataScope(AssemblyPath);
        var result = Extract(scope.Reader, "ILSpyMcp.Caching.DecompileCache");
        Assert.Contains("ILSpyMcp.Caching.CacheKey", result);
        Assert.Contains("ILSpyMcp.Caching.DecompileCache+CacheEntry", result);
    }

    [Fact]
    public void ToolPipeline_方法返回与字段_收集内部类型()
    {
        // ExecuteAsync 返回 Task<ToolPipelineResult>；字段 _cache: DecompileCache、_inflight:
        // ConcurrentDictionary<CacheKey,...> （泛型实例化归约收集 CacheKey）
        using var scope = new MetadataScope(AssemblyPath);
        var result = Extract(scope.Reader, "ILSpyMcp.Pipeline.ToolPipeline");
        Assert.Contains("ILSpyMcp.Pipeline.ToolPipelineResult", result);
        Assert.Contains("ILSpyMcp.Caching.DecompileCache", result);
        Assert.Contains("ILSpyMcp.Caching.CacheKey", result);
    }

    [Fact]
    public void 跨程序集类型_不收集()
    {
        // Task/List/String 等 System 类型是外部 TypeReference，签名解码时不应被收集
        using var scope = new MetadataScope(AssemblyPath);
        var result = Extract(scope.Reader, "ILSpyMcp.Pipeline.ToolPipeline");
        Assert.DoesNotContain("System.Threading.Tasks.Task", result);
        Assert.DoesNotContain("System.String", result);
        Assert.DoesNotContain("System.Collections.Generic.List", result);
    }

    [Fact]
    public void WithExternal_内部集合与缺省API一致_外部集合含跨程序集类型()
    {
        // ToolPipeline 方法返回 Task<ToolPipelineResult>：内部集合不变，外部集合含泛型 Task`1（带程序集归属 System.Runtime）
        using var scope = new MetadataScope(AssemblyPath);
        var (internalSet, external) = ExtractWithExternal(scope.Reader, "ILSpyMcp.Pipeline.ToolPipeline");
        var defaultResult = Extract(scope.Reader, "ILSpyMcp.Pipeline.ToolPipeline");

        Assert.Equal(defaultResult, internalSet);
        Assert.Contains("ILSpyMcp.Pipeline.ToolPipelineResult", internalSet);
        Assert.Contains("ILSpyMcp.Caching.DecompileCache", internalSet);
        Assert.Contains("System.Threading.Tasks.Task`1 [System.Runtime]", external);
        Assert.Contains("System.Collections.Generic.List`1 [System.Collections]", external);
    }

    [Fact]
    public void WithExternal_事件外部类型_被收集()
    {
        // Members.Changed 为 event EventHandler?（跨程序集 TypeReference）：缺省 API 事件外部类型整体漏掉，WithExternal 应收集
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var (_, external) = ExtractWithExternal(scope.Reader, "ILSpyMcp.Samples.Members");

        Assert.Contains("System.EventHandler [System.Runtime]", external);
    }

    private static List<string> Extract(MetadataReader reader, string typeFullName)
    {
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        return ReferenceExtractor.ExtractMemberSignatureReferences(reader, reader.GetTypeDefinition(handle!.Value)).ToList();
    }

    private static (List<string> Internal, List<string> External) ExtractWithExternal(MetadataReader reader, string typeFullName)
    {
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        var (internalSet, external) = ReferenceExtractor.ExtractMemberSignatureReferencesWithExternal(reader, reader.GetTypeDefinition(handle!.Value));
        return (internalSet.ToList(), external.ToList());
    }

    /// <summary>
    /// 持有打开的 PEReader 与元数据读取器，保证 reader 在断言期间有效（PE 释放后 reader 访问会崩）。
    /// </summary>
    private sealed class MetadataScope : IDisposable
    {
        private readonly FileStream _fs;
        private readonly PEReader _pe;

        public MetadataScope(string path)
        {
            _fs = File.OpenRead(path);
            _pe = new PEReader(_fs);
            Reader = _pe.GetMetadataReader();
        }

        public MetadataReader Reader { get; }

        public void Dispose()
        {
            _pe.Dispose();
            _fs.Dispose();
        }
    }
}