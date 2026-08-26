using ILSpyMcp.Metadata;
using ILSpyMcp.Services;
using ILSpyMcp.Tools;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// field_access 工具层用例：typeName+fieldName 定位字段 / fieldToken 定位 / 跨程序集多匹配 #MEMBER 清单 / 参数校验 / 未找到。 经
/// AppServices.ConfigureForTest 注入隔离缓存，与 ToolPipelineTests 等同属 AppServices collection 串行执行。
/// </summary>
[Collection("AppServices")]
public class FieldAccessToolTests
{
    [Fact]
    public async Task FieldAccess_typeName加fieldName_输出三段且Writes含FieldWriter()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await FieldAccessTool.FieldAccess(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.FieldHolder", "Data", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("读取该字段的成员:", result);
            Assert.Contains("写入该字段的成员:", result);
            Assert.Contains("取地址的成员:", result);
            Assert.Contains("ILSpyMcp.Samples.FieldUser::", result);
            Assert.Contains("ILSpyMcp.Samples.FieldWriter::", result);
            Assert.DoesNotContain("at System", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task FieldAccess_fieldToken_按token定位字段()
    {
        AppServices.ConfigureForTest();
        try
        {
            var token = FieldTokenOf();
            var result = await FieldAccessTool.FieldAccess(TestDataPaths.TestSamplesDll, fieldToken: token, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("ILSpyMcp.Samples.FieldWriter::", result);
            Assert.Contains("ILSpyMcp.Samples.FieldUser::", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task FieldAccess_fieldName跨程序集多匹配_返回MEMBER清单()
    {
        AppServices.ConfigureForTest();
        try
        {
            // 跨程序集搜字段名含 "D"：Uses.Derived / Uses.Dog / FieldHolder.Data 多个字段命中 → #MEMBER 清单
            var result = await FieldAccessTool.FieldAccess(TestDataPaths.TestSamplesDll, fieldName: "D", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("#MEMBER", result);
            Assert.Contains("fieldToken", result);
            Assert.Contains("ILSpyMcp.Samples.FieldHolder", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task FieldAccess_取地址段_空段输出无占位()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await FieldAccessTool.FieldAccess(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.FieldHolder", "Data", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("取地址的成员:", result);
            Assert.Contains("（无）", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task FieldAccess_缺fieldName_返回必填提示()
    {
        var result = await FieldAccessTool.FieldAccess(TestDataPaths.TestSamplesDll, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("请指定 fieldName", result);
    }

    [Fact]
    public async Task FieldAccess_fieldToken非法_返回提示()
    {
        var result = await FieldAccessTool.FieldAccess(TestDataPaths.TestSamplesDll, fieldToken: "0xZZZZ", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("不是有效的元数据 token", result);
    }

    [Fact]
    public async Task FieldAccess_fieldToken为方法token_返回提示()
    {
        var result = await FieldAccessTool.FieldAccess(TestDataPaths.TestSamplesDll, fieldToken: "0x06000001", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("不是字段定义的元数据 token", result);
    }

    [Fact]
    public async Task FieldAccess_typeName不存在_返回未找到提示()
    {
        var result = await FieldAccessTool.FieldAccess(TestDataPaths.TestSamplesDll, "No.Such.Type", "Data", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public async Task FieldAccess_fieldName未匹配_返回未找到字段提示()
    {
        var result = await FieldAccessTool.FieldAccess(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.FieldHolder", "NoSuchField", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("未找到字段名包含", result);
    }

    /// <summary>
    /// 取测试程序集 FieldHolder.Data 字段的元数据 token（0x04 开头），供 fieldToken 用例使用。
    /// </summary>
    private static string FieldTokenOf()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var typeHandle = MetadataNaming.FindType(reader, "ILSpyMcp.Samples.FieldHolder");
        Assert.True(typeHandle.HasValue, "测试程序集中未找到 FieldHolder");
        foreach (var fieldHandle in reader.GetTypeDefinition(typeHandle.Value).GetFields())
        {
            if (reader.GetString(reader.GetFieldDefinition(fieldHandle).Name) == "Data")
            {
                return $"0x{MetadataTokens.GetToken(fieldHandle):x8}";
            }
        }
        throw new InvalidOperationException("FieldHolder 未找到字段 Data");
    }
}