using ILSpyMcp.Caching;
using ILSpyMcp.Formatting;
using ILSpyMcp.Services;
using ILSpyMcp.Tools;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// 元数据工具共享缓存用例： <see cref="ToolExecutor.RunMetadata"/> 辅助（命中/未命中/错误不入缓存）与各元数据工具的二次命中断言。 与
/// ToolPipelineTests 同属 AppServices collection，串行执行避免静态状态竞态。
/// </summary>
[Collection("AppServices")]
public class MetadataCacheTests
{
    private static readonly string SamplesDll = TestDataPaths.TestSamplesDll;

    [Fact]
    public void RunMetadata_首次回源二次命中_且produce只执行一次()
    {
        Init();
        try
        {
            var calls = 0;
            string Call() => ToolExecutor.RunMetadata(SamplesDll, "test-sig", "",
                new FormatContext(SamplesDll, "测试", IsListing: true), _ =>
                {
                    calls++;
                    return new List<string> { "a", "b" };
                }, default);

            var first = Call();
            var second = Call();

            Assert.Equal(1, calls);
            Assert.DoesNotContain("缓存:   命中", first);
            Assert.Contains("缓存:   命中", second);
            Assert.Contains("1\ta", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public void RunMetadata_错误提示_原样返回且不入缓存()
    {
        Init();
        try
        {
            var calls = 0;
            string Call() => ToolExecutor.RunMetadata(SamplesDll, "err-sig", "",
                new FormatContext(SamplesDll, "测试"), _ =>
                {
                    calls++;
                    throw new InvalidOperationException("未找到类型 No.Such.Type");
                }, default);

            var first = Call();
            var second = Call();

            Assert.Equal(2, calls); // 未入缓存，第二次仍回源
            Assert.Contains("未找到类型", first);
            Assert.Equal(first, second); // 两次返回一致
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public void RunMetadata_IO异常_返回提示且不入缓存()
    {
        Init();
        try
        {
            var calls = 0;
            string Call() => ToolExecutor.RunMetadata(SamplesDll, "io-sig", "",
                new FormatContext(SamplesDll, "测试"), _ =>
                {
                    calls++;
                    throw new IOException("模拟读取失败");
                }, default);

            var first = Call();
            var second = Call();

            Assert.Equal(2, calls);
            Assert.Contains("无法读取程序集元数据", first);
            Assert.Contains("模拟读取失败", first);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task ListTypes_二次调用_头部标注缓存命中()
    {
        Init();
        try
        {
            var first = await ListTypesTool.ListTypes(SamplesDll, "c", nameContains: "Members", cancellationToken: TestContext.Current.CancellationToken);
            var second = await ListTypesTool.ListTypes(SamplesDll, "c", nameContains: "Members", cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain("缓存:   命中", first);
            Assert.Contains("缓存:   命中", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task Signature_二次调用_头部标注缓存命中()
    {
        Init();
        try
        {
            var first = await SignatureTool.Signature(SamplesDll, "ILSpyMcp.Samples.Members", cancellationToken: TestContext.Current.CancellationToken);
            var second = await SignatureTool.Signature(SamplesDll, "ILSpyMcp.Samples.Members", cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain("缓存:   命中", first);
            Assert.Contains("缓存:   命中", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task Signature_参数不同_各自独立缓存()
    {
        Init();
        try
        {
            var first = await SignatureTool.Signature(SamplesDll, "ILSpyMcp.Samples.Members", cancellationToken: TestContext.Current.CancellationToken);
            var different = await SignatureTool.Signature(SamplesDll, "ILSpyMcp.Samples.Props", cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain("缓存:   命中", different); // 不同 typeName → 不同签名 → 未命中
            Assert.Contains("public static string StaticProp", different);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task Hierarchy_二次调用_头部标注缓存命中()
    {
        Init();
        try
        {
            var first = await HierarchyTool.Hierarchy(SamplesDll, "ILSpyMcp.Samples.Dog", cancellationToken: TestContext.Current.CancellationToken);
            var second = await HierarchyTool.Hierarchy(SamplesDll, "ILSpyMcp.Samples.Dog", cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain("缓存:   命中", first);
            Assert.Contains("缓存:   命中", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task Dependencies_二次调用_头部标注缓存命中()
    {
        Init();
        try
        {
            var first = await DependenciesTool.Dependencies(SamplesDll, "ILSpyMcp.Samples.Shared", cancellationToken: TestContext.Current.CancellationToken);
            var second = await DependenciesTool.Dependencies(SamplesDll, "ILSpyMcp.Samples.Shared", cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain("缓存:   命中", first);
            Assert.Contains("缓存:   命中", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallGraph_类型级_二次调用_头部标注缓存命中()
    {
        Init();
        try
        {
            var first = await CallGraphTool.CallGraph(SamplesDll, typeName: "ILSpyMcp.Samples.Caller", cancellationToken: TestContext.Current.CancellationToken);
            var second = await CallGraphTool.CallGraph(SamplesDll, typeName: "ILSpyMcp.Samples.Caller", cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain("缓存:   命中", first);
            Assert.Contains("缓存:   命中", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallGraph_token级_二次调用_头部标注缓存命中()
    {
        Init();
        try
        {
            var token = TestDataPaths.FirstCalleeMethodToken(SamplesDll);
            var first = await CallGraphTool.CallGraph(SamplesDll, token: token, cancellationToken: TestContext.Current.CancellationToken);
            var second = await CallGraphTool.CallGraph(SamplesDll, token: token, cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain("缓存:   命中", first);
            Assert.Contains("缓存:   命中", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task AssemblyInfo_二次调用_头部标注缓存命中()
    {
        Init();
        try
        {
            var first = await AssemblyInfoTool.AssemblyInfo(SamplesDll, cancellationToken: TestContext.Current.CancellationToken);
            var second = await AssemblyInfoTool.AssemblyInfo(SamplesDll, cancellationToken: TestContext.Current.CancellationToken);

            Assert.DoesNotContain("缓存:   命中", first);
            Assert.Contains("缓存:   命中", second);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public void RunMetadataPe_首次回源二次命中_且produce只执行一次()
    {
        Init();
        try
        {
            var calls = 0;
            string Call() => ToolExecutor.RunMetadataPe(SamplesDll, "pe-sig", "",
                new FormatContext(SamplesDll, "测试", IsListing: true), (_, reader) =>
                {
                    calls++;
                    return new List<string> { reader.GetAssemblyDefinition() is var a ? reader.GetString(a.Name) : "" };
                }, default);
            var first = Call();
            var second = Call();
            Assert.Equal(1, calls);
            Assert.DoesNotContain("缓存:   命中", first);
            Assert.Contains("缓存:   命中", second);
        }
        finally { AppServices.ResetForTest(); }
    }

    [Fact]
    public async Task Signature_未找到类型_重复查询不入缓存()
    {
        Init();
        try
        {
            var first = await SignatureTool.Signature(SamplesDll, "No.Such.Type", cancellationToken: TestContext.Current.CancellationToken);
            var second = await SignatureTool.Signature(SamplesDll, "No.Such.Type", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("未找到类型", first);
            Assert.Contains("未找到类型", second);
            Assert.DoesNotContain("缓存:   命中", second); // 错误提示原样返回，不带头部
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    /// <summary>
    /// 以 1MB 小缓存重建 AppServices（元数据工具与反编译工具共用同一缓存实例），测试结束恢复默认。
    /// </summary>
    private static void Init()
    {
        AppServices.ConfigureForTest(new DecompileCache(1 * 1024 * 1024));
    }
}