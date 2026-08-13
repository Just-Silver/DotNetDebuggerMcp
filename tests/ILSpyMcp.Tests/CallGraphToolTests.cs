using ILSpyMcp.Tools;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// call_graph 工具层用例：段标题输出 / 无内部调用（无）占位 / 反向段 / 类型不存在 / 参数校验 / lines 分页。
/// </summary>
public class CallGraphToolTests
{
    [Fact]
    public async Task CallGraph_Caller_含正向与反向段标题()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Caller");

        Assert.Contains("方法体调用的内部类型:", result);
        Assert.Contains("程序集内方法体调用此类型的类型:", result);
        Assert.Contains("ILSpyMcp.Samples.Callee", result);
    }

    [Fact]
    public async Task CallGraph_Uses_方法体无内部调用_输出无占位()
    {
        // Uses.Run 方法体为空、仅默认 ctor 调 Object..ctor（外部），正向段应为（无）占位而非报错
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Uses");

        Assert.Contains("（无）", result);
        Assert.DoesNotContain("at System", result);
    }

    [Fact]
    public async Task CallGraph_WithClosure_编译器生成调用被过滤_输出无占位()
    {
        // Make 只调闭包类型（编译器生成），过滤后正向段为（无）
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.WithClosure");

        Assert.Contains("（无）", result);
    }

    [Fact]
    public async Task CallGraph_类型不存在_返回未找到提示()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "No.Such.Type");

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public async Task CallGraph_缺typeName_返回必填提示()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "");

        Assert.Contains("请指定 typeName", result);
    }

    [Fact]
    public async Task CallGraph_lines非法格式_返回格式提示()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Caller", lines: "abc");

        Assert.Contains("lines 参数格式应为", result);
    }

    [Fact]
    public async Task CallGraph_includeExternal_输出外部段()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Caller", includeExternal: true);

        Assert.Contains("方法体调用的内部类型:", result);
        Assert.Contains("方法体调用的外部类型:", result);
        Assert.Contains("System.Console [System.Console]", result);
        Assert.Contains("ILSpyMcp.Samples.Callee", result);
    }

    [Fact]
    public async Task CallGraph_缺省includeExternal_无外部段()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Caller");

        Assert.DoesNotContain("方法体调用的外部类型:", result);
    }
}
