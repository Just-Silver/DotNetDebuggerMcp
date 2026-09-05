using DotNetDebuggerMcp.Tools;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// dependencies 工具层用例：内部段标题 / includeExternal 外部段开关 / 无外部引用占位 / 类型不存在 / 参数校验。
/// </summary>
public class DependenciesToolTests
{
    [Fact]
    public async Task Dependencies_Members_含内部段标题()
    {
        var result = await DependenciesTool.Dependencies(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Members", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("成员签名引用的内部类型:", result);
        Assert.Contains("程序集内引用此类型的类型:", result);
    }

    [Fact]
    public async Task Dependencies_includeExternal_输出外部段()
    {
        // Members.Changed 为 event EventHandler?（跨程序集）：includeExternal 应追加外部段并带程序集归属
        var result = await DependenciesTool.Dependencies(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Members", includeExternal: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("成员签名引用的内部类型:", result);
        Assert.Contains("成员签名引用的外部类型:", result);
        Assert.Contains("System.EventHandler [System.Runtime]", result);
    }

    [Fact]
    public async Task Dependencies_缺省includeExternal_无外部段()
    {
        var result = await DependenciesTool.Dependencies(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Members", cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("成员签名引用的外部类型:", result);
    }

    [Fact]
    public async Task Dependencies_includeExternal_无外部引用_输出无占位()
    {
        // Uses 成员签名仅引用内部类型（DerivedClass/Dog），外部段应为（无）占位而非报错
        var result = await DependenciesTool.Dependencies(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Uses", includeExternal: true, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("成员签名引用的外部类型:", result);
        Assert.Contains("（无）", result);
        Assert.DoesNotContain("at System", result);
    }

    [Fact]
    public async Task Dependencies_类型不存在_返回未找到提示()
    {
        var result = await DependenciesTool.Dependencies(TestDataPaths.TestSamplesDll, "No.Such.Type", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public async Task Dependencies_缺typeName_返回必填提示()
    {
        var result = await DependenciesTool.Dependencies(TestDataPaths.TestSamplesDll, "", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("请指定 typeName", result);
    }
}