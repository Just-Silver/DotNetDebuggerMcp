using ILSpyMcp.Tools;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// generic_instantiations 工具层用例：typeName 无 arity 短名命中 / 带 arity 全名命中 / 方法体调用段 / 无实例化（无）占位 / 类型不存在 / 参数校验。
/// </summary>
public class GenericInstantiationToolTests
{
    [Fact]
    public async Task GenericInstantiations_GenericBox无arity_命中成员签名段()
    {
        var result = await GenericInstantiationTool.GenericInstantiations(TestDataPaths.TestSamplesDll, "GenericBox", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("成员签名中的泛型实例化:", result);
        Assert.Contains("ILSpyMcp.Samples.GenericUser::", result);
        Assert.Contains("GenericBox<int>", result);
        Assert.Contains("GenericBox<string>", result);
    }

    [Fact]
    public async Task GenericInstantiations_GenericBox带arity全名_同样命中()
    {
        var result = await GenericInstantiationTool.GenericInstantiations(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.GenericBox`1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("GenericBox<int>", result);
    }

    [Fact]
    public async Task GenericInstantiations_GenericHelper_输出方法体调用段()
    {
        var result = await GenericInstantiationTool.GenericInstantiations(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.GenericHelper", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("方法体调用中的泛型实例化:", result);
        Assert.Contains("Echo<int>", result);
    }

    [Fact]
    public async Task GenericInstantiations_无实例化类型_两段输出无占位()
    {
        var result = await GenericInstantiationTool.GenericInstantiations(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Caller", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("（无）", result);
        Assert.DoesNotContain("at System", result);
    }

    [Fact]
    public async Task GenericInstantiations_类型不存在_返回未找到提示()
    {
        var result = await GenericInstantiationTool.GenericInstantiations(TestDataPaths.TestSamplesDll, "No.Such.Type", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public async Task GenericInstantiations_缺typeName_返回必填提示()
    {
        var result = await GenericInstantiationTool.GenericInstantiations(TestDataPaths.TestSamplesDll, "", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("请指定 typeName", result);
    }
}