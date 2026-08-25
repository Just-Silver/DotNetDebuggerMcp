using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// StringLiteralScanner 字符串字面量反查用例：子串命中成员 / 忽略大小写 / typeName 限定范围。 素材：生成测试程序集（tests/TestData）中的
/// StringHolder（Log 含"不支持高性能计数器"、Query 含"ORDER BY GetDate()"、Get 返回"配置Key:SqlSugar:Enabled"）。
/// </summary>
public class StringLiteralScannerTests
{
    [Fact]
    public void 子串搜索命中成员()
    {
        using var scope = new MetadataScope();
        var hits = new StringLiteralScanner(scope.Pe).Scan("不支持高性能计数器");

        Assert.Contains(hits, h => h.MemberSignature.Contains("Log") && h.Value == "不支持高性能计数器");
    }

    [Fact]
    public void 子串搜索忽略大小写()
    {
        // "order by" 小写应命中 Query 的大写 "ORDER BY GetDate()"
        using var scope = new MetadataScope();
        var hits = new StringLiteralScanner(scope.Pe).Scan("order by");

        Assert.Contains(hits, h => h.MemberSignature.Contains("Query") && h.Value == "ORDER BY GetDate()");
    }

    [Fact]
    public void typeName限定范围()
    {
        using var scope = new MetadataScope();
        var handle = MetadataNaming.FindType(scope.Reader, "ILSpyMcp.Samples.StringHolder");
        Assert.True(handle.HasValue, "测试程序集中未找到 StringHolder");

        var hits = new StringLiteralScanner(scope.Pe).Scan("Order", handle);

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.Equal("ILSpyMcp.Samples.StringHolder", h.TypeFullName));
    }

    /// <summary>
    /// 持有打开的 PEReader 与元数据读取器，保证 reader 在断言期间有效（PE 释放后 reader 访问会崩）。
    /// </summary>
    private sealed class MetadataScope : IDisposable
    {
        private readonly FileStream _fs;
        private readonly PEReader _pe;

        public MetadataScope()
        {
            _fs = File.OpenRead(TestDataPaths.TestSamplesDll);
            _pe = new PEReader(_fs);
            Reader = _pe.GetMetadataReader();
        }

        public PEReader Pe => _pe;

        public MetadataReader Reader { get; }

        public void Dispose()
        {
            _pe.Dispose();
            _fs.Dispose();
        }
    }
}