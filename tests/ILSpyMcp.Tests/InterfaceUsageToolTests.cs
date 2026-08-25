using ILSpyMcp.Tools;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// interface_usage 工具层用例：实现者段 / includeIndirect 间接实现者 / 调用点段 / 无调用点（无）占位 / 类型不存在 / 参数校验。
/// </summary>
public class InterfaceUsageToolTests
{
    [Fact]
    public async Task InterfaceUsage_IWorker_实现者段含WorkerBase()
    {
        var result = await InterfaceUsageTool.InterfaceUsage(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.IWorker");

        Assert.Contains("实现该接口的类型:", result);
        Assert.Contains("ILSpyMcp.Samples.WorkerBase", result);
        Assert.Contains("方法体调用接口成员的调用点:", result);
        Assert.Contains("成员签名引用该接口的类型:", result);
    }

    [Fact]
    public async Task InterfaceUsage_IWorker_includeIndirect_含间接实现者WorkerDerived()
    {
        var result = await InterfaceUsageTool.InterfaceUsage(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.IWorker", includeIndirect: true);

        Assert.Contains("ILSpyMcp.Samples.WorkerDerived", result);
    }

    [Fact]
    public async Task InterfaceUsage_IAnimal_调用点段含AnimalCaller()
    {
        var result = await InterfaceUsageTool.InterfaceUsage(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.IAnimal");

        Assert.Contains("ILSpyMcp.Samples.AnimalCaller::Run → Speak", result);
    }

    [Fact]
    public async Task InterfaceUsage_IWorker_无调用点与引用_输出无占位()
    {
        var result = await InterfaceUsageTool.InterfaceUsage(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.IWorker");

        Assert.Contains("（无）", result);
    }

    [Fact]
    public async Task InterfaceUsage_类型不存在_返回未找到提示()
    {
        var result = await InterfaceUsageTool.InterfaceUsage(TestDataPaths.TestSamplesDll, "No.Such.Type");

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public async Task InterfaceUsage_传class_返回非接口提示()
    {
        // Dog 为 class（实现 IAnimal）：interface_usage 仅适用于接口，非接口应返回中文提示而非输出貌似有效的三段伪结果
        var result = await InterfaceUsageTool.InterfaceUsage(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Dog");

        Assert.Contains("不是接口类型", result);
        Assert.DoesNotContain("实现该接口的类型:", result);
    }

    [Fact]
    public async Task InterfaceUsage_缺typeName_返回必填提示()
    {
        var result = await InterfaceUsageTool.InterfaceUsage(TestDataPaths.TestSamplesDll, "");

        Assert.Contains("请指定 typeName", result);
    }
}