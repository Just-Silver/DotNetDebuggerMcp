using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Tools.Metadata;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// search_string 工具层用例：命中 StringHolder 字符串 / 未命中 / typeName 限定 / 参数校验。 经
/// AppServices.ConfigureForTest 注入隔离缓存，与 ToolPipelineTests 等同属 AppServices collection 串行执行。
/// </summary>
[Collection("AppServices")]
public class SearchStringToolTests
{
    [Fact]
    public async Task SearchString_命中_输出StringHolder成员行()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await SearchStringTool.SearchString(TestDataPaths.TestSamplesDll, "不支持高性能计数器", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains($"{TestDataPaths.SamplesNamespace}.StringHolder::", result);
            Assert.Contains("\"不支持高性能计数器\"", result);
            Assert.Contains(" 0x06", result); // 方法 token 行尾
            Assert.DoesNotContain("at System", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task SearchString_忽略大小写_命中Query的ORDERBY()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await SearchStringTool.SearchString(TestDataPaths.TestSamplesDll, "order by", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("Query", result);
            Assert.Contains("\"ORDER BY GetDate()\"", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task SearchString_未命中_输出零匹配()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await SearchStringTool.SearchString(TestDataPaths.TestSamplesDll, "不存在的字符串字面量xyz", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("匹配实体: 0 个", result);
            Assert.DoesNotContain("at System", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task SearchString_typeName限定_仅返回StringHolder内()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await SearchStringTool.SearchString(TestDataPaths.TestSamplesDll, "Order", $"{TestDataPaths.SamplesNamespace}.StringHolder", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains($"{TestDataPaths.SamplesNamespace}.StringHolder::", result);
            Assert.DoesNotContain("No.Such", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task SearchString_typeName不存在_返回未找到提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await SearchStringTool.SearchString(TestDataPaths.TestSamplesDll, "Order", "No.Such.Type", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("未找到类型", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task SearchString_缺search_返回必填提示()
    {
        var result = await SearchStringTool.SearchString(TestDataPaths.TestSamplesDll, "", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("请指定 search", result);
    }
}