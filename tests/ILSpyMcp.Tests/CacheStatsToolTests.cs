using ILSpyMcp.Caching;
using ILSpyMcp.Services;
using ILSpyMcp.Tools;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// cache_stats 工具用例：缓存状态报告的占用/条目数/命中率/逐条明细。经 <see cref="AppServices.ConfigureForTest"/> 注入小缓存并预置条目，
/// 与 ToolPipelineTests 同属 AppServices collection 串行执行。
/// </summary>
[Collection("AppServices")]
public class CacheStatsToolTests
{
    [Fact]
    public async Task CacheStats_空缓存_输出占用0与无明细()
    {
        AppServices.ConfigureForTest(new DecompileCache(1024));
        try
        {
            var result = await CacheStatsTool.CacheStats(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("当前占用: 0 B / 1.0 KB（0.0%）", result);
            Assert.Contains("条目数: 0", result);
            Assert.Contains("命中率: 暂无查询", result);
            Assert.Contains("条目明细: （无）", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CacheStats_含条目与命中_输出占用命中率与明细()
    {
        AppServices.ConfigureForTest(new DecompileCache(1024 * 1024));
        try
        {
            var cache = AppServices.Cache;
            var big = cache.BuildKey(@"C:\data\ILSpyMcp.TestSamples.dll", "type\u001FILSpyMcp.Samples.BigClass");
            var small = cache.BuildKey(@"C:\data\Other.dll", "assembly-info");
            cache.Put(big, new List<string> { "line1", "line2" });
            cache.Put(small, new List<string> { "x" });
            cache.Get(big);
            cache.Get(big);
            cache.Get(new CacheKey(@"C:\data\other.dll", "fp", "nope")); // 未命中

            var result = await CacheStatsTool.CacheStats(cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("条目数: 2", result);
            Assert.Contains("命中 2 次，未命中 1 次（66.7%）", result);
            Assert.Contains("decompile: ILSpyMcp.Samples.BigClass", result); // 签名渲染为工具名 + 参数
            Assert.Contains("assembly_info", result);
            Assert.Contains("ilspymcp.testsamples.dll", result);
            // 明细按占用降序：big（2 行）在 small（1 行）之前
            Assert.True(result.IndexOf("BigClass", StringComparison.Ordinal) < result.IndexOf("other.dll", StringComparison.Ordinal));
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CacheStats_lines分页_明细按行号切片()
    {
        AppServices.ConfigureForTest(new DecompileCache(1024 * 1024));
        try
        {
            var cache = AppServices.Cache;
            for (var i = 0; i < 3; i++)
            {
                cache.Put(cache.BuildKey(@"C:\data\a.dll", $"sig-{i}"), new List<string> { $"line{i}" });
            }

            var result = await CacheStatsTool.CacheStats(lines: "1-1", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("1\t", result); // 带行号的明细切片
            Assert.DoesNotContain("2\t", result);
            Assert.DoesNotContain("已截断", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }
}